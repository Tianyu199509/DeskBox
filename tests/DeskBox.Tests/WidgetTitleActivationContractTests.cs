namespace DeskBox.Tests;

public sealed class WidgetTitleActivationContractTests
{
    [Fact]
    public void TitleBarActivation_UsesOnlyTheClickedWidget()
    {
        string manager = Read(
            "src/DeskBox/Services/WidgetManager.ZOrder.cs");
        string layer = Read(
            "src/DeskBox/Services/WidgetLayerService.cs");
        string contentWindow = Read(
            "src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs");
        string quickCaptureWindow = Read(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.WindowInteraction.cs");

        Assert.Contains(
            "ActivateWidgetFromTitle(HWnd)",
            contentWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ActivateWidgetFromTitle(_hWnd)",
            quickCaptureWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ActivateAllVisibleWidgetsFromTitle",
            contentWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ActivateAllVisibleWidgetsFromTitle",
            quickCaptureWindow,
            StringComparison.Ordinal);

        string method = SliceMethod(
            manager,
            "public void ActivateWidgetFromTitle",
            "/// <summary>");
        Assert.Contains("[activeHwnd]", method, StringComparison.Ordinal);
        Assert.Contains(
            "WidgetLayerService.ActivateWindowFromTitle(activeHwnd)",
            method,
            StringComparison.Ordinal);
        Assert.Contains("UsesDesktopPinnedMode()", method, StringComparison.Ordinal);
        Assert.Contains("WidgetsRaisedFromTray", method, StringComparison.Ordinal);
        Assert.Contains("!IsWidgetWindow(activeHwnd)", method, StringComparison.Ordinal);
        Assert.Contains(
            "!Win32Helper.IsWindowVisible(activeHwnd)",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GetLoadedDesktopWindows()",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BringGroupTemporarilyToFront",
            method,
            StringComparison.Ordinal);

        string layerMethod = SliceMethod(
            layer,
            "public static void ActivateWindowFromTitle",
            "/// <summary>");
        Assert.DoesNotContain(
            "windowHandles",
            layerMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "windowHandle",
            layerMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.BringWindowTemporarilyToFront(windowHandle)",
            layerMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetForegroundWindow(windowHandle)",
            layerMethod,
            StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private static string SliceMethod(
        string source,
        string startMarker,
        string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }
}
