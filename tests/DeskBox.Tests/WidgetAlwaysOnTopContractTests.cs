using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class WidgetAlwaysOnTopContractTests
{
    [Fact]
    public void WidgetConfig_PersistsAlwaysOnTopPreference()
    {
        var config = new WidgetConfig { IsAlwaysOnTop = true };

        string json = JsonSerializer.Serialize(config);
        WidgetConfig? restored = JsonSerializer.Deserialize<WidgetConfig>(json);

        Assert.Contains("\"IsAlwaysOnTop\":true", json, StringComparison.Ordinal);
        Assert.True(restored?.IsAlwaysOnTop);
    }

    [Fact]
    public void BothWidgetMenus_ExposeAlwaysOnTopToggle()
    {
        Assert.Contains(
            "WidgetAlwaysOnTopMenuBuilder.Create",
            Read("src/DeskBox/Views/ContentWidgetWindow.Commands.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetAlwaysOnTopMenuBuilder.Create",
            Read("src/DeskBox/Views/QuickCaptureWidgetWindow.Menus.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SharedWindowLayer_AppliesAndProtectsPersistentTopMostState()
    {
        string window = Read("src/DeskBox/Views/WidgetWindowBase.Bounds.cs");
        string layer = Read("src/DeskBox/Services/WidgetLayerService.cs");

        Assert.Contains("Config.IsAlwaysOnTop", window, StringComparison.Ordinal);
        Assert.Contains("WidgetLayerService.SetAlwaysOnTop", window, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(layer, "if (IsAlwaysOnTop(windowHandle))") >= 8,
            "Every single-window layer transition must protect persistent topmost state.");
        Assert.Contains("Win32Helper.HWND_TOPMOST", layer, StringComparison.Ordinal);
        Assert.Contains("s_alwaysOnTopWindows.Remove(windowHandle)", layer, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetGroups_ShareAlwaysOnTopAsWindowState()
    {
        string groupConfig = Read("src/DeskBox/Models/WidgetGroupConfig.cs");
        string groupManager = Read("src/DeskBox/Services/WidgetManager.Groups.cs");

        Assert.Contains("public bool IsAlwaysOnTop", groupConfig, StringComparison.Ordinal);
        Assert.Contains("group.IsAlwaysOnTop = member.IsAlwaysOnTop", groupManager, StringComparison.Ordinal);
        Assert.Contains("member.IsAlwaysOnTop = group.IsAlwaysOnTop", groupManager, StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureWidgetReset_ClearsAlwaysOnTopPreference()
    {
        string manager = Read("src/DeskBox/Services/WidgetManager.FeatureWidgets.cs");

        Assert.Contains("config.IsAlwaysOnTop = false;", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void RestingLayerTransitions_PreserveLogicalAlwaysOnTopState()
    {
        string contentWindow = Read("src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs");
        string quickCaptureWindow = Read("src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs");
        string sharedInteraction = Read("src/DeskBox/Views/WidgetWindowBase.Interaction.cs");
        string collapse = Read("src/DeskBox/Views/WidgetWindowBase.Collapse.cs");

        Assert.Contains("if (Config.IsAlwaysOnTop)", contentWindow, StringComparison.Ordinal);
        Assert.Contains("if (Config.IsAlwaysOnTop)", quickCaptureWindow, StringComparison.Ordinal);
        Assert.Contains("ApplyAlwaysOnTopPreference();", sharedInteraction, StringComparison.Ordinal);
        Assert.Contains("expanded-state-topmost-preserved", collapse, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRuntimeLocale_ContainsAlwaysOnTopLabel()
    {
        string stringsRoot = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] localeFiles = Directory.GetFiles(
            stringsRoot,
            "*.json",
            SearchOption.TopDirectoryOnly);

        Assert.Equal(12, localeFiles.Length);
        Assert.All(localeFiles, path => Assert.Contains(
            "\"Widget.AlwaysOnTop\"",
            File.ReadAllText(path),
            StringComparison.Ordinal));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private static int CountOccurrences(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;
}
