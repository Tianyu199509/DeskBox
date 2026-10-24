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
