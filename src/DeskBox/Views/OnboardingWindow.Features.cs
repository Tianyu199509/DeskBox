using CommunityToolkit.WinUI.Animations;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private void SetupStep2Features()
    {
        // This page teaches the stable tray entry point. It intentionally does
        // not open a live menu or create another widget during onboarding.
    }

    // ════════════════════════════════════════════════════════════
    //  Step 3: Appearance (capsule toggle handler)
    // ════════════════════════════════════════════════════════════

    private void Step3CapsuleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        _settingsService.Settings.WidgetCollapseBehavior = toggle.IsOn
            ? SettingsService.WidgetCollapseBehaviorSmart
            : SettingsService.WidgetCollapseBehaviorExpanded;
        _settingsService.Settings.WidgetCapsuleModeEnabled = toggle.IsOn;
        _settingsService.SaveDebounced();
    }
}
