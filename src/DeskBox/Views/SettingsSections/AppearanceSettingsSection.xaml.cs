using System.ComponentModel;
using DeskBox.Features.Appearance;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Views.SettingsSections;

public sealed partial class AppearanceSettingsSection : UserControl
{
    public AppearanceSettingsSection()
    {
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; set; } = null!;

    public event EventHandler<SettingsSectionNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler<RoutedEventArgs>? ChooseBackgroundImageRequested;
    public event EventHandler<RoutedEventArgs>? ClearPerWidgetBackgroundsRequested;
    public event EventHandler<RoutedEventArgs>? OpenSystemTransparencyRequested;
    public event EventHandler? SliderDragStarted;
    public event EventHandler? SliderDragCompleted;

    public void ExpandMaterialSection()
    {
        MaterialExpander.IsExpanded = true;
    }

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sectionTag })
        {
            NavigationRequested?.Invoke(this, new SettingsSectionNavigationRequestedEventArgs(sectionTag));
        }
    }

    private void AccentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AppearanceSettingsViewModel editor ||
            sender is not Button { Tag: string hex } ||
            !AccentColorHelper.TryParseHex(hex, out var color))
        {
            return;
        }

        editor.NotifyAccentPresetPicked(AccentColorHelper.ToHex(color));
    }

    private void ChooseWidgetBackgroundImage_Click(object sender, RoutedEventArgs e) =>
        ChooseBackgroundImageRequested?.Invoke(sender, e);

    private void ClearWidgetBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppearanceSettingsViewModel editor)
        {
            editor.ClearCurrentBackgroundImage();
        }
    }

    private void ClearPerWidgetBackgroundsButton_Click(object sender, RoutedEventArgs e) =>
        ClearPerWidgetBackgroundsRequested?.Invoke(sender, e);

    private void OpenSystemTransparencySettingsButton_Click(object sender, RoutedEventArgs e) =>
        OpenSystemTransparencyRequested?.Invoke(sender, e);

    private void AppearanceSlider_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e) =>
        SliderDragStarted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e) =>
        SliderDragCompleted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        SliderDragStarted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        SliderDragCompleted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_LostFocus(object sender, RoutedEventArgs e) =>
        SliderDragCompleted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
    }
}
