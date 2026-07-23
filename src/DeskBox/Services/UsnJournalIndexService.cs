using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Everything-style full-disk indexer built on the NTFS USN change journal.
/// It enumerates every MFT record on each fixed NTFS volume via FSCTL_ENUM_USN_DATA,
/// reconstructs full paths from the file-reference-number (FRN) hierarchy, and answers
/// filename queries in-memory. This indexes the whole disk in seconds, independent of
/// folder selection.
///
/// Reading the USN journal requires an elevated volume handle. When DeskBox is not
/// running as administrator (its normal mode, since elevation breaks drag-and-drop),
/// opening the volume fails and <see cref="IsAvailable"/> stays false so callers fall
/// back to the directory-scan index. The service never throws for that case.
/// </summary>
public sealed partial class UsnJournalIndexService : IDisposable
{
    // ── Win32 constants ──────────────────────────────────────────────
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    /// <summary>CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 44, METHOD_NEITHER, FILE_ANY_ACCESS)</summary>
    private const uint FsctlEnumUsnData = 0x000900B3;

    private const int FileAttributeDirectory = 0x10;

    /// <summary>The NTFS root directory always lives at FRN 5.</summary>
    private const ulong RootFileReferenceNumber = 5;

    /// <summary>Hard cap on in-memory entries to prevent unbounded memory growth.</summary>
    private const int MaxIndexEntries = 500_000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumData
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref MftEnumData lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>Raw MFT record captured during enumeration.</summary>
    private readonly record struct RawRecord(ulong Parent, string Name, bool IsDir, long Timestamp);

    /// <summary>Resolved index entry, shape-compatible with the directory-scan index.</summary>
    private sealed record UsnEntry(
        string FileName,
        string DirectoryPath,
        string FullPath,
        bool IsDirectory,
        DateTime LastModified);

    /// <summary>Top-level directory names skipped on every volume to keep system noise out of the index.</summary>
    private static readonly string[] s_systemDirectoryNames =
    [
        "Windows", "ProgramData", "Program Files", "Program Files (x86)",
        "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs",
        "Config.Msi", "MSOCache", "WinSxS", "servicing", "assembly", "Intel", "AMD"
    ];

    private readonly ConcurrentDictionary<string, UsnEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private CancellationTokenSource? _scanCts;
    private Task? _scanTask;
    private int _isScanning;
    private int _isPaused;
    private volatile bool _isAvailable;
    private bool _isDisposed;

    /// <summary>True once at least one volume was indexed via the USN journal.</summary>
    public bool IsAvailable => _isAvailable;

    public bool IsScanning => Volatile.Read(ref _isScanning) == 1;

    public bool IsPaused => Volatile.Read(ref _isPaused) == 1;

    public int EntryCount => _index.Count;

    public int IndexedCount => _index.Count;

    public event Action? IndexUpdated;

    /// <summary>Raised periodically during indexing with the current entry count.</summary>
    public event Action<int>? ProgressChanged;

    /// <summary>Pauses an in-progress scan.</summary>
    public void PauseIndexing()
    {
        if (IsScanning && !IsPaused)
        {
            Volatile.Write(ref _isPaused, 1);
            _pauseGate.Reset();
        }
    }

    /// <summary>Resumes a paused scan.</summary>
    public void ResumeIndexing()
    {
        if (IsPaused)
        {
            Volatile.Write(ref _isPaused, 0);
            _pauseGate.Set();
        }
    }

    /// <summary>Starts background enumeration of every fixed NTFS volume.</summary>
    public void StartIndexing()
    {
        if (_isDisposed || IsScanning)
        {
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        _scanTask = Task.Run(() => EnumerateAllVolumes(token), token);
    }

    public void StopIndexing()
    {
        _scanCts?.Cancel();
    }

    /// <summary>Searches the full-disk index by file name.</summary>
    public IReadOnlyList<SearchResultItem> Search(string query, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query) || _index.IsEmpty)
        {
            return [];
        }

        string normalizedQuery = query.Trim();
        if (maxResults <= 0)
        {
            return [];
        }

        var topResults = new PriorityQueue<SearchResultItem, (double Score, long ModifiedTicks)>();

        foreach (var (_, entry) in _index)
        {
            double score = SearchIndexService.ComputeRelevance(entry.FileName, normalizedQuery);
            if (score <= 0)
            {
                continue;
            }

            var result = new SearchResultItem
            {
                Kind = entry.IsDirectory ? SearchResultKind.Folder : SearchResultKind.File,
                Title = entry.FileName,
                Subtitle = entry.DirectoryPath,
                DetailPath = entry.FullPath,
                ModifiedAt = entry.LastModified,
                RelevanceScore = score,
                Glyph = entry.IsDirectory ? "\uE8B7" : null
            };

            long modifiedTicks = entry.LastModified.ToUniversalTime().Ticks;
            topResults.Enqueue(result, (score, modifiedTicks));
            if (topResults.Count > maxResults)
            {
                topResults.Dequeue();
            }
        }

        return topResults.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(r => r.RelevanceScore)
            .ThenByDescending(r => r.ModifiedAt)
            .Take(maxResults)
            .ToList();
    }

    private void EnumerateAllVolumes(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _isScanning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            bool anyVolumeIndexed = false;

            foreach (string drive in Directory.GetLogicalDrives())
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var info = new DriveInfo(drive);
                    if (!info.IsReady || info.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    if (!string.Equals(info.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (EnumerateVolume(drive, token))
                    {
                        anyVolumeIndexed = true;
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[UsnIndex] Drive {drive} skipped: {ex.Message}");
                }
            }

            _isAvailable = anyVolumeIndexed && !_index.IsEmpty;

            if (_isAvailable)
            {
                IndexUpdated?.Invoke();
                App.Log($"[UsnIndex] USN indexing complete. {_index.Count} entries across all volumes.");
            }
            else
            {
                App.Log("[UsnIndex] USN journal unavailable (not elevated or no NTFS volume). Search falls back to the directory index.");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation.
        }
        catch (Exception ex)
        {
            App.Log($"[UsnIndex] Indexing error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isScanning, 0);
            Volatile.Write(ref _isPaused, 0);
            _pauseGate.Set();
        }
    }

    /// <summary>
    /// Enumerates a single volume's MFT via the USN journal and merges the resolved
    /// paths into the index. Returns false when the volume cannot be opened (the
    /// non-elevated case) so callers know to rely on the fallback index.
    /// </summary>
    private bool EnumerateVolume(string driveRoot, CancellationToken token)
    {
        string root = driveRoot.TrimEnd('\\');
        string volumePath = @"\\.\" + root;

        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFile(volumePath, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                App.Log($"[UsnIndex] Cannot open {volumePath} (elevation required). Skipping volume.");
                return false;
            }

            var records = new Dictionary<ulong, RawRecord>();
            int bufferSize = 1024 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var med = new MftEnumData
                {
                    StartFileReferenceNumber = 0,
                    LowUsn = 0,
                    HighUsn = long.MaxValue
                };
                int inputSize = Marshal.SizeOf<MftEnumData>();

                while (!token.IsCancellationRequested)
                {
                    bool ok = DeviceIoControl(handle, FsctlEnumUsnData, ref med, inputSize, buffer, bufferSize, out int bytesReturned, IntPtr.Zero);
                    if (!ok || bytesReturned < 8)
                    {
                        break; // End of journal (or access error) — stop enumerating.
                    }

                    ulong nextFrn = (ulong)Marshal.ReadInt64(buffer, 0);
                    ParseRecords(buffer, 8, bytesReturned, records);

                    if (nextFrn == med.StartFileReferenceNumber)
                    {
                        break; // No forward progress — guard against an infinite loop.
                    }

                    med.StartFileReferenceNumber = nextFrn;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (records.Count == 0)
            {
                return false;
            }

            BuildPaths(root, records);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[UsnIndex] Volume {root} enumeration failed: {ex.Message}");
            return false;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>Parses contiguous USN_RECORD v2 structures from the enumeration buffer.</summary>
    private static void ParseRecords(IntPtr buffer, int start, int end, Dictionary<ulong, RawRecord> records)
    {
        int offset = start;

        // USN_RECORD v2 layout (byte offsets):
        //   0  RecordLength (4)      8  FileReferenceNumber (8)
        //   16 ParentFileReferenceNumber (8)   32 TimeStamp (8)
        //   52 FileAttributes (4)    56 FileNameLength (2)   58 FileNameOffset (2)
        //   60 FileName (variable, UTF-16)
        while (offset + 60 <= end)
        {
            int recordLength = Marshal.ReadInt32(buffer, offset);
            if (recordLength <= 0 || offset + recordLength > end)
            {
                break;
            }

            ulong frn = (ulong)Marshal.ReadInt64(buffer, offset + 8);
            ulong parentFrn = (ulong)Marshal.ReadInt64(buffer, offset + 16);
            long timestamp = Marshal.ReadInt64(buffer, offset + 32);
            int fileAttributes = Marshal.ReadInt32(buffer, offset + 52);
            short fileNameLength = Marshal.ReadInt16(buffer, offset + 56);
            short fileNameOffset = Marshal.ReadInt16(buffer, offset + 58);

            if (fileNameLength > 0 && offset + fileNameOffset + fileNameLength <= end)
            {
                string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + fileNameOffset), fileNameLength / 2) ?? string.Empty;
                if (name.Length > 0)
                {
                    bool isDir = (fileAttributes & FileAttributeDirectory) != 0;
                    records[frn] = new RawRecord(parentFrn, name, isDir, timestamp);
                }
            }

            offset += recordLength;
        }
    }

    /// <summary>
    /// Reconstructs full paths from the FRN hierarchy and fills the index, skipping
    /// well-known system directories. Directory paths are memoized so each chain is
    /// resolved once.
    /// </summary>
    private void BuildPaths(string root, Dictionary<ulong, RawRecord> records)
    {
        // Memoized full path per directory FRN; the volume root anchors the walk.
        var directoryPaths = new Dictionary<ulong, string> { [RootFileReferenceNumber] = root };

        string? ResolveDirectoryPath(ulong frn)
        {
            if (directoryPaths.TryGetValue(frn, out string? cached))
            {
                return cached;
            }

            // Walk up until a known ancestor, collecting the unknown chain.
            var chain = new List<ulong>();
            var seen = new HashSet<ulong>();
            ulong current = frn;
            string basePath;

            while (true)
            {
                if (directoryPaths.TryGetValue(current, out string? known))
                {
                    basePath = known;
                    break;
                }

                if (!records.TryGetValue(current, out RawRecord record) || !record.IsDir)
                {
                    return null; // Broken chain (deleted/reused FRN) — unresolvable.
                }

                if (!seen.Add(current))
                {
                    return null; // Cycle guard.
                }

                chain.Add(current);

                if (record.Parent == current)
                {
                    // Self-parent marks a volume root.
                    basePath = root;
                    directoryPaths[current] = root;
                    chain.RemoveAt(chain.Count - 1);
                    break;
                }

                current = record.Parent;
            }

            // Rebuild downward from the known ancestor to the requested directory.
            string path = basePath;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                ulong chainFrn = chain[i];
                path = path + "\\" + records[chainFrn].Name;
                directoryPaths[chainFrn] = path;
            }

            return directoryPaths.TryGetValue(frn, out string? resolved) ? resolved : null;
        }

        foreach (var (frn, record) in records)
        {
            if (frn == RootFileReferenceNumber)
            {
                continue;
            }

            if (_index.Count >= MaxIndexEntries)
            {
                break;
            }

            string? parentPath = ResolveDirectoryPath(record.Parent);
            if (parentPath is null)
            {
                continue; // Orphaned entry (parent chain could not be resolved).
            }

            string fullPath = parentPath + "\\" + record.Name;
            if (IsSystemPath(fullPath, root))
            {
                continue;
            }

            _index[fullPath] = new UsnEntry(
                record.Name,
                parentPath,
                fullPath,
                record.IsDir,
                TimestampToDateTime(record.Timestamp));

            // Report progress every 5000 entries.
            if (_index.Count % 5000 == 0)
            {
                ProgressChanged?.Invoke(_index.Count);
            }
        }
    }

    /// <summary>True when the path lives under a top-level system directory of the volume.</summary>
    private static bool IsSystemPath(string fullPath, string root)
    {
        foreach (string systemName in s_systemDirectoryNames)
        {
            string prefix = root + "\\" + systemName;
            if (fullPath.Length >= prefix.Length &&
                fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (fullPath.Length == prefix.Length || fullPath[prefix.Length] == '\\'))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTime TimestampToDateTime(long timestamp)
    {
        try
        {
            return timestamp > 0 ? DateTime.FromFileTime(timestamp) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _pauseGate.Dispose();
        _index.Clear();
    }
}
