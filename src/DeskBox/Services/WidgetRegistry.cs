using DeskBox.Models;

namespace DeskBox.Services;

public sealed record WidgetKindRegistration(
    WidgetKind WidgetKind,
    bool CanCreateWindow,
    bool IsImplemented);

/// <summary>
/// Central registry for widget kinds known to DeskBox.
/// It keeps future kinds persistable without making them creatable before their windows exist.
/// </summary>
public sealed class WidgetRegistry
{
    private readonly IReadOnlyDictionary<WidgetKind, WidgetKindRegistration> _registrations;

    public static WidgetRegistry Default { get; } = CreateDefault();

    public WidgetRegistry(IEnumerable<WidgetKindRegistration> registrations)
    {
        _registrations = registrations.ToDictionary(registration => registration.WidgetKind);
    }

    public bool IsKnown(WidgetKind widgetKind)
    {
        return _registrations.ContainsKey(widgetKind);
    }

    public bool CanCreateWindow(WidgetKind widgetKind)
    {
        return _registrations.TryGetValue(widgetKind, out var registration) &&
               registration.CanCreateWindow;
    }

    public bool IsImplemented(WidgetKind widgetKind)
    {
        return _registrations.TryGetValue(widgetKind, out var registration) &&
               registration.IsImplemented;
    }

    public bool IsAvailableForSession(WidgetConfig widget, AppSettings settings)
    {
        if (!CanCreateWindow(widget.WidgetKind))
        {
            return false;
        }

        return widget.WidgetKind != WidgetKind.QuickCapture || settings.QuickCaptureEnabled;
    }

    private static WidgetRegistry CreateDefault()
    {
        return new WidgetRegistry(
        [
            new(WidgetKind.File, CanCreateWindow: true, IsImplemented: true),
            new(WidgetKind.QuickCapture, CanCreateWindow: true, IsImplemented: true),
            new(WidgetKind.Weather, CanCreateWindow: false, IsImplemented: false),
            new(WidgetKind.Todo, CanCreateWindow: true, IsImplemented: true),
            new(WidgetKind.Tags, CanCreateWindow: false, IsImplemented: false),
            new(WidgetKind.Music, CanCreateWindow: false, IsImplemented: false),
            new(WidgetKind.SystemMonitor, CanCreateWindow: false, IsImplemented: false)
        ]);
    }
}
