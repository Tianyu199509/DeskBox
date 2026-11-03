#if DESKBOX_AOT_SMOKE_HARNESS || DESKBOX_STORE
using CommunityToolkit.WinUI.Controls;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
        RegisterAttachedSafe("Key", typeof(string), typeof(Localized), OnLocalizationPropertyChanged);

    public static readonly DependencyProperty ToolTipKeyProperty =
        RegisterAttachedSafe("ToolTipKey", typeof(string), typeof(Localized), OnLocalizationPropertyChanged);

    public static readonly DependencyProperty HeaderKeyProperty =
        RegisterAttachedSafe("HeaderKey", typeof(string), typeof(Localized), OnLocalizationPropertyChanged);

    public static readonly DependencyProperty DescriptionKeyProperty =
        RegisterAttachedSafe("DescriptionKey", typeof(string), typeof(Localized), OnLocalizationPropertyChanged);

    public static readonly DependencyProperty PlaceholderKeyProperty =
        RegisterAttachedSafe("PlaceholderKey", typeof(string), typeof(Localized), OnLocalizationPropertyChanged);

    public static readonly DependencyProperty AutomationNameKeyProperty =
        RegisterAttachedSafe("AutomationNameKey", typeof(string), typeof(Localized), OnLocalizationPropertyChanged);

    private static DependencyProperty RegisterAttachedSafe(
        string name,
        Type propertyType,
        Type ownerType,
        PropertyChangedCallback callback)
    {
        try
        {
            return DependencyProperty.RegisterAttached(
                name,
                propertyType,
                ownerType,
                new PropertyMetadata(null, callback));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null!;
        }
    }

    private static readonly object s_lock = new();
    private static readonly object s_trackedSentinel = new();
    private static readonly List<WeakReference<DependencyObject>> s_targets = [];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, object> s_trackedSet = new();

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

    public static string? GetPlaceholderKey(DependencyObject obj)
    {
        return (string?)obj.GetValue(PlaceholderKeyProperty);
    }

    public static void SetPlaceholderKey(DependencyObject obj, string? value)
    {
        obj.SetValue(PlaceholderKeyProperty, value);
    }

    public static string? GetAutomationNameKey(DependencyObject obj)
    {
        return (string?)obj.GetValue(AutomationNameKeyProperty);
    }

    public static void SetAutomationNameKey(DependencyObject obj, string? value)
    {
        obj.SetValue(AutomationNameKeyProperty, value);
    }

    public static void RefreshAll(LocalizationService localizationService)
    {
        List<DependencyObject> snapshot;
        lock (s_lock)
        {
            snapshot = new List<DependencyObject>(s_targets.Count);
            for (int index = s_targets.Count - 1; index >= 0; index--)
            {
                if (!s_targets[index].TryGetTarget(out var target))
                {
                    s_targets.RemoveAt(index);
                    continue;
                }

                snapshot.Add(target);
            }
        }

        for (int index = snapshot.Count - 1; index >= 0; index--)
        {
            DependencyObject target = snapshot[index];
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
        lock (s_lock)
        {
            for (int index = s_targets.Count - 1; index >= 0; index--)
            {
                if (!s_targets[index].TryGetTarget(out var target))
                {
                    s_targets.RemoveAt(index);
                    continue;
                }

                if (ReferenceEquals(target, root) || IsDescendantOf(target, root))
                {
                    s_trackedSet.Remove(target);
                    s_targets.RemoveAt(index);
                }
            }
        }
    }

    public static void PruneDeadTargets()
    {
        lock (s_lock)
        {
            for (int index = s_targets.Count - 1; index >= 0; index--)
            {
                if (!s_targets[index].TryGetTarget(out _))
                {
                    s_targets.RemoveAt(index);
                }
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
        if (CurrentService is { } localizationService)
        {
            Apply(target, localizationService);
        }
    }

    private static LocalizationService? CurrentService => App.Current?.LocalizationService;

    /// <summary>
    /// Resolves one key outside the attached-property pipeline (e.g. the
    /// shared InfoTip flyout fills its text at open time). Falls back to
    /// the key itself, matching LocalizationService.T's fallback.
    /// </summary>
    public static string ActiveCultureName =>
        CurrentService?.CurrentCultureName ?? LocalizationService.ResolveAmbientCultureName();

    public static bool IsActiveChinese =>
        ActiveCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string T(string key)
    {
        return CurrentService is { } svc
            ? svc.T(key)
            : LocalizationService.TranslateForCulture(LocalizationService.ResolveAmbientCultureName(), key);
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, T(key), args);
    }

    private static void Track(DependencyObject target)
    {
        lock (s_lock)
        {
            if (!s_trackedSet.TryAdd(target, s_trackedSentinel))
            {
                return;
            }

            s_targets.Add(new WeakReference<DependencyObject>(target));
        }
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

        string? placeholderKey = GetPlaceholderKey(target);
        if (!string.IsNullOrWhiteSpace(placeholderKey))
        {
            string placeholder = localizationService.T(placeholderKey);
            switch (target)
            {
                case TextBox placeholderTextBox:
                    placeholderTextBox.PlaceholderText = placeholder;
                    break;
                case AutoSuggestBox placeholderAutoSuggestBox:
                    placeholderAutoSuggestBox.PlaceholderText = placeholder;
                    break;
            }
        }

        string? automationNameKey = GetAutomationNameKey(target);
        if (!string.IsNullOrWhiteSpace(automationNameKey) && target is UIElement automationElement)
        {
            AutomationProperties.SetName(automationElement, localizationService.T(automationNameKey));
        }
    }

    private static Func<DependencyObject, string, bool>? s_externalTrySetHeader;
    private static Func<DependencyObject, string, bool>? s_externalTrySetDescription;

    internal static void RegisterExternalHeaderDescriptionAdapter(
        Func<DependencyObject, string, bool> trySetHeader,
        Func<DependencyObject, string, bool> trySetDescription)
    {
        s_externalTrySetHeader = trySetHeader;
        s_externalTrySetDescription = trySetDescription;
    }

    private static void SetLocalizedHeader(DependencyObject target, string value)
    {
        if (s_externalTrySetHeader?.Invoke(target, value) == true)
        {
            return;
        }

        switch (target)
        {
#if DESKBOX_AOT_SMOKE_HARNESS || DESKBOX_STORE
            case SettingsCard settingsCard:
                settingsCard.Header = InfoTip.TryCreateHeaderContent(settingsCard, value) ?? value;
                break;
            case SettingsExpander settingsExpander:
                settingsExpander.Header = InfoTip.TryCreateHeaderContent(settingsExpander, value) ?? value;
                break;
#endif
            case TextBox textBox:
                textBox.Header = value;
                break;
            case InfoBar infoBar:
                infoBar.Title = value;
                break;
            default:
                System.Diagnostics.Debug.WriteLine(
                    $"[Localized] HeaderKey ignored for unsupported target {target.GetType().FullName}.");
                break;
        }
    }

    private static void SetLocalizedDescription(DependencyObject target, string value)
    {
        if (s_externalTrySetDescription?.Invoke(target, value) == true)
        {
            return;
        }

        switch (target)
        {
#if DESKBOX_AOT_SMOKE_HARNESS || DESKBOX_STORE
            case SettingsCard settingsCard:
                settingsCard.Description = value;
                break;
            case SettingsExpander settingsExpander:
                settingsExpander.Description = value;
                break;
#endif
            case InfoBar infoBar:
                infoBar.Message = value;
                break;
            default:
                System.Diagnostics.Debug.WriteLine(
                    $"[Localized] DescriptionKey ignored for unsupported target {target.GetType().FullName}.");
                break;
        }
    }
}
