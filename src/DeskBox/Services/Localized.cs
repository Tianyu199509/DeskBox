using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

public static class Localized
{
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

            Apply(target, localizationService);
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
        if (s_targets.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, target)))
        {
            return;
        }

        s_targets.Add(new WeakReference<DependencyObject>(target));
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
            SetObjectProperty(target, "Header", localizationService.T(headerKey));
        }

        string? descriptionKey = GetDescriptionKey(target);
        if (!string.IsNullOrWhiteSpace(descriptionKey))
        {
            SetObjectProperty(target, "Description", localizationService.T(descriptionKey));
        }

        string? toolTipKey = GetToolTipKey(target);
        if (!string.IsNullOrWhiteSpace(toolTipKey) && target is UIElement element)
        {
            ToolTipService.SetToolTip(element, localizationService.T(toolTipKey));
        }
    }

    private static void SetObjectProperty(DependencyObject target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName);
        if (property?.CanWrite == true)
        {
            property.SetValue(target, value);
        }
    }
}
