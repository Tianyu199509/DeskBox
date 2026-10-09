using DeskBox.Contracts;
using DeskBox.Features.GroupNavigation;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class GroupNavigationSettingsCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void DefaultWrites_PersistThroughThePort_SkipUnchangedSaves_AndReportChanges()
    {
        var settings = new SettingsService(_root);
        var coordinator = new GroupNavigationSettingsCoordinator(settings);
        var editor = new GroupNavigationSettingsViewModel(coordinator, _ => _);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        // Defaults from a fresh profile: Tabs / IconAndText / wheel on /
        // hover off / switch animation Auto. Flip all five through the
        // editor seam.
        Assert.True(editor.SetDefaultNavigationStyle(WidgetGroupNavigationStyles.Stack));
        Assert.True(editor.SetDefaultTitleDisplayMode(WidgetGroupTitleDisplayModes.IconOnly));
        Assert.True(editor.SetWheelSwitchEnabled(false));
        Assert.True(editor.SetHoverSwitchEnabled(true));
        Assert.True(editor.SetWidgetGroupSwitchAnimationStyle(
            WidgetGroupSwitchAnimationStyles.None));

        WidgetLayoutSettingsSlice layout = settings.Settings.WidgetLayout;
        Assert.Equal(WidgetGroupNavigationStyles.Stack, layout.WidgetGroupDefaultNavigationStyle);
        Assert.Equal(WidgetGroupTitleDisplayModes.IconOnly, layout.WidgetGroupDefaultTitleDisplayMode);
        Assert.False(layout.WidgetGroupWheelSwitchEnabled);
        Assert.True(layout.WidgetGroupHoverSwitchEnabled);
        Assert.Equal(WidgetGroupSwitchAnimationStyles.None, layout.WidgetGroupSwitchAnimationStyle);

        // Five real changes schedule five SettingsChanged passes; re-sending
        // the same values reports no change and must not save again, exactly
        // like the shell setters skipped unchanged writes before the
        // migration (no notification, no projection rebuild).
        Assert.Equal(5, notified);
        Assert.False(editor.SetDefaultNavigationStyle(WidgetGroupNavigationStyles.Stack));
        Assert.False(editor.SetDefaultTitleDisplayMode(WidgetGroupTitleDisplayModes.IconOnly));
        Assert.False(editor.SetWheelSwitchEnabled(false));
        Assert.False(editor.SetHoverSwitchEnabled(true));
        Assert.False(editor.SetWidgetGroupSwitchAnimationStyle(
            WidgetGroupSwitchAnimationStyles.None));
        Assert.Equal(5, notified);

        // A real flip still reports true and saves.
        Assert.True(editor.SetDefaultNavigationStyle(WidgetGroupNavigationStyles.Tabs));
        Assert.Equal(6, notified);
    }

    [Fact]
    public void InvalidValues_NormalizeLikeTheSettingsPageDid()
    {
        var settings = new SettingsService(_root);
        var coordinator = new GroupNavigationSettingsCoordinator(settings);

        // The defaults never accept FollowDefault (that is a per-group
        // override value): it normalizes to Tabs, which a fresh profile
        // already stores, so the write is a no-op — matching the page
        // setter's normalize-then-compare-raw-store skip.
        Assert.False(coordinator.SetDefaultNavigationStyle(
            WidgetGroupNavigationStyles.FollowDefault));
        Assert.Equal(
            WidgetGroupNavigationStyles.Tabs,
            settings.Settings.WidgetLayout.WidgetGroupDefaultNavigationStyle);

        // Legacy "Auto" keeps its stacked meaning; invalid values collapse
        // to the fresh-install defaults, matching both the page setters and
        // the load pipeline's normalization.
        Assert.True(coordinator.SetDefaultNavigationStyle("Auto"));
        Assert.Equal(
            WidgetGroupNavigationStyles.Stack,
            settings.Settings.WidgetLayout.WidgetGroupDefaultNavigationStyle);

        Assert.True(coordinator.SetDefaultNavigationStyle("Nonsense"));
        Assert.Equal(
            WidgetGroupNavigationStyles.Tabs,
            settings.Settings.WidgetLayout.WidgetGroupDefaultNavigationStyle);

        // The stored title mode is still the fresh IconAndText default, so a
        // null/bogus write normalizes to it without changing anything; a
        // real selection then flips the store and a normalized-equal
        // re-send is a no-op.
        Assert.False(coordinator.SetDefaultTitleDisplayMode(null));
        Assert.True(coordinator.SetDefaultTitleDisplayMode(WidgetGroupTitleDisplayModes.IconOnly));
        Assert.True(coordinator.SetDefaultTitleDisplayMode("Bogus"));
        Assert.Equal(
            WidgetGroupTitleDisplayModes.IconAndText,
            settings.Settings.WidgetLayout.WidgetGroupDefaultTitleDisplayMode);
        Assert.False(coordinator.SetDefaultTitleDisplayMode("IconAndText"));

        // The switch-animation store starts at the fresh Auto default: an
        // invalid or null write normalizes back to Auto without changing
        // anything, a real selection persists, and a normalized-equal
        // re-send is a no-op.
        Assert.False(coordinator.SetWidgetGroupSwitchAnimationStyle(null));
        Assert.False(coordinator.SetWidgetGroupSwitchAnimationStyle("Bogus"));
        Assert.True(coordinator.SetWidgetGroupSwitchAnimationStyle(
            WidgetGroupSwitchAnimationStyles.Horizontal));
        Assert.True(coordinator.SetWidgetGroupSwitchAnimationStyle("Nonsense"));
        Assert.Equal(
            WidgetGroupSwitchAnimationStyles.Auto,
            settings.Settings.WidgetLayout.WidgetGroupSwitchAnimationStyle);
        Assert.False(coordinator.SetWidgetGroupSwitchAnimationStyle(
            WidgetGroupSwitchAnimationStyles.Auto));
    }

    [Fact]
    public async Task GroupNavigationWrites_RoundTripThroughTheDeviceLayerFile()
    {
        // The four defaults live on the device-domain WidgetLayout slice:
        // writing through the port and saving must persist them through the
        // paired layout+settings commit, and a fresh load must read them
        // back from widget-layout.json, not from legacy settings keys.
        var settings = new SettingsService(_root);
        await settings.LoadAsync();
        var coordinator = new GroupNavigationSettingsCoordinator(settings);

        Assert.True(coordinator.SetDefaultNavigationStyle(WidgetGroupNavigationStyles.Stack));
        Assert.True(coordinator.SetDefaultTitleDisplayMode(WidgetGroupTitleDisplayModes.TextOnly));
        Assert.True(coordinator.SetWheelSwitchEnabled(false));
        Assert.True(coordinator.SetHoverSwitchEnabled(true));
        Assert.True(coordinator.SetWidgetGroupSwitchAnimationStyle(
            WidgetGroupSwitchAnimationStyles.None));
        await settings.SaveAsync();

        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        GroupNavigationSettingsSnapshot snapshot =
            new GroupNavigationSettingsCoordinator(reloaded).ReadAll();
        Assert.Equal(WidgetGroupNavigationStyles.Stack, snapshot.DefaultNavigationStyle);
        Assert.Equal(WidgetGroupTitleDisplayModes.TextOnly, snapshot.DefaultTitleDisplayMode);
        Assert.False(snapshot.WheelSwitchEnabled);
        Assert.True(snapshot.HoverSwitchEnabled);
        Assert.Equal(WidgetGroupSwitchAnimationStyles.None, snapshot.SwitchAnimationStyle);
        Assert.True(reloaded.Layout.IsAuthoritative);

        // Reloaded values are already normalized, so re-sending them is a
        // no-op on the next session's coordinator too.
        var nextSession = new GroupNavigationSettingsCoordinator(reloaded);
        Assert.False(nextSession.SetDefaultNavigationStyle(WidgetGroupNavigationStyles.Stack));
        Assert.False(nextSession.SetDefaultTitleDisplayMode(WidgetGroupTitleDisplayModes.TextOnly));
        Assert.False(nextSession.SetWheelSwitchEnabled(false));
        Assert.False(nextSession.SetHoverSwitchEnabled(true));
        Assert.False(nextSession.SetWidgetGroupSwitchAnimationStyle(
            WidgetGroupSwitchAnimationStyles.None));
    }

    [Fact]
    public async Task StoppedCoordinator_RejectsFurtherWrites()
    {
        var settings = new SettingsService(_root);
        var coordinator = new GroupNavigationSettingsCoordinator(settings);
        coordinator.Stop();

        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetDefaultNavigationStyle(WidgetGroupNavigationStyles.Stack));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetDefaultTitleDisplayMode(WidgetGroupTitleDisplayModes.IconOnly));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetWheelSwitchEnabled(false));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetHoverSwitchEnabled(true));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetWidgetGroupSwitchAnimationStyle(
                WidgetGroupSwitchAnimationStyles.None));
        Assert.True(coordinator.IsStopped);

        await settings.SaveAsync();
        Assert.Equal(
            WidgetGroupNavigationStyles.Tabs,
            settings.Settings.WidgetLayout.WidgetGroupDefaultNavigationStyle);
        Assert.Equal(
            WidgetGroupTitleDisplayModes.IconAndText,
            settings.Settings.WidgetLayout.WidgetGroupDefaultTitleDisplayMode);
        Assert.True(settings.Settings.WidgetLayout.WidgetGroupWheelSwitchEnabled);
        Assert.False(settings.Settings.WidgetLayout.WidgetGroupHoverSwitchEnabled);
        Assert.Equal(
            WidgetGroupSwitchAnimationStyles.Auto,
            settings.Settings.WidgetLayout.WidgetGroupSwitchAnimationStyle);
    }
}
