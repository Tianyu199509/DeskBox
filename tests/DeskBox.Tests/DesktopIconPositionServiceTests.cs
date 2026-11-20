using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.DesktopOrganization;

namespace DeskBox.Tests;

public sealed class DesktopIconPositionServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        "DesktopIconPositionService",
        Guid.NewGuid().ToString("N"));

    private string PersonalDesktop => Path.Combine(_tempRoot, "Desktop");
    private string PublicDesktop => Path.Combine(_tempRoot, "PublicDesktop");
    private string ManagedBoxA => Path.Combine(_tempRoot, "DeskBox", "Apps");
    private string ManagedBoxB => Path.Combine(_tempRoot, "DeskBox", "Tools");
    private string LedgerPath => Path.Combine(_tempRoot, "data", "desktop-icon-positions.json");

    public DesktopIconPositionServiceTests()
    {
        Directory.CreateDirectory(PersonalDesktop);
        Directory.CreateDirectory(PublicDesktop);
        Directory.CreateDirectory(ManagedBoxA);
        Directory.CreateDirectory(ManagedBoxB);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class FakeDesktopIconPositionBackend : IDesktopIconPositionBackend
    {
        public Dictionary<string, DesktopIconPoint> DesktopPositions { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, DesktopIconPoint> RestoredCalls { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool FailRestore { get; set; }

        public int CaptureCallCount { get; private set; }
        public int RestoreCallCount { get; private set; }

        public IReadOnlyDictionary<string, DesktopIconPoint> TryCaptureDesktopPositions(
            IReadOnlyCollection<string> normalizedPaths)
        {
            CaptureCallCount++;
            var captured = new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in normalizedPaths)
            {
                if (DesktopPositions.TryGetValue(path, out DesktopIconPoint pt))
                {
                    captured[path] = pt;
                }
            }

            return captured;
        }

        public IReadOnlySet<string> TryRestoreDesktopPositions(
            IReadOnlyDictionary<string, DesktopIconPoint> desiredPositionsByPath)
        {
            RestoreCallCount++;
            var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (FailRestore)
            {
                return restored;
            }

            foreach ((string path, DesktopIconPoint pt) in desiredPositionsByPath)
            {
                RestoredCalls[path] = pt;
                restored.Add(path);
            }

            return restored;
        }
    }

    private DesktopIconPositionService CreateService(FakeDesktopIconPositionBackend backend) =>
        new(
            new DesktopIconPositionLedgerStore(LedgerPath),
            backend,
            () => PersonalDesktop,
            () => PublicDesktop);

    [Fact]
    public async Task OrganizerRoundTrip_DragIntoWidgetAndMoveBackToDesktop_RestoresOriginalPosition()
    {
        var backend = new FakeDesktopIconPositionBackend();
        string desktopItem = Path.Combine(PersonalDesktop, "WeChat.lnk");
        await File.WriteAllTextAsync(desktopItem, "shortcut");
        backend.DesktopPositions[desktopItem] = new DesktopIconPoint(2010, 406);

        DesktopIconPositionService positionService = CreateService(backend);
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        var settings = new SettingsService(settingsPath);
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(
            settings,
            fileService,
            desktopPathProvider: () => PersonalDesktop,
            recoveryJournalPath: Path.Combine(_tempRoot, "recovery.json"),
            desktopIconPositions: positionService);
        var widget = new WidgetConfig
        {
            Id = "widget-apps",
            Name = "Apps",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = ManagedBoxA,
        };

        OrganizerOperationResult dropResult = await organizer.OrganizeDropAsync(
            widget,
            widget.Name,
            [desktopItem],
            move: true);
        string movedPath = Assert.Single(dropResult.CompletedItems).DestinationPath;
        Assert.True(File.Exists(movedPath));
        Assert.Equal(1, backend.CaptureCallCount);

        await organizer.MoveItemsBackToDesktopAsync(widget, widget.Name, [movedPath]);

        Assert.True(File.Exists(desktopItem));
        Assert.Equal(1, backend.RestoreCallCount);
        Assert.Equal(new DesktopIconPoint(2010, 406), backend.RestoredCalls[desktopItem]);
    }

    [Fact]
    public async Task OrganizerCopy_DoesNotCaptureDesktopPosition()
    {
        var backend = new FakeDesktopIconPositionBackend();
        string desktopItem = Path.Combine(PersonalDesktop, "Notes.txt");
        await File.WriteAllTextAsync(desktopItem, "hello");
        backend.DesktopPositions[desktopItem] = new DesktopIconPoint(300, 400);

        DesktopIconPositionService positionService = CreateService(backend);
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings.json"));
        var organizer = TestOrganizerServices.Create(
            settings,
            new FileService(),
            desktopPathProvider: () => PersonalDesktop,
            recoveryJournalPath: Path.Combine(_tempRoot, "recovery.json"),
            desktopIconPositions: positionService);
        var widget = new WidgetConfig
        {
            Id = "widget-apps",
            Name = "Apps",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = ManagedBoxA,
        };

        await organizer.OrganizeDropAsync(widget, widget.Name, [desktopItem], move: false);

        Assert.Equal(0, backend.CaptureCallCount);
    }

    [Fact]
    public async Task OrganizerUndo_RestoresOriginalDesktopPosition_IncludingSameStemFolderAndLnk()
    {
        var backend = new FakeDesktopIconPositionBackend();
        string folderOnDesktop = Path.Combine(PersonalDesktop, "WeChat");
        string lnkOnDesktop = Path.Combine(PersonalDesktop, "WeChat.lnk");
        Directory.CreateDirectory(folderOnDesktop);
        await File.WriteAllTextAsync(Path.Combine(folderOnDesktop, "child.txt"), "x");
        await File.WriteAllTextAsync(lnkOnDesktop, "lnk");
        backend.DesktopPositions[folderOnDesktop] = new DesktopIconPoint(100, 200);
        backend.DesktopPositions[lnkOnDesktop] = new DesktopIconPoint(500, 600);

        DesktopIconPositionService positionService = CreateService(backend);
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings.json"));
        var organizer = TestOrganizerServices.Create(
            settings,
            new FileService(),
            desktopPathProvider: () => PersonalDesktop,
            recoveryJournalPath: Path.Combine(_tempRoot, "recovery.json"),
            desktopIconPositions: positionService);
        var widget = new WidgetConfig
        {
            Id = "widget-apps",
            Name = "Apps",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = ManagedBoxA,
        };

        OrganizerOperationResult drop = await organizer.OrganizeDropAsync(
            widget,
            widget.Name,
            [folderOnDesktop, lnkOnDesktop],
            move: true);
        Assert.True(drop.History.CanUndo);

        await organizer.UndoAsync(drop.History.Id);

        Assert.Equal(new DesktopIconPoint(100, 200), backend.RestoredCalls[folderOnDesktop]);
        Assert.Equal(new DesktopIconPoint(500, 600), backend.RestoredCalls[lnkOnDesktop]);
    }

    [Fact]
    public async Task Restore_RefusesToClaimPositionWhenConflictRenamedFile()
    {
        var backend = new FakeDesktopIconPositionBackend();
        string originalDesktop = Path.Combine(PersonalDesktop, "WeChat.lnk");
        string movedPath = Path.Combine(ManagedBoxA, "WeChat.lnk");
        string conflictRenamed = Path.Combine(PersonalDesktop, "WeChat (2).lnk");
        backend.DesktopPositions[originalDesktop] = new DesktopIconPoint(2010, 406);

        DesktopIconPositionService positionService = CreateService(backend);
        DesktopIconCaptureSnapshot snapshot = await positionService.CaptureBeforeMoveFromDesktopAsync(
            [new FileService.FileTransferPlan(originalDesktop, movedPath)]);
        await positionService.CommitAfterMoveOffDesktopAsync(
            snapshot,
            [new FileService.FileTransferResult(originalDesktop, movedPath)]);

        await positionService.RestoreAfterMoveToDesktopAsync(
            [new FileService.FileTransferResult(movedPath, conflictRenamed)]);

        Assert.Equal(0, backend.RestoreCallCount);
        Assert.Empty(backend.RestoredCalls);
    }

    [Fact]
    public async Task OutboundDropForget_ClearsSavedCoordinateSoManualDropIsNotPulledBack()
    {
        var backend = new FakeDesktopIconPositionBackend();
        string originalDesktop = Path.Combine(PersonalDesktop, "App.lnk");
        string movedPath = Path.Combine(ManagedBoxA, "App.lnk");
        backend.DesktopPositions[originalDesktop] = new DesktopIconPoint(900, 500);

        DesktopIconPositionService positionService = CreateService(backend);
        DesktopIconCaptureSnapshot snapshot = await positionService.CaptureBeforeMoveFromDesktopAsync(
            [new FileService.FileTransferPlan(originalDesktop, movedPath)]);
        await positionService.CommitAfterMoveOffDesktopAsync(
            snapshot,
            [new FileService.FileTransferResult(originalDesktop, movedPath)]);

        await positionService.ForgetEntriesAsync([movedPath]);
        await positionService.RestoreAfterMoveToDesktopAsync(
            [new FileService.FileTransferResult(movedPath, originalDesktop)]);

        Assert.Equal(0, backend.RestoreCallCount);
    }

    [Fact]
    public async Task PublicDesktopMove_IsRecognizedAndRestoredAfterWidgetRelocation()
    {
        var backend = new FakeDesktopIconPositionBackend();
        string publicItem = Path.Combine(PublicDesktop, "SharedTool.lnk");
        string boxAPath = Path.Combine(ManagedBoxA, "SharedTool.lnk");
        string boxBPath = Path.Combine(ManagedBoxB, "SharedTool.lnk");
        backend.DesktopPositions[publicItem] = new DesktopIconPoint(1280, 101);

        DesktopIconPositionService positionService = CreateService(backend);
        DesktopIconCaptureSnapshot snapshot = await positionService.CaptureBeforeMoveFromDesktopAsync(
            [new FileService.FileTransferPlan(publicItem, boxAPath)]);
        await positionService.CommitAfterMoveOffDesktopAsync(
            snapshot,
            [new FileService.FileTransferResult(publicItem, boxAPath)]);

        // Move from Box A -> Box B, then back to Public Desktop.
        await positionService.RelocateMovesAsync([new FileService.FileTransferResult(boxAPath, boxBPath)]);
        await positionService.RestoreAfterMoveToDesktopAsync(
            [new FileService.FileTransferResult(boxBPath, publicItem)]);

        Assert.Equal(new DesktopIconPoint(1280, 101), backend.RestoredCalls[publicItem]);
    }

    [Fact]
    public void ViewBoundsGuard_AllowsVisiblePointsAndRejectsDisconnectedMonitorPoints()
    {
        Assert.True(DesktopIconPositionInterop.IsPointWithinViewBounds(
            new DesktopIconPoint(2010, 406),
            left: 0,
            top: 0,
            right: 2560,
            bottom: 1440));
        Assert.False(DesktopIconPositionInterop.IsPointWithinViewBounds(
            new DesktopIconPoint(3606, 2),
            left: 0,
            top: 0,
            right: 1920,
            bottom: 1080));
    }

    [Fact]
    public void ServiceRegistry_WiresDesktopIconPositionServiceIntoOrganizerService()
    {
        using DeskBoxServiceContainer container = ServiceRegistry.CreateContainer();
        Assert.NotNull(container.DesktopIconPositionService);
        Assert.Same(container.DesktopIconPositionService, container.OrganizerService.DesktopIconPositions);
    }
}
