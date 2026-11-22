using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Windows.Graphics;

namespace DeskBox.Tests;

public sealed class TransferTopologyAndWidgetSandboxGapTests : IDisposable
{
    private readonly string _tempRoot;

    public TransferTopologyAndWidgetSandboxGapTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                foreach (string file in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch
                    {
                    }
                }

                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task FileService_MoveEntryAsync_MidDirectoryFailureRollsBackAlreadyMovedSiblings()
    {
        string srcDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "move-src")).FullName;
        string subDir = Directory.CreateDirectory(Path.Combine(srcDir, "nested")).FullName;
        string siblingFile = Path.Combine(srcDir, "00-first.txt");
        string lockedFile = Path.Combine(subDir, "99-locked.txt");
        await File.WriteAllTextAsync(siblingFile, "must-survive-in-source");
        await File.WriteAllTextAsync(lockedFile, "locked-payload");

        string dstParent = Directory.CreateDirectory(Path.Combine(_tempRoot, "move-dst-parent")).FullName;
        string dstDir = Path.Combine(dstParent, "move-src");
        var fileService = new FileService();

        // Holding FileShare.None on nested/99-locked.txt forces same-volume Directory.Move to fail
        // and fall back through MoveEntryAsync -> MoveDirectoryAsync -> MoveDirectoryWithProgressAsync,
        // where 00-first.txt copies first, 99-locked.txt fails, and CleanupCopiedDestinationTree rolls back!
        using (var exclusiveLock = new FileStream(
                   lockedFile,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                fileService.TransferItemsWithResultAsync([srcDir], dstParent, move: true));
        }

        // Source sibling file must remain intact and partial copied destination tree must be rolled back.
        Assert.True(File.Exists(siblingFile));
        Assert.Equal("must-survive-in-source", await File.ReadAllTextAsync(siblingFile));
        Assert.False(File.Exists(Path.Combine(dstDir, "00-first.txt")));
        Assert.False(Directory.Exists(dstDir));
    }

    [Fact]
    public void DropStagingFileOps_EnforcesExactMaxTotalBytesBoundary_ClearsReadOnly_AndRollsBackDirectoryOverflow()
    {
        string externalTempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "7zO" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var stagedToClean = new List<string>();
        try
        {
            string exactFile = Path.Combine(externalTempDir, "exact-512.bin");
            File.WriteAllBytes(exactFile, new byte[512]);
            File.SetAttributes(exactFile, FileAttributes.ReadOnly);

            IReadOnlyList<string> exactResult = DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                [exactFile],
                hasShellIdList: true,
                out IReadOnlyList<DroppedItemOriginKind> exactOrigins,
                out bool exactStagedAny,
                maxTotalBytes: 512);
            stagedToClean.AddRange(exactResult);

            Assert.True(exactStagedAny);
            string stagedExactPath = Assert.Single(exactResult);
            Assert.NotEqual(exactFile, stagedExactPath);
            Assert.Equal(DroppedItemOriginKind.TransientArchiveOrCache, Assert.Single(exactOrigins));
            Assert.Equal(0, (int)(File.GetAttributes(stagedExactPath) & FileAttributes.ReadOnly));

            File.SetAttributes(exactFile, FileAttributes.Normal);

            string overByOneFile = Path.Combine(externalTempDir, "over-513.bin");
            File.WriteAllBytes(overByOneFile, new byte[513]);

            IReadOnlyList<string> overResult = DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                [overByOneFile],
                hasShellIdList: true,
                out _,
                out bool overStagedAny,
                maxTotalBytes: 512);

            Assert.False(overStagedAny);
            Assert.Empty(overResult);

            string overflowDir = Directory.CreateDirectory(Path.Combine(externalTempDir, "overflow-folder")).FullName;
            File.WriteAllBytes(Path.Combine(overflowDir, "part1.bin"), new byte[300]);
            File.WriteAllBytes(Path.Combine(overflowDir, "part2.bin"), new byte[300]);

            IReadOnlyList<string> dirOverflowResult = DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                [overflowDir],
                hasShellIdList: true,
                out _,
                out bool dirStagedAny,
                maxTotalBytes: 512);

            Assert.False(dirStagedAny);
            Assert.Empty(dirOverflowResult);
        }
        finally
        {
            foreach (string staged in stagedToClean)
            {
                try
                {
                    string? parent = Path.GetDirectoryName(staged);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    {
                        Directory.Delete(parent, recursive: true);
                    }
                }
                catch
                {
                }
            }

            try
            {
                foreach (string f in Directory.EnumerateFiles(externalTempDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }
                Directory.Delete(externalTempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task WidgetStacksAndLayoutUndo_ThreeItemDetachPreservesPair_EvictsOldestSnapshot_AndKeepsExternalFile()
    {
        (SettingsService _, WidgetViewModel stackVm, _) = await CreateInitializedWidgetAsync(
            ["s1.txt", "s2.txt", "s3.txt", "loose.txt"],
            WidgetSortMode.Manual,
            fileStacksEnabled: true);

        using (stackVm)
        {
            WidgetItem s1 = stackVm.VisibleItems.Single(i => i.Name == "s1");
            WidgetItem s2 = stackVm.VisibleItems.Single(i => i.Name == "s2");
            WidgetItem s3 = stackVm.VisibleItems.Single(i => i.Name == "s3");
            Assert.True(stackVm.CreateManualStack([s1, s2, s3], anchorTargetItem: s1));

            WidgetStackItem initialStack = Assert.Single(stackVm.VisibleItems.OfType<WidgetStackItem>());
            Assert.Equal(3, initialStack.Members.Count);
            stackVm.SetStackExpanded(initialStack, true);

            WidgetItem detachedMember = initialStack.Members[0];
            Assert.True(stackVm.MoveVisibleItemForReorder(
                detachedMember,
                visibleInsertionIndex: stackVm.VisibleItems.Count()));

            WidgetStackItem survivingStack = Assert.Single(stackVm.VisibleItems.OfType<WidgetStackItem>());
            Assert.Equal(2, survivingStack.Members.Count);
            Assert.DoesNotContain(survivingStack.Members, m => m.Name == detachedMember.Name);
        }

        // Verify 26 snapshots evict snapshot #0 (oldest), NOT snapshot #25 (newest),
        // and that an externally added file after snapshot capture survives Undo.
        string[] names = Enumerable.Range(0, 27).Select(i => $"f{i:D2}.txt").ToArray();
        (_, WidgetViewModel undoVm, string undoFolder) = await CreateInitializedWidgetAsync(
            names,
            WidgetSortMode.Manual,
            fileStacksEnabled: false);

        using (undoVm)
        {
            // Mutation 0 moves f01 to index 0; mutation k moves f(k+1) to index 0.
            // After 26 mutations (0..25), snapshot #0 (pre-mutation-0) is evicted and
            // snapshot #1 (state right after mutation 0, where Items[0] == "f01") is the oldest kept.
            for (int mutation = 0; mutation < WidgetViewModel.MaxLayoutUndoSnapshots + 1; mutation++)
            {
                string targetName = $"f{mutation + 1:D2}";
                WidgetItem item = undoVm.Items.Single(x => x.Name == targetName);
                Assert.True(undoVm.MoveItemsForReorder([item], targetIndex: 0));
                undoVm.PersistManualOrder();
            }

            int undoSteps = 0;
            while (undoVm.TryUndoLatestLayoutChange())
            {
                undoSteps++;
            }

            Assert.Equal(WidgetViewModel.MaxLayoutUndoSnapshots, undoSteps);
            Assert.Equal("f01", undoVm.Items[0].Name);

            // Capture one more layout undo entry, add an external file, then undo: external file must remain.
            WidgetItem tailItem = undoVm.Items[^1];
            Assert.True(undoVm.MoveItemsForReorder([tailItem], targetIndex: 0));
            undoVm.PersistManualOrder();

            string externalFilePath = Path.Combine(undoFolder, "external-arrival.txt");
            await File.WriteAllTextAsync(externalFilePath, "arrived");
            await undoVm.RefreshFolderContentsAsync();
            Assert.Contains(undoVm.Items, x => x.Name == "external-arrival");

            Assert.True(undoVm.TryUndoLatestLayoutChange());
            Assert.Contains(undoVm.Items, x => x.Name == "external-arrival");
        }
    }

    [Fact]
    public void WidgetGroupStandaloneSizeMemory_VersionZeroUsesRawPhysicalPixelsWhileCurrentScalesByDpi()
    {
        var fallback = new SizeInt32(200, 150);

        var legacyMember = new WidgetConfig
        {
            Width = 300,
            Height = 200,
            BoundsCoordinateVersion = 0
        };
        WidgetGroupStandaloneSizeMemory.CaptureIfStandalone(legacyMember, wasAlreadyGrouped: false);
        SizeInt32 legacyResolved = WidgetGroupStandaloneSizeMemory.ResolveDetachedPhysicalSize(
            legacyMember,
            dpiScale: 1.5,
            fallback);
        Assert.Equal(new SizeInt32(300, 200), legacyResolved);

        var modernMember = new WidgetConfig
        {
            Width = 300,
            Height = 200,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
        };
        WidgetGroupStandaloneSizeMemory.CaptureIfStandalone(modernMember, wasAlreadyGrouped: false);
        SizeInt32 modernResolved = WidgetGroupStandaloneSizeMemory.ResolveDetachedPhysicalSize(
            modernMember,
            dpiScale: 1.5,
            fallback);
        Assert.Equal(new SizeInt32(450, 300), modernResolved);
    }

    [Fact]
    public async Task CustomWidgetSandbox_ManifestAutoFixer_ExpressionEvaluator_AndNetworkEngineEnforceInvariants()
    {
        // 1. AutoFixer: strips $state. prefix, ignores $item.* / $index, and ignores "state." inside URLs.
        const string rawManifest = """
        {
          "schemaVersion": 1,
          "id": "autofix-audit",
          "name": "AutoFix Audit",
          "sources": [
            { "id": "feed", "type": "rss", "url": "https://www.state.gov/rss-feed.xml" }
          ],
          "collections": { "tasks": [] },
          "state": {},
          "root": "root",
          "elements": {
            "root": {
              "type": "Column",
              "children": ["search_input", "tasks_list"]
            },
            "search_input": {
              "type": "TextBox",
              "bind": "$state.searchQuery"
            },
            "tasks_list": {
              "type": "List",
              "items": "tasks",
              "itemTemplate": "task_row"
            },
            "task_row": {
              "type": "Row",
              "children": ["task_toggle", "task_title"]
            },
            "task_toggle": {
              "type": "Toggle",
              "bind": "$item.done"
            },
            "task_title": {
              "type": "Text",
              "text": "${$index}: ${$item.title}"
            }
          }
        }
        """;

        string fixedJson = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(rawManifest);
        using (JsonDocument doc = JsonDocument.Parse(fixedJson))
        {
            JsonElement stateObj = doc.RootElement.GetProperty("state");
            Assert.True(stateObj.TryGetProperty("searchQuery", out _));
            Assert.False(stateObj.TryGetProperty("$state.searchQuery", out _));
            Assert.False(stateObj.TryGetProperty("$item.done", out _));
            Assert.False(stateObj.TryGetProperty("done", out _));
            Assert.False(stateObj.TryGetProperty("gov", out _));
        }

        // 2. ExpressionEvaluator: value-preserving || / && short-circuit and 0.1 + 0.2 == 0.3 tolerance.
        var evalCtx = new CustomWidgetEvaluationContext
        {
            CultureName = "en-US",
            State = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["empty"] = "",
                ["name"] = "DeskBox",
                ["role"] = "Desktop"
            }
        };
        Assert.Equal("fallback", CustomWidgetExpressionEvaluator.Interpolate("${state.empty || 'fallback'}", evalCtx));
        Assert.Equal("DeskBox", CustomWidgetExpressionEvaluator.Interpolate("${state.name || 'fallback'}", evalCtx));
        Assert.Equal("Desktop", CustomWidgetExpressionEvaluator.Interpolate("${state.name && state.role}", evalCtx));
        Assert.True(CustomWidgetExpressionEvaluator.EvaluateCondition("0.1 + 0.2 == 0.3", evalCtx));

        // 3. NetworkSourceEngine: cross-origin 307 POST strips body & switches to GET; streaming cap enforces 512 KB.
        var recordedRequests = new List<(HttpMethod Method, Uri Uri, string? Body)>();
        var handler = new DelegateHttpMessageHandler(async (request, ct) =>
        {
            string? reqBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(ct)
                : null;
            recordedRequests.Add((request.Method, request.RequestUri!, reqBody));

            if (string.Equals(request.RequestUri!.Host, "origin.example", StringComparison.OrdinalIgnoreCase))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("https://target.example/landed");
                return redirect;
            }

            if (string.Equals(request.RequestUri.AbsolutePath, "/exact-cap", StringComparison.Ordinal))
            {
                return CreateChunkedResponse(CustomWidgetNetworkSecurity.MaxResponseBytes);
            }

            if (string.Equals(request.RequestUri.AbsolutePath, "/over-cap", StringComparison.Ordinal))
            {
                return CreateChunkedResponse(CustomWidgetNetworkSecurity.MaxResponseBytes + 1);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
        });

        var engine = new CustomWidgetNetworkSourceEngine(
            Path.Combine(_tempRoot, "net-cache"),
            handler,
            static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        CustomWidgetInteractiveHttpOutcome redirectOutcome = await engine.SendInteractiveHttpRequestAsync(
            "https://origin.example/submit",
            method: "POST",
            body: "{\"secret\":\"do-not-leak-cross-origin\"}",
            crossOriginHostApprover: static (_, _) => Task.FromResult(true));

        Assert.True(redirectOutcome.Succeeded, redirectOutcome.ErrorMessage);
        Assert.Equal(2, recordedRequests.Count);
        Assert.Equal(HttpMethod.Post, recordedRequests[0].Method);
        Assert.NotNull(recordedRequests[0].Body);
        Assert.Equal(HttpMethod.Get, recordedRequests[1].Method);
        Assert.Null(recordedRequests[1].Body);

        CustomWidgetInteractiveHttpOutcome exactOutcome = await engine.SendInteractiveHttpRequestAsync(
            "https://target.example/exact-cap");
        Assert.True(exactOutcome.Succeeded, exactOutcome.ErrorMessage);
        Assert.Equal(CustomWidgetNetworkSecurity.MaxResponseBytes, exactOutcome.ResponseBody.Length);

        CustomWidgetInteractiveHttpOutcome overOutcome = await engine.SendInteractiveHttpRequestAsync(
            "https://target.example/over-cap");
        Assert.False(overOutcome.Succeeded);
        Assert.Contains("1024 KB", overOutcome.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage CreateChunkedResponse(int byteLength)
    {
        var content = new StreamContent(new MemoryStream(Encoding.ASCII.GetBytes(new string('a', byteLength))));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
        content.Headers.ContentLength = null;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        response.Headers.TransferEncodingChunked = true;
        return response;
    }

    private async Task<(SettingsService Settings, WidgetViewModel ViewModel, string FolderPath)> CreateInitializedWidgetAsync(
        string[] fileNames,
        WidgetSortMode sortMode,
        bool fileStacksEnabled)
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings", Guid.NewGuid().ToString("N"));
        string folderPath = Path.Combine(_tempRoot, "files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folderPath);
        foreach (string name in fileNames)
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, name), name);
        }

        var settings = new SettingsService(settingsRoot);
        await settings.LoadAsync();
        settings.Settings.FileStacksEnabled = fileStacksEnabled;
        settings.Settings.FileStackAutoStacking = false;
        settings.Settings.FileStackOpenMode = SettingsService.FileStackOpenModeInline;
        settings.Settings.ShowFileExtensions = false;

        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "TopologyGapTest",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = sortMode
        };
        settings.Settings.Widgets.Add(config);

        var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();
        return (settings, vm, folderPath);
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
