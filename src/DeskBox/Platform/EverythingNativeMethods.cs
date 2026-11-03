using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Platform;

internal readonly record struct EverythingIpcResultEntry(
    string FileName,
    string DirectoryPath,
    bool IsFolder,
    long? FileSize,
    long? DateModifiedFileTime);

internal readonly record struct EverythingIpcQueryReply(
    uint TotalResults,
    IReadOnlyList<EverythingIpcResultEntry> Items);

/// <summary>
/// Pure Win32 <c>WM_USER</c> + <c>WM_COPYDATA</c> client for the Everything 1.4/1.5
/// IPC v2 binary protocol with zero external SDK DLL or dynamic VC++ runtime dependencies
/// across Direct and Microsoft Store NativeAOT builds.
/// </summary>
internal static unsafe partial class EverythingNativeMethods
{
    internal const string DefaultIpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    private const string ReplyWindowClassName = "DeskBoxEverythingIpcReplyWindow";

    internal const uint ErrorOk = 0;
    internal const uint ErrorIpc = 2;

    internal const uint SortNameAscending = 1;

    internal const uint RequestFileName = 0x00000001;
    internal const uint RequestPath = 0x00000002;
    private const uint RequestFullPathAndFileName = 0x00000004;
    private const uint RequestExtension = 0x00000008;
    internal const uint RequestSize = 0x00000010;
    private const uint RequestDateCreated = 0x00000020;
    internal const uint RequestDateModified = 0x00000040;

    private const uint DefaultRequestFlags =
        RequestFileName | RequestPath | RequestSize | RequestDateModified;

    private const uint ItemFlagFolder = 0x00000001;
    private const uint ItemFlagDrive = 0x00000002;

    private const uint WmUser = 0x0400;
    private const uint WmCopyData = 0x004A;
    private const nuint IpcGetMajorVersion = 0;
    private const nuint IpcGetMinorVersion = 1;
    private const nuint IpcGetRevision = 2;
    private const nuint IpcGetBuildNumber = 3;
    private const nuint IpcIsDbLoaded = 401;
    private const nuint CopyDataQuery2W = 18;

    private const uint SmtoAbortIfHung = 0x0002;
    private const uint WsPopup = 0x80000000;
    private const uint PmRemove = 0x0001;
    private const uint QsAllInput = 0x04FF;
    private const uint MsgfltAllow = 1;
    private const int GwlpUserData = -21;
    private const int MaxReplyPayloadBytes = 64 * 1024 * 1024;

    private static readonly object ClassRegistrationGate = new();
    private static bool s_replyClassRegistered;
    private static int s_nextReplyToken = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyDataStruct
    {
        public nuint dwData;
        public uint cbData;
        public nint lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassExW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public NativePoint pt;
    }

    private sealed class ReplyCaptureState(uint replyToken)
    {
        public uint ReplyToken { get; } = replyToken;
        public byte[]? Payload { get; set; }
        public volatile bool Received;
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nuint wParam,
        nint lParam,
        uint fuFlags,
        uint uTimeout,
        out nuint lpdwResult);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? lpModuleName);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(ref WndClassExW lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "ChangeWindowMessageFilterEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(
        nint hwnd,
        uint message,
        uint action,
        nint pChangeFilterStruct);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(
        out NativeMsg lpMsg,
        nint hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax,
        uint wRemoveMsg);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref NativeMsg lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref NativeMsg lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "MsgWaitForMultipleObjectsEx", SetLastError = true)]
    private static partial uint MsgWaitForMultipleObjectsEx(
        uint nCount,
        nint pHandles,
        uint dwMilliseconds,
        uint dwWakeMask,
        uint dwFlags);

    internal static (bool Connected, string? Version, uint Error) ProbeConnection(
        string ipcWindowClass = DefaultIpcWindowClass)
    {
        nint ipcHwnd = FindWindow(ipcWindowClass, null);
        if (ipcHwnd == 0)
        {
            return (false, null, ErrorIpc);
        }

        if (!TryQueryUserUint(ipcHwnd, IpcIsDbLoaded, out uint dbLoaded))
        {
            return (false, null, ErrorIpc);
        }

        if (dbLoaded == 0)
        {
            return (false, null, ErrorOk);
        }

        _ = TryQueryUserUint(ipcHwnd, IpcGetMajorVersion, out uint major);
        _ = TryQueryUserUint(ipcHwnd, IpcGetMinorVersion, out uint minor);
        _ = TryQueryUserUint(ipcHwnd, IpcGetRevision, out uint revision);
        _ = TryQueryUserUint(ipcHwnd, IpcGetBuildNumber, out uint build);
        string version = $"{major}.{minor}.{revision}.{build}";
        return (true, version, ErrorOk);
    }

    internal static bool TryQuery(
        string searchText,
        uint offset,
        uint maxResults,
        CancellationToken cancellationToken,
        out EverythingIpcQueryReply reply,
        out uint errorCode,
        string ipcWindowClass = DefaultIpcWindowClass,
        int timeoutMilliseconds = 10_000)
    {
        reply = new EverythingIpcQueryReply(0, []);
        errorCode = ErrorOk;

        cancellationToken.ThrowIfCancellationRequested();
        nint ipcHwnd = FindWindow(ipcWindowClass, null);
        if (ipcHwnd == 0)
        {
            errorCode = ErrorIpc;
            return false;
        }

        EnsureReplyWindowClassRegistered();
        nint hInstance = GetModuleHandle(null);
        nint replyHwnd = CreateWindowEx(
            0,
            ReplyWindowClassName,
            string.Empty,
            WsPopup,
            0,
            0,
            0,
            0,
            0,
            0,
            hInstance,
            0);
        if (replyHwnd == 0)
        {
            errorCode = ErrorIpc;
            return false;
        }

        uint replyToken = unchecked((uint)Interlocked.Increment(ref s_nextReplyToken));
        if (replyToken == 0)
        {
            replyToken = 1;
        }

        var state = new ReplyCaptureState(replyToken);
        GCHandle stateHandle = GCHandle.Alloc(state);
        try
        {
            _ = ChangeWindowMessageFilterEx(replyHwnd, WmCopyData, MsgfltAllow, 0);
            _ = SetWindowLongPtr(replyHwnd, GwlpUserData, GCHandle.ToIntPtr(stateHandle));

            byte[] queryPacket = BuildQuery2Packet(
                replyHwnd,
                replyToken,
                searchFlags: 0,
                offset,
                maxResults,
                DefaultRequestFlags,
                SortNameAscending,
                searchText);

            uint sendTimeoutMs = (uint)Math.Clamp(timeoutMilliseconds, 500, 30_000);
            fixed (byte* packetPtr = queryPacket)
            {
                var cds = new CopyDataStruct
                {
                    dwData = CopyDataQuery2W,
                    cbData = (uint)queryPacket.Length,
                    lpData = (nint)packetPtr,
                };

                // Use SMTO_ABORTIFHUNG without SMTO_BLOCK so incoming inter-thread
                // SendMessage(WM_COPYDATA) replies from Everything dispatch directly
                // to ReplyWndProc while SendMessageTimeout is in flight.
                nint sent = SendMessageTimeout(
                    ipcHwnd,
                    WmCopyData,
                    (nuint)replyHwnd,
                    (nint)(&cds),
                    SmtoAbortIfHung,
                    sendTimeoutMs,
                    out nuint sendResult);
                if (sent == 0 || sendResult == 0)
                {
                    errorCode = ErrorIpc;
                    return false;
                }
            }

            var stopwatch = Stopwatch.StartNew();
            while (!state.Received)
            {
                PumpPendingMessages(replyHwnd);
                if (state.Received)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                long remaining = timeoutMilliseconds - stopwatch.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    errorCode = ErrorIpc;
                    return false;
                }

                uint sliceMs = (uint)Math.Min(50, remaining);
                _ = MsgWaitForMultipleObjectsEx(0, 0, sliceMs, QsAllInput, 0);
            }

            if (state.Payload is null ||
                !TryParseList2Reply(state.Payload, out reply))
            {
                errorCode = ErrorIpc;
                return false;
            }

            return true;
        }
        finally
        {
            _ = SetWindowLongPtr(replyHwnd, GwlpUserData, 0);
            _ = DestroyWindow(replyHwnd);
            if (stateHandle.IsAllocated)
            {
                stateHandle.Free();
            }
        }
    }

    internal static byte[] BuildQuery2Packet(
        nint replyHwnd,
        uint replyCopyDataMessage,
        uint searchFlags,
        uint offset,
        uint maxResults,
        uint requestFlags,
        uint sortType,
        string searchText)
    {
        string normalized = searchText ?? string.Empty;
        int stringBytes = checked((normalized.Length + 1) * sizeof(char));
        byte[] packet = new byte[checked(28 + stringBytes)];
        Span<byte> span = packet;

        // EVERYTHING_IPC_QUERY2 header: 7 little-endian DWORDs (28 bytes),
        // followed immediately by the null-terminated UTF-16LE search string.
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], unchecked((uint)replyHwnd.ToInt64()));
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], replyCopyDataMessage);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], searchFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], maxResults);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..24], requestFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], sortType);

        Encoding.Unicode.GetBytes(normalized.AsSpan(), span.Slice(28, normalized.Length * sizeof(char)));
        return packet;
    }

    internal static bool TryParseList2Reply(
        ReadOnlySpan<byte> buffer,
        out EverythingIpcQueryReply reply)
    {
        reply = new EverythingIpcQueryReply(0, []);
        if (buffer.Length < 20)
        {
            return false;
        }

        uint totalItems = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..4]);
        uint numItems = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]);
        uint replyFlags = BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..16]);

        long tableEnd = 20L + ((long)numItems * 8L);
        if (tableEnd > buffer.Length)
        {
            return false;
        }

        var items = new List<EverythingIpcResultEntry>((int)Math.Min(numItems, 16_384));
        for (uint i = 0; i < numItems; i++)
        {
            int itemHeaderOffset = 20 + ((int)i * 8);
            uint itemFlags = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(itemHeaderOffset, 4));
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(itemHeaderOffset + 4, 4));
            if (dataOffset >= (uint)buffer.Length)
            {
                return false;
            }

            int cursor = (int)dataOffset;
            string fileName = string.Empty;
            string directoryPath = string.Empty;
            long? fileSize = null;
            long? dateModified = null;

            if ((replyFlags & RequestFileName) != 0 &&
                !TryReadLengthPrefixedUtf16(buffer, ref cursor, out fileName))
            {
                return false;
            }

            if ((replyFlags & RequestPath) != 0 &&
                !TryReadLengthPrefixedUtf16(buffer, ref cursor, out directoryPath))
            {
                return false;
            }

            if ((replyFlags & RequestFullPathAndFileName) != 0 &&
                !TryReadLengthPrefixedUtf16(buffer, ref cursor, out _))
            {
                return false;
            }

            if ((replyFlags & RequestExtension) != 0 &&
                !TryReadLengthPrefixedUtf16(buffer, ref cursor, out _))
            {
                return false;
            }

            if ((replyFlags & RequestSize) != 0)
            {
                if (cursor + 8 > buffer.Length)
                {
                    return false;
                }

                long rawSize = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(cursor, 8));
                cursor += 8;
                if (rawSize >= 0)
                {
                    fileSize = rawSize;
                }
            }

            if ((replyFlags & RequestDateCreated) != 0)
            {
                if (cursor + 8 > buffer.Length)
                {
                    return false;
                }

                cursor += 8;
            }

            if ((replyFlags & RequestDateModified) != 0)
            {
                if (cursor + 8 > buffer.Length)
                {
                    return false;
                }

                long rawModified = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(cursor, 8));
                cursor += 8;
                if (rawModified > 0)
                {
                    dateModified = rawModified;
                }
            }

            bool isFolder = (itemFlags & (ItemFlagFolder | ItemFlagDrive)) != 0;
            items.Add(new EverythingIpcResultEntry(
                fileName,
                directoryPath,
                isFolder,
                isFolder ? null : fileSize,
                dateModified));
        }

        reply = new EverythingIpcQueryReply(totalItems, items);
        return true;
    }

    private static bool TryReadLengthPrefixedUtf16(
        ReadOnlySpan<byte> buffer,
        ref int cursor,
        out string value)
    {
        value = string.Empty;
        if (cursor < 0 || cursor + 4 > buffer.Length)
        {
            return false;
        }

        uint charLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(cursor, 4));
        if (charLen > 32_768)
        {
            return false;
        }

        long byteSpan = ((long)charLen + 1L) * sizeof(char);
        if ((long)cursor + 4L + byteSpan > buffer.Length)
        {
            return false;
        }

        int charBytes = (int)charLen * sizeof(char);
        value = charBytes == 0
            ? string.Empty
            : Encoding.Unicode.GetString(buffer.Slice(cursor + 4, charBytes));
        cursor += 4 + (int)byteSpan;
        return true;
    }

    private static bool TryQueryUserUint(nint ipcHwnd, nuint command, out uint value)
    {
        nint ok = SendMessageTimeout(
            ipcHwnd,
            WmUser,
            command,
            0,
            SmtoAbortIfHung,
            2000,
            out nuint rawResult);
        if (ok == 0)
        {
            value = 0;
            return false;
        }

        value = unchecked((uint)rawResult);
        return true;
    }

    private static void PumpPendingMessages(nint replyHwnd)
    {
        while (PeekMessage(out NativeMsg msg, replyHwnd, 0, 0, PmRemove))
        {
            _ = TranslateMessage(ref msg);
            _ = DispatchMessage(ref msg);
        }
    }

    private static void EnsureReplyWindowClassRegistered()
    {
        if (s_replyClassRegistered)
        {
            return;
        }

        lock (ClassRegistrationGate)
        {
            if (s_replyClassRegistered)
            {
                return;
            }

            nint hInstance = GetModuleHandle(null);
            fixed (char* classNamePtr = ReplyWindowClassName)
            {
                var wndClass = new WndClassExW
                {
                    cbSize = (uint)sizeof(WndClassExW),
                    lpfnWndProc = &ReplyWndProc,
                    hInstance = hInstance,
                    lpszClassName = classNamePtr,
                };
                ushort atom = RegisterClassEx(ref wndClass);
                if (atom != 0 || Marshal.GetLastWin32Error() == 1410) // ERROR_CLASS_ALREADY_EXISTS
                {
                    s_replyClassRegistered = true;
                }
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint ReplyWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WmCopyData && lParam != 0)
        {
            nint statePtr = GetWindowLongPtr(hwnd, GwlpUserData);
            if (statePtr != 0)
            {
                try
                {
                    var handle = GCHandle.FromIntPtr(statePtr);
                    if (handle.Target is ReplyCaptureState state)
                    {
                        CopyDataStruct* cds = (CopyDataStruct*)lParam;
                        if (unchecked((uint)cds->dwData) == state.ReplyToken &&
                            cds->lpData != 0 &&
                            cds->cbData >= 20 &&
                            cds->cbData <= MaxReplyPayloadBytes)
                        {
                            byte[] copy = new byte[cds->cbData];
                            Marshal.Copy(cds->lpData, copy, 0, (int)cds->cbData);
                            state.Payload = copy;
                            state.Received = true;
                            return 1;
                        }
                    }
                }
                catch
                {
                }
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
