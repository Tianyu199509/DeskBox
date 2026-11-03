using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>One writer for Quick Capture enablement and presentation preferences.</summary>
public sealed class QuickCaptureSettingsCoordinator : IQuickCaptureSettings
{
    private readonly SettingsService _settings;
    private readonly Func<bool, bool, Task> _applyWidget;
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Action<Exception> _reportError;
    private readonly SemaphoreSlim _widgetGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private QuickCaptureSettingsSnapshot _last;
    private QuickCaptureTabSettings _lastTabs;
    private QuickCapturePresentationSettings _lastPresentation;
    private QuickCaptureTextSizeSettings _lastTextSizes;
    private int _requestGeneration;
    private bool _desiredEnabled;
    private bool _widgetOperationActive;
    private bool _stopping;
    private Task? _stopTask;

    public QuickCaptureSettingsCoordinator(
        SettingsService settings,
        Func<bool, bool, Task> applyWidget,
        Func<Action, bool> tryEnqueue,
        Action<Exception> reportError)
    {
        _settings = settings;
        _applyWidget = applyWidget;
        _tryEnqueue = tryEnqueue;
        _reportError = reportError;
        _last = Read();
        _lastTabs = ReadTabs();
        _lastPresentation = ReadPresentation();
        _lastTextSizes = ReadTextSizes();
        _desiredEnabled = _last.Enabled;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public event Action? Changed;

    public QuickCaptureSettingsSnapshot Read()
    {
        AppSettings settings = _settings.Settings;
        return new(FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture));
    }

    public QuickCaptureTabSettings ReadTabs()
    {
        AppSettings settings = _settings.Settings;
        QuickCaptureTabSettings tabs = ReadTabsCore(settings.QuickCapture);
        if (!tabs.ShowRecordsTab && !tabs.ShowPinnedTab)
        {
            return tabs with
            {
                DefaultView = SettingsService.QuickCaptureDefaultViewRecords,
                ShowRecordsTab = true
            };
        }

        string defaultView = SettingsService.NormalizeQuickCaptureDefaultView(
            tabs.DefaultView);
        if (!SettingsService.IsQuickCaptureTabVisible(settings, defaultView))
            defaultView = SettingsService.GetFirstVisibleQuickCaptureTab(settings);
        return tabs with { DefaultView = defaultView };
    }

    public void SetDefaultView(string? view)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        QuickCaptureTabSettings before = ReadTabsCore(quickCapture);
        string normalized = SettingsService.NormalizeQuickCaptureDefaultView(view);
        SetTabVisibleCore(quickCapture, normalized, visible: true);
        quickCapture.QuickCaptureDefaultView = normalized;
        PublishTabsIfChanged(before);
    }

    public void SetTabVisible(string? view, bool visible)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        AppSettings settings = _settings.Settings;
        QuickCaptureSettingsSlice quickCapture = settings.QuickCapture;
        QuickCaptureTabSettings before = ReadTabsCore(quickCapture);
        SetTabVisibleCore(quickCapture,
            SettingsService.NormalizeQuickCaptureDefaultView(view), visible);
        if (!quickCapture.QuickCaptureShowRecordsTab &&
            !quickCapture.QuickCaptureShowPinnedTab)
        {
            quickCapture.QuickCaptureShowRecordsTab = true;
        }
        string defaultView = SettingsService.NormalizeQuickCaptureDefaultView(
            quickCapture.QuickCaptureDefaultView);
        if (!SettingsService.IsQuickCaptureTabVisible(settings, defaultView))
            defaultView = SettingsService.GetFirstVisibleQuickCaptureTab(settings);
        quickCapture.QuickCaptureDefaultView = defaultView;
        PublishTabsIfChanged(before);
    }

    public void SetTabBarVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        QuickCaptureTabSettings before = ReadTabsCore(quickCapture);
        quickCapture.QuickCaptureShowTabBar = visible;
        PublishTabsIfChanged(before);
    }

    public void ResetTabPreferences(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        QuickCaptureTabSettings before = ReadTabsCore(quickCapture);
        quickCapture.QuickCaptureDefaultView =
            SettingsService.QuickCaptureDefaultViewRecords;
        quickCapture.QuickCaptureShowTabBar = true;
        quickCapture.QuickCaptureShowRecordsTab = true;
        quickCapture.QuickCaptureShowPinnedTab = true;
        PublishTabsIfChanged(before, scheduleSave);
    }

    private static QuickCaptureTabSettings ReadTabsCore(
        QuickCaptureSettingsSlice quickCapture) => new(
            quickCapture.QuickCaptureDefaultView,
            quickCapture.QuickCaptureShowTabBar,
            quickCapture.QuickCaptureShowRecordsTab,
            quickCapture.QuickCaptureShowPinnedTab);

    private static void SetTabVisibleCore(
        QuickCaptureSettingsSlice quickCapture, string view, bool visible)
    {
        switch (view)
        {
            case SettingsService.QuickCaptureDefaultViewPinned:
                quickCapture.QuickCaptureShowPinnedTab = visible;
                break;
            default:
                quickCapture.QuickCaptureShowRecordsTab = visible;
                break;
        }
    }

    private void PublishTabsIfChanged(
        QuickCaptureTabSettings before, bool scheduleSave = true)
    {
        if (before == ReadTabsCore(_settings.Settings.QuickCapture)) return;
        _lastTabs = ReadTabs();
        if (scheduleSave) _settings.SaveDebounced();
        Changed?.Invoke();
    }

    public QuickCapturePresentationSettings ReadPresentation()
    {
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        return new(
            SettingsService.NormalizeWidgetTabStyle(quickCapture.QuickCaptureTabStyle),
            quickCapture.QuickCaptureShowCreatedTime,
            SettingsService.NormalizeItemPreviewLineCount(
                quickCapture.QuickCaptureItemPreviewLineCount));
    }

    public void SetTabStyle(string? style)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        QuickCapturePresentationSettings before = ReadPresentationCore(quickCapture);
        quickCapture.QuickCaptureTabStyle = SettingsService.NormalizeWidgetTabStyle(style);
        PublishPresentationIfChanged(before);
    }

    public void SetShowCreatedTime(bool visible)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        QuickCapturePresentationSettings before = ReadPresentationCore(quickCapture);
        quickCapture.QuickCaptureShowCreatedTime = visible;
        PublishPresentationIfChanged(before);
    }

    public void SetPreviewLineCount(int lineCount)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        QuickCapturePresentationSettings before = ReadPresentationCore(quickCapture);
        quickCapture.QuickCaptureItemPreviewLineCount =
            SettingsService.NormalizeItemPreviewLineCount(lineCount);
        PublishPresentationIfChanged(before);
    }

    public void ResetPresentationPreferences(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        QuickCapturePresentationSettings before = ReadPresentationCore(quickCapture);
        quickCapture.QuickCaptureTabStyle = SettingsService.WidgetTabStyleButton;
        quickCapture.QuickCaptureShowCreatedTime = true;
        quickCapture.QuickCaptureItemPreviewLineCount =
            SettingsService.DefaultQuickCaptureItemPreviewLineCount;
        PublishPresentationIfChanged(before, scheduleSave);
    }

    private static QuickCapturePresentationSettings ReadPresentationCore(
        QuickCaptureSettingsSlice quickCapture) => new(
            quickCapture.QuickCaptureTabStyle,
            quickCapture.QuickCaptureShowCreatedTime,
            quickCapture.QuickCaptureItemPreviewLineCount);

    private void PublishPresentationIfChanged(
        QuickCapturePresentationSettings before, bool scheduleSave = true)
    {
        if (before == ReadPresentationCore(_settings.Settings.QuickCapture)) return;
        _lastPresentation = ReadPresentation();
        if (scheduleSave) _settings.SaveDebounced();
        Changed?.Invoke();
    }

    public QuickCaptureTextSizeSettings ReadTextSizes()
    {
        AppSettings settings = _settings.Settings;
        QuickCaptureSettingsSlice quickCapture = settings.QuickCapture;
        return new(
            SettingsService.NormalizeTextSize(
                quickCapture.QuickCaptureListTextSize > 0
                    ? quickCapture.QuickCaptureListTextSize
                    : settings.WidgetShell.TextSize),
            SettingsService.NormalizeTextSize(
                quickCapture.QuickCaptureContentTextSize > 0
                    ? quickCapture.QuickCaptureContentTextSize
                    : settings.WidgetShell.TextSize));
    }

    public bool TrySetListTextSize(double size, bool scheduleSave = true) =>
        TrySetTextSize(size, list: true, scheduleSave: scheduleSave);

    public bool TrySetContentTextSize(double size, bool scheduleSave = true) =>
        TrySetTextSize(size, list: false, scheduleSave: scheduleSave);

    private bool TrySetTextSize(double size, bool list, bool scheduleSave)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (!double.IsFinite(size)) return false;

        double normalized = Math.Clamp(
            Math.Round(size * 2d, MidpointRounding.AwayFromZero) / 2d,
            SettingsService.MinTextSize,
            SettingsService.MaxTextSize);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        double previous = list
            ? quickCapture.QuickCaptureListTextSize
            : quickCapture.QuickCaptureContentTextSize;
        if (Math.Abs(previous - normalized) <= 0.0001) return false;

        if (list)
            quickCapture.QuickCaptureListTextSize = normalized;
        else
            quickCapture.QuickCaptureContentTextSize = normalized;
        _lastTextSizes = ReadTextSizes();
        if (scheduleSave)
        {
            _settings.RequestAppearancePreview();
            _settings.SaveDebounced(changeKind: SettingsChangeKind.Appearance);
        }
        Changed?.Invoke();
        return true;
    }

    public QuickCaptureEditorSettings ReadEditorSettings()
    {
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        return new(
            SettingsService.NormalizeEditorEnterBehavior(
                quickCapture.QuickCaptureEditorEnterBehavior),
            SettingsService.NormalizeQuickCaptureFormat(
                quickCapture.QuickCaptureDefaultFormat),
            SettingsService.NormalizeQuickCaptureWideLayout(
                quickCapture.QuickCaptureWideLayout),
            SettingsService.NormalizeQuickCaptureWideOpenMode(
                quickCapture.QuickCaptureWideOpenMode),
            quickCapture.QuickCaptureAllowRemoteImages);
    }

    public void SetEditorEnterBehavior(string? behavior)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        string normalized = SettingsService.NormalizeEditorEnterBehavior(behavior);
        if (string.Equals(
                quickCapture.QuickCaptureEditorEnterBehavior,
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        quickCapture.QuickCaptureEditorEnterBehavior = normalized;
        _settings.SaveDebounced();
    }

    public void SetEditorFormat(string? format)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        string normalized = SettingsService.NormalizeQuickCaptureFormat(format);
        if (string.Equals(
                quickCapture.QuickCaptureDefaultFormat,
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        quickCapture.QuickCaptureDefaultFormat = normalized;
        _settings.SaveDebounced();
    }

    public void SetWideLayout(string? layout)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        string normalized = SettingsService.NormalizeQuickCaptureWideLayout(layout);
        if (string.Equals(
                quickCapture.QuickCaptureWideLayout,
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        quickCapture.QuickCaptureWideLayout = normalized;
        _settings.SaveDebounced();
    }

    public void SetWideOpenMode(string? mode)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        string normalized = SettingsService.NormalizeQuickCaptureWideOpenMode(mode);
        if (string.Equals(
                quickCapture.QuickCaptureWideOpenMode,
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        quickCapture.QuickCaptureWideOpenMode = normalized;
        _settings.SaveDebounced();
    }

    public void SetAllowRemoteImages(bool allowed)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        if (quickCapture.QuickCaptureAllowRemoteImages == allowed)
        {
            return;
        }

        quickCapture.QuickCaptureAllowRemoteImages = allowed;
        _settings.SaveDebounced();
    }

    public void ResetEditorPreferences(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        bool changed =
            !string.Equals(
                quickCapture.QuickCaptureEditorEnterBehavior,
                SettingsService.EditorEnterBehaviorCtrlEnterSaves,
                StringComparison.Ordinal) ||
            !string.Equals(
                quickCapture.QuickCaptureDefaultFormat,
                SettingsService.QuickCaptureFormatMarkdown,
                StringComparison.Ordinal) ||
            !string.Equals(
                quickCapture.QuickCaptureWideLayout,
                SettingsService.QuickCaptureWideLayoutAuto,
                StringComparison.Ordinal) ||
            !string.Equals(
                quickCapture.QuickCaptureWideOpenMode,
                SettingsService.QuickCaptureWideOpenReading,
                StringComparison.Ordinal) ||
            quickCapture.QuickCaptureAllowRemoteImages != false ||
            quickCapture.LastQuickCaptureFileWidgetId.Length != 0;
        quickCapture.QuickCaptureEditorEnterBehavior =
            SettingsService.EditorEnterBehaviorCtrlEnterSaves;
        quickCapture.QuickCaptureDefaultFormat =
            SettingsService.QuickCaptureFormatMarkdown;
        quickCapture.QuickCaptureWideLayout =
            SettingsService.QuickCaptureWideLayoutAuto;
        quickCapture.QuickCaptureWideOpenMode =
            SettingsService.QuickCaptureWideOpenReading;
        quickCapture.QuickCaptureAllowRemoteImages = false;
        quickCapture.LastQuickCaptureFileWidgetId = string.Empty;
        if (scheduleSave && changed)
        {
            _settings.SaveDebounced();
        }
    }

    public async Task SetEnabledAsync(bool enabled, bool reveal = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        cancellationToken.ThrowIfCancellationRequested();
        WriteEnabled(enabled);
        int generation = ++_requestGeneration;
        await ApplyWidgetAsync(generation, enabled, reveal, cancellationToken);
    }

    /// <summary>Widget creation and teardown call this instead of writing settings themselves.</summary>
    public void CommitEnabledState(bool enabled)
    {
        if (_stopping) return;
        // An older window operation may finish after a newer settings click.
        // Its window can be closed by the queued successor, but it must not
        // rewrite the newer persisted choice.
        if (_widgetOperationActive && enabled != _desiredEnabled) return;
        WriteEnabled(enabled);
    }

    private void WriteEnabled(bool enabled)
    {
        AppSettings settings = _settings.Settings;
        QuickCaptureSettingsSnapshot before = Read();
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.QuickCapture, enabled);
        if (!enabled)
        {
            settings.QuickCapture.QuickCaptureClipboardEnabled = false;
            settings.QuickCapture.QuickCaptureImageClipboardEnabled = false;
        }
        _desiredEnabled = enabled;
        PublishIfChanged(before);
    }

    private void PublishIfChanged(QuickCaptureSettingsSnapshot before)
    {
        QuickCaptureSettingsSnapshot current = Read();
        if (before != current)
        {
            _last = current;
            _settings.SaveDebounced();
            Changed?.Invoke();
        }
    }

    private async Task ApplyWidgetAsync(int generation, bool enabled, bool reveal,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        await _widgetGate.WaitAsync(linked.Token);
        try
        {
            if (generation != _requestGeneration || _stopping) return;
            _widgetOperationActive = true;
            await _applyWidget(enabled, reveal);
        }
        finally
        {
            _widgetOperationActive = false;
            _widgetGate.Release();
        }
    }

    private void OnSettingsChanged()
    {
        if (_stopping) return;
        _tryEnqueue(RefreshFromSettings);
    }

    public void RefreshFromSettings()
    {
        if (_stopping) return;
        QuickCaptureSettingsSnapshot current = Read();
        QuickCaptureTabSettings tabs = ReadTabs();
        QuickCapturePresentationSettings presentation = ReadPresentation();
        QuickCaptureTextSizeSettings textSizes = ReadTextSizes();
        bool normalized = false;
        if (_settings.Settings.QuickCapture.QuickCaptureClipboardEnabled ||
            _settings.Settings.QuickCapture.QuickCaptureImageClipboardEnabled)
        {
            _settings.Settings.QuickCapture.QuickCaptureClipboardEnabled = false;
            _settings.Settings.QuickCapture.QuickCaptureImageClipboardEnabled = false;
            normalized = true;
            _settings.SaveDebounced();
        }
        bool enabledChanged = current.Enabled != _last.Enabled;
        bool stateChanged = current != _last || normalized;
        bool tabsChanged = tabs != _lastTabs;
        bool presentationChanged = presentation != _lastPresentation;
        bool textSizesChanged = textSizes != _lastTextSizes;
        if (!stateChanged && !tabsChanged && !presentationChanged && !textSizesChanged) return;
        _last = current;
        _lastTabs = tabs;
        _lastPresentation = presentation;
        _lastTextSizes = textSizes;
        _desiredEnabled = current.Enabled;
        Changed?.Invoke();
        if (enabledChanged)
        {
            int generation = ++_requestGeneration;
            _ = ApplyExternalWidgetChangeAsync(generation, current.Enabled);
        }
    }

    private async Task ApplyExternalWidgetChangeAsync(int generation, bool enabled)
    {
        try { await ApplyWidgetAsync(generation, enabled, reveal: enabled, CancellationToken.None); }
        catch (OperationCanceledException) when (_stopping) { }
        catch (Exception ex) { _reportError(ex); }
    }

    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _stopping = true;
        _settings.SettingsChanged -= OnSettingsChanged;
        _lifetime.Cancel();
        await _widgetGate.WaitAsync();
        _widgetGate.Release();
        _lifetime.Dispose();
        _widgetGate.Dispose();
    }
}