using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class EverythingIntegrationTests
{
    [Theory]
    [InlineData("budget report", false, "\"budget\" \"report\"")]
    [InlineData("\"budget report\" 2026", false, "\"budget report\" \"2026\"")]
    [InlineData("\"C:\\Users\\alice\\Desktop\\app用户评价-1.5.3.4.rp\"", false, "\"C:\\Users\\alice\\Desktop\\app用户评价-1.5.3.4.rp\"")]
    [InlineData("a|b !draft <v2>", false, "\"a|b\" \"!draft\" \"<v2>\"")]
    [InlineData("app用户评价\u30001.5.3.4", false, "\"app用户评价\" \"1.5.3.4\"")]
    [InlineData("\"\"   \"  \"", false, "")]
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
    [InlineData("app用户评价-1.5.3.4.rp", "app 1.5.3.4", 48)]
    [InlineData("app用户评价-1.5.3.4.rp", "用户评价 rp", 45)]
    [InlineData("app用户评价-1.5.3.4.rp", "Desktop app用户评价", 25)]
    [InlineData("readme.txt", "Desktop app用户评价", 10)]
    public void ProviderResultRelevance_PreservesDeskBoxRanking(
        string fileName,
        string query,
        double expected)
    {
        Assert.Equal(expected, EverythingSearchService.ComputeRelevance(fileName, query));
    }

    [Fact]
    public void QuotedPathQuery_ScoresExactFullPathAndSuppressesQuickCreateNoise()
    {
        const string quotedPath = "\"C:\\Users\\alice\\Desktop\\app用户评价-1.5.3.4.rp\"";
        const string fullPath = @"C:\Users\alice\Desktop\app用户评价-1.5.3.4.rp";

        Assert.True(SearchQuerySyntaxParser.LooksLikeFileSystemPath(quotedPath));
        Assert.Equal(
            100,
            EverythingSearchService.ComputeRelevance("app用户评价-1.5.3.4.rp", quotedPath, fullPath));
        Assert.Equal(
            10,
            EverythingSearchService.ComputeRelevance("other.txt", quotedPath, @"C:\Users\alice\Desktop\other.txt"));
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

    [Fact]
    public void QueryNative_SupplementsFirstPageWithPrefixAndRecentHitsWithoutBreakingNextOffset()
    {
        string customClass = "DeskBoxEverythingMock_" + Guid.NewGuid().ToString("N");
        long olderTime = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero).ToFileTime();
        long recentTime = new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero).ToFileTime();
        var observedRequests = new List<MockQuery2Request>();

        byte[] Respond(MockQuery2Request req)
        {
            lock (observedRequests)
            {
                observedRequests.Add(req);
            }

            if (req.SearchText == "\"app*\"" && req.SortType == EverythingNativeMethods.SortNameAscending)
            {
                return BuildSyntheticList2Buffer(
                    totalItems: 2,
                    ("app.exe", @"C:\Tools", IsFolder: false, Size: 2048L, DateModified: olderTime),
                    ("app用户评价-1.5.3.4.rp", @"C:\Users\alice\Desktop", IsFolder: false, Size: 16384L, DateModified: recentTime));
            }

            if (req.SearchText == "\"app\"" && req.SortType == EverythingNativeMethods.SortDateModifiedDescending)
            {
                return BuildSyntheticList2Buffer(
                    totalItems: 1200,
                    ("app用户评价-1.5.3.4.rp", @"C:\Users\alice\Desktop", IsFolder: false, Size: 16384L, DateModified: recentTime),
                    ("my-app-spec.docx", @"C:\Users\alice\Documents", IsFolder: false, Size: 8192L, DateModified: recentTime));
            }

            if (req.Offset == 2)
            {
                return BuildSyntheticList2Buffer(
                    totalItems: 1200,
                    ("003_archive_app.zip", @"D:\Archive", IsFolder: false, Size: 4096L, DateModified: olderTime),
                    ("004_backup_app.zip", @"D:\Archive", IsFolder: false, Size: 4096L, DateModified: olderTime));
            }

            return BuildSyntheticList2Buffer(
                totalItems: 1200,
                ("001_app_cache.tmp", @"C:\Users\alice\AppData\Local\Temp", IsFolder: false, Size: 512L, DateModified: olderTime),
                ("002_apple.png", @"C:\Assets", IsFolder: false, Size: 1024L, DateModified: olderTime));
        }

        using var ready = new ManualResetEventSlim(false);
        using var stop = new ManualResetEventSlim(false);
        Exception? serverError = null;
        var serverThread = new Thread(() =>
        {
            try
            {
                RunMockEverythingServer(customClass, Respond, ready, stop);
            }
            catch (Exception ex)
            {
                serverError = ex;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "MockEverythingSupplementalIpcServer",
        };
        serverThread.SetApartmentState(ApartmentState.STA);
        serverThread.Start();

        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(serverError);

            SearchFileQueryPage firstPage = EverythingSearchService.QueryNative(
                providerQuery: "\"app\"",
                rankingQuery: "app",
                resultOffset: 0,
                resultCount: 2,
                CancellationToken.None,
                advancedSyntax: false,
                ipcWindowClass: customClass);

            Assert.Equal(1200, firstPage.TotalMatchedCount);
            Assert.Equal(2, firstPage.NextOffset);
            Assert.Equal(5, firstPage.Items.Count);
            Assert.Single(firstPage.Items, i => i.Title == "app用户评价-1.5.3.4.rp");
            Assert.Contains(firstPage.Items, i => i.Title == "my-app-spec.docx");

            var ranked = SearchResultRanker.MergeAndRank(firstPage.Items, "app", maxResults: 10);
            Assert.Equal("app", Path.GetFileNameWithoutExtension(ranked[0].Title));
            Assert.Equal("app用户评价-1.5.3.4.rp", ranked[1].Title);

            lock (observedRequests)
            {
                Assert.Equal(3, observedRequests.Count);
                Assert.Equal(0u, observedRequests[0].SearchFlags);
                observedRequests.Clear();
            }

            // Subsequent LoadMore pages issue only the primary alphabetical slice.
            SearchFileQueryPage secondPage = EverythingSearchService.QueryNative(
                providerQuery: "\"app\"",
                rankingQuery: "app",
                resultOffset: firstPage.NextOffset,
                resultCount: 2,
                CancellationToken.None,
                advancedSyntax: false,
                ipcWindowClass: customClass);
            Assert.Equal(4, secondPage.NextOffset);
            lock (observedRequests)
            {
                MockQuery2Request loadMoreReq = Assert.Single(observedRequests);
                Assert.Equal(2u, loadMoreReq.Offset);
                Assert.Equal(EverythingNativeMethods.SortNameAscending, loadMoreReq.SortType);
                observedRequests.Clear();
            }

            // Path queries enable SearchFlagMatchPath when advanced syntax is disabled.
            _ = EverythingSearchService.QueryNative(
                providerQuery: "\"C:\\Program\" \"Files\\App\"",
                rankingQuery: @"C:\Program Files\App",
                resultOffset: 0,
                resultCount: 10,
                CancellationToken.None,
                advancedSyntax: false,
                ipcWindowClass: customClass);
            lock (observedRequests)
            {
                Assert.Equal(EverythingNativeMethods.SearchFlagMatchPath, observedRequests[0].SearchFlags);
            }
        }
        finally
        {
            stop.Set();
            _ = serverThread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private readonly record struct MockQuery2Request(
        uint SearchFlags,
        uint Offset,
        uint MaxResults,
        uint SortType,
        string SearchText);

    private static readonly ConcurrentDictionary<nint, Func<MockQuery2Request, byte[]>> s_mockResponders = new();

    private delegate nint MockWndProcDelegate(nint hwnd, uint msg, nuint wParam, nint lParam);

    private static readonly MockWndProcDelegate s_mockWndProc = MockEverythingWndProc;

    private static void RunMockEverythingServer(
        string className,
        byte[] replyPayload,
        ManualResetEventSlim ready,
        ManualResetEventSlim stop) =>
        RunMockEverythingServer(className, _ => replyPayload, ready, stop);

    private static void RunMockEverythingServer(
        string className,
        Func<MockQuery2Request, byte[]> responder,
        ManualResetEventSlim ready,
        ManualResetEventSlim stop)
    {
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
        s_mockResponders[hwnd] = responder;
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
            s_mockResponders.TryRemove(hwnd, out _);
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
            if (cds.dwData == 18 &&
                cds.cbData >= 28 &&
                cds.lpData != 0 &&
                s_mockResponders.TryGetValue(hwnd, out var responder))
            {
                uint replyHwnd = (uint)Marshal.ReadInt32(cds.lpData, 0);
                uint replyToken = (uint)Marshal.ReadInt32(cds.lpData, 4);
                uint searchFlags = (uint)Marshal.ReadInt32(cds.lpData, 8);
                uint offset = (uint)Marshal.ReadInt32(cds.lpData, 12);
                uint maxResults = (uint)Marshal.ReadInt32(cds.lpData, 16);
                uint sortType = (uint)Marshal.ReadInt32(cds.lpData, 24);
                int textByteLen = Math.Max(0, (int)cds.cbData - 28 - sizeof(char));
                string searchText = textByteLen > 0
                    ? Marshal.PtrToStringUni(cds.lpData + 28, textByteLen / sizeof(char))
                    : string.Empty;

                byte[] payload = responder(new MockQuery2Request(
                    searchFlags,
                    offset,
                    maxResults,
                    sortType,
                    searchText));
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
