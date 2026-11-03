using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class EverythingIntegrationTests
{
    [Theory]
    [InlineData("budget report", false, "\"budget report\"")]
    [InlineData("ext:pdf budget", true, "ext:pdf budget")]
    [InlineData("  notes  ", false, "\"notes\"")]
    public void ProviderQuery_IsLiteralUnlessAdvancedSyntaxIsEnabled(
        string query,
        bool advancedSyntax,
        string expected)
    {
        Assert.Equal(expected, EverythingSearchService.BuildProviderQuery(query, advancedSyntax));
    }

    [Theory]
    [InlineData("report.txt", "report.txt", 100)]
    [InlineData("report.txt", "report", 90)]
    [InlineData("report-final.txt", "report", 80)]
    [InlineData("annual-report.txt", "report", 50)]
    public void ProviderResultRelevance_PreservesDeskBoxRanking(
        string fileName,
        string query,
        double expected)
    {
        Assert.Equal(expected, EverythingSearchService.ComputeRelevance(fileName, query));
    }

    [Fact]
    public void LegacyCleanup_RemovesOnlyDeskBoxOwnedIndexArtifacts()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DeskBox-LegacySearchCleanup-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(cache);
        string[] obsolete =
        [
            Path.Combine(cache, "search-index.json"),
            Path.Combine(cache, "search-index-v2.json.tmp"),
            Path.Combine(cache, "search-index-v2.json.roots")
        ];
        string retained = Path.Combine(cache, "search-history.json");
        try
        {
            foreach (string path in obsolete)
            {
                File.WriteAllText(path, "obsolete");
            }

            File.WriteAllText(retained, "keep");
            Assert.Equal(obsolete.Length, LegacySearchIndexCleanupService.TryCleanup(root));
            Assert.All(obsolete, path => Assert.False(File.Exists(path)));
            Assert.True(File.Exists(retained));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ProjectUsesPureWin32IpcWithoutBundlingExternalEverythingSdkDll()
    {
        string project = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/DeskBox.csproj"));
        string cargo = File.ReadAllText(TestPaths.FromRepository("native/Cargo.toml"));
        string ipcClient = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/EverythingNativeMethods.cs"));

        Assert.DoesNotContain("<TargetPath>EverythingSdk.dll</TargetPath>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("EverythingSdk.dll", ipcClient, StringComparison.Ordinal);
        Assert.DoesNotContain("DeskBoxSearchCore", project, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox-search-core", cargo, StringComparison.Ordinal);
        Assert.Contains("EVERYTHING_TASKBAR_NOTIFICATION", ipcClient, StringComparison.Ordinal);
    }

    [Fact]
    public void PureIpcClient_RoundTripsQuery2PacketAndParsesList2Reply()
    {
        byte[] packet = EverythingNativeMethods.BuildQuery2Packet(
            replyHwnd: 0x00123456,
            replyCopyDataMessage: 0x2001,
            searchFlags: 0,
            offset: 5,
            maxResults: 25,
            requestFlags: EverythingNativeMethods.RequestFileName |
                EverythingNativeMethods.RequestPath |
                EverythingNativeMethods.RequestSize |
                EverythingNativeMethods.RequestDateModified,
            sortType: EverythingNativeMethods.SortNameAscending,
            searchText: "\"DeskBox.csproj\"");

        // 28-byte header + (16 + 1) * 2 UTF-16LE bytes = 62 bytes.
        Assert.Equal(62, packet.Length);
        Assert.Equal(0x00123456u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(0, 4)));
        Assert.Equal(0x2001u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4)));
        Assert.Equal(25u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16, 4)));
        Assert.Equal(0x53u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24, 4)));
        Assert.Equal("\"DeskBox.csproj\"", Encoding.Unicode.GetString(packet.AsSpan(28, 32)));

        long expectedModified = new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero).ToFileTime();
        byte[] list2 = BuildSyntheticList2Buffer(
            totalItems: 42,
            ("report.pdf", @"D:\Docs", IsFolder: false, Size: 8192L, DateModified: expectedModified),
            ("Projects", @"D:\", IsFolder: true, Size: 0L, DateModified: expectedModified));

        Assert.True(EverythingNativeMethods.TryParseList2Reply(list2, out EverythingIpcQueryReply reply));
        Assert.Equal(42u, reply.TotalResults);
        Assert.Equal(2, reply.Items.Count);

        Assert.Equal("report.pdf", reply.Items[0].FileName);
        Assert.Equal(@"D:\Docs", reply.Items[0].DirectoryPath);
        Assert.False(reply.Items[0].IsFolder);
        Assert.Equal(8192L, reply.Items[0].FileSize);
        Assert.Equal(expectedModified, reply.Items[0].DateModifiedFileTime);

        Assert.Equal("Projects", reply.Items[1].FileName);
        Assert.Equal(@"D:\", reply.Items[1].DirectoryPath);
        Assert.True(reply.Items[1].IsFolder);
        Assert.Null(reply.Items[1].FileSize);
    }

    [Fact]
    public void PureIpcClient_CommunicatesOverLiveWin32WmCopyDataWindow()
    {
        string customClass = "DeskBoxEverythingMock_" + Guid.NewGuid().ToString("N");
        long expectedModified = new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.Zero).ToFileTime();
        byte[] replyPayload = BuildSyntheticList2Buffer(
            totalItems: 1,
            ("invoice.xlsx", @"C:\Finance", IsFolder: false, Size: 4096L, DateModified: expectedModified));

        using var ready = new ManualResetEventSlim(false);
        using var stop = new ManualResetEventSlim(false);
        Exception? serverError = null;

        var serverThread = new Thread(() =>
        {
            try
            {
                RunMockEverythingServer(customClass, replyPayload, ready, stop);
            }
            catch (Exception ex)
            {
                serverError = ex;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "MockEverythingIpcServer",
        };
        serverThread.SetApartmentState(ApartmentState.STA);
        serverThread.Start();

        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(serverError);

            var probe = EverythingNativeMethods.ProbeConnection(customClass);
            Assert.True(probe.Connected);
            Assert.Equal("1.4.1.1026", probe.Version);
            Assert.Equal(EverythingNativeMethods.ErrorOk, probe.Error);

            bool queried = EverythingNativeMethods.TryQuery(
                "\"invoice\"",
                offset: 0,
                maxResults: 10,
                CancellationToken.None,
                out EverythingIpcQueryReply reply,
                out uint errorCode,
                ipcWindowClass: customClass,
                timeoutMilliseconds: 5000);
            Assert.True(queried);
            Assert.Equal(EverythingNativeMethods.ErrorOk, errorCode);
            Assert.Equal(1u, reply.TotalResults);
            EverythingIpcResultEntry item = Assert.Single(reply.Items);
            Assert.Equal("invoice.xlsx", item.FileName);
            Assert.Equal(@"C:\Finance", item.DirectoryPath);
            Assert.Equal(4096L, item.FileSize);
        }
        finally
        {
            stop.Set();
            _ = serverThread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static byte[] BuildSyntheticList2Buffer(
        uint totalItems,
        params (string Name, string Path, bool IsFolder, long Size, long DateModified)[] entries)
    {
        uint flags = EverythingNativeMethods.RequestFileName |
            EverythingNativeMethods.RequestPath |
            EverythingNativeMethods.RequestSize |
            EverythingNativeMethods.RequestDateModified;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);

        bw.Write(totalItems);
        bw.Write((uint)entries.Length);
        bw.Write(0u); // offset
        bw.Write(flags);
        bw.Write(EverythingNativeMethods.SortNameAscending);

        long itemTablePos = ms.Position;
        for (int i = 0; i < entries.Length; i++)
        {
            bw.Write(0u);
            bw.Write(0u);
        }

        var offsets = new uint[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            offsets[i] = (uint)ms.Position;
            WriteLengthPrefixedUtf16(bw, entries[i].Name);
            WriteLengthPrefixedUtf16(bw, entries[i].Path);
            bw.Write(entries[i].Size);
            bw.Write(entries[i].DateModified);
        }

        long endPos = ms.Position;
        ms.Position = itemTablePos;
        for (int i = 0; i < entries.Length; i++)
        {
            bw.Write(entries[i].IsFolder ? 1u : 0u);
            bw.Write(offsets[i]);
        }

        ms.Position = endPos;
        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteLengthPrefixedUtf16(BinaryWriter bw, string text)
    {
        bw.Write((uint)text.Length);
        bw.Write(Encoding.Unicode.GetBytes(text));
        bw.Write((ushort)0);
    }

    private static byte[]? s_activeMockReply;

    private delegate nint MockWndProcDelegate(nint hwnd, uint msg, nuint wParam, nint lParam);

    private static readonly MockWndProcDelegate s_mockWndProc = MockEverythingWndProc;

    private static void RunMockEverythingServer(
        string className,
        byte[] replyPayload,
        ManualResetEventSlim ready,
        ManualResetEventSlim stop)
    {
        s_activeMockReply = replyPayload;
        nint hInst = MockGetModuleHandle(null);
        nint classNamePtr = Marshal.StringToHGlobalUni(className);
        try
        {
            var wc = new MockWndClassExW
            {
                cbSize = (uint)Marshal.SizeOf<MockWndClassExW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_mockWndProc),
                hInstance = hInst,
                lpszClassName = classNamePtr,
            };
            _ = MockRegisterClassEx(ref wc);
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr);
        }

        nint hwnd = MockCreateWindowEx(0, className, string.Empty, 0x80000000u, 0, 0, 0, 0, 0, 0, hInst, 0);
        try
        {
            ready.Set();
            while (!stop.IsSet)
            {
                while (MockPeekMessage(out MockMsg msg, 0, 0, 0, 1))
                {
                    _ = MockTranslateMessage(ref msg);
                    _ = MockDispatchMessage(ref msg);
                }

                Thread.Sleep(10);
            }
        }
        finally
        {
            if (hwnd != 0)
            {
                _ = MockDestroyWindow(hwnd);
            }
        }
    }

    private static nint MockEverythingWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == 0x0400) // WM_USER
        {
            return wParam switch
            {
                401 => 1,
                0 => 1,
                1 => 4,
                2 => 1,
                3 => 1026,
                _ => 0,
            };
        }

        if (msg == 0x004A && lParam != 0) // WM_COPYDATA
        {
            var cds = Marshal.PtrToStructure<EverythingNativeMethods.CopyDataStruct>(lParam);
            if (cds.dwData == 18 && cds.cbData >= 28 && cds.lpData != 0 && s_activeMockReply is { } payload)
            {
                uint replyHwnd = (uint)Marshal.ReadInt32(cds.lpData, 0);
                uint replyToken = (uint)Marshal.ReadInt32(cds.lpData, 4);
                nint replyPtr = Marshal.AllocHGlobal(payload.Length);
                nint replyCdsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<EverythingNativeMethods.CopyDataStruct>());
                try
                {
                    Marshal.Copy(payload, 0, replyPtr, payload.Length);
                    var replyCds = new EverythingNativeMethods.CopyDataStruct
                    {
                        dwData = replyToken,
                        cbData = (uint)payload.Length,
                        lpData = replyPtr,
                    };
                    Marshal.StructureToPtr(replyCds, replyCdsPtr, fDeleteOld: false);
                    _ = MockSendMessage((nint)replyHwnd, 0x004A, (nuint)hwnd, replyCdsPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(replyCdsPtr);
                    Marshal.FreeHGlobal(replyPtr);
                }

                return 1;
            }
        }

        return MockDefWindowProc(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MockWndClassExW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MockMsg
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint MockGetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static extern ushort MockRegisterClassEx(ref MockWndClassExW lpwcx);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint MockCreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow")]
    private static extern bool MockDestroyWindow(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint MockDefWindowProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool MockPeekMessage(out MockMsg lpMsg, nint hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    private static extern bool MockTranslateMessage(ref MockMsg lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint MockDispatchMessage(ref MockMsg lpMsg);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint MockSendMessage(nint hWnd, uint msg, nuint wParam, nint lParam);
}
