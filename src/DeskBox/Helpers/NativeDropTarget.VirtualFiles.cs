using System.Runtime.InteropServices;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.DragDrop;

namespace DeskBox.Helpers;

public sealed partial class NativeDropTarget
{
    private const int GlobalMemoryCopyBufferSize = 81920;

    // Internal for the materialization worker, which runs the whole stream
    // read on its own apartment after the drop callback returned.
    internal static IReadOnlyList<string> ExtractVirtualFiles(NativeOleDataObject dataObject) =>
        ExtractVirtualFiles(dataObject, out _, out _);

    internal static IReadOnlyList<string> ExtractVirtualFiles(
        NativeOleDataObject dataObject,
        out int skippedOrFailedCount,
        out bool budgetExceeded,
        long? maxBytesOverride = null)
    {
        skippedOrFailedCount = 0;
        budgetExceeded = false;
        var descriptorFormat = new NativeFormatEtc
        {
            ClipboardFormat = s_fileGroupDescriptorFormat,
            TargetDevice = IntPtr.Zero,
            Aspect = DVASPECT_CONTENT,
            Index = -1,
            MediumType = TYMED_HGLOBAL,
        };
        int descriptorHr = dataObject.GetData(
            ref descriptorFormat,
            out NativeStorageMedium descriptorMedium);
        if (descriptorHr != S_OK || descriptorMedium.Content == IntPtr.Zero)
        {
            if (descriptorHr >= 0)
            {
                OleDropTargetNativeMethods.ReleaseStgMedium(ref descriptorMedium);
            }

            App.LogVerbose(
                "[DropTarget] Virtual extraction: FILEGROUPDESCRIPTOR GetData " +
                $"failed hr=0x{descriptorHr:X8} empty={descriptorMedium.Content == IntPtr.Zero}");
            return [];
        }

        List<FILEDESCRIPTORW> descriptors;
        try
        {
            descriptors = ReadVirtualFileDescriptors(descriptorMedium.Content);
        }
        finally
        {
            OleDropTargetNativeMethods.ReleaseStgMedium(ref descriptorMedium);
        }

        if (descriptors.Count == 0)
        {
            App.LogVerbose(
                "[DropTarget] Virtual extraction: descriptor parse produced " +
                "no entries");
            return [];
        }

        string virtualDropsRoot = DropStagingFileOps.GetVirtualDropsRootDirectory();
        DropStagingFileOps.EnsureSafeStagingRootDirectory(virtualDropsRoot);
        string temporaryDirectory = Path.Combine(
            virtualDropsRoot,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var paths = new List<string>();
        var seenTopLevelEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Defer creating & registering directory descriptors until after child
        // streams are materialized: if every child stream under a directory
        // fails, we must not leave an empty directory shell on disk or report
        // it as a successful drop (FL-09). Genuine empty directories are created
        // after the loop once we know no child under them failed.
        var pendingDirectories = new List<(string DestinationPath, string TopLevelEntryPath)>();
        var seenPendingDirectoryDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoriesWithFailedChildren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        long effectiveBudgetBytes = DropStagingBudgetPolicy.ResolveDynamicByteBudget(
            temporaryDirectory,
            explicitLimitBytes: maxBytesOverride);
        long remainingBudgetBytes = effectiveBudgetBytes;
        for (int index = 0; index < descriptors.Count; index++)
        {
            FILEDESCRIPTORW descriptor = descriptors[index];
            string descriptorFileName = descriptor.GetFileName();
            bool isDirectory = (descriptor.dwFlags & FD_ATTRIBUTES) != 0 &&
                (descriptor.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            if (!DropStagingFileOps.TryBuildVirtualDropDestinationPath(
                    temporaryDirectory,
                    descriptorFileName,
                    isDirectory,
                    index,
                    out string destinationPath,
                    out string topLevelEntryPath))
            {
                skippedOrFailedCount++;
                App.Log(
                    $"[DropTarget] Rejected unsafe or invalid virtual descriptor " +
                    $"index={index} name='{descriptorFileName}'");
                continue;
            }

            if (isDirectory)
            {
                if (seenPendingDirectoryDestinations.Add(destinationPath))
                {
                    pendingDirectories.Add((destinationPath, topLevelEntryPath));
                }

                continue;
            }

            long? declaredFileSize = (descriptor.dwFlags & FD_FILESIZE) != 0
                ? unchecked((long)(((ulong)descriptor.nFileSizeHigh << 32) | descriptor.nFileSizeLow))
                : null;
            if (TrySaveVirtualFileContents(
                    dataObject,
                    index,
                    destinationPath,
                    ref remainingBudgetBytes,
                    effectiveBudgetBytes,
                    out bool itemBudgetExceeded,
                    declaredFileSize))
            {
                bool isTopLevelFile = string.Equals(
                    destinationPath,
                    topLevelEntryPath,
                    StringComparison.OrdinalIgnoreCase);
                string resolvedPath = isTopLevelFile
                    ? VirtualDropFileNameResolver.AddMissingExtensionFromContent(
                        destinationPath)
                    : destinationPath;
                if (isTopLevelFile &&
                    !string.Equals(
                        resolvedPath,
                        destinationPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    App.Log(
                        $"[DropTarget] Added missing virtual-file extension " +
                        $"source='{destinationPath}' resolved='{resolvedPath}'");
                }

                // The stream came from a web context (browser drag); a local
                // file without MOTW would bypass SmartScreen scrutiny.
                ZoneIdentifierWriter.TryMarkFile(
                    resolvedPath,
                    ZoneIdentifierWriter.VirtualDropSource);
                string entryToImport = isTopLevelFile ? resolvedPath : topLevelEntryPath;
                if (seenTopLevelEntries.Add(entryToImport))
                {
                    paths.Add(entryToImport);
                }
            }
            else if (itemBudgetExceeded)
            {
                budgetExceeded = true;
                skippedOrFailedCount += Math.Max(1, descriptors.Count - index);
                App.Log(
                    "[DropTarget] Virtual drop aborted: payload exceeded " +
                    $"materialization budget ({effectiveBudgetBytes} bytes) at index={index}.");
                DropStagingFileOps.CleanupVirtualDropBatchDirectory(temporaryDirectory);
                return [];
            }
            else
            {
                skippedOrFailedCount++;
                if (!string.Equals(destinationPath, topLevelEntryPath, StringComparison.OrdinalIgnoreCase))
                {
                    directoriesWithFailedChildren.Add(topLevelEntryPath);
                }
            }
        }

        // Genuine empty directories (no failed children) are created on disk
        // and remain importable.
        foreach ((string directoryDestination, string directoryTopLevel) in pendingDirectories)
        {
            if (directoriesWithFailedChildren.Contains(directoryTopLevel))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(directoryDestination);
            }
            catch
            {
                skippedOrFailedCount++;
                continue;
            }

            if (seenTopLevelEntries.Add(directoryTopLevel))
            {
                paths.Add(directoryTopLevel);
            }
        }

        if (paths.Count == 0)
        {
            App.LogVerbose(
                $"[DropTarget] Virtual extraction: {descriptors.Count} " +
                "descriptor(s) but no stream saved (FILECONTENTS reads all " +
                "failed)");
            DropStagingFileOps.CleanupVirtualDropBatchDirectory(temporaryDirectory);
        }

        return paths;
    }

    internal static unsafe List<FILEDESCRIPTORW> ReadVirtualFileDescriptors(IntPtr descriptorHandle)
    {
        nuint rawBufferSize = OleDropTargetNativeMethods.GlobalSize(descriptorHandle);
        if (rawBufferSize < sizeof(uint))
        {
            return [];
        }

        long bufferSize = rawBufferSize > long.MaxValue ? long.MaxValue : (long)rawBufferSize;
        IntPtr pointer = OleDropTargetNativeMethods.GlobalLock(descriptorHandle);
        if (pointer == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            int count = Marshal.ReadInt32(pointer);
            if (count <= 0 || count > 4096)
            {
                return [];
            }

            // The declared count comes from the drag source and cannot be
            // trusted: a small HGLOBAL with a large count would make the loop
            // below read past the allocation. Clamp to what the buffer can
            // actually hold, exactly like the Shell IDList reader above.
            int descriptorSize = sizeof(FILEDESCRIPTORW);
            int capacity = (int)Math.Max(
                0,
                (bufferSize - sizeof(uint)) / descriptorSize);
            if (capacity < count)
            {
                App.Log(
                    $"[DropTarget] FileGroupDescriptor count={count} exceeds " +
                    $"buffer={bufferSize}; clamped to {capacity}.");
                count = capacity;
            }

            if (count == 0)
            {
                return [];
            }

            var descriptors = new List<FILEDESCRIPTORW>(count);
            byte* descriptorBase = (byte*)pointer + sizeof(uint);
            for (int index = 0; index < count; index++)
            {
                descriptors.Add(*(FILEDESCRIPTORW*)(descriptorBase + ((nuint)index * (nuint)descriptorSize)));
            }

            return descriptors;
        }
        finally
        {
            OleDropTargetNativeMethods.GlobalUnlock(descriptorHandle);
        }
    }

    private static bool TrySaveVirtualFileContents(
        NativeOleDataObject dataObject,
        int index,
        string destinationPath,
        ref long remainingBudgetBytes,
        long effectiveBudgetBytes,
        out bool budgetExceeded,
        long? declaredFileSize = null)
    {
        budgetExceeded = false;
        // Try TYMED_ISTREAM first — FileContents from browser drag sources
        // (Chrome / Edge / Firefox) is strictly an IStream. Asking for the
        // combined mask TYMED_ISTREAM | TYMED_HGLOBAL in one FORMATETC is
        // unreliable across OLE sources: some reject the combined mask
        // outright (returning DV_E_FORMATETC), which made the widget silently
        // ignore browser drops even though DragEnter/Drop fired. Fall back to
        // TYMED_HGLOBAL for sources that provide the contents as a memory blob.
        NativeStorageMedium contentsMedium = default;
        uint actualTymed = 0;
        IntPtr actualMedium = IntPtr.Zero;
        foreach (uint tymed in new uint[] { TYMED_ISTREAM, TYMED_HGLOBAL })
        {
            var contentsFormat = new NativeFormatEtc
            {
                ClipboardFormat = s_fileContentsFormat,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = index,
                MediumType = tymed,
            };
            int hr = dataObject.GetData(ref contentsFormat, out contentsMedium);
            if (hr == S_OK && contentsMedium.Content != IntPtr.Zero)
            {
                actualTymed = contentsMedium.MediumType;
                actualMedium = contentsMedium.Content;
                break;
            }
            // Release any partially populated medium before retrying with a
            // different tymed (GetData may have set unionMember or pUnkForRelease).
            if (hr >= 0 || contentsMedium.Content != IntPtr.Zero || contentsMedium.ReleaseUnknown != IntPtr.Zero)
            {
                OleDropTargetNativeMethods.ReleaseStgMedium(ref contentsMedium);
                contentsMedium = default;
            }
        }

        if (actualMedium == IntPtr.Zero)
        {
            App.Log(
                $"[DropTarget] No FileContents payload for virtual file index={index} " +
                $"destination='{destinationPath}' (browser may not have provided a stream)");
            return false;
        }

        try
        {
            if ((actualTymed & TYMED_ISTREAM) != 0)
            {
                SaveComStream(actualMedium, destinationPath, ref remainingBudgetBytes, declaredFileSize);
                return true;
            }

            if ((actualTymed & TYMED_HGLOBAL) != 0)
            {
                SaveGlobalMemory(actualMedium, destinationPath, ref remainingBudgetBytes, declaredFileSize);
                return true;
            }

            App.Log(
                $"[DropTarget] Unexpected FileContents tymed=0x{actualTymed:X} " +
                $"for index={index}");
            return false;
        }
        catch (VirtualDropBudgetExceededException ex)
        {
            budgetExceeded = true;
            remainingBudgetBytes = 0;
            App.Log(
                $"[DropTarget] Virtual file index={index} exceeded the " +
                $"materialization budget ({effectiveBudgetBytes} bytes): {ex.Message}");
            DropStagingFileOps.TryDeleteStagingFile(destinationPath);
            return false;
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to save virtual file '{destinationPath}': {ex.Message}");
            DropStagingFileOps.TryDeleteStagingFile(destinationPath);
            return false;
        }
        finally
        {
            OleDropTargetNativeMethods.ReleaseStgMedium(ref contentsMedium);
        }
    }

    private static void SaveComStream(
        IntPtr streamPointer,
        string destinationPath,
        ref long remainingBudgetBytes,
        long? declaredFileSize = null)
    {
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        long before = remainingBudgetBytes;
        try
        {
            NativeComStreamReader.CopyTo(streamPointer, destination, remainingBudgetBytes, declaredFileSize);
            remainingBudgetBytes = Math.Max(
                0,
                remainingBudgetBytes - destination.Length);
        }
        catch
        {
            remainingBudgetBytes = Math.Max(0, before - destination.Length);
            throw;
        }
    }

    internal static long ResolveEffectiveGlobalMemoryLength(long globalSize, long? declaredFileSize)
    {
        if (declaredFileSize is >= 0 && declaredFileSize.Value < globalSize)
        {
            return declaredFileSize.Value;
        }

        return globalSize;
    }

    private static void SaveGlobalMemory(
        IntPtr memoryHandle,
        string destinationPath,
        ref long remainingBudgetBytes,
        long? declaredFileSize = null)
    {
        nuint rawSizeU = OleDropTargetNativeMethods.GlobalSize(memoryHandle);
        long rawSize = rawSizeU > long.MaxValue ? long.MaxValue : (long)rawSizeU;
        long size = ResolveEffectiveGlobalMemoryLength(rawSize, declaredFileSize);
        if (size < 0)
        {
            throw new IOException("Virtual file memory payload size is invalid.");
        }

        if (size > remainingBudgetBytes)
        {
            remainingBudgetBytes = 0;
            throw new VirtualDropBudgetExceededException(
                remainingBudgetBytes,
                $"Virtual file memory payload of {size} bytes exceeds the budget.");
        }

        IntPtr pointer = OleDropTargetNativeMethods.GlobalLock(memoryHandle);
        if (pointer == IntPtr.Zero)
        {
            throw new IOException("Could not lock virtual file memory payload.");
        }

        try
        {
            using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (size > 0)
            {
                int chunkLength = (int)Math.Min(size, GlobalMemoryCopyBufferSize);
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(chunkLength);
                try
                {
                    long offset = 0;
                    while (offset < size)
                    {
                        int chunk = (int)Math.Min(chunkLength, size - offset);
                        Marshal.Copy((nint)(pointer.ToInt64() + offset), buffer, 0, chunk);
                        destination.Write(buffer, 0, chunk);
                        offset += chunk;
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            remainingBudgetBytes -= size;
        }
        finally
        {
            OleDropTargetNativeMethods.GlobalUnlock(memoryHandle);
        }
    }
}
