using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Shared read-only diagnostics for widget host windows.
/// This does not own z-order, animation, DWM, or visibility decisions.
/// </summary>
public sealed class WidgetWindowDiagnostics
{
    private const double MinAnimationExtent = 1.0;
    private readonly WidgetConfig _config;
    private readonly Func<IntPtr> _windowHandleProvider;

    public WidgetWindowDiagnostics(string logKind, WidgetConfig config, Func<IntPtr> windowHandleProvider)
    {
        LogKind = logKind;
        _config = config;
        _windowHandleProvider = windowHandleProvider;
    }

    public string LogKind { get; }

    public string ShortWidgetId => ShortId(_config.Id);

    public WidgetWindowIdentity Identity => new(
        WidgetId: _config.Id,
        WidgetKind: _config.WidgetKind,
        Name: _config.Name,
        LogKind: LogKind,
        ShortWidgetId: ShortWidgetId,
        WindowHandle: _windowHandleProvider(),
        AnimationBounds: AnimationBounds);

    public Windows.Foundation.Rect AnimationBounds => new(
        _config.X,
        _config.Y,
        Math.Max(MinAnimationExtent, _config.Width),
        Math.Max(MinAnimationExtent, _config.Height));

    public string FormatTrayWindowMessage(string message)
    {
        return $"[TrayWindow] {LogKind} {_config.Name}#{ShortWidgetId} hwnd=0x{_windowHandleProvider().ToInt64():X} {message}";
    }

    internal static string ShortId(string id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? "none"
            : id.Length <= 8 ? id : id[..8];
    }
}

public sealed record WidgetWindowIdentity(
    string WidgetId,
    WidgetKind WidgetKind,
    string Name,
    string LogKind,
    string ShortWidgetId,
    IntPtr WindowHandle,
    Windows.Foundation.Rect AnimationBounds)
{
    public string DisplayName => $"{Name}#{ShortWidgetId}";

    public string LogDisplayName => $"{LogKind} {DisplayName}";
}
