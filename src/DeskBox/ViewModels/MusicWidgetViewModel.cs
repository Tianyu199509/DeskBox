using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace DeskBox.ViewModels;

public sealed partial class MusicWidgetViewModel : ObservableObject, IDisposable
{
    private const int MaxTransientEmptyInfoRetries = 4;
    private const int ExpandedProgressRefreshMs = 500;
    private const int CompactProgressRefreshMs = 1000;
    private const int MediaPropertiesSettleDelayMs = 180;
    private static readonly TimeSpan RevealFullRefreshFreshness =
        TimeSpan.FromSeconds(30);

    private readonly MusicSessionService _musicSessionService;
    private readonly MusicVolumeService _musicVolumeService;
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly WidgetConfig _config;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _progressTimer;

    private string? _preferredSessionId;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
    private string _sourceAppUserModelId = string.Empty;
    private string _sourceDisplayName = string.Empty;
    private MusicPlaybackState _playbackState = MusicPlaybackState.Unknown;
    private TimeSpan _position;
    private TimeSpan _duration;
    private BitmapImage? _thumbnailImage;
    private bool _canPlay;
    private bool _canPause;
    private bool _canGoPrevious;
    private bool _canGoNext;
    private bool _canSeek;
    private bool _canChangeShuffle;
    private bool _canChangeRepeat;
    private bool _isRefreshing;
    private bool _hasCurrentMediaInfo;
    private int _hiddenMediaStateDirty;
    private long _lastFullRefreshCompletedUtcTicks;
    private int _timelineRefreshGeneration;
    private int _playbackRefreshGeneration;
    private int _coverGeneration;
    private string? _lastCoverSignature;
    private string? _coverRetrySignature;
    private int _coverRetryCount;
    private int _transientEmptyInfoRetryCount;
    private int _transientEmptyInfoGeneration;
    private bool _fullRefreshPending;
    private bool _isWindowVisible;
    private bool _isWindowRevealCompleted;
    private bool _isCompactCollapsed;
    private bool _isSeeking;
    private bool _isChangingPlaybackMode;
    private bool _isChangingSystemVolume;
    private bool _isChangingSessionVolume;
    private bool _isRefreshingVolume;
    private double? _pendingSystemVolume;
    private double? _pendingSessionVolume;
    private bool _isDisposed;
    private DateTimeOffset _lastPositionSyncAt;
    private TimeSpan _lastSyncedPosition;
    private double _textSize = SettingsService.DefaultTextSize;
    private double _seekValue;
    private double _systemVolume = 0.5;
    private double _sessionVolume = 0.5;
    private bool _hasSessionVolume;
    private bool _useArtworkBackdrop = true;
    private bool _enableCoverHoverMotion = true;
    private string _displayMode = SettingsService.MusicDisplayModeAuto;
    private Color _artworkColor = AccentColorHelper.DefaultAccentColor;
    private bool _hasArtworkColor;
    private MusicPlaybackMode _playbackMode = MusicPlaybackMode.Normal;

    public MusicWidgetViewModel(
        WidgetConfig config,
        MusicSessionService musicSessionService,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = null,
        MusicVolumeService? musicVolumeService = null)
    {
        _config = config;
        _musicSessionService = musicSessionService;
        _musicVolumeService = musicVolumeService ?? new MusicVolumeService();
        _localizationService = localizationService;
        _settingsService = settingsService;
        _dispatcherQueue = dispatcherQueue ?? TryGetCurrentDispatcherQueue();

        if (_settingsService is not null)
        {
            _textSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
            ApplyMusicSettings(_settingsService.Settings);
            _settingsService.SettingsChanged += OnSettingsChanged;
        }

        _localizationService.LanguageChanged += OnLanguageChanged;

        if (_dispatcherQueue is not null)
        {
            _progressTimer = _dispatcherQueue.CreateTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(
                ResolveProgressRefreshIntervalMs(isCompactCollapsed: false));
            _progressTimer.IsRepeating = true;
            _progressTimer.Tick += ProgressTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }

        AttachServiceEvents();
    }

    private static Microsoft.UI.Dispatching.DispatcherQueue? TryGetCurrentDispatcherQueue()
    {
        try
        {
            return Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    internal static int ResolveProgressRefreshIntervalMs(bool isCompactCollapsed) =>
        isCompactCollapsed ? CompactProgressRefreshMs : ExpandedProgressRefreshMs;

    internal static bool ShouldRunProgressTimer(
        MusicPlaybackState playbackState,
        bool hasCurrentMediaInfo) =>
        hasCurrentMediaInfo &&
        playbackState is (MusicPlaybackState.Playing or MusicPlaybackState.Unknown);

    public ObservableCollection<string> SessionDisplayNames { get; } = [];

    public ObservableCollection<string> SessionIds { get; } = [];

    public bool IsFollowingSystemSession => string.IsNullOrWhiteSpace(_preferredSessionId);

    public string? PreferredSessionId => _preferredSessionId;

    public string DisplayName => _config.IsDefaultTitle
        ? _localizationService.T("Music.Title")
        : _config.Name;

    public double WidgetX => _config.X;

    public double WidgetY => _config.Y;

    public string Title
    {
        get => string.IsNullOrWhiteSpace(_title) ? _localizationService.T("Music.EmptyTitle") : _title;
        private set => SetProperty(ref _title, value);
    }

    public string Artist
    {
        get => string.IsNullOrWhiteSpace(_artist) ? SourceDisplayName : _artist;
        private set => SetProperty(ref _artist, value);
    }

    public string Album
    {
        get => _album;
        private set => SetProperty(ref _album, value);
    }

    public string SourceDisplayName
    {
        get => string.IsNullOrWhiteSpace(_sourceDisplayName) ? _localizationService.T("Music.SourceUnknown") : _sourceDisplayName;
        private set => SetProperty(ref _sourceDisplayName, value);
    }

    public bool UseArtworkBackdrop
    {
        get => _useArtworkBackdrop;
        private set
        {
            if (SetProperty(ref _useArtworkBackdrop, value))
            {
                OnPropertyChanged(nameof(ArtworkBackdropVisibility));
                RaiseMusicAccentPropertiesChanged();
            }
        }
    }

    public bool EnableCoverHoverMotion
    {
        get => _enableCoverHoverMotion;
        private set => SetProperty(ref _enableCoverHoverMotion, value);
    }

    public string DisplayMode
    {
        get => _displayMode;
        private set => SetProperty(ref _displayMode, SettingsService.NormalizeMusicDisplayMode(value));
    }

    public MusicPlaybackState PlaybackState
    {
        get => _playbackState;
        private set
        {
            if (SetProperty(ref _playbackState, value))
            {
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(PlayPauseGlyph));
                OnPropertyChanged(nameof(PlayIconVisibility));
                OnPropertyChanged(nameof(PauseIconVisibility));
                OnPropertyChanged(nameof(PlayPauseTooltip));
                OnPropertyChanged(nameof(StatusText));

                // Start or stop the progress timer based on playback state.
                // Also start for Unknown when a current media snapshot exists — many
                // players report "Changing" (buffering) at song start, which maps to
                // Unknown. A missing session also maps to Unknown and must stay idle.
                UpdateProgressTimerState();
            }
        }
    }

    public TimeSpan Position
    {
        get => _position;
        private set
        {
            if (SetProperty(ref _position, value))
            {
                if (!_isSeeking)
                {
                    SeekValue = Math.Clamp(value.TotalSeconds, 0, SeekMaximum);
                }

                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    public TimeSpan Duration
    {
        get => _duration;
        private set
        {
            if (SetProperty(ref _duration, value))
            {
                OnPropertyChanged(nameof(SeekMaximum));
                OnPropertyChanged(nameof(HasSeekableTimeline));
                OnPropertyChanged(nameof(CanInteractWithProgress));
                OnPropertyChanged(nameof(DurationText));
                if (!_isSeeking)
                {
                    SeekValue = Math.Clamp(Position.TotalSeconds, 0, SeekMaximum);
                }
            }
        }
    }

    public BitmapImage? ThumbnailImage
    {
        get => _thumbnailImage;
        private set
        {
            if (SetProperty(ref _thumbnailImage, value))
            {
                OnPropertyChanged(nameof(ThumbnailVisibility));
                OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
            }
        }
    }

    public double SeekValue
    {
        get => _seekValue;
        set => SetProperty(ref _seekValue, value);
    }

    public double SeekMaximum => Math.Max(1, Duration.TotalSeconds);

    public double TextSize
    {
        get => _textSize;
        private set
        {
            if (SetProperty(ref _textSize, value))
            {
                OnPropertyChanged(nameof(TitleTextSize));
                OnPropertyChanged(nameof(MinimalTitleTextSize));
                OnPropertyChanged(nameof(SecondaryTextSize));
                OnPropertyChanged(nameof(CaptionTextSize));
            }
        }
    }

    public double TitleTextSize => Math.Min(SettingsService.MaxTextSize + 3, TextSize + 4);

    public double MinimalTitleTextSize => Math.Min(SettingsService.MaxTextSize + 1, TextSize + 1.5);

    public double SecondaryTextSize => Math.Max(SettingsService.MinTextSize, TextSize - 1);

    public double CaptionTextSize => Math.Max(SettingsService.MinTextSize - 1, TextSize - 2);

    public bool IsPlaying => PlaybackState == MusicPlaybackState.Playing;

    public bool HasSession => !string.IsNullOrWhiteSpace(_sourceDisplayName) ||
                              !string.IsNullOrWhiteSpace(_title) ||
                              PlaybackState is MusicPlaybackState.Playing or MusicPlaybackState.Paused;

    public bool CanPlayPause => CanPlay || CanPause;

    public bool CanPlay
    {
        get => _canPlay;
        private set
        {
            if (SetProperty(ref _canPlay, value))
            {
                OnPropertyChanged(nameof(CanPlayPause));
            }
        }
    }

    public bool CanPause
    {
        get => _canPause;
        private set
        {
            if (SetProperty(ref _canPause, value))
            {
                OnPropertyChanged(nameof(CanPlayPause));
            }
        }
    }

    public bool CanGoPrevious
    {
        get => _canGoPrevious;
        private set => SetProperty(ref _canGoPrevious, value);
    }

    public bool CanGoNext
    {
        get => _canGoNext;
        private set => SetProperty(ref _canGoNext, value);
    }

    public bool CanSeek
    {
        get => _canSeek;
        private set
        {
            if (SetProperty(ref _canSeek, value))
            {
                OnPropertyChanged(nameof(HasSeekableTimeline));
                OnPropertyChanged(nameof(CanInteractWithProgress));
            }
        }
    }

    public bool CanChangeShuffle
    {
        get => _canChangeShuffle;
        private set
        {
            if (SetProperty(ref _canChangeShuffle, value))
            {
                OnPropertyChanged(nameof(CanChangePlaybackMode));
            }
        }
    }

    public bool CanChangeRepeat
    {
        get => _canChangeRepeat;
        private set
        {
            if (SetProperty(ref _canChangeRepeat, value))
            {
                OnPropertyChanged(nameof(CanChangePlaybackMode));
            }
        }
    }

    public bool CanChangePlaybackMode => CanChangeShuffle || CanChangeRepeat;

    public MusicPlaybackMode PlaybackMode
    {
        get => _playbackMode;
        private set
        {
            if (SetProperty(ref _playbackMode, value))
            {
                OnPropertyChanged(nameof(PlaybackModeGlyph));
                OnPropertyChanged(nameof(PlaybackModeTooltip));
                OnPropertyChanged(nameof(PlaybackModeOpacity));
            }
        }
    }

    public bool HasSeekableTimeline => Duration > TimeSpan.Zero && SeekMaximum > 1;

    public bool CanInteractWithProgress => CanSeek && HasSeekableTimeline;

    public Visibility ThumbnailVisibility => ThumbnailImage is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ThumbnailPlaceholderVisibility => ThumbnailImage is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SessionPickerVisibility => SessionIds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ArtworkBackdropVisibility => UseArtworkBackdrop ? Visibility.Visible : Visibility.Collapsed;

    public CornerRadius ArtworkBackdropCornerRadius => new(GetArtworkBackdropCornerRadius());

    public Color ArtworkBackdropStartColor => Color.FromArgb(0x5A, _artworkColor.R, _artworkColor.G, _artworkColor.B);

    public Color ArtworkBackdropMidColor => Color.FromArgb(0x20, _artworkColor.R, _artworkColor.G, _artworkColor.B);

    public Color ArtworkBackdropEndColor => Color.FromArgb(0x00, _artworkColor.R, _artworkColor.G, _artworkColor.B);

    public SolidColorBrush MusicAccentBrush => new(GetMusicAccentColor());

    public SolidColorBrush PlayPauseButtonBackgroundBrush => new(GetMusicAccentColor());

    public string StatusText => PlaybackState switch
    {
        MusicPlaybackState.Playing => _localizationService.T("Music.Status.Playing"),
        MusicPlaybackState.Paused => _localizationService.T("Music.Status.Paused"),
        MusicPlaybackState.Stopped => _localizationService.T("Music.Status.Stopped"),
        _ => HasSession ? _localizationService.T("Music.Status.Ready") : _localizationService.T("Music.Status.NoSession")
    };

    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE102";

    public Visibility PlayIconVisibility => IsPlaying ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PauseIconVisibility => IsPlaying ? Visibility.Visible : Visibility.Collapsed;

    public string PlayPauseTooltip => IsPlaying
        ? _localizationService.T("Music.Control.Pause")
        : _localizationService.T("Music.Control.Play");

    public string PreviousTooltip => _localizationService.T("Music.Control.Previous");

    public string NextTooltip => _localizationService.T("Music.Control.Next");

    public string SourcePickerTooltip => _localizationService.T("Music.SwitchSource");

    public string FollowSystemSourceLabel => _localizationService.T("Music.FollowSystemSource");

    public string NoAvailableSourcesLabel => _localizationService.T("Music.NoAvailableSources");

    public string PlaybackModeGlyph => PlaybackMode switch
    {
        MusicPlaybackMode.Shuffle => "\uE8B1",
        MusicPlaybackMode.Repeat => "\uE8EE",
        _ => "\uE8AB"
    };

    public string PlaybackModeTooltip => PlaybackMode switch
    {
        MusicPlaybackMode.Shuffle => _localizationService.T("Music.Control.Mode.Shuffle"),
        MusicPlaybackMode.Repeat => _localizationService.T("Music.Control.Mode.Repeat"),
        _ => _localizationService.T("Music.Control.Mode.Normal")
    };

    public double PlaybackModeOpacity => PlaybackMode == MusicPlaybackMode.Normal ? 0.74 : 1.0;

    public string VolumeTooltip => _localizationService.T("Music.Control.Volume");

    public double SystemVolume
    {
        get => _systemVolume;
        set
        {
            double normalizedValue = NormalizeVolume(value);
            if (SetProperty(ref _systemVolume, normalizedValue))
            {
                OnPropertyChanged(nameof(SystemVolumeText));
            }
        }
    }

    public double SessionVolume
    {
        get => _sessionVolume;
        set
        {
            double normalizedValue = NormalizeVolume(value);
            if (SetProperty(ref _sessionVolume, normalizedValue))
            {
                OnPropertyChanged(nameof(SessionVolumeText));
            }
        }
    }

    public bool HasSessionVolume
    {
        get => _hasSessionVolume;
        private set
        {
            if (SetProperty(ref _hasSessionVolume, value))
            {
                OnPropertyChanged(nameof(SessionVolumeSliderOpacity));
                OnPropertyChanged(nameof(SessionVolumeText));
            }
        }
    }

    public double SessionVolumeSliderOpacity => HasSessionVolume ? 1.0 : 0.45;

    public string SystemVolumeLabel => _localizationService.T("Music.Volume.System");

    public string SessionVolumeLabel => _localizationService.T("Music.Volume.App");

    public string SystemVolumeText => FormatPercent(SystemVolume);

    public double VolumeTextSize => CaptionTextSize + 2;

    public string SessionVolumeText => HasSessionVolume
        ? FormatPercent(SessionVolume)
        : _localizationService.T("Music.Volume.Unavailable");

    public string RefreshTooltip => _localizationService.T("Common.Refresh");

    public string PositionText => FormatTime(Position);

    public string DurationText => Duration > TimeSpan.Zero ? FormatTime(Duration) : "--:--";

    public int SelectedSessionIndex
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_preferredSessionId))
            {
                return 0;
            }

            int index = SessionIds.IndexOf(_preferredSessionId);
            return index < 0 ? 0 : index;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _fullRefreshPending = false;
        ++_coverGeneration;
        ++_transientEmptyInfoGeneration;
        ++_timelineRefreshGeneration;
        ++_playbackRefreshGeneration;
        DetachServiceEvents();
        if (_progressTimer is not null)
        {
            _progressTimer.Stop();
            _progressTimer.Tick -= ProgressTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerReleased();
        }

        _localizationService.LanguageChanged -= OnLanguageChanged;
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }

        _musicSessionService.Dispose();

        // Release the thumbnail BitmapImage reference so the native WIC
        // texture can be reclaimed by GC rather than lingering until gen2.
        ThumbnailImage = null;

        PerformanceLogger.ActiveMusicTimerCount = 0;
        PerformanceLogger.MusicProgressTimerIntervalMs = 0;
    }

    private IReadOnlyList<MusicSessionOption> RefreshSessionList()
    {
        IReadOnlyList<MusicSessionOption> rawOptions = _musicSessionService.GetSessionOptions();
        string unknownSource = _localizationService.T("Music.SourceUnknown");
        string[] baseDisplayNames = rawOptions
            .Select(option => string.IsNullOrWhiteSpace(option.SourceDisplayName)
                ? unknownSource
                : option.SourceDisplayName)
            .ToArray();
        IReadOnlyList<string> displayNames = MusicSessionService.DisambiguateSourceDisplayNames(baseDisplayNames);
        MusicSessionOption[] options = rawOptions
            .Select((option, index) => option with { SourceDisplayName = displayNames[index] })
            .OrderByDescending(option => string.Equals(
                option.SessionId,
                _preferredSessionId,
                StringComparison.Ordinal))
            .ThenByDescending(option => option.IsSystemCurrent)
            .ThenByDescending(option => option.PlaybackState == MusicPlaybackState.Playing)
            .ThenBy(option => option.SourceDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        SessionIds.Clear();
        SessionDisplayNames.Clear();

        foreach (MusicSessionOption option in options)
        {
            SessionIds.Add(option.SessionId);
            SessionDisplayNames.Add(option.SourceDisplayName);
        }

        if (!string.IsNullOrWhiteSpace(_preferredSessionId) &&
            !SessionIds.Contains(_preferredSessionId))
        {
            _preferredSessionId = null;
            OnPropertyChanged(nameof(IsFollowingSystemSession));
            OnPropertyChanged(nameof(PreferredSessionId));
        }

        OnPropertyChanged(nameof(SessionPickerVisibility));
        OnPropertyChanged(nameof(SelectedSessionIndex));
        return options;
    }

    private void AttachServiceEvents()
    {
        _musicSessionService.SessionsChanged += OnSessionsChanged;
        _musicSessionService.CurrentSessionChanged += OnCurrentSessionChanged;
        _musicSessionService.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _musicSessionService.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _musicSessionService.TimelinePropertiesChanged += OnTimelineChanged;
    }

    private void DetachServiceEvents()
    {
        _musicSessionService.SessionsChanged -= OnSessionsChanged;
        _musicSessionService.CurrentSessionChanged -= OnCurrentSessionChanged;
        _musicSessionService.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _musicSessionService.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _musicSessionService.TimelinePropertiesChanged -= OnTimelineChanged;
    }

    /// <summary>
    /// Full refresh: reloads everything including cover art.
    /// Triggered by session list changes or media property changes (song change).
    /// Debounced: SMTC can fire MediaPropertiesChanged multiple times in quick
    /// succession during a song transition. We coalesce them into a single refresh
    /// to prevent the title/artist from flickering.
    /// </summary>
    private int _mediaPropertiesPendingGeneration;
    private int? _pendingFullRefreshMediaGeneration;
    private void OnSessionsChanged(object? sender, EventArgs e) => ScheduleFullRefresh();
    private void OnCurrentSessionChanged(object? sender, EventArgs e) => ScheduleFullRefresh();
    private void OnMediaPropertiesChanged(object? sender, EventArgs e)
    {
        ScheduleDebouncedMediaRefresh();
    }

    private void ScheduleDebouncedMediaRefresh()
    {
        // SMTC commonly publishes old, partial, and final metadata snapshots while
        // changing tracks. Wait for that burst to settle and let the newest request
        // supersede every earlier one, including the fallback queued by the button.
        int gen = Interlocked.Increment(ref _mediaPropertiesPendingGeneration);
        if (!CanRunWindowMediaRefresh())
        {
            Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = RunDebouncedMediaRefreshAsync(gen));
            return;
        }

        _ = RunDebouncedMediaRefreshAsync(gen);
    }

    private async Task RunDebouncedMediaRefreshAsync(int generation)
    {
        await Task.Delay(MediaPropertiesSettleDelayMs);
        if (!CanRunWindowMediaRefresh() || generation != _mediaPropertiesPendingGeneration)
        {
            return;
        }
        _ = RefreshAsync(generation);
    }

    /// <summary>
    /// Lightweight refresh: only updates position/duration without reloading cover.
    /// Timeline events fire very frequently (every ~1s), so we must avoid cover reload.
    /// </summary>
    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        if (!CanRunWindowMediaRefresh())
        {
            Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = RefreshTimelineAsync());
            return;
        }

        _ = RefreshTimelineAsync();
    }

    /// <summary>
    /// Lightweight refresh: only updates playback state/capabilities without reloading cover.
    /// </summary>
    private void OnPlaybackInfoChanged(object? sender, EventArgs e)
    {
        if (!CanRunWindowMediaRefresh())
        {
            Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = RefreshPlaybackAsync());
            return;
        }

        _ = RefreshPlaybackAsync();
    }

    private void ScheduleFullRefresh()
    {
        if (!CanRunWindowMediaRefresh())
        {
            Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = RefreshAsync());
            return;
        }

        _ = RefreshAsync();
    }

    /// <summary>
    /// Lightweight timeline-only refresh. Updates position and duration from SMTC
    /// without reloading cover art or media properties.
    /// </summary>
    private async Task RefreshTimelineAsync()
    {
        if (!CanRunWindowMediaRefresh()) return;

        int gen = ++_timelineRefreshGeneration;
        try
        {
            var info = await _musicSessionService.GetCurrentTimelineAsync(_preferredSessionId);
            if (!CanRunWindowMediaRefresh() || gen != _timelineRefreshGeneration || info is null) return;

            Position = info.Position;
            Duration = info.Duration;
            _lastSyncedPosition = info.Position;
            _lastPositionSyncAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
                App.Log($"[MusicWidget] Timeline refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Lightweight playback-only refresh. Updates playback state and capabilities
    /// without reloading cover art or media properties.
    /// </summary>
    private async Task RefreshPlaybackAsync()
    {
        if (!CanRunWindowMediaRefresh()) return;

        int gen = ++_playbackRefreshGeneration;
        try
        {
            var info = await _musicSessionService.GetCurrentPlaybackAsync(_preferredSessionId);
            if (!CanRunWindowMediaRefresh() || gen != _playbackRefreshGeneration || info is null) return;

            PlaybackState = info.PlaybackState;
            CanPlay = info.CanPlay;
            CanPause = info.CanPause;
            CanGoPrevious = info.CanGoPrevious;
            CanGoNext = info.CanGoNext;
            CanSeek = info.CanSeek && Duration > TimeSpan.Zero;
            CanChangeShuffle = info.CanChangeShuffle;
            CanChangeRepeat = info.CanChangeRepeat;
            PlaybackMode = info.PlaybackMode;
            RaiseDisplayPropertiesChanged();
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
                App.Log($"[MusicWidget] Playback refresh failed: {ex.Message}");
        }
    }

    private void ProgressTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed ||
            !_isWindowVisible ||
            !_isWindowRevealCompleted ||
            !ShouldRunProgressTimer(PlaybackState, _hasCurrentMediaInfo))
        {
            sender.Stop();
            UpdateMusicTimerDiagnostics();
            return;
        }

        if (Duration > TimeSpan.Zero && !_isSeeking)
        {
            // Extrapolate from the last SMTC-synced position so the progress bar
            // grows smoothly from zero. This also covers the buffering/Changing
            // window (mapped to Unknown) where the player is actually outputting
            // audio but SMTC hasn't reported Playing yet.
            double elapsedSinceSync = (DateTimeOffset.UtcNow - _lastPositionSyncAt).TotalSeconds;
            double newPosition = Math.Min(Duration.TotalSeconds, _lastSyncedPosition.TotalSeconds + elapsedSinceSync);
            Position = TimeSpan.FromSeconds(newPosition);
        }
    }

    private bool CanRunWindowMediaRefresh() =>
        !_isDisposed && _isWindowVisible && _isWindowRevealCompleted;

    private void UpdateMusicTimerDiagnostics()
    {
        PerformanceLogger.ActiveMusicTimerCount = _progressTimer?.IsRunning == true ? 1 : 0;
        PerformanceLogger.MusicProgressTimerIntervalMs = _progressTimer is null
            ? 0
            : ResolveProgressRefreshIntervalMs(_isCompactCollapsed);
    }

    private void UpdateProgressTimerState()
    {
        if (_progressTimer is null)
        {
            UpdateMusicTimerDiagnostics();
            return;
        }

        _progressTimer.Stop();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(
            ResolveProgressRefreshIntervalMs(_isCompactCollapsed));

        if (!_isDisposed &&
            _isWindowVisible &&
            _isWindowRevealCompleted &&
            ShouldRunProgressTimer(PlaybackState, _hasCurrentMediaInfo))
        {
            _progressTimer.Start();
        }

        UpdateMusicTimerDiagnostics();
    }

    private void OnLanguageChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        // These properties have localized fallbacks in their getters, so they must
        // be raised explicitly when the language changes (the backing fields haven't
        // changed, but the displayed string may differ).
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(SourceDisplayName));
        OnPropertyChanged(nameof(SourcePickerTooltip));
        RaiseDisplayPropertiesChanged();
        RefreshSessionList();
    }

    private void OnSettingsChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(OnSettingsChanged);
            return;
        }

        ApplyAppearance();
    }

    private void ApplyMusicSettings(AppSettings settings)
    {
        UseArtworkBackdrop = settings.MusicUseArtworkBackdrop;
        EnableCoverHoverMotion = settings.MusicEnableCoverHoverMotion;
        DisplayMode = settings.MusicDisplayMode;
        OnPropertyChanged(nameof(ArtworkBackdropCornerRadius));
    }

    private double GetArtworkBackdropCornerRadius()
    {
        string preference = WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
            _settingsService?.Settings.WidgetCornerPreference);
        return preference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 6,
            SettingsService.WidgetCornerPreferenceRound => 10,
            _ => 8
        };
    }

    private void RaiseDisplayPropertiesChanged()
    {
        // Title, Artist, SourceDisplayName, and DisplayName are backed by fields
        // whose setters already raise PropertyChanged when the value changes.
        // We must NOT raise them here unconditionally — doing so during playback
        // (when RefreshAsync fires from SMTC events) causes the marquee to restart
        // on every refresh, preventing it from ever scrolling.
        // Localized fallback notifications are handled explicitly in OnLanguageChanged.
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PlayIconVisibility));
        OnPropertyChanged(nameof(PauseIconVisibility));
        OnPropertyChanged(nameof(PlayPauseTooltip));
        OnPropertyChanged(nameof(PreviousTooltip));
        OnPropertyChanged(nameof(NextTooltip));
        OnPropertyChanged(nameof(PlaybackModeTooltip));
        OnPropertyChanged(nameof(VolumeTooltip));
        OnPropertyChanged(nameof(SystemVolumeLabel));
        OnPropertyChanged(nameof(SessionVolumeLabel));
        OnPropertyChanged(nameof(SessionVolumeText));
        OnPropertyChanged(nameof(RefreshTooltip));
        OnPropertyChanged(nameof(ThumbnailVisibility));
        OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
        OnPropertyChanged(nameof(ArtworkBackdropVisibility));
        OnPropertyChanged(nameof(ArtworkBackdropCornerRadius));
        OnPropertyChanged(nameof(MusicAccentBrush));
        OnPropertyChanged(nameof(PlayPauseButtonBackgroundBrush));
        OnPropertyChanged(nameof(HasSeekableTimeline));
        OnPropertyChanged(nameof(CanInteractWithProgress));
    }

    private MusicPlaybackMode GetNextPlaybackMode()
    {
        return PlaybackMode switch
        {
            MusicPlaybackMode.Normal when CanChangeShuffle => MusicPlaybackMode.Shuffle,
            MusicPlaybackMode.Normal when CanChangeRepeat => MusicPlaybackMode.Repeat,
            MusicPlaybackMode.Shuffle when CanChangeRepeat => MusicPlaybackMode.Repeat,
            MusicPlaybackMode.Shuffle => MusicPlaybackMode.Normal,
            MusicPlaybackMode.Repeat => MusicPlaybackMode.Normal,
            _ => MusicPlaybackMode.Normal
        };
    }

    private static byte ClampByte(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";
        }

        return $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private static double NormalizeVolume(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(Math.Round(value, 3), 0.0, 1.0)
            : 0.0;
    }

    private static string FormatPercent(double value)
    {
        return $"{Math.Clamp((int)Math.Round(NormalizeVolume(value) * 100), 0, 100)}%";
    }
}
