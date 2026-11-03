using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Views;

/// <summary>
/// Coordinates standalone Quick Capture note pop-out / sticky reference windows
/// and the Quick Jot floating capture window so each note reuses at most one
/// pop-out window and non-topmost windows participate in raised-band z-order.
/// </summary>
public sealed class QuickCaptureWindowCoordinator
{
    private readonly QuickCaptureService _quickCaptureService;
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly LocalizationService _localizationService;
    private readonly Dictionary<string, QuickCapturePopoutWindow> _popoutsByItemId = new(StringComparer.Ordinal);
    private QuickCapturePopoutWindow? _quickJotWindow;

    public QuickCaptureWindowCoordinator(
        QuickCaptureService quickCaptureService,
        SettingsService settingsService,
        ThemeService themeService,
        LocalizationService localizationService)
    {
        _quickCaptureService = quickCaptureService;
        _settingsService = settingsService;
        _themeService = themeService;
        _localizationService = localizationService;
    }

    public QuickCapturePopoutWindow OpenOrActivatePopout(
        string itemId,
        bool startAlwaysOnTop = false,
        bool startEditing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        if (_popoutsByItemId.TryGetValue(itemId, out QuickCapturePopoutWindow? existing) && !existing.IsClosed)
        {
            if (startAlwaysOnTop && !existing.IsAlwaysOnTop)
            {
                existing.SetAlwaysOnTop(true);
            }

            existing.ShowAndFocus();
            return existing;
        }

        var window = new QuickCapturePopoutWindow(
            itemId,
            startAlwaysOnTop,
            startEditing,
            _quickCaptureService,
            _settingsService,
            _themeService,
            _localizationService);
        _popoutsByItemId[itemId] = window;
        window.WindowClosed += (_, _) =>
        {
            if (_popoutsByItemId.TryGetValue(itemId, out QuickCapturePopoutWindow? current) &&
                ReferenceEquals(current, window))
            {
                _popoutsByItemId.Remove(itemId);
            }
        };

        window.ShowAndFocus();
        return window;
    }

    public QuickCapturePopoutWindow ShowQuickJot(IntPtr previousForegroundHwnd = default)
    {
        IntPtr prev = previousForegroundHwnd != IntPtr.Zero
            ? previousForegroundHwnd
            : Win32Helper.GetForegroundWindow();

        if (_quickJotWindow is { IsClosed: false } existing)
        {
            existing.ShowAndFocus(prev);
            return existing;
        }

        var window = new QuickCapturePopoutWindow(
            itemId: null,
            startAlwaysOnTop: true,
            startEditing: true,
            _quickCaptureService,
            _settingsService,
            _themeService,
            _localizationService,
            previousForegroundHwnd: prev);
        _quickJotWindow = window;
        window.WindowClosed += (_, _) =>
        {
            if (ReferenceEquals(_quickJotWindow, window))
            {
                _quickJotWindow = null;
            }
        };

        window.ShowAndFocus(prev);
        return window;
    }

    public void AppendNonTopmostHandles(List<IntPtr> handles)
    {
        foreach (QuickCapturePopoutWindow window in _popoutsByItemId.Values)
        {
            if (!window.IsClosed && !window.IsAlwaysOnTop && window.WindowHandle != IntPtr.Zero)
            {
                handles.Add(window.WindowHandle);
            }
        }
    }

    public void CloseAll()
    {
        QuickCapturePopoutWindow[] popouts = _popoutsByItemId.Values.ToArray();
        _popoutsByItemId.Clear();
        foreach (QuickCapturePopoutWindow window in popouts)
        {
            if (!window.IsClosed)
            {
                try
                {
                    window.Close();
                }
                catch
                {
                }
            }
        }

        if (_quickJotWindow is { IsClosed: false } jot)
        {
            _quickJotWindow = null;
            try
            {
                jot.Close();
            }
            catch
            {
            }
        }
    }
}
