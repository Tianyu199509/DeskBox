#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    internal AotSettingsWindowSnapshot CaptureAotSmokeSnapshot()
    {
        string? selectedSection =
            (SettingsNavigationView.SelectedItem as NavigationViewItem)?.Tag as string;
        string[] visibleSections = _settingsSectionElements
            .Where(entry => entry.Value.Visibility == Visibility.Visible)
            .Select(entry => entry.Key)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();

        return new AotSettingsWindowSnapshot(
            WindowNative.GetWindowHandle(this).ToInt64(),
            _appWindow.IsVisible,
            SettingsRoot.XamlRoot is not null,
            SettingsRoot.ActualWidth,
            SettingsRoot.ActualHeight,
            Title,
            _currentSettingsSection,
            selectedSection,
            visibleSections);
    }
}

internal sealed record AotSettingsWindowSnapshot(
    long WindowHandle,
    bool IsAppWindowVisible,
    bool HasXamlRoot,
    double ActualWidth,
    double ActualHeight,
    string Title,
    string CurrentSection,
    string? SelectedSection,
    IReadOnlyList<string> VisibleSections);
#endif
