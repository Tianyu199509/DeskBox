using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>Windows copy and stream enumeration for verified, non-destructive storage copies.</summary>
internal static unsafe class StorageMigrationNativeMethods
{
    internal static void PublishCopy(string source, string destination) =>
        File.Move(source, destination, overwrite: false);

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
            // 38: no named data stream. 87 (invalid parameter): FAT32/exFAT
            // volumes do not support the stream enumeration at all.
            if (error is 38 or 87) return result;
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

    internal static void Copy(string source, string destination, Action<long> progress, CancellationToken token)
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

    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
}
