using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Services;

public static class Localized
{
    // Runtime application of HeaderKey/DescriptionKey only targets controls
    // with real Header/Description properties (SettingsCard, SettingsExpander,
    // TextBox). On plain containers (Grid, StackPanel, Expander) these keys are
    // intentional search-catalog markers: update-settings-search-catalog.ps1
    // indexes them while the visible text is rendered by an inner TextBlock
    // bound to the same key via Localized.Key. Unsupported targets are ignored
    // at runtime by design.

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(Localized),
            new PropertyMetadata(null, OnLocalizationPropertyChanged));

    public static readonly DependencyProperty ToolTipKeyProperty =
        DependencyProperty.RegisterAttached(
            "ToolTipKey",
            typeof(string),
            typeof(Localized),
            new PropertyMetadata(null, OnLocalizationPropertyChanged));

    public static readonly DependencyProperty HeaderKeyProperty =
        DependencyProperty.RegisterAttached(
            "HeaderKey",
            typeof(string),
            typeof(Localized),
            new PropertyMetadata(null, OnLocalizationPropertyChanged));

    public static readonly DependencyProperty DescriptionKeyProperty =
        DependencyProperty.RegisterAttached(
            "DescriptionKey",
            typeof(string),
            typeof(Localized),
            new PropertyMetadata(null, OnLocalizationPropertyChanged));

    private static readonly List<WeakReference<DependencyObject>> s_targets = [];

    public static string? GetKey(DependencyObject obj)
    {
        return (string?)obj.GetValue(KeyProperty);
    }

    public static void SetKey(DependencyObject obj, string? value)
    {
        obj.SetValue(KeyProperty, value);
    }

    public static string? GetToolTipKey(DependencyObject obj)
    {
        return (string?)obj.GetValue(ToolTipKeyProperty);
    }

    public static void SetToolTipKey(DependencyObject obj, string? value)
    {
        obj.SetValue(ToolTipKeyProperty, value);
    }

    public static string? GetHeaderKey(DependencyObject obj)
    {
        return (string?)obj.GetValue(HeaderKeyProperty);
    }

    public static void SetHeaderKey(DependencyObject obj, string? value)
    {
        obj.SetValue(HeaderKeyProperty, value);
    }

    public static string? GetDescriptionKey(DependencyObject obj)
    {
        return (string?)obj.GetValue(DescriptionKeyProperty);
    }

    public static void SetDescriptionKey(DependencyObject obj, string? value)
    {
        obj.SetValue(DescriptionKeyProperty, value);
    }

        public static void RefreshAll(LocalizationService localizationService)
    {
        for (int index = s_targets.Count - 1; index >= 0; index--)
        {
            if (!s_targets[index].TryGetTarget(out var target))
            {
                s_targets.RemoveAt(index);
                continue;
            }

            try
            {
                Apply(target, localizationService);
            }
            catch (Exception ex)
            {
                // An exception on a single element (e.g., disposed object,
                // cross-thread access) must not abort the entire refresh.
                System.Diagnostics.Debug.WriteLine(
                    $"[Localized] RefreshAll Apply failed for {target.GetType().Name}: {ex.Message}");
            }
        }
    }

    public static void UntrackTree(DependencyObject root)
    {
        for (int index = s_targets.Count - 1; index >= 0; index--)
        {
            if (!s_targets[index].TryGetTarget(out var target) ||
                ReferenceEquals(target, root) ||
                IsDescendantOf(target, root))
            {
                s_targets.RemoveAt(index);
            }
        }
    }

    public static void PruneDeadTargets()
    {
        for (int index = s_targets.Count - 1; index >= 0; index--)
        {
            if (!s_targets[index].TryGetTarget(out _))
            {
                s_targets.RemoveAt(index);
            }
        }
    }

    private static void OnLocalizationPropertyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is null)
        {
            return;
        }

        Track(target);
        if (App.Current?.LocalizationService is { } localizationService)
        {
            Apply(target, localizationService);
        }
    }

    private static void Track(DependencyObject target)
    {
        for (int index = s_targets.Count - 1; index >= 0; index--)
        {
            if (!s_targets[index].TryGetTarget(out var existing))
            {
                s_targets.RemoveAt(index);
                continue;
            }

            if (ReferenceEquals(existing, target))
            {
                return;
            }
        }

        s_targets.Add(new WeakReference<DependencyObject>(target));
    }

    private static bool IsDescendantOf(DependencyObject target, DependencyObject root)
    {
        DependencyObject? current = target;
        while (current is not null)
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static void Apply(DependencyObject target, LocalizationService localizationService)
    {
        string? key = GetKey(target);
        if (!string.IsNullOrWhiteSpace(key))
        {
            string text = localizationService.T(key);
            switch (target)
            {
                case TextBlock textBlock:
                    textBlock.Text = text;
                    break;
                case ContentControl contentControl:
                    contentControl.Content = text;
                    break;
            }
        }

        string? headerKey = GetHeaderKey(target);
        if (!string.IsNullOrWhiteSpace(headerKey))
        {
            SetLocalizedHeader(target, localizationService.T(headerKey));
        }

        string? descriptionKey = GetDescriptionKey(target);
        if (!string.IsNullOrWhiteSpace(descriptionKey))
        {
            SetLocalizedDescription(target, localizationService.T(descriptionKey));
        }

        string? toolTipKey = GetToolTipKey(target);
        if (!string.IsNullOrWhiteSpace(toolTipKey) && target is UIElement element)
        {
            ToolTipService.SetToolTip(element, localizationService.T(toolTipKey));
        }
    }

    private static void SetLocalizedHeader(DependencyObject target, string value)
    {
        switch (target)
        {
            case SettingsCard settingsCard:
                settingsCard.Header = value;
                break;
            case SettingsExpander settingsExpander:
                settingsExpander.Header = value;
                break;
            case TextBox textBox:
                textBox.Header = value;
                break;
            default:
                System.Diagnostics.Debug.WriteLine(
                    $"[Localized] HeaderKey ignored for unsupported target {target.GetType().FullName}.");
                break;
        }
    }

    private static void SetLocalizedDescription(DependencyObject target, string value)
    {
        switch (target)
        {
            case SettingsCard settingsCard:
                settingsCard.Description = value;
                break;
            case SettingsExpander settingsExpander:
                settingsExpander.Description = value;
                break;
            default:
                System.Diagnostics.Debug.WriteLine(
                    $"[Localized] DescriptionKey ignored for unsupported target {target.GetType().FullName}.");
                break;
        }
    }
}
