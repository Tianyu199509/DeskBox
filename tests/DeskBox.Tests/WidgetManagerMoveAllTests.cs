using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Behavioral contracts for the screen-binding move-all batch (spec 5.9/5.10)
/// and the disconnect-collapse policy (spec 5.7 / S23): the undo token must
/// survive the move itself, refuse restore after any later placement change,
/// and preserve per-topology compact placements verbatim; a system-collapsed
/// surface must auto-expand when its home display returns, and manual
/// expand/collapse must clear the marker so reconnect never auto-expands a
/// user-touched surface.
/// </summary>
public sealed class WidgetManagerMoveAllTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    private sealed record Harness(SettingsService Settings, WidgetManager Manager);

    private Harness Create()
    {
        string desktopPath = Directory.CreateDirectory(
            Path.Combine(_root, "desktop")).FullName;
        var settingsService = new SettingsService(Path.Combine(_root, "settings"));
        var fileService = new FileService();
        var manager = new WidgetManager(
            settingsService,
            fileService,
            TestOrganizerServices.Create(settingsService, fileService),
            new ThemeService(settingsService),
            new QuickCaptureService(
                new QuickCaptureStore(Path.Combine(_root, "quick-capture"))),
            () => desktopPath,
            recycleManagedFolderDeletes: false);
        return new Harness(settingsService, manager);
    }

    private static WidgetConfig AddPinnedWidget(
        Harness harness, string id, string screenId, double x, double y)
    {
        var widget = new WidgetConfig
        {
            Id = id,
            Name = id,
            WidgetKind = WidgetKind.File,
            ScreenBindingMode = WidgetScreenBindingMode.Pinned,
            BoundScreenId = screenId,
            X = x,
            Y = y
        };
        harness.Settings.Settings.Widgets.Add(widget);
        return widget;
    }

    [Fact]
    public async Task MoveAll_ThenUndo_RestoresPreviousBindingsAndGeometry()
    {
        Harness harness = Create();
        WidgetConfig first = AddPinnedWidget(harness, "w1", "screen-a", 10, 20);
        WidgetConfig second = AddPinnedWidget(harness, "w2", "screen-a", 30, 40);

        (int moved, WidgetMoveAllUndoToken token) =
            await harness.Manager.MoveAllWidgetSurfacesToDisplayAsync("screen-b");
        Assert.True(moved >= 2);
        Assert.All(
            harness.Settings.Settings.Widgets,
            widget => Assert.Equal("screen-b", widget.BoundScreenId));

        bool undone = await harness.Manager.UndoMoveAllWidgetSurfacesAsync(token);
        Assert.True(undone);
        Assert.All(
            harness.Settings.Settings.Widgets,
            widget =>
            {
                Assert.Equal("screen-a", widget.BoundScreenId);
                Assert.Equal(WidgetScreenBindingMode.Pinned, widget.ScreenBindingMode);
            });
        Assert.Equal(10, first.X);
        Assert.Equal(20, first.Y);
        Assert.Equal(30, second.X);
        Assert.Equal(40, second.Y);
    }

    [Fact]
    public async Task MoveAll_Undo_RefusedAfterLaterPlacementChange()
    {
        Harness harness = Create();
        AddPinnedWidget(harness, "w1", "screen-a", 10, 20);
        AddPinnedWidget(harness, "w2", "screen-a", 30, 40);

        (_, WidgetMoveAllUndoToken token) =
            await harness.Manager.MoveAllWidgetSurfacesToDisplayAsync("screen-b");

        // Any later user placement (single-surface move, drag commit, …)
        // invalidates the snapshot (H3).
        await harness.Manager.MoveSurfaceToDisplayAsync("w1", "screen-c");

        Assert.False(await harness.Manager.UndoMoveAllWidgetSurfacesAsync(token));
        Assert.All(
            harness.Settings.Settings.Widgets,
            widget => Assert.NotEqual("screen-a", widget.BoundScreenId));
    }

    [Fact]
    public async Task MoveAll_Undo_PreservesProfileCompactPlacement()
    {
        Harness harness = Create();
        WidgetConfig widget = AddPinnedWidget(harness, "w1", "screen-a", 10, 20);

        var profile = new WidgetTopologyLayoutProfile();
        profile.Surfaces["w1"] = new WidgetSurfaceLayoutProfile
        {
            X = 10,
            Y = 20,
            IsAuthoritative = true,
            CompactPlacement = new WidgetCompactPlacement
            {
                X = 11,
                Y = 21,
                PositionAnchor = "TopLeft",
                PositionMarginX = 1,
                PositionMarginY = 2,
                PositionMonitorStableId = "screen-a"
            }
        };
        harness.Settings.Settings.WidgetTopologyLayouts["v4-test"] = profile;
        harness.Settings.Settings.ActiveWidgetTopologyKey = "v4-test";

        (_, WidgetMoveAllUndoToken token) =
            await harness.Manager.MoveAllWidgetSurfacesToDisplayAsync("screen-b");
        Assert.True(await harness.Manager.UndoMoveAllWidgetSurfacesAsync(token));

        WidgetSurfaceLayoutProfile restored =
            harness.Settings.Settings.WidgetTopologyLayouts["v4-test"].Surfaces["w1"];
        Assert.NotNull(restored.CompactPlacement);
        Assert.Equal(11, restored.CompactPlacement.X);
        Assert.Equal(21, restored.CompactPlacement.Y);
        Assert.Equal("TopLeft", restored.CompactPlacement.PositionAnchor);
        Assert.Equal(1, restored.CompactPlacement.PositionMarginX);
        Assert.Equal(2, restored.CompactPlacement.PositionMarginY);
        Assert.Equal("screen-a", restored.CompactPlacement.PositionMonitorStableId);
        // The restored entry must not alias the token's snapshot: a later
        // capsule move on the live entry cannot mutate the undo token.
        restored.CompactPlacement.X = 99;
        Assert.Equal(11, token.ActiveProfileEntries!["w1"].CompactPlacement!.X);
    }

    [Fact]
    public async Task MoveAll_PersistsOnce_NotPerSurface()
    {
        Harness harness = Create();
        AddPinnedWidget(harness, "w1", "screen-a", 10, 20);
        AddPinnedWidget(harness, "w2", "screen-a", 30, 40);
        AddPinnedWidget(harness, "w3", "screen-a", 50, 60);

        // The batch runs UpdateWidget(notifySubscribers:false) per surface
        // and its debounced saves never notify, so the ONLY SettingsChanged
        // notification must come from the single trailing checked save (the
        // old code saved — and notified — once per surface).
        int notifications = 0;
        harness.Settings.SettingsChanged += () => notifications++;

        (int moved, WidgetMoveAllUndoToken token) =
            await harness.Manager.MoveAllWidgetSurfacesToDisplayAsync("screen-b");

        Assert.True(moved >= 3);
        Assert.True(token.Persisted);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task MoveAll_SaveFails_StillReturnsTokenAndSurfacesApplied()
    {
        Harness harness = Create();
        WidgetConfig first = AddPinnedWidget(harness, "w1", "screen-a", 10, 20);
        WidgetConfig second = AddPinnedWidget(harness, "w2", "screen-a", 30, 40);

        // Sabotage the store (SettingsServiceTests technique): move the
        // settings directory aside and park a same-named FILE in its place,
        // so the batch's single trailing save cannot even stage its temp
        // file. The internal SettingsService constructor already created
        // the directory.
        string settingsRoot = Path.Combine(_root, "settings");
        string movedAside = settingsRoot + "-moved";
        Directory.Move(settingsRoot, movedAside);
        File.WriteAllText(settingsRoot, "not a directory");

        (int moved, WidgetMoveAllUndoToken token) =
            await harness.Manager.MoveAllWidgetSurfacesToDisplayAsync("screen-b");

        // The in-memory batch result and the undo token survive the failed
        // persistence; only the physical save is reported as failed.
        Assert.True(moved >= 2);
        Assert.False(token.Persisted);
        Assert.NotNull(harness.Settings.LastPersistenceFailure);
        Assert.All(
            harness.Settings.Settings.Widgets,
            widget => Assert.Equal("screen-b", widget.BoundScreenId));

        // Restore the store: the undo path must still work against the
        // in-memory state the failed batch left behind.
        File.Delete(settingsRoot);
        Directory.Move(movedAside, settingsRoot);
        Assert.True(await harness.Manager.UndoMoveAllWidgetSurfacesAsync(token));
        Assert.All(
            harness.Settings.Settings.Widgets,
            widget => Assert.Equal("screen-a", widget.BoundScreenId));
        Assert.Equal(10, first.X);
        Assert.Equal(20, first.Y);
        Assert.Equal(30, second.X);
        Assert.Equal(40, second.Y);
    }

    [Fact]
    public async Task MoveAll_PersistenceFailure_DoesNotThrow()
    {
        Harness harness = Create();
        AddPinnedWidget(harness, "w1", "screen-a", 10, 20);

        string settingsRoot = Path.Combine(_root, "settings");
        string movedAside = settingsRoot + "-moved";
        Directory.Move(settingsRoot, movedAside);
        File.WriteAllText(settingsRoot, "not a directory");

        // SaveToFileOnlyAsync converts every persistence failure into a
        // false result, so a broken store must never bubble an exception
        // out of the batch.
        int moved = 0;
        WidgetMoveAllUndoToken? token = null;
        Exception? thrown = null;
        try
        {
            (moved, token) = await harness.Manager
                .MoveAllWidgetSurfacesToDisplayAsync("screen-b");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.Null(thrown);
        Assert.True(moved >= 1);
        Assert.NotNull(token);
        Assert.False(token!.Persisted);
    }

    [Fact]
    public void DisconnectCollapse_ExpandsStillCollapsedSurfaceWhenHomeReturns()
    {
        Harness harness = Create();
        harness.Settings.Settings.WidgetDisplayDisconnectBehavior =
            SettingsService.WidgetDisplayDisconnectCollapseToCapsule;
        WidgetConfig widget = AddPinnedWidget(harness, "w1", "screen-a", 10, 20);
        widget.IsCollapsed = true;
        widget.DisconnectCollapsedForScreenId = "screen-a";

        harness.Manager.ApplyDisconnectCollapsePolicy(["screen-a"]);

        Assert.False(widget.IsCollapsed);
        Assert.Null(widget.DisconnectCollapsedForScreenId);
    }

    [Fact]
    public void DisconnectCollapse_ClearsMarkerForManuallyExpandedSurfaceWhenHomeReturns()
    {
        Harness harness = Create();
        harness.Settings.Settings.WidgetDisplayDisconnectBehavior =
            SettingsService.WidgetDisplayDisconnectCollapseToCapsule;
        WidgetConfig widget = AddPinnedWidget(harness, "w1", "screen-a", 10, 20);
        widget.IsVisible = true;
        widget.IsCollapsed = false;
        widget.DisconnectCollapsedForScreenId = "screen-a";

        harness.Manager.ApplyDisconnectCollapsePolicy(["screen-a"]);

        Assert.False(widget.IsCollapsed);
        Assert.Null(widget.DisconnectCollapsedForScreenId);
    }

    [Fact]
    public void DisconnectCollapse_LeavesCollapsedSurfaceAloneWhileHomeIsAway()
    {
        Harness harness = Create();
        harness.Settings.Settings.WidgetDisplayDisconnectBehavior =
            SettingsService.WidgetDisplayDisconnectCollapseToCapsule;
        WidgetConfig widget = AddPinnedWidget(harness, "w1", "screen-a", 10, 20);
        widget.IsCollapsed = true;
        widget.DisconnectCollapsedForScreenId = "screen-a";

        harness.Manager.ApplyDisconnectCollapsePolicy(["screen-b"]);

        Assert.True(widget.IsCollapsed);
        Assert.Equal("screen-a", widget.DisconnectCollapsedForScreenId);
    }

    [Fact]
    public void ClearDisconnectCollapseMarker_ClearsGroupAndEveryMember()
    {
        Harness harness = Create();
        WidgetConfig first = AddPinnedWidget(harness, "w1", "screen-a", 10, 20);
        WidgetConfig second = AddPinnedWidget(harness, "w2", "screen-a", 30, 40);
        var group = new WidgetGroupConfig
        {
            SurfaceId = "surface-1",
            MemberIds = [first.Id, second.Id],
            IsCollapsed = true,
            DisconnectCollapsedForScreenId = "screen-a"
        };
        harness.Settings.Settings.WidgetGroups.Add(group);
        first.DisconnectCollapsedForScreenId = "screen-a";
        second.DisconnectCollapsedForScreenId = "screen-a";

        harness.Manager.ClearDisconnectCollapseMarker(first);

        Assert.Null(group.DisconnectCollapsedForScreenId);
        Assert.Null(first.DisconnectCollapsedForScreenId);
        Assert.Null(second.DisconnectCollapsedForScreenId);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
