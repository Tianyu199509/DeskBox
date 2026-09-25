using DeskBox.Contracts;
using DeskBox.Features.QuickCapture;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>One writer for Quick Capture enablement, recording, and presentation preferences.</summary>
public sealed class QuickCaptureSettingsCoordinator : IQuickCaptureSettings
{
    private static readonly TimeSpan RecentTrimQuietPeriod = TimeSpan.FromMilliseconds(350);

    private readonly SettingsService _settings;
    private readonly QuickCaptureClipboardRuntime _clipboard;
    private readonly Func<bool, bool, Task> _applyWidget;
    private readonly Func<Action, bool> _tryEnqueue;
    private readonly Action<Exception> _reportError;
    private readonly Func<int, CancellationToken, Task> _trimRecentItems;
    private readonly SemaphoreSlim _widgetGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _recentTrimLock = new();
    private Task _recentTrimTask = Task.CompletedTask;
    private int? _pendingRecentLimit;
    private int _recentTrimGeneration;
    private bool _recentTrimWorkerActive;
    private QuickCaptureSettingsSnapshot _last;
    private QuickCaptureTabSettings _lastTabs;
    private QuickCapturePresentationSettings _lastPresentation;
    private QuickCaptureTextSizeSettings _lastTextSizes;
    private int _lastRecentLimit;
    private int _requestGeneration;
    private bool _desiredEnabled;
    private bool _widgetOperationActive;
    private bool _hasReconciledClipboard;
    private bool _stopping;
    private Task? _stopTask;

    public QuickCaptureSettingsCoordinator(SettingsService settings,
        QuickCaptureClipboardRuntime clipboard, Func<bool, bool, Task> applyWidget,
        Func<Action, bool> tryEnqueue, Action<Exception> reportError,
        Func<int, CancellationToken, Task>? trimRecentItems = null)
    {
        _settings = settings;
        _clipboard = clipboard;
        _applyWidget = applyWidget;
        _tryEnqueue = tryEnqueue;
        _reportError = reportError;
        _trimRecentItems = trimRecentItems ?? ((_, _) => Task.CompletedTask);
        _last = Read();
        _lastTabs = ReadTabs();
        _lastPresentation = ReadPresentation();
        _lastTextSizes = ReadTextSizes();
        _lastRecentLimit = ReadRecentLimit();
        _desiredEnabled = _last.Enabled;
        _settings.SettingsChanged += OnSettingsChanged;
        _clipboard.DiagnosticsChanged += OnDiagnosticsChanged;
    }

    public event Action? Changed;
    public event Action? DiagnosticsChanged;
    public QuickCaptureClipboardDiagnostics? ClipboardDiagnostics => _clipboard.CurrentDiagnostics;
    internal Task PendingRecentTrim
    {
        get { lock (_recentTrimLock) return _recentTrimTask; }
    }

    public QuickCaptureSettingsSnapshot Read()
    {
        AppSettings settings = _settings.Settings;
        return new(
            FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture),
            settings.QuickCapture.QuickCaptureClipboardEnabled,
            settings.QuickCapture.QuickCaptureImageClipboardEnabled);
    }

    public QuickCaptureTabSettings ReadTabs()
    {
        AppSettings settings = _settings.Settings;
        QuickCaptureTabSettings tabs = ReadTabsCore(settings.QuickCapture);
        if (!tabs.ShowRecordsTab && !tabs.ShowPinnedTab && !tabs.ShowRecentTab)
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
            !quickCapture.QuickCaptureShowPinnedTab &&
            !quickCapture.QuickCaptureShowRecentTab)
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
        quickCapture.QuickCaptureShowRecentTab = true;
        PublishTabsIfChanged(before, scheduleSave);
    }

    private static QuickCaptureTabSettings ReadTabsCore(
        QuickCaptureSettingsSlice quickCapture) => new(
            quickCapture.QuickCaptureDefaultView,
            quickCapture.QuickCaptureShowTabBar,
            quickCapture.QuickCaptureShowRecordsTab,
            quickCapture.QuickCaptureShowPinnedTab,
            quickCapture.QuickCaptureShowRecentTab);

    private static void SetTabVisibleCore(
        QuickCaptureSettingsSlice quickCapture, string view, bool visible)
    {
        switch (view)
        {
            case SettingsService.QuickCaptureDefaultViewPinned:
                quickCapture.QuickCaptureShowPinnedTab = visible;
                break;
            case SettingsService.QuickCaptureDefaultViewRecent:
                quickCapture.QuickCaptureShowRecentTab = visible;
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

    public int ReadRecentLimit() => QuickCaptureService.NormalizeRecentLimit(
        _settings.Settings.QuickCapture.QuickCaptureRecentLimit);

    public void SetRecentLimit(int limit)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        int normalized = QuickCaptureService.NormalizeRecentLimit(limit);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        if (quickCapture.QuickCaptureRecentLimit == normalized) return;
        quickCapture.QuickCaptureRecentLimit = normalized;
        _lastRecentLimit = normalized;
        _settings.SaveDebounced();
        QueueRecentTrim(normalized);
        Changed?.Invoke();
    }

    public void ResetRecentLimit(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        bool changed = quickCapture.QuickCaptureRecentLimit !=
            QuickCaptureService.DefaultRecentLimit;
        quickCapture.QuickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
        _lastRecentLimit = QuickCaptureService.DefaultRecentLimit;
        if (scheduleSave) QueueRecentTrim(QuickCaptureService.DefaultRecentLimit);
        else CancelPendingRecentTrim();
        if (changed)
        {
            if (scheduleSave) _settings.SaveDebounced();
            Changed?.Invoke();
        }
    }

    private void QueueRecentTrim(int limit)
    {
        lock (_recentTrimLock)
        {
            if (_stopping) return;
            _pendingRecentLimit = limit;
            _recentTrimGeneration++;
            if (_recentTrimWorkerActive) return;
            _recentTrimWorkerActive = true;
            _recentTrimTask = ProcessRecentTrimsAsync();
        }
    }

    private void CancelPendingRecentTrim()
    {
        lock (_recentTrimLock)
        {
            _pendingRecentLimit = null;
            _recentTrimGeneration++;
        }
    }

    private async Task ProcessRecentTrimsAsync()
    {
        while (true)
        {
            int limit;
            int generation;
            lock (_recentTrimLock)
            {
                if (_stopping || _pendingRecentLimit is null)
                {
                    _recentTrimWorkerActive = false;
                    return;
                }
                limit = _pendingRecentLimit.Value;
                generation = _recentTrimGeneration;
            }

            // Wait for a quiet interval so rapid numeric edits cannot launch
            // an obsolete, destructive trim before the final value is known.
            try { await Task.Delay(RecentTrimQuietPeriod, _lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                lock (_recentTrimLock) _recentTrimWorkerActive = false;
                return;
            }

            lock (_recentTrimLock)
            {
                if (_stopping)
                {
                    _recentTrimWorkerActive = false;
                    return;
                }
                if (generation != _recentTrimGeneration) continue;
                _pendingRecentLimit = null;
            }

            try { await _trimRecentItems(limit, _lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                try { _reportError(ex); }
                catch { /* The worker must still drain on shutdown. */ }
            }
        }
    }

    public async Task SetEnabledAsync(bool enabled, bool reveal = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        cancellationToken.ThrowIfCancellationRequested();
        WriteEnabled(enabled);
        int generation = ++_requestGeneration;
        try { await ApplyWidgetAsync(generation, enabled, reveal, cancellationToken); }
        finally { if (!enabled) await _clipboard.DrainRetiredAsync(); }
    }

    public async Task SetClipboardEnabledAsync(bool enabled, bool captureCurrent = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        cancellationToken.ThrowIfCancellationRequested();
        bool enableWidget = enabled && !Read().Enabled;
        if (enableWidget) WriteEnabled(true);
        WriteRecording(enabled, enabled && Read().ImageEnabled, captureCurrent && enabled);
        Task widget = enableWidget
            ? ApplyWidgetAsync(++_requestGeneration, enabled: true, reveal: true, cancellationToken)
            : Task.CompletedTask;
        await SaveRecordingAsync(widget);
    }

    /// <summary>The open widget enables recent capture without revealing another window.</summary>
    public async Task EnableClipboardFromOpenWidgetAsync()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        QuickCaptureSettingsSnapshot before = Read();
        AppSettings settings = _settings.Settings;
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.QuickCapture, true);
        settings.QuickCapture.QuickCaptureClipboardEnabled = true;
        _desiredEnabled = true;
        ++_requestGeneration;
        QuickCaptureSettingsSnapshot current = Read();
        _last = current;

        // SettingsChanged may be delivered during SaveAsync. The explicit
        // refresh below must remain the first capture after persistence.
        bool hadReconciledClipboard = _hasReconciledClipboard;
        _hasReconciledClipboard = true;
        try { await _settings.SaveAsync(); }
        catch
        {
            _hasReconciledClipboard = hadReconciledClipboard;
            throw;
        }

        if (before != current) Changed?.Invoke();
        _clipboard.Refresh(captureCurrent: true);
    }

    public async Task SetImageEnabledAsync(bool enabled, bool captureCurrent = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        cancellationToken.ThrowIfCancellationRequested();
        bool enableWidget = enabled && !Read().Enabled;
        if (enableWidget) WriteEnabled(true);
        WriteRecording(enabled || Read().ClipboardEnabled, enabled, captureCurrent && enabled);
        Task widget = enableWidget
            ? ApplyWidgetAsync(++_requestGeneration, enabled: true, reveal: true, cancellationToken)
            : Task.CompletedTask;
        await SaveRecordingAsync(widget);
    }

    private async Task SaveRecordingAsync(Task widget)
    {
        try { await widget; }
        finally
        {
            try { await _settings.SaveAsync(); }
            finally { await _clipboard.DrainRetiredAsync(); }
        }
    }

    public async Task ResetRecordingAsync()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        WriteRecording(clipboardEnabled: false, imageEnabled: false, captureCurrent: false);
        await _clipboard.DrainRetiredAsync();
    }

    /// <summary>Widget creation and teardown call this instead of writing settings themselves.</summary>
    public void CommitEnabledState(bool enabled)
    {
        if (_stopping) return;
        // An older window operation may finish after a newer settings click.
        // Its window can be closed by the queued successor, but it must not
        // re-enable the listener or rewrite the newer persisted choice.
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
        PublishIfChanged(before, captureCurrent: false);
    }

    private void WriteRecording(bool clipboardEnabled, bool imageEnabled, bool captureCurrent)
    {
        QuickCaptureSettingsSnapshot before = Read();
        _settings.Settings.QuickCapture.QuickCaptureClipboardEnabled = clipboardEnabled;
        _settings.Settings.QuickCapture.QuickCaptureImageClipboardEnabled = clipboardEnabled && imageEnabled;
        PublishIfChanged(before, captureCurrent);
    }

    private void PublishIfChanged(QuickCaptureSettingsSnapshot before, bool captureCurrent)
    {
        QuickCaptureSettingsSnapshot current = Read();
        if (before != current)
        {
            _last = current;
            _settings.SaveDebounced();
            Changed?.Invoke();
        }
        if (before != current || captureCurrent || !_hasReconciledClipboard)
        {
            _clipboard.Refresh(captureCurrent);
            _hasReconciledClipboard = true;
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
        _tryEnqueue(() => RefreshFromSettings());
    }

    public void RefreshFromSettings(bool captureCurrent = false)
    {
        if (_stopping) return;
        QuickCaptureSettingsSnapshot current = Read();
        QuickCaptureTabSettings tabs = ReadTabs();
        QuickCapturePresentationSettings presentation = ReadPresentation();
        QuickCaptureTextSizeSettings textSizes = ReadTextSizes();
        int recentLimit = ReadRecentLimit();
        bool normalized = false;
        if (!current.Enabled && (current.ClipboardEnabled || current.ImageEnabled))
        {
            QuickCaptureSettingsSnapshot before = current;
            _settings.Settings.QuickCapture.QuickCaptureClipboardEnabled = false;
            _settings.Settings.QuickCapture.QuickCaptureImageClipboardEnabled = false;
            current = Read();
            if (before != current)
            {
                normalized = true;
                _settings.SaveDebounced();
            }
        }
        bool enabledChanged = current.Enabled != _last.Enabled;
        bool stateChanged = current != _last || normalized;
        bool tabsChanged = tabs != _lastTabs;
        bool presentationChanged = presentation != _lastPresentation;
        bool textSizesChanged = textSizes != _lastTextSizes;
        bool recentLimitChanged = recentLimit != _lastRecentLimit;
        bool refreshClipboard = stateChanged || captureCurrent || !_hasReconciledClipboard;
        if (!stateChanged && !tabsChanged && !presentationChanged &&
            !textSizesChanged &&
            !recentLimitChanged && !refreshClipboard) return;
        if (stateChanged || tabsChanged || presentationChanged ||
            textSizesChanged || recentLimitChanged)
        {
            _last = current;
            _lastTabs = tabs;
            _lastPresentation = presentation;
            _lastTextSizes = textSizes;
            _lastRecentLimit = recentLimit;
            _desiredEnabled = current.Enabled;
            if (recentLimitChanged) QueueRecentTrim(recentLimit);
            Changed?.Invoke();
        }
        if (refreshClipboard)
        {
            _clipboard.Refresh(captureCurrent);
            _hasReconciledClipboard = true;
        }
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

    private void OnDiagnosticsChanged() => DiagnosticsChanged?.Invoke();

    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        Task recentTrim;
        lock (_recentTrimLock)
        {
            _stopping = true;
            _pendingRecentLimit = null;
            recentTrim = _recentTrimTask;
        }
        _settings.SettingsChanged -= OnSettingsChanged;
        _clipboard.DiagnosticsChanged -= OnDiagnosticsChanged;
        _lifetime.Cancel();
        await recentTrim;
        await _clipboard.StopAsync();
        await _widgetGate.WaitAsync();
        _widgetGate.Release();
        _lifetime.Dispose();
        _widgetGate.Dispose();
    }
}
