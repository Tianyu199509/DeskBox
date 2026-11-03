using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>Windows copy and stream enumeration for verified, non-destructive storage copies.</summary>
internal static class StorageMigrationNativeMethods
{
    private const uint OpenExisting = 3;
    private const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileAttributeTagInfoClass = 9;
    private const uint CloudReparseTagMask = 0xFFFF0FFFu;
    private const uint IoReparseTagCloud = 0x9000001Au;
    private const uint UnhydratedCloudAttributeMask =
        0x00001000u | // FILE_ATTRIBUTE_OFFLINE
        0x00040000u | // FILE_ATTRIBUTE_RECALL_ON_OPEN
        0x00400000u;  // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        IntPtr hFile,
        int fileInformationClass,
        out FileAttributeTagInfo lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(
        string lpExistingFileName,
        string lpNewFileName,
        uint dwFlags);

    internal static bool TryRenameCaseOnlyInPlace(
        string sourcePath,
        string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }

        return MoveFileExW(
            Extended(sourcePath),
            Extended(destinationPath),
            dwFlags: 0);
    }

    internal static bool IsAllowedStorageReparseAttributes(
        uint fileAttributes,
        uint reparseTag,
        out bool isUnhydratedCloudPlaceholder)
    {
        bool isCloudTag = (reparseTag & CloudReparseTagMask) == IoReparseTagCloud;
        if (!isCloudTag)
        {
            isUnhydratedCloudPlaceholder = false;
            return false;
        }

        isUnhydratedCloudPlaceholder = (fileAttributes & UnhydratedCloudAttributeMask) != 0;
        return !isUnhydratedCloudPlaceholder;
    }

    internal static unsafe bool IsHydratedCloudReparsePoint(
        string path,
        out bool isUnhydratedCloudPlaceholder)
    {
        isUnhydratedCloudPlaceholder = false;
        IntPtr handle = CreateFileW(
            Extended(path),
            dwDesiredAccess: 0,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out FileAttributeTagInfo tagInfo,
                    (uint)sizeof(FileAttributeTagInfo)))
            {
                return false;
            }

            return IsAllowedStorageReparseAttributes(
                tagInfo.FileAttributes,
                tagInfo.ReparseTag,
                out isUnhydratedCloudPlaceholder);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> exists and is
    /// either a normal non-reparse entry or a locally hydrated cloud reparse
    /// point (<c>IO_REPARSE_TAG_CLOUD*</c>), rejecting symlinks, junctions, and
    /// unhydrated cloud placeholders.
    /// </summary>
    internal static unsafe bool IsSafeTraversablePath(string path)
    {
        IntPtr handle = CreateFileW(
            Extended(path),
            dwDesiredAccess: 0,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out FileAttributeTagInfo tagInfo,
                    (uint)sizeof(FileAttributeTagInfo)))
            {
                return false;
            }

            const uint FileAttributeReparsePoint = 0x00000400u;
            if ((tagInfo.FileAttributes & FileAttributeReparsePoint) == 0)
            {
                return (tagInfo.FileAttributes & UnhydratedCloudAttributeMask) == 0;
            }

            return IsAllowedStorageReparseAttributes(
                tagInfo.FileAttributes,
                tagInfo.ReparseTag,
                out _);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    internal static void PublishCopy(string source, string destination) =>
        File.Move(source, destination, overwrite: false);

    private const uint FileReadAttributes = 0x0080u;
    private const uint FsctlReadFileUsnData = 0x000900EBu;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadFileUsnDataInput
    {
        public ushort MinMajorVersion;
        public ushort MaxMajorVersion;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref ReadFileUsnDataInput lpInBuffer,
        uint nInBufferSize,
        byte* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        byte* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>
    /// Reads the monotonic NTFS/ReFS MFT Update Sequence Number (<c>FSCTL_READ_FILE_USN_DATA</c>)
    /// without requiring elevation. Any data, alternate stream, or metadata mutation bumps USN
    /// even when timestamps and file size are restored.
    /// </summary>
    internal static unsafe bool TryReadFileUsn(string path, out long usn)
    {
        usn = 0;
        IntPtr handle = CreateFileW(
            Extended(path),
            FileReadAttributes,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            byte* buffer = stackalloc byte[512];
            var input = new ReadFileUsnDataInput { MinMajorVersion = 2, MaxMajorVersion = 3 };
            if (!DeviceIoControl(
                    handle,
                    FsctlReadFileUsnData,
                    ref input,
                    (uint)sizeof(ReadFileUsnDataInput),
                    buffer,
                    512,
                    out uint bytesReturned,
                    IntPtr.Zero) &&
                !DeviceIoControl(
                    handle,
                    FsctlReadFileUsnData,
                    IntPtr.Zero,
                    0,
                    buffer,
                    512,
                    out bytesReturned,
                    IntPtr.Zero))
            {
                return false;
            }

            if (bytesReturned < 32)
            {
                return false;
            }

            ReadOnlySpan<byte> span = new(buffer, (int)bytesReturned);
            ushort major = MemoryMarshal.Read<ushort>(span.Slice(4, 2));
            if (major == 2)
            {
                usn = MemoryMarshal.Read<long>(span.Slice(24, 8));
                return true;
            }

            if (major == 3 && bytesReturned >= 48)
            {
                usn = MemoryMarshal.Read<long>(span.Slice(40, 8));
                return true;
            }

            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Copies the default data stream and any NTFS alternate data streams in a single
    /// streaming pass while computing the exact framed SHA-256 digest in-flight.
    /// </summary>
    internal static string CopyStreamsAndHash(
        string source,
        string temporaryDestination,
        Action<long> copyProgress,
        CancellationToken token)
    {
        FileAttributes sourceAttributes = File.GetAttributes(source);
        DateTime creationUtc = File.GetCreationTimeUtc(source);
        DateTime lastWriteUtc = File.GetLastWriteTimeUtc(source);

        var streams = ReadStreams(source)
            .OrderBy(static s => s.Name, StringComparer.Ordinal)
            .ToList();
        if (streams.Count == 0)
        {
            streams.Add(("::$DATA", 0));
        }

        string extendedSource = Extended(source);
        string extendedDestination = Extended(temporaryDestination);

        // Exclusively reserve the base file first (matching COPY_FILE_FAIL_IF_EXISTS)
        // before opening any alternate stream that might sort lexically before "::$DATA".
        using (new FileStream(extendedDestination, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
        }

        using var aggregate = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long totalTransferred = 0;
        try
        {
            foreach (var stream in streams)
            {
                token.ThrowIfCancellationRequested();
                aggregate.AppendData(
                    System.Text.Encoding.UTF8.GetBytes(stream.Name + "\0" + stream.Length + "\0"));
                string inputPath = stream.Name == "::$DATA"
                    ? extendedSource
                    : extendedSource + stream.Name;
                string outputPath = stream.Name == "::$DATA"
                    ? extendedDestination
                    : extendedDestination + stream.Name;

                using var input = new FileStream(
                    inputPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    buffer.Length,
                    FileOptions.SequentialScan);
                using var output = new FileStream(
                    outputPath,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.Read,
                    buffer.Length,
                    FileOptions.SequentialScan);

                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    ReadOnlySpan<byte> chunk = buffer.AsSpan(0, read);
                    aggregate.AppendData(chunk);
                    output.Write(chunk);
                    totalTransferred += read;
                    copyProgress(totalTransferred);
                }

                output.Flush(flushToDisk: true);
                if (stream.Length == 0)
                {
                    copyProgress(totalTransferred);
                }
            }

            File.SetCreationTimeUtc(extendedDestination, creationUtc);
            File.SetLastWriteTimeUtc(extendedDestination, lastWriteUtc);
            const FileAttributes PreservedAttributeMask =
                FileAttributes.ReadOnly |
                FileAttributes.Hidden |
                FileAttributes.System |
                FileAttributes.Archive |
                FileAttributes.NotContentIndexed;
            FileAttributes targetAttributes = sourceAttributes & PreservedAttributeMask;
            File.SetAttributes(
                extendedDestination,
                targetAttributes == 0 ? FileAttributes.Normal : targetAttributes);

            return Convert.ToHexString(aggregate.GetHashAndReset());
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static void TryPruneEmptyMigrationWorkspace(string? temporaryDirectory)
    {
        if (string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            return;
        }

        try
        {
            string fullTaskTemp = Path.GetFullPath(temporaryDirectory);
            if (Directory.Exists(fullTaskTemp) &&
                !Directory.EnumerateFileSystemEntries(fullTaskTemp).Any())
            {
                Directory.Delete(fullTaskTemp, recursive: false);
            }

            string? migrationWorkspace = Path.GetDirectoryName(fullTaskTemp);
            if (!string.IsNullOrWhiteSpace(migrationWorkspace) &&
                Directory.Exists(migrationWorkspace) &&
                !Directory.EnumerateFileSystemEntries(migrationWorkspace).Any())
            {
                Directory.Delete(migrationWorkspace, recursive: false);
            }
        }
        catch
        {
            // Non-empty or busy workspace directories are intentionally left intact.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileExW(
        string source, string destination, IntPtr progress, IntPtr state,
        IntPtr cancel, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string path, int level, out StreamData data, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr find, out StreamData data);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr find);

    internal static IReadOnlyList<(string Name, long Length)> ReadStreams(string path)
    {
        var result = new List<(string, long)>();
        IntPtr find = FindFirstStreamW(Extended(path), 0, out StreamData data, 0);
        if (find == new IntPtr(-1))
        {
            int error = Marshal.GetLastWin32Error();
            // 1/50/87/123: non-NTFS or virtual volumes do not support stream
            // enumeration; 38: EOF (no named data stream).
            if (error is 1 or 38 or 50 or 87 or 123) return result;
            throw new IOException($"Cannot inspect file streams: '{path}'.", new Win32Exception(error));
        }
        try
        {
            do { result.Add((data.Name, data.Size)); }
            while (FindNextStreamW(find, out data));
            int error = Marshal.GetLastWin32Error();
            if (error != 38) throw new IOException($"Cannot inspect file streams: '{path}'.", new Win32Exception(error));
        }
        finally { FindClose(find); }
        return result;
    }

    /// <summary>
    /// Copies all non-default NTFS <c>:$DATA</c> alternate data streams (such as
    /// <c>:favicon:$DATA</c> on browser <c>.url</c> shortcuts) onto an existing
    /// destination file. Uses <c>FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE</c>
    /// so it coexists with the exclusive FCB delete-lock handles held by
    /// <c>FileService</c> during chunked cross-volume moves. Silently skips
    /// destination volumes that do not support named streams (FAT32/exFAT).
    /// </summary>
    internal static async Task CopyAlternateDataStreamsAsync(
        string sourceFilePath,
        string destinationFilePath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Name, long Length)> streams;
        try
        {
            streams = ReadStreams(sourceFilePath);
        }
        catch (IOException)
        {
            return;
        }

        if (streams.Count == 0)
        {
            return;
        }

        const uint genericRead = 0x8000_0000;
        const uint genericWrite = 0x4000_0000;
        const uint shareReadWriteDelete = 0x0000_0001 | 0x0000_0002 | 0x0000_0004;
        const uint createAlways = 2;
        const uint openExisting = 3;
        const uint fileFlagOverlapped = 0x4000_0000;
        const int bufferSize = 64 * 1024;

        string extendedSource = Extended(Path.GetFullPath(sourceFilePath));
        string extendedDestination = Extended(Path.GetFullPath(destinationFilePath));

        byte[]? rentedBuffer = null;
        try
        {
            foreach ((string streamName, _) in streams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(streamName) ||
                    !streamName.StartsWith(':') ||
                    string.Equals(streamName, "::$DATA", StringComparison.OrdinalIgnoreCase) ||
                    !streamName.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var sourceStreamHandle = FileTransferNativeMethods.CreateFileW(
                    extendedSource + streamName,
                    genericRead,
                    shareReadWriteDelete,
                    IntPtr.Zero,
                    openExisting,
                    fileFlagOverlapped,
                    IntPtr.Zero);
                if (sourceStreamHandle.IsInvalid)
                {
                    continue;
                }

                using var destinationStreamHandle = FileTransferNativeMethods.CreateFileW(
                    extendedDestination + streamName,
                    genericWrite,
                    shareReadWriteDelete,
                    IntPtr.Zero,
                    createAlways,
                    fileFlagOverlapped,
                    IntPtr.Zero);
                if (destinationStreamHandle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    // Non-NTFS destination volume (FAT32/exFAT/SMB without ADS):
                    // keep the primary file transfer intact.
                    if (error is 1 or 50 or 87 or 123)
                    {
                        return;
                    }

                    throw new IOException(
                        $"Cannot create alternate stream '{streamName}' on '{destinationFilePath}'.",
                        new Win32Exception(error));
                }

                rentedBuffer ??= System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);
                await using var sourceStream = new FileStream(
                    sourceStreamHandle,
                    FileAccess.Read,
                    4096,
                    isAsync: true);
                await using var destinationStream = new FileStream(
                    destinationStreamHandle,
                    FileAccess.Write,
                    4096,
                    isAsync: true);

                while (true)
                {
                    int read = await sourceStream.ReadAsync(
                        rentedBuffer.AsMemory(0, bufferSize),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destinationStream.WriteAsync(
                        rentedBuffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                }

                await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (rentedBuffer is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
    }

    internal static unsafe void Copy(string source, string destination, Action<long> progress, CancellationToken token)
    {
        var state = new CopyState(progress, token);
        GCHandle handle = GCHandle.Alloc(state);
        try
        {
            var callback = (delegate* unmanaged[Stdcall]<long, long, long, long, uint, uint, IntPtr, IntPtr, IntPtr, uint>)&OnProgress;
            // CreateNew; never decrypt EFS files to make an unsupported target work.
            if (!CopyFileExW(Extended(source), Extended(destination), (IntPtr)callback,
                    GCHandle.ToIntPtr(handle), IntPtr.Zero, 0x00000001))
            {
                int error = Marshal.GetLastWin32Error();
                token.ThrowIfCancellationRequested();
                throw state.Error ?? new IOException($"Cannot copy '{source}'.", new Win32Exception(error));
            }
            if (state.Error is { } failure) throw failure;
            token.ThrowIfCancellationRequested();
        }
        finally { handle.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnProgress(long total, long transferred, long streamSize, long streamTransferred,
        uint streamNumber, uint reason, IntPtr source, IntPtr destination, IntPtr context)
    {
        var state = (CopyState)GCHandle.FromIntPtr(context).Target!;
        try
        {
            if (state.Token.IsCancellationRequested) return 2; // PROGRESS_STOP preserves the partial copy.
            if (streamTransferred == streamSize && !FlushFileBuffers(destination))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            state.Progress(transferred);
            return 0;
        }
        catch (Exception ex)
        {
            state.Error = ex;
            return 2;
        }
    }

    private sealed class CopyState(Action<long> progress, CancellationToken token)
    {
        internal Action<long> Progress { get; } = progress;
        internal CancellationToken Token { get; } = token;
        internal Exception? Error { get; set; }
    }

    internal static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
}
