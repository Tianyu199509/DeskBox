using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

/// <summary>
/// 将番茄钟状态机投影为格子界面所需的文本与进度。
/// 倒计时刷新只更新内存；仅状态转换或恢复修复会触发配置持久化。
/// </summary>
public sealed class PomodoroWidgetViewModel : ObservableObject, IDisposable
{
    private const string PlayGlyph = "\uE102";
    private const string PauseGlyph = "\uE769";

    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly DispatcherQueueTimer? _refreshTimer;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly PomodoroTimerStateMachine _stateMachine;
    private PomodoroTimerSnapshot _snapshot;
    private bool _isInitialized;
    private bool _isWindowVisible = true;
    private bool _isDisposed;

    public PomodoroWidgetViewModel(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        DispatcherQueue? dispatcherQueue = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(localizationService);
        if (config.WidgetKind != WidgetKind.Pomodoro)
        {
            throw new ArgumentException(
                "Pomodoro content requires a Pomodoro widget config.",
                nameof(config));
        }

        Config = config;
        _localizationService = localizationService;
        _settingsService = settingsService;
        // 测试线程或尚未初始化 WinUI 的后台线程可能没有可用的
        // DispatcherQueue；这种情况下仍允许使用同步状态机能力，
        // 只是不创建自动刷新计时器。
        _dispatcherQueue = dispatcherQueue ?? TryGetCurrentDispatcherQueue();
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);

        DateTimeOffset now = GetUtcNow();
        _stateMachine = new PomodoroTimerStateMachine(config, now);
        _snapshot = _stateMachine.GetSnapshot(now);

        if (_dispatcherQueue is { } queue)
        {
            _refreshTimer = queue.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(1);
            _refreshTimer.IsRepeating = true;
            _refreshTimer.Tick += RefreshTimer_Tick;
        }
        _localizationService.LanguageChanged += LocalizationService_LanguageChanged;

        if (_stateMachine.ShouldPersistRestore)
        {
            PersistConfig();
        }
    }

    public WidgetConfig Config { get; }

    public string CountdownText => FormatCountdown(_snapshot.Remaining);

    public string PhaseText => _localizationService.T(
        IsFocusPhase ? "Pomodoro.Phase.Focus" : "Pomodoro.Phase.Break");

    public string DescriptionText => _localizationService.T(
        IsFocusPhase
            ? "Pomodoro.Focus.Description"
            : "Pomodoro.Break.Description");

    public string RoundSummaryText => _localizationService.Format(
        "Pomodoro.RoundSummary",
        RoundNumber);

    public string PrimaryActionText => _localizationService.T(
        IsRunning ? "Pomodoro.Action.Pause" : "Pomodoro.Action.Start");

    public string PrimaryActionGlyph => IsRunning ? PauseGlyph : PlayGlyph;

    public string ResetActionText =>
        _localizationService.T("Pomodoro.Action.Reset");

    public string SkipActionText =>
        _localizationService.T("Pomodoro.Action.Skip");

    public bool IsRunning => _snapshot.IsRunning;

    public bool IsFocusPhase => _snapshot.Phase == PomodoroTimerPhase.Focus;

    public int CompletedFocusRounds => _snapshot.CompletedFocusRounds;

    public int CompletedRoundsInCycle
    {
        get
        {
            int remainder = Math.Max(0, CompletedFocusRounds) % 4;
            return !IsFocusPhase && CompletedFocusRounds > 0 && remainder == 0
                ? 4
                : remainder;
        }
    }

    public int RoundNumber
    {
        get
        {
            if (IsFocusPhase)
            {
                return CompletedFocusRounds % 4 + 1;
            }

            int completedInCycle = CompletedRoundsInCycle;
            return completedInCycle == 0 ? 1 : completedInCycle;
        }
    }

    public double Progress
    {
        get
        {
            TimeSpan duration = IsFocusPhase
                ? PomodoroTimerStateMachine.FocusDuration
                : PomodoroTimerStateMachine.BreakDuration;
            if (duration <= TimeSpan.Zero)
            {
                return 0;
            }

            return Math.Clamp(
                1 - _snapshot.Remaining.TotalMilliseconds /
                duration.TotalMilliseconds,
                0,
                1);
        }
    }

    /// <summary>自然完成一个阶段时触发，用于播放一次性完成反馈。</summary>
    public event EventHandler? CompletionOccurred;

    public Task InitializeAsync()
    {
        ThrowIfDisposed();
        _isInitialized = true;
        RefreshFromClock();
        UpdateRefreshTimer();
        return Task.CompletedTask;
    }

    public Task RefreshAsync()
    {
        ThrowIfDisposed();
        RefreshFromClock();
        return Task.CompletedTask;
    }

    public void StartPause()
    {
        RunOnUiThread(() =>
        {
            PomodoroTimerUpdate update = IsRunning
                ? _stateMachine.Pause(GetUtcNow())
                : _stateMachine.Start(GetUtcNow());
            ApplyUpdate(update);
        });
    }

    public void Reset()
    {
        RunOnUiThread(() => ApplyUpdate(_stateMachine.Reset(GetUtcNow())));
    }

    public void Skip()
    {
        RunOnUiThread(() => ApplyUpdate(_stateMachine.Skip(GetUtcNow())));
    }

    public void OnActivated()
    {
        if (!_isDisposed)
        {
            RefreshFromClock();
        }
    }

    public void OnDeactivated()
    {
        // 格子失去前台焦点后仍可能可见，因此继续刷新倒计时。
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_isDisposed)
        {
            return;
        }

        _isWindowVisible = visible;
        if (visible && _isInitialized)
        {
            RefreshFromClock();
        }

        UpdateRefreshTimer();
    }

    public void OnWindowRevealCompleted()
    {
        if (!_isDisposed)
        {
            RefreshFromClock();
        }
    }

    public void ApplyAppearance()
    {
        PublishPresentation();
    }

    private void RefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        RefreshFromClock();
    }

    private void RefreshFromClock()
    {
        if (_isDisposed)
        {
            return;
        }

        ApplyUpdate(_stateMachine.Tick(GetUtcNow()));
    }

    private void ApplyUpdate(PomodoroTimerUpdate update)
    {
        _snapshot = update.Snapshot;
        if (update.ShouldPersist)
        {
            PersistConfig();
        }

        PublishPresentation();
        UpdateRefreshTimer();
        if (update.Transition is PomodoroTimerTransition.FocusCompleted or
            PomodoroTimerTransition.BreakCompleted)
        {
            CompletionOccurred?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PersistConfig()
    {
        if (_settingsService is null)
        {
            return;
        }

        try
        {
            _settingsService.UpdateWidget(Config, notifySubscribers: false);
        }
        catch (Exception ex)
        {
            App.Log($"[PomodoroWidget] Failed to persist timer state: {ex}");
        }
    }

    private void UpdateRefreshTimer()
    {
        bool shouldRun = _isInitialized &&
            _isWindowVisible &&
            !_isDisposed &&
            IsRunning;
        if (shouldRun)
        {
            if (_refreshTimer is not null && !_refreshTimer.IsRunning)
            {
                _refreshTimer.Start();
            }
        }
        else if (_refreshTimer is not null && _refreshTimer.IsRunning)
        {
            _refreshTimer.Stop();
        }
    }

    private void LocalizationService_LanguageChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            PublishPresentation();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(PublishPresentation);
        }
    }

    private static DispatcherQueue? TryGetCurrentDispatcherQueue()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (Exception ex) when (
            ex is System.Runtime.InteropServices.COMException or
            InvalidOperationException)
        {
            // WinUI/COM 尚未在当前线程初始化时，探测 DispatcherQueue
            // 会抛异常；降级为无计时器模式即可保持状态操作可用。
            return null;
        }
    }

    private void PublishPresentation()
    {
        OnPropertyChanged(nameof(CountdownText));
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(RoundSummaryText));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryActionGlyph));
        OnPropertyChanged(nameof(ResetActionText));
        OnPropertyChanged(nameof(SkipActionText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFocusPhase));
        OnPropertyChanged(nameof(CompletedFocusRounds));
        OnPropertyChanged(nameof(CompletedRoundsInCycle));
        OnPropertyChanged(nameof(RoundNumber));
        OnPropertyChanged(nameof(Progress));
    }

    private void RunOnUiThread(Action action)
    {
        ThrowIfDisposed();
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                action();
            }
        });
    }

    private DateTimeOffset GetUtcNow() =>
        _utcNowProvider().ToUniversalTime();

    private static string FormatCountdown(TimeSpan remaining)
    {
        long totalSeconds = (long)Math.Ceiling(Math.Max(
            0,
            remaining.TotalMilliseconds) / 1000d);
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_refreshTimer is not null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;
        }
        _localizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        CompletionOccurred = null;
    }
}
