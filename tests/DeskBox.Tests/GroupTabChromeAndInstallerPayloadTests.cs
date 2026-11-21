using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox.Tests;

public sealed class GroupTabChromeAndInstallerPayloadTests
{
    [Fact]
    public void RetailNativeAotPackagingScripts_RetainAndRequireWinUiPhoneXamlModule_AndSmokeBootSettingsWindow()
    {
        string publishScript = File.ReadAllText(TestPaths.FromRepository(
            "scripts/publish-aot-retail.ps1"));
        string distributionScript = File.ReadAllText(TestPaths.FromRepository(
            "scripts/build-stage-7c1-distribution.ps1"));
        string smokeScript = File.ReadAllText(TestPaths.FromRepository(
            "scripts/run-aot-agent-clipboard-companion-smoke.ps1"));

        // Microsoft.ui.xaml.dll dynamically LoadLibrary's Microsoft.UI.Xaml.Phone.dll
        // when instantiating ComboBox/Picker/NavigationView templates in SettingsWindow.
        Assert.Contains("\"Microsoft.UI.Xaml.Phone.dll\"", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain("^Microsoft\\.UI\\.Xaml\\.Phone\\.dll", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"Microsoft.UI.Xaml.Phone.dll\"", distributionScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft\\.UI\\.Xaml\\.Phone\\.dll", distributionScript, StringComparison.Ordinal);

        // Smoke script must exercise --open-settings and verify SettingsWindow reaches Window shown.
        Assert.Contains("\"--open-settings\"", smokeScript, StringComparison.Ordinal);
        Assert.Contains("[SettingsHost] Window shown", smokeScript, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupTabConnectedChromeTokens_MatchCompactFluentProportionsAndSoftStrokeLayering()
    {
        // Compact top inset (~4.5 DIP inside a 36 DIP group tab strip -> ~31.5 DIP tab arch).
        Assert.Equal(4.5, WidgetGroupTitleSwitcher.TabsModeOuterInset);
        Assert.Equal(6.0, WidgetGroupTitleSwitcher.ConnectedTabFootRadius);

        // Fluent 1.5.9-parity subtle surface fill (~9%) + soft border (~10%) + separator (~8%)
        // instead of harsh 0x2E wireframe strokes.
        Assert.Equal((byte)0x1A, WidgetGroupTitleSwitcher.DefaultConnectedTabContourAlpha);
        Assert.Equal((byte)0x17, WidgetGroupTitleSwitcher.DefaultConnectedTabFillAlpha);
        Assert.Equal((byte)0x1F, WidgetGroupTitleSwitcher.HoverConnectedTabFillAlpha);
        Assert.Equal((byte)0x14, WidgetGroupTitleSwitcher.DefaultInactiveSeparatorAlpha);

        // Top corner radius caps cleanly at 8.0 DIP (Win11 TabView OverlayCornerRadius)
        // and scales smoothly across Round/Small/Square presets.
        Assert.Equal(8.0, WidgetGroupTitleSwitcher.ComputeTabsConcentricCornerRadius(16.0));
        Assert.Equal(6.0, WidgetGroupTitleSwitcher.ComputeTabsConcentricCornerRadius(12.0));
        Assert.Equal(5.0, WidgetGroupTitleSwitcher.ComputeTabsConcentricCornerRadius(8.0));
        Assert.Equal(5.0, WidgetGroupTitleSwitcher.ComputeTabsConcentricCornerRadius(4.0));
        Assert.Equal(4.5, WidgetGroupTitleSwitcher.ComputeTabsConcentricCornerRadius(0.0));
    }

    [Fact]
    public void GroupTabTitleFontWeight_RemainsNormalAcrossSelectedEqualAndHighContrastModes()
    {
        foreach (bool isSelected in new[] { false, true })
        {
            foreach (bool isEqualTabs in new[] { false, true })
            {
                foreach (bool isHighContrast in new[] { false, true })
                {
                    Assert.Equal(
                        (ushort)400,
                        WidgetGroupTitleSwitcher.ResolveTabTitleFontWeight(
                            isSelected,
                            isEqualTabs,
                            isHighContrast).Weight);
                }
            }
        }
    }

    [Fact]
    public void StackPopoverMaterialBackdrop_PreservesControllerAcrossTargetReplacementAndHealsMissingController()
    {
        object oldTarget = new();
        object newTarget = new();

        // Late OnTargetDisconnected from a replaced SystemBackdropElement must NOT
        // tear down the newly connected target's DesktopAcrylicController / MicaController.
        Assert.False(WidgetMaterialSystemBackdrop.ShouldReleaseStateOnTargetDisconnected(
            currentRawTarget: newTarget,
            disconnectedTarget: oldTarget));
        Assert.True(WidgetMaterialSystemBackdrop.ShouldReleaseStateOnTargetDisconnected(
            currentRawTarget: newTarget,
            disconnectedTarget: newTarget));
        Assert.False(WidgetMaterialSystemBackdrop.ShouldReleaseStateOnTargetDisconnected(
            currentRawTarget: null,
            disconnectedTarget: oldTarget));

        var neutralAcrylic = new WidgetMaterialBackdropAppearance(
            SettingsService.WidgetMaterialTypeAcrylic,
            IsDark: true,
            AccentColorHelper.DefaultAccentColor,
            SurfaceOpacity: 0.85,
            MaterialIntensity: 0.5);

        // If the controller is absent while appearance matches, UpdateAppearance MUST NOT
        // short-circuit — it must re-apply and rebuild the controller.
        Assert.False(WidgetMaterialSystemBackdrop.ShouldSkipAppearanceUpdate(
            neutralAcrylic,
            neutralAcrylic,
            isDisposed: false,
            isSuspended: false,
            hasActiveController: false));
        Assert.True(WidgetMaterialSystemBackdrop.ShouldSkipAppearanceUpdate(
            neutralAcrylic,
            neutralAcrylic,
            isDisposed: false,
            isSuspended: false,
            hasActiveController: true));

        // Connected popover element without an active controller must trigger ApplyBackdropCore.
        Assert.False(StackPopoverHostWindow.HasReadyBackdropAttachment(
            hasWindowBackdrop: true,
            expectsMaterialBackdrop: true,
            isBackdropElementVisible: true,
            backdropMatchesMaterialInstance: true,
            isBackdropElementConnectedToTree: true,
            hasActiveMaterialController: false));
        Assert.True(StackPopoverHostWindow.HasReadyBackdropAttachment(
            hasWindowBackdrop: true,
            expectsMaterialBackdrop: true,
            isBackdropElementVisible: true,
            backdropMatchesMaterialInstance: true,
            isBackdropElementConnectedToTree: true,
            hasActiveMaterialController: true));
    }
}
