using System.Globalization;
using System.Text;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Services;

internal sealed class ClipboardCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _clipboardTimer;
    private CustomWidgetClipboardSnapshot _clipboardSnapshot = CustomWidgetClipboardSnapshot.Empty;
    private int _clipboardRefreshInFlight;
    private uint _lastClipboardSequenceNumber;
    private bool _hasClipboardSequenceSample;
    private bool _isUsingClipboardStandIn;

    internal ClipboardCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "clipboard";
    public IReadOnlyList<string> CapabilityIds => ["clipboard"];

    internal CustomWidgetClipboardSnapshot Snapshot
    {
        get => _clipboardSnapshot;
        set => _clipboardSnapshot = value;
    }

    internal void ClearStandInIfActive()
    {
        if (_isUsingClipboardStandIn)
        {
            _clipboardSnapshot = CustomWidgetClipboardSnapshot.Empty;
            _isUsingClipboardStandIn = false;
        }
    }

    public void UpdateSubscription() => UpdateClipboardSourceSubscription();

    public void Stop()
    {
        if (_clipboardTimer is not null)
        {
            _clipboardTimer.Stop();
            _clipboardTimer.Tick -= OnClipboardTimerTick;
            _clipboardTimer = null;
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesClipboard() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "clipboard", StringComparison.OrdinalIgnoreCase)) ||
        CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "clipboard.");

    internal void UpdateClipboardSourceSubscription()
    {
        bool shouldRun = !_vm.IsDisposedInternal &&
                         _vm.IsWindowVisibleInternal &&
                         !_vm.IsStudioGalleryThumbnailPreview &&
                         (!_vm.IsCompactCollapsedInternal ||
                          CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "clipboard.") ||
                          (_vm.CompiledScriptInternal is not null &&
                           (CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "$computed.") ||
                            CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "state.")))) &&
                         ManifestUsesClipboard() &&
                         _vm.IsPermissionAllowed(CustomWidgetPermissionKind.SystemAndClipboard);

        if (!shouldRun)
        {
            if (_clipboardTimer is not null)
            {
                _clipboardTimer.Stop();
                _clipboardTimer.Tick -= OnClipboardTimerTick;
                _clipboardTimer = null;
            }
            if (!_vm.IsPermissionAllowed(CustomWidgetPermissionKind.SystemAndClipboard))
            {
                bool hadClipboardText = _clipboardSnapshot.HasText || !string.IsNullOrEmpty(_clipboardSnapshot.Text);
                _clipboardSnapshot = CustomWidgetClipboardSnapshot.Empty;
                _hasClipboardSequenceSample = false;
                if (hadClipboardText)
                {
                    _vm.RefreshCapsuleProjection(bumpLiveState: true);
                    _vm.NotifyViewRebuildRequested();
                }
            }
            return;
        }

        CustomWidgetSourceSpec? spec = _vm.ManifestInternal.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "clipboard", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 2000, 1000, 30_000);

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (_clipboardTimer is null)
            {
                _clipboardTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _clipboardTimer.IsRepeating = true;
                _clipboardTimer.Tick += OnClipboardTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_clipboardTimer.Interval != interval)
            {
                _clipboardTimer.Interval = interval;
            }
            if (!_clipboardTimer.IsRunning)
            {
                _clipboardTimer.Start();
            }
        }

        _ = RefreshClipboardNowAsync();
    }

    internal void OnClipboardTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_vm.IsDisposedInternal)
        {
            _ = RefreshClipboardNowAsync();
        }
    }

    internal async Task RefreshClipboardNowAsync()
    {
        if (_vm.IsDisposedInternal || !ManifestUsesClipboard() || !_vm.IsPermissionAllowed(CustomWidgetPermissionKind.SystemAndClipboard))
        {
            return;
        }

        uint currentSequence = _vm.ClipboardSequenceNumberProviderOverride?.Invoke()
            ?? ClipboardPlatformInterop.GetClipboardSequenceNumber();
        if (_hasClipboardSequenceSample && currentSequence == _lastClipboardSequenceNumber)
        {
            return;
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(() => _ = RefreshClipboardNowAsync());
            return;
        }

        if (Interlocked.CompareExchange(ref _clipboardRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            string? text = null;
            try
            {
                if (_vm.ClipboardTextReaderOverride is not null)
                {
                    text = await _vm.ClipboardTextReaderOverride();
                }
                else
                {
                    DataPackageView content = Clipboard.GetContent();
                    if (content.Contains(StandardDataFormats.Text))
                    {
                        text = await content.GetTextAsync();
                    }
                }
            }
            catch
            {
                if (!(_vm.IsPreviewOnlyInternal && !_vm.IsStudioGalleryThumbnailPreview && _vm.AllowPreviewHostStandIns && _vm.ClipboardTextReaderOverride is null))
                {
                    return;
                }
            }

            if (_vm.IsDisposedInternal)
            {
                return;
            }

            _lastClipboardSequenceNumber = currentSequence;
            _hasClipboardSequenceSample = true;

            bool usingStandIn = false;
            if (string.IsNullOrEmpty(text) &&
                _vm.IsPreviewOnlyInternal &&
                !_vm.IsStudioGalleryThumbnailPreview &&
                _vm.AllowPreviewHostStandIns &&
                _vm.ClipboardTextReaderOverride is null)
            {
                text = !_vm.ActiveCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "https://deskbox.fun — Sample clipboard snippet"
                    : LocalizationService.IsTraditionalChineseCulture(_vm.ActiveCultureName)
                        ? "https://deskbox.fun — 範例剪貼簿文字"
                        : "https://deskbox.fun — 示例剪贴板文本";
                usingStandIn = true;
            }
            _isUsingClipboardStandIn = usingStandIn;

            CustomWidgetClipboardSnapshot prev = _clipboardSnapshot;
            CustomWidgetClipboardSnapshot next = CustomWidgetClipboardSnapshot.FromText(text);
            if (!string.Equals(prev.Text, next.Text, StringComparison.Ordinal))
            {
                _clipboardSnapshot = next;
                _vm.TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
                _vm.RefreshCapsuleProjection(bumpLiveState: true);
                if (prev.HasText == next.HasText &&
                    !CapabilityProviderHelpers.ManifestWhenReferencesNonPresenceToken(_vm.ManifestInternal, "clipboard."))
                {
                    _vm.NotifyMetricsRefreshed();
                }
                else
                {
                    _vm.NotifyViewRebuildRequested();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _clipboardRefreshInFlight, 0);
        }
    }

}

internal sealed class MusicCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private const int MaxIdleEmptyMusicProbesBeforeSleep = 3;
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _musicTimer;
    private CustomWidgetMusicSnapshot _musicSnapshot = CustomWidgetMusicSnapshot.Empty;
    private int _musicRefreshInFlight;
    private int _musicRefreshPending;
    private int _musicRefreshPendingRebuild;
    private int _consecutiveEmptyMusicProbes;
    private IDisposable? _musicObserverSubscription;

    internal MusicCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "music";
    public IReadOnlyList<string> CapabilityIds => ["music"];

    internal CustomWidgetMusicSnapshot Snapshot => _musicSnapshot;

    public void UpdateSubscription() => UpdateMusicSourceSubscription();

    public void Stop()
    {
        _musicObserverSubscription?.Dispose();
        _musicObserverSubscription = null;
        _consecutiveEmptyMusicProbes = 0;
        if (_musicTimer is not null)
        {
            _musicTimer.Stop();
            _musicTimer.Tick -= OnMusicTimerTick;
            _musicTimer = null;
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesMusic() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "music", StringComparison.OrdinalIgnoreCase)) ||
        CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "music.");

    internal void UpdateMusicSourceSubscription()
    {
        bool shouldRun = !_vm.IsDisposedInternal &&
                         _vm.IsWindowVisibleInternal &&
                         !_vm.IsStudioGalleryThumbnailPreview &&
                         (!_vm.IsCompactCollapsedInternal || CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "music.")) &&
                         ManifestUsesMusic();

        if (!shouldRun)
        {
            if (_musicTimer is not null)
            {
                _musicTimer.Stop();
                _musicTimer.Tick -= OnMusicTimerTick;
                _musicTimer = null;
            }
            _musicObserverSubscription?.Dispose();
            _musicObserverSubscription = null;
            _consecutiveEmptyMusicProbes = 0;
            return;
        }

        _consecutiveEmptyMusicProbes = 0;
        if (_musicObserverSubscription is null && _vm.MusicSnapshotProviderOverride is null)
        {
            _musicObserverSubscription = CustomWidgetHostSnapshotService.SubscribeMusicChanged((_, _) =>
            {
                if (_vm.IsDisposedInternal)
                {
                    return;
                }

                void WakeAndRefresh()
                {
                    if (_vm.IsDisposedInternal)
                    {
                        return;
                    }

                    _consecutiveEmptyMusicProbes = 0;
                    if (_musicTimer is { IsRunning: false })
                    {
                        _musicTimer.Start();
                    }

                    _ = RefreshMusicNowAsync(triggerRebuild: true);
                }

                if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
                {
                    _vm.DispatcherQueueInternal.TryEnqueue(WakeAndRefresh);
                }
                else
                {
                    WakeAndRefresh();
                }
            });
        }

        CustomWidgetSourceSpec? spec = _vm.ManifestInternal.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "music", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 1000, 1000, 30_000);

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (_musicTimer is null)
            {
                _musicTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _musicTimer.IsRepeating = true;
                _musicTimer.Tick += OnMusicTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_musicTimer.Interval != interval)
            {
                _musicTimer.Interval = interval;
            }
            if (!_musicTimer.IsRunning)
            {
                _musicTimer.Start();
            }
        }

        _ = RefreshMusicNowAsync(triggerRebuild: true);
    }

    internal void OnMusicTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_vm.IsDisposedInternal && (_musicSnapshot.IsPlaying || !_musicSnapshot.HasSession))
        {
            _ = RefreshMusicNowAsync(triggerRebuild: _musicSnapshot.IsPlaying);
        }
    }

    internal async Task RefreshMusicNowAsync(bool triggerRebuild = true)
    {
        if (_vm.IsDisposedInternal || !ManifestUsesMusic())
        {
            return;
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(() => _ = RefreshMusicNowAsync(triggerRebuild));
            return;
        }

        if (triggerRebuild)
        {
            Interlocked.Exchange(ref _musicRefreshPendingRebuild, 1);
        }

        if (Interlocked.CompareExchange(ref _musicRefreshInFlight, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _musicRefreshPending, 1);
            return;
        }

        try
        {
            while (!_vm.IsDisposedInternal && ManifestUsesMusic())
            {
                Interlocked.Exchange(ref _musicRefreshPending, 0);
                bool effectiveRebuild = triggerRebuild || Interlocked.Exchange(ref _musicRefreshPendingRebuild, 0) != 0;
                triggerRebuild = false;

                CustomWidgetMusicSnapshot next = _vm.MusicSnapshotProviderOverride is not null
                    ? await _vm.MusicSnapshotProviderOverride(CancellationToken.None)
                    : await CustomWidgetHostSnapshotService.CaptureMusicSnapshotAsync();
                if (_vm.IsDisposedInternal || !ManifestUsesMusic())
                {
                    return;
                }

                bool trackOrStateChanged = false;
                bool timelineChanged = false;
                _vm.RunWithHeadlessStateGate(() =>
                {
                    trackOrStateChanged =
                        _musicSnapshot.HasSession != next.HasSession ||
                        _musicSnapshot.IsPlaying != next.IsPlaying ||
                        !string.Equals(_musicSnapshot.Status, next.Status, StringComparison.Ordinal) ||
                        !string.Equals(_musicSnapshot.Title, next.Title, StringComparison.Ordinal) ||
                        !string.Equals(_musicSnapshot.Artist, next.Artist, StringComparison.Ordinal) ||
                        !string.Equals(_musicSnapshot.CoverPath, next.CoverPath, StringComparison.Ordinal);
                    timelineChanged =
                        _musicSnapshot.PositionSeconds != next.PositionSeconds ||
                        _musicSnapshot.DurationSeconds != next.DurationSeconds ||
                        _musicSnapshot.ProgressPercent != next.ProgressPercent;

                    _musicSnapshot = next;

                    if (trackOrStateChanged &&
                        _vm.CompiledScriptInternal is not null &&
                        _vm.CompiledScriptInternal.ExportedFunctions.Contains("onSourceData"))
                    {
                        CustomWidgetSourceSpec? spec = _vm.ManifestInternal.Sources
                            .FirstOrDefault(static s => string.Equals(s.Type, "music", StringComparison.OrdinalIgnoreCase));
                        _vm.InvokeScriptCore(
                            functionName: "onSourceData",
                            arg: null,
                            eventValue: null,
                            currentItem: null,
                            dropFile: null,
                            sourceId: spec?.Id ?? "music",
                            rawSourcePayload: null,
                            timeoutMs: CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                            requireExport: false,
                            allowCopy: false,
                            out _,
                            out _,
                            out _,
                            out _,
                            callKind: CustomWidgetScriptCallKind.SourceData);
                    }
                });

                if (next.IsPlaying)
                {
                    _consecutiveEmptyMusicProbes = 0;
                    if (_musicTimer is { IsRunning: false })
                    {
                        _musicTimer.Start();
                    }
                }
                else if (next.HasSession)
                {
                    _consecutiveEmptyMusicProbes = 0;
                    if (_musicObserverSubscription is not null &&
                        CustomWidgetHostSnapshotService.IsSharedMusicObserverConnected &&
                        _musicTimer is { IsRunning: true })
                    {
                        _musicTimer.Stop();
                    }
                }
                else
                {
                    _consecutiveEmptyMusicProbes++;
                    if (_musicObserverSubscription is not null &&
                        CustomWidgetHostSnapshotService.IsSharedMusicObserverConnected &&
                        _consecutiveEmptyMusicProbes >= MaxIdleEmptyMusicProbesBeforeSleep &&
                        _musicTimer is { IsRunning: true })
                    {
                        _musicTimer.Stop();
                    }
                }

                if (effectiveRebuild)
                {
                    if (trackOrStateChanged)
                    {
                        _vm.RefreshCapsuleProjection(bumpLiveState: true);
                        _vm.NotifyViewRebuildRequested();
                    }
                    else if (timelineChanged)
                    {
                        _vm.RefreshCapsuleProjection(bumpLiveState: false);
                        _vm.NotifyMetricsRefreshed();
                    }
                }

                if (Interlocked.CompareExchange(ref _musicRefreshPending, 0, 1) == 0)
                {
                    break;
                }

                triggerRebuild = true;
            }
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _musicRefreshInFlight, 0);
            if (!_vm.IsDisposedInternal &&
                ManifestUsesMusic() &&
                Interlocked.CompareExchange(ref _musicRefreshPending, 0, 1) == 1)
            {
                _ = RefreshMusicNowAsync(triggerRebuild: true);
            }
        }
    }

}

internal sealed class TodoCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _todoTimer;
    private CustomWidgetTodoSnapshot _todoSnapshot = CustomWidgetTodoSnapshot.Empty;
    private int _todoRefreshInFlight;
    private int _todoRefreshPending;
    private int _todoRefreshPendingRebuild;
    private bool _isSubscribedToTodoSettingsChanged;
    private string _lastTodoSnapshotFingerprint = string.Empty;
    private IDisposable? _todoObserverSubscription;
    private bool _isUsingTodoStandIn;
    private bool _hasCapturedHostSnapshot;

    internal TodoCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "todo";
    public IReadOnlyList<string> CapabilityIds => ["todo"];

    internal CustomWidgetTodoSnapshot Snapshot => _todoSnapshot;
    internal bool HasLiveStore => !_vm.IsPreviewOnlyInternal || (_hasCapturedHostSnapshot && !_isUsingTodoStandIn);

    internal void ClearStandInIfActive()
    {
        if (_isUsingTodoStandIn)
        {
            _todoSnapshot = CustomWidgetTodoSnapshot.Empty;
            _isUsingTodoStandIn = false;
        }
    }

    public void UpdateSubscription() => UpdateTodoSourceSubscription();

    public void Stop()
    {
        _todoObserverSubscription?.Dispose();
        _todoObserverSubscription = null;
        if (_isSubscribedToTodoSettingsChanged && _vm.SettingsServiceInternal is not null)
        {
            _vm.SettingsServiceInternal.SettingsChanged -= OnHostSettingsChangedForTodo;
            _isSubscribedToTodoSettingsChanged = false;
        }
        if (_todoTimer is not null)
        {
            _todoTimer.Stop();
            _todoTimer.Tick -= OnTodoTimerTick;
            _todoTimer = null;
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesTodo() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "todo", StringComparison.OrdinalIgnoreCase)) ||
        CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "todo.");

    internal void OnHostSettingsChangedForTodo()
    {
        if (_vm.IsDisposedInternal ||
            _vm.SettingsServiceInternal?.LastNotifiedChangeKind == SettingsChangeKind.Appearance)
        {
            return;
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(() => _ = RefreshTodoNowAsync(triggerRebuild: true));
        }
        else
        {
            _ = RefreshTodoNowAsync(triggerRebuild: true);
        }
    }

    internal void UpdateTodoSourceSubscription()
    {
        bool shouldRun = !_vm.IsDisposedInternal &&
                         _vm.IsWindowVisibleInternal &&
                         !_vm.IsStudioGalleryThumbnailPreview &&
                         (!_vm.IsCompactCollapsedInternal || CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "todo.")) &&
                         ManifestUsesTodo();

        if (!shouldRun)
        {
            if (_todoTimer is not null)
            {
                _todoTimer.Stop();
                _todoTimer.Tick -= OnTodoTimerTick;
                _todoTimer = null;
            }
            _todoObserverSubscription?.Dispose();
            _todoObserverSubscription = null;
            if (_isSubscribedToTodoSettingsChanged && _vm.SettingsServiceInternal is not null)
            {
                _vm.SettingsServiceInternal.SettingsChanged -= OnHostSettingsChangedForTodo;
                _isSubscribedToTodoSettingsChanged = false;
            }
            if (_vm.IsStudioGalleryThumbnailPreview &&
                !_vm.IsDisposedInternal &&
                ManifestUsesTodo() &&
                _todoSnapshot.TotalCount == 0 &&
                _vm.TodoSnapshotProviderOverride is null)
            {
                CustomWidgetSourceSpec? previewSpec = _vm.ManifestInternal.Sources
                    .FirstOrDefault(static s => string.Equals(s.Type, "todo", StringComparison.OrdinalIgnoreCase));
                CustomWidgetTodoSnapshot standIn = BuildStudioPreviewTodoStandIn(_vm.ActiveCultureName);
                _isUsingTodoStandIn = true;
                if (ApplyTodoSnapshot(previewSpec, standIn, usingTodoStandIn: true))
                {
                    _vm.RefreshCapsuleProjection(bumpLiveState: false);
                }
            }
            return;
        }

        if (_todoObserverSubscription is null && _vm.TodoSnapshotProviderOverride is null)
        {
            _todoObserverSubscription = CustomWidgetHostSnapshotService.SubscribeTodoChanged(() =>
            {
                if (_vm.IsDisposedInternal)
                {
                    return;
                }

                if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
                {
                    _vm.DispatcherQueueInternal.TryEnqueue(() => _ = RefreshTodoNowAsync(triggerRebuild: true));
                }
                else
                {
                    _ = RefreshTodoNowAsync(triggerRebuild: true);
                }
            });
        }

        if (!_isSubscribedToTodoSettingsChanged &&
            _vm.SettingsServiceInternal is not null &&
            _vm.TodoSnapshotProviderOverride is null)
        {
            _vm.SettingsServiceInternal.SettingsChanged += OnHostSettingsChangedForTodo;
            _isSubscribedToTodoSettingsChanged = true;
        }

        CustomWidgetSourceSpec? spec = _vm.ManifestInternal.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "todo", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 30_000, 5000, 300_000);

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (_todoTimer is null)
            {
                _todoTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _todoTimer.IsRepeating = true;
                _todoTimer.Tick += OnTodoTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_todoTimer.Interval != interval)
            {
                _todoTimer.Interval = interval;
            }
            if (!_todoTimer.IsRunning)
            {
                _todoTimer.Start();
            }
        }

        _ = RefreshTodoNowAsync(triggerRebuild: true);
    }

    internal void OnTodoTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_vm.IsDisposedInternal)
        {
            _ = RefreshTodoNowAsync(triggerRebuild: true);
        }
    }

    internal async Task RefreshTodoNowAsync(bool triggerRebuild = true)
    {
        if (_vm.IsDisposedInternal || !ManifestUsesTodo())
        {
            return;
        }

        if (_vm.DispatcherQueueInternal is not null && !_vm.DispatcherQueueInternal.HasThreadAccess)
        {
            _vm.DispatcherQueueInternal.TryEnqueue(() => _ = RefreshTodoNowAsync(triggerRebuild));
            return;
        }

        if (triggerRebuild)
        {
            Interlocked.Exchange(ref _todoRefreshPendingRebuild, 1);
        }

        if (Interlocked.CompareExchange(ref _todoRefreshInFlight, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _todoRefreshPending, 1);
            return;
        }

        try
        {
            while (!_vm.IsDisposedInternal && ManifestUsesTodo())
            {
                Interlocked.Exchange(ref _todoRefreshPending, 0);
                bool effectiveRebuild = triggerRebuild || Interlocked.Exchange(ref _todoRefreshPendingRebuild, 0) != 0;
                triggerRebuild = false;

                CustomWidgetSourceSpec? spec = _vm.ManifestInternal.Sources
                    .FirstOrDefault(static s => string.Equals(s.Type, "todo", StringComparison.OrdinalIgnoreCase));
                int maxItems = Math.Clamp(spec?.MaxItems is > 0 ? spec.MaxItems : 20, 1, 200);

                CustomWidgetTodoSnapshot next = _vm.TodoSnapshotProviderOverride is not null
                    ? await _vm.TodoSnapshotProviderOverride(maxItems, CancellationToken.None)
                    : await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(_vm.SettingsServiceInternal, maxItems);
                if (_vm.IsDisposedInternal || !ManifestUsesTodo())
                {
                    return;
                }

                bool usingTodoStandIn = false;
                if (next.TotalCount == 0 &&
                    _vm.IsPreviewOnlyInternal &&
                    (_vm.AllowPreviewHostStandIns || _vm.IsStudioGalleryThumbnailPreview) &&
                    _vm.TodoSnapshotProviderOverride is null)
                {
                    next = BuildStudioPreviewTodoStandIn(_vm.ActiveCultureName);
                    usingTodoStandIn = true;
                }
                _isUsingTodoStandIn = usingTodoStandIn;
                _hasCapturedHostSnapshot = true;

                bool changed = ApplyTodoSnapshot(spec, next, usingTodoStandIn);

                if (effectiveRebuild && changed)
                {
                    _vm.RefreshCapsuleProjection(bumpLiveState: true);
                    _vm.NotifyViewRebuildRequested();
                }

                if (Interlocked.CompareExchange(ref _todoRefreshPending, 0, 1) == 0)
                {
                    break;
                }

                triggerRebuild = true;
            }
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _todoRefreshInFlight, 0);
            if (!_vm.IsDisposedInternal &&
                ManifestUsesTodo() &&
                Interlocked.CompareExchange(ref _todoRefreshPending, 0, 1) == 1)
            {
                _ = RefreshTodoNowAsync(triggerRebuild: true);
            }
        }
    }

    private bool ApplyTodoSnapshot(
        CustomWidgetSourceSpec? spec,
        CustomWidgetTodoSnapshot next,
        bool usingTodoStandIn)
    {
        string nextFingerprint = BuildTodoSnapshotFingerprint(next);
        bool changed = false;
        _vm.RunWithHeadlessStateGate(() =>
        {
            string targetCol = spec?.TargetCollection ?? "todos";
            if (string.IsNullOrWhiteSpace(targetCol))
            {
                targetCol = "todos";
            }

            changed =
                !string.Equals(_lastTodoSnapshotFingerprint, nextFingerprint, StringComparison.Ordinal) ||
                !_vm.CollectionsInternal.ContainsKey(targetCol) ||
                _vm.CollectionsInternal[targetCol].Count != next.Records.Count;

            _lastTodoSnapshotFingerprint = nextFingerprint;
            _todoSnapshot = next;
            bool preserveScriptPreviewSeed =
                usingTodoStandIn &&
                !_vm.IsStudioGalleryThumbnailPreview &&
                _vm.CollectionsInternal.TryGetValue(targetCol, out List<CustomWidgetRecord>? existingPreview) &&
                existingPreview.Count > 0;
            if (!preserveScriptPreviewSeed)
            {
                _vm.CollectionsInternal[targetCol] = new List<CustomWidgetRecord>(next.Records);
                if (usingTodoStandIn)
                {
                    _vm.SampleSeededCollectionsInternal.Add(targetCol);
                }
            }

            if (changed && _vm.CompiledScriptInternal is not null)
            {
                if (_vm.CompiledScriptInternal.ExportedFunctions.Contains("onSourceData"))
                {
                    _vm.InvokeScriptCore(
                        functionName: "onSourceData",
                        arg: null,
                        eventValue: null,
                        currentItem: null,
                        dropFile: null,
                        sourceId: spec?.Id ?? "todo",
                        rawSourcePayload: null,
                        timeoutMs: CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                        requireExport: false,
                        allowCopy: false,
                        out _,
                        out _,
                        out _,
                        out _,
                        callKind: CustomWidgetScriptCallKind.SourceData);
                }
                else
                {
                    _vm.TryRunScriptLifecycleHook(
                        "onInit",
                        CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs,
                        persistIfMutated: false);
                }
            }
        });
        return changed;
    }

    internal static CustomWidgetTodoSnapshot BuildStudioPreviewTodoStandIn(string cultureName)
    {
        bool isZh = !string.IsNullOrWhiteSpace(cultureName) &&
                    cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        DateTime localNow = DateTime.Now;
        string todayMmDd = localNow.ToString("MM-dd", CultureInfo.InvariantCulture);
        string tomorrowMmDd = localNow.AddDays(1).ToString("MM-dd", CultureInfo.InvariantCulture);
        string todayIso = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string tomorrowIso = localNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        static string ResolveStandInCopy(string culture, string key, string zhCnFallback, string zhTwFallback, string enFallback)
        {
            string translated = LocalizationService.TranslateForCulture(culture, key);
            if (!string.IsNullOrWhiteSpace(translated) && !string.Equals(translated, key, StringComparison.Ordinal))
            {
                return translated;
            }

            if (!string.IsNullOrWhiteSpace(culture) && culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return LocalizationService.IsTraditionalChineseCulture(culture) ? zhTwFallback : zhCnFallback;
            }

            return enFallback;
        }

        string[] titles =
        [
            ResolveStandInCopy(cultureName, "CustomWidget.PreviewStandIn.Todo1", "完成自定义小组件布局走查", "完成自訂小工具版面配置檢查", "Review custom widget layout"),
            ResolveStandInCopy(cultureName, "CustomWidget.PreviewStandIn.Todo2", "回复设计与技术评审意见", "回覆設計與技術評審意見", "Follow up on design feedback"),
            ResolveStandInCopy(cultureName, "CustomWidget.PreviewStandIn.Todo3", "整理本周迭代发布清单", "整理本週迭代發佈清單", "Organize weekly release notes")
        ];
        string listName = ResolveStandInCopy(cultureName, "CustomWidget.PreviewStandIn.TodoListName", "示例待办", "範例待辦", "Sample Todo");

        CustomWidgetRecord MakeTodo(string id, string title, bool done, bool important, string due, string dueIso)
        {
            string doneStr = done ? "true" : "false";
            string importantStr = important ? "true" : "false";
            return new CustomWidgetRecord
            {
                Id = id,
                Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["id"] = id,
                    ["title"] = title,
                    ["text"] = title,
                    ["done"] = doneStr,
                    ["isCompleted"] = doneStr,
                    ["important"] = importantStr,
                    ["isImportant"] = importantStr,
                    ["priority"] = important ? "high" : "normal",
                    ["color"] = important ? "#EF4444" : string.Empty,
                    ["colorName"] = important ? "red" : string.Empty,
                    ["dueDate"] = due,
                    ["dueIso"] = dueIso,
                    ["dueText"] = due,
                    ["widgetId"] = "sample-todo",
                    ["listName"] = listName,
                    ["isOverdue"] = "false",
                    ["stepsSummary"] = string.Empty
                }
            };
        }

        IReadOnlyList<CustomWidgetRecord> records =
        [
            MakeTodo("sample-todo-1", titles[0], done: false, important: true, due: $"{todayMmDd} 18:00", dueIso: todayIso),
            MakeTodo("sample-todo-2", titles[1], done: false, important: false, due: tomorrowMmDd, dueIso: tomorrowIso),
            MakeTodo("sample-todo-3", titles[2], done: true, important: false, due: todayMmDd, dueIso: todayIso)
        ];

        return new CustomWidgetTodoSnapshot(
            TotalCount: 3,
            PendingCount: 2,
            CompletedCount: 1,
            TodayPendingCount: 1,
            TodayCompletedCount: 1,
            OverdueCount: 0,
            ImportantPendingCount: 1,
            CompletionPercent: 33,
            NextTaskTitle: titles[0],
            NextTaskDueText: $"{todayMmDd} 18:00",
            Summary: "1/3 (33%)",
            Records: records);
    }

    internal static string BuildTodoSnapshotFingerprint(CustomWidgetTodoSnapshot snap)
    {
        var sb = new StringBuilder(280);
        sb.Append(DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
          .Append(snap.TotalCount).Append('|')
          .Append(snap.PendingCount).Append('|')
          .Append(snap.CompletedCount).Append('|')
          .Append(snap.CompletionPercent).Append('|')
          .Append(snap.TodayPendingCount).Append('|')
          .Append(snap.TodayCompletedCount).Append('|')
          .Append(snap.OverdueCount).Append('|')
          .Append(snap.ImportantPendingCount).Append('|')
          .Append(snap.NextTaskTitle).Append('|')
          .Append(snap.NextTaskDueText).Append('|')
          .Append(snap.Summary);
        for (int i = 0; i < snap.Records.Count; i++)
        {
            CustomWidgetRecord rec = snap.Records[i];
            sb.Append('\n').Append(rec.Id);
            AppendFingerprintField(sb, rec, "title");
            AppendFingerprintField(sb, rec, "isCompleted");
            AppendFingerprintField(sb, rec, "isImportant");
            AppendFingerprintField(sb, rec, "priority");
            AppendFingerprintField(sb, rec, "color");
            AppendFingerprintField(sb, rec, "dueText");
            AppendFingerprintField(sb, rec, "listName");
            AppendFingerprintField(sb, rec, "isOverdue");
            AppendFingerprintField(sb, rec, "stepsSummary");
        }

        return sb.ToString();
    }

    internal static void AppendFingerprintField(StringBuilder sb, CustomWidgetRecord rec, string key)
    {
        sb.Append('|');
        if (rec.Fields.TryGetValue(key, out string? val) && !string.IsNullOrEmpty(val))
        {
            sb.Append(val);
        }
    }

}
