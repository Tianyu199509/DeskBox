using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetStartupRestorePolicyTests
{
    [Fact]
    public void SelectEnabledWidgets_IncludesPreviouslyHiddenWidgets()
    {
        var hidden = new WidgetConfig
        {
            Id = "hidden",
            IsVisible = false
        };
        var disabled = new WidgetConfig
        {
            Id = "disabled",
            IsVisible = false,
            IsDisabled = true
        };
        var deleted = new WidgetConfig
        {
            Id = "deleted",
            IsVisible = false
        };
        var settings = new AppSettings
        {
            Widgets = [hidden, disabled, deleted]
        };

        IReadOnlyList<WidgetConfig> selected =
            WidgetStartupRestorePolicy.SelectEnabledWidgets(
                settings,
                id => string.Equals(id, deleted.Id, StringComparison.Ordinal));

        Assert.Equal(hidden.Id, Assert.Single(selected).Id);
    }

    [Fact]
    public void SelectEnabledWidgets_RestoresOnlyTheActiveGroupSurface()
    {
        var first = new WidgetConfig { Id = "first", IsVisible = false };
        var second = new WidgetConfig { Id = "second", IsVisible = false };
        var settings = new AppSettings
        {
            Widgets = [first, second],
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    Id = "group",
                    SurfaceId = "surface",
                    MemberIds = [first.Id, second.Id],
                    ActiveMemberId = second.Id,
                    IsVisible = false
                }
            ]
        };

        IReadOnlyList<WidgetConfig> selected =
            WidgetStartupRestorePolicy.SelectEnabledWidgets(settings, _ => false);

        Assert.Equal(second.Id, Assert.Single(selected).Id);
    }

    [Fact]
    public void GetStartupHideReason_HidesOnlyForQuickRevealOrSilentStartup()
    {
        Assert.Equal(
            "quick-reveal-layer",
            WidgetStartupRestorePolicy.GetStartupHideReason(
                usesQuickRevealLayer: true,
                settings: new AppSettings { }));
        Assert.Equal(
            "silent-startup",
            WidgetStartupRestorePolicy.GetStartupHideReason(
                usesQuickRevealLayer: false,
                settings: new AppSettings { SilentStartup = true }));
        // Quick reveal keeps its dedicated reason even when the silent-startup
        // preference is also on: the log must name the layer, not the setting.
        Assert.Equal(
            "quick-reveal-layer",
            WidgetStartupRestorePolicy.GetStartupHideReason(
                usesQuickRevealLayer: true,
                settings: new AppSettings { SilentStartup = true }));
        Assert.Null(WidgetStartupRestorePolicy.GetStartupHideReason(
            usesQuickRevealLayer: false,
            settings: new AppSettings { }));
    }

    [Fact]
    public void GetStartupHideReason_RejectsNullSettings()
    {
        Assert.Throws<ArgumentNullException>(
            () => WidgetStartupRestorePolicy.GetStartupHideReason(false, null!));
    }

    [Fact]
    public void MarkVisible_SynchronizesTheWholeGroupAndStandaloneWidget()
    {
        var first = new WidgetConfig { Id = "first", IsVisible = false };
        var second = new WidgetConfig { Id = "second", IsVisible = false };
        var standalone = new WidgetConfig { Id = "standalone", IsVisible = false };
        var group = new WidgetGroupConfig
        {
            Id = "group",
            SurfaceId = "surface",
            MemberIds = [first.Id, second.Id],
            ActiveMemberId = first.Id,
            IsVisible = false
        };
        var settings = new AppSettings
        {
            Widgets = [first, second, standalone],
            WidgetGroups = [group]
        };

        bool changed = WidgetStartupRestorePolicy.MarkVisible(
            settings,
            [first, standalone]);

        Assert.True(changed);
        Assert.True(group.IsVisible);
        Assert.True(first.IsVisible);
        Assert.True(second.IsVisible);
        Assert.True(standalone.IsVisible);
        Assert.False(WidgetStartupRestorePolicy.MarkVisible(
            settings,
            [first, standalone]));
    }

    /// <summary>
    /// Pins the restore orchestration so a hidden startup (quick-reveal layer
    /// or silent startup) hides widgets from the moment they are created,
    /// instead of showing them first and hiding only after the whole restore
    /// pass (a visible flash window of seconds on widget-heavy desktops).
    /// The hidden state is session-only: it is never written back into the
    /// persisted config/group visibility flags.
    /// </summary>
    [Fact]
    public void RestoreWidgets_CreatesWidgetsHiddenOnHiddenStartup()
    {
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        string trayAnimation = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.TrayAnimation.cs"));

        // The hide decision is made before the restore loop and feeds the
        // creation parameter.
        int decisionIndex = manager.IndexOf(
            "bool hideWidgetsAtStartup = WidgetStartupRestorePolicy.GetStartupHideReason(",
            StringComparison.Ordinal);
        int restoreIndex = manager.IndexOf(
            "await StartupWidgetRestoreRunner.RestoreAsync(",
            StringComparison.Ordinal);
        Assert.True(decisionIndex >= 0 && restoreIndex > decisionIndex,
            "the startup hide decision must precede the restore loop");

        Assert.Contains(
            "keepPreparedForAnimation: hideWidgetsAtStartup);",
            manager,
            StringComparison.Ordinal);

        // The group re-show pass must not run on a hidden startup: it would
        // surface every group right before the unified hide.
        Assert.Contains(
            "if (!hideWidgetsAtStartup)",
            manager,
            StringComparison.Ordinal);

        // The hidden session is process-local: armed before the restore loop
        // (so creation and deferred reconciliation both see it) ...
        int armIndex = manager.IndexOf(
            "_startupHiddenSessionActive = hideWidgetsAtStartup;",
            StringComparison.Ordinal);
        Assert.True(
            armIndex >= 0 && armIndex > decisionIndex && armIndex < restoreIndex,
            "the hidden-session flag must be armed before the restore loop");

        // ... honored by the automatic re-show passes that read persisted
        // group visibility (initial restore, deferred startup reconciliation,
        // topology restores routing through the same helper) ...
        Assert.Contains(
            "if (_startupHiddenSessionActive)",
            manager,
            StringComparison.Ordinal);

        // ... and cleared by the first explicit user reveal.
        Assert.Contains(
            "ClearStartupHiddenSession(\"set-all-visible\");",
            manager,
            StringComparison.Ordinal);
        Assert.Contains(
            "ClearStartupHiddenSession(source);",
            trayAnimation,
            StringComparison.Ordinal);

        // A hidden startup must never rewrite the persisted/group visibility:
        // widgets created hidden are filtered out of the tray-batch hide, and
        // zeroing the flags here used to pollute settings.json, so a kill
        // mid-session (or simply turning the preference off) could leave the
        // next launch with a hidden-looking persisted state.
        Assert.DoesNotContain(
            "config.IsVisible = false;",
            manager,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "group.IsVisible = false;",
            manager,
            StringComparison.Ordinal);

        // MarkVisible still re-arms visibility before the windows are created,
        // so a restart with the preference turned off restores every surface.
        Assert.Contains(
            "WidgetStartupRestorePolicy.MarkVisible(",
            manager,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: the process is killed during a silent-startup session. The
    /// session state is process-local and the hidden path never writes
    /// visibility, so the persisted state the next launch reads is still the
    /// user's real one: hiding again needs the preference, showing needs its
    /// removal — never a repair of polluted flags.
    /// </summary>
    [Fact]
    public void HiddenStartupSessionKill_LeavesUserVisibilityIntactForNextLaunch()
    {
        var settings = new AppSettings { SilentStartup = true };
        settings.Widgets.Add(new WidgetConfig { Id = "widget-1", IsVisible = true });

        // What a kill mid-session leaves on disk: the untouched user state.
        Assert.True(settings.Widgets[0].IsVisible);

        // Next launch with the preference still on: hide again.
        Assert.Equal(
            "silent-startup",
            WidgetStartupRestorePolicy.GetStartupHideReason(
                usesQuickRevealLayer: false,
                settings: settings));

        // Next launch after the user turned the preference off: show from the
        // same untouched state.
        settings.SilentStartup = false;
        Assert.Null(WidgetStartupRestorePolicy.GetStartupHideReason(
            usesQuickRevealLayer: false,
            settings: settings));
        WidgetStartupRestorePolicy.MarkVisible(
            settings,
            WidgetStartupRestorePolicy.SelectEnabledWidgets(settings, _ => false));
        Assert.All(settings.Widgets, widget => Assert.True(widget.IsVisible));
    }

    /// <summary>
    /// Scenario: the user turns the silent-startup preference off and
    /// restarts. Every enabled surface (standalone widgets and grouped
    /// surfaces) must come back visible.
    /// </summary>
    [Fact]
    public void SilentStartupTurnedOff_RestoresEveryEnabledSurface()
    {
        var grouped = new WidgetConfig { Id = "grouped", IsVisible = true };
        var standalone = new WidgetConfig { Id = "standalone", IsVisible = true };
        var group = new WidgetGroupConfig
        {
            Id = "group",
            SurfaceId = "surface",
            MemberIds = [grouped.Id],
            ActiveMemberId = grouped.Id,
            IsVisible = true
        };
        var settings = new AppSettings
        {
            SilentStartup = true,
            Widgets = [grouped, standalone],
            WidgetGroups = [group]
        };

        settings.SilentStartup = false;
        Assert.Null(WidgetStartupRestorePolicy.GetStartupHideReason(
            usesQuickRevealLayer: false,
            settings: settings));

        WidgetStartupRestorePolicy.MarkVisible(
            settings,
            WidgetStartupRestorePolicy.SelectEnabledWidgets(settings, _ => false));

        Assert.True(group.IsVisible);
        Assert.True(grouped.IsVisible);
        Assert.True(standalone.IsVisible);
    }
}
