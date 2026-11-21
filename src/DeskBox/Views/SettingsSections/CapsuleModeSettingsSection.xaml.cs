using DeskBox.Features.Performance;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Views.SettingsSections;

public sealed partial class CapsuleModeSettingsSection : UserControl
{
    private readonly Dictionary<string, SvgImageSource> _overrideIconSources =
        new(StringComparer.Ordinal);
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(SettingsViewModel),
            typeof(CapsuleModeSettingsSection),
            new PropertyMetadata(null));

    public static readonly DependencyProperty PerformanceProperty =
        DependencyProperty.Register(
            nameof(Performance),
            typeof(PerformanceSettingsViewModel),
            typeof(CapsuleModeSettingsSection),
            new PropertyMetadata(null));

    public CapsuleModeSettingsSection()
    {
        InitializeComponent();
    }

    public SettingsViewModel? ViewModel
    {
        get => (SettingsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public PerformanceSettingsViewModel? Performance
    {
        get => (PerformanceSettingsViewModel?)GetValue(PerformanceProperty);
        set => SetValue(PerformanceProperty, value);
    }

    public event EventHandler<SettingsSectionNavigationRequestedEventArgs>? NavigationRequested;

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sectionTag })
        {
            NavigationRequested?.Invoke(this, new SettingsSectionNavigationRequestedEventArgs(sectionTag));
        }
    }

    private void ResetCapsuleWidgetOverrideButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string widgetId } &&
            !string.IsNullOrWhiteSpace(widgetId))
        {
            ViewModel?.ResetCapsuleOverridesForWidget(widgetId);
        }
    }

    private void CapsuleOverrideIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ImageIcon icon)
        {
            SyncCapsuleOverrideIcon(icon, icon.DataContext);
        }
    }

    private void CapsuleOverrideIcon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is ImageIcon icon)
        {
            SyncCapsuleOverrideIcon(icon, args.NewValue);
        }
    }

    private void SyncCapsuleOverrideIcon(ImageIcon icon, object? dataContext)
    {
        string assetName = dataContext is CapsuleOverrideSettingsItem { ColorIconAssetName: { Length: > 0 } name }
            ? name
            : "default";
        if (!_overrideIconSources.TryGetValue(assetName, out SvgImageSource? source))
        {
            source = new SvgImageSource(new Uri($"ms-appx:///Assets/WidgetTitleIcons/{assetName}.svg"));
            _overrideIconSources[assetName] = source;
        }

        if (!ReferenceEquals(icon.Source, source))
        {
            icon.Source = source;
        }
    }
}
