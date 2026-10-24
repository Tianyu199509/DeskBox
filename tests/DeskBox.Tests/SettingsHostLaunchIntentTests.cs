namespace DeskBox.Tests;

/// <summary>
/// Batch R1 pins: the spawn-carried launch intent must actually be CONSUMED
/// by the settings host (the cold-start deep link was once dead code — the
/// parsers existed with zero callers while commit messages claimed
/// otherwise), and the coordinator must carry the one-click-update and
/// installer-result intents on the spawn command line instead of pushing
/// onto a pipe that is not connected yet.
/// </summary>
public sealed class SettingsHostLaunchIntentTests
{
    [Fact]
    public void HostFactory_WiresRealChannelsForTheDictionaryAndStoreDomains()
    {
        string host = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.SettingsHost.cs"));

        // The Todo/QuickCapture enable switches are layout-domain dictionary
        // writes: they must ride the SetFeatureWidgetEnabled command — the
        // null/no-op callback shapes once silently dropped them (the host
        // committed a snapshot whose dictionary the main process strips;
        // batch R8). QuickCapture's wiring is pinned by its (enabled, _)
        // shape; Search's (_, _) no-op is deliberate and stays (its enable
        // state is a settings.json slice, so the snapshot IS the channel).
        Assert.Contains(
            "SetFeatureWidgetEnabled(WidgetKind.Todo, enabled)", host, StringComparison.Ordinal);
        Assert.Contains(
            "SetFeatureWidgetEnabled(WidgetKind.QuickCapture, enabled)", host, StringComparison.Ordinal);
        Assert.Contains("applyWidget: (enabled, _) =>", host, StringComparison.Ordinal);

        // The appearance page's per-widget background clear is a
        // layout-domain write: same rule, dedicated command.
        Assert.Contains("clearPerWidgetBackgroundsExternal:", host, StringComparison.Ordinal);

        // Boot adoption: the host mirrors the MAIN process's memory, never
        // disk-loads settings (corrupt-disk defaults must not round-trip
        // back over a healthy main process; load-time store writes belong
        // to the single writer).
        Assert.Contains("SettingsIpcMethods.SettingsSnapshot", host, StringComparison.Ordinal);
        Assert.Contains(
            "settings.AdoptExternalSnapshot(snapshotJson)", host, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await settings.LoadAsync()", host, StringComparison.Ordinal);

        // The glance store relay: per-widget data edits belong to the MAIN
        // process (sole writer + live Changed listeners).
        Assert.Contains("GlanceWidgetStore.SaveRelay =", host, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchArguments_ParseSectionGlanceOneClickAndInstallResult()
    {
        string[] args =
        [
            "DeskBox.exe",
            "--settings",
            "1234",
            "--settings-section",
            "About",
            "--settings-one-click-update",
            "--settings-install-result",
            "failed",
        ];
        Assert.Equal("About", App.SettingsHostLaunch.TryGetSectionArgument(args));
        Assert.Null(App.SettingsHostLaunch.TryGetGlanceArgument(args));
        Assert.True(App.SettingsHostLaunch.HasOneClickUpdateArgument(args));
        Assert.Equal("failed", App.SettingsHostLaunch.TryGetInstallResultArgument(args));
    }

    [Fact]
    public void LaunchArguments_GlanceAndSection_PreferGlanceLikeConsumptionOrder()
    {
        string[] args =
        [
            "DeskBox.exe",
            "--settings",
            "1234",
            "--settings-glance",
            "widget-7",
        ];
        Assert.Equal("widget-7", App.SettingsHostLaunch.TryGetGlanceArgument(args));
        Assert.Null(App.SettingsHostLaunch.TryGetSectionArgument(args));
        Assert.False(App.SettingsHostLaunch.HasOneClickUpdateArgument(args));
        Assert.Null(App.SettingsHostLaunch.TryGetInstallResultArgument(args));
    }

    [Fact]
    public void LaunchArguments_DoNotSwallowNeighboringArgumentNames()
    {
        // The jump list argument must never be misread as a host deep link.
        string[] args =
        [
            "DeskBox.exe",
            "--settings",
            "1234",
            "--open-settings",
        ];
        Assert.Null(App.SettingsHostLaunch.TryGetSectionArgument(args));
        Assert.Null(App.SettingsHostLaunch.TryGetGlanceArgument(args));
        Assert.False(App.SettingsHostLaunch.HasOneClickUpdateArgument(args));
    }

    [Fact]
    public void HostBoot_ConsumesEveryLaunchIntentParser()
    {
        string host = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.SettingsHost.cs"));
        // Pinned as CALLS on the launch-argument snapshot — definitions
        // alone were the dead-code regression this batch closed.
        Assert.Contains("SettingsHostLaunch.TryGetGlanceArgument(launchArguments)", host, StringComparison.Ordinal);
        Assert.Contains("SettingsHostLaunch.TryGetSectionArgument(launchArguments)", host, StringComparison.Ordinal);
        Assert.Contains("SettingsHostLaunch.HasOneClickUpdateArgument(launchArguments)", host, StringComparison.Ordinal);
        Assert.Contains("SettingsHostLaunch.TryGetInstallResultArgument(launchArguments)", host, StringComparison.Ordinal);
        // And the consumed intents actually drive the window.
        Assert.Contains("window.RunOneClickUpdateFlowAsync();", host, StringComparison.Ordinal);
        Assert.Contains("window.QueueUpdateInstallResultDialog(launchInstallResult);", host, StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_CarriesSpawnIntentOnTheCommandLine()
    {
        string coordinator = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.SettingsHostCoordinator.cs"));
        Assert.Contains("\"--settings-one-click-update\"", coordinator, StringComparison.Ordinal);
        Assert.Contains("\"--settings-install-result\"", coordinator, StringComparison.Ordinal);
        // The warm path still pushes the intents over the connected pipe.
        Assert.Contains("SettingsIpcMethods.HostOneClickUpdate, null", coordinator, StringComparison.Ordinal);
        Assert.Contains("SettingsIpcMethods.HostQueueUpdateResult, installResult", coordinator, StringComparison.Ordinal);
    }
}
