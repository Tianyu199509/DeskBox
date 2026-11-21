namespace DeskBox.Tests;

public sealed class TrayEfficiencyModeContractTests
{
    [Fact]
    public void TrayStartup_NeverEnablesProcessEfficiencyModeOrPullsHNotifyIcon()
    {
        string traySource = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.Tray.cs"));
        string hostSource = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Platform/NativeTrayIconHost.cs"));
        string projectSource = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));

        Assert.DoesNotContain("H.NotifyIcon", traySource, StringComparison.Ordinal);
        Assert.DoesNotContain("H.NotifyIcon", projectSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetEfficiencyMode(", traySource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetEfficiencyMode(", hostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessEfficiencyModeStartup", traySource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessEfficiencyModeStartup", hostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessPowerThrottling", hostSource, StringComparison.Ordinal);
    }
}
