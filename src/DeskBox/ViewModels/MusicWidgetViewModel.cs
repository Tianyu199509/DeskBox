using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Lyrics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace DeskBox.ViewModels;

internal readonly record struct DecodedCoverResult(
    bool Succeeded,
    BitmapImage? Image,
    Color? Color);

public sealed partial class MusicWidgetViewModel : ObservableObject, IDisposable
{
    private const int MaxTransientEmptyInfoRetries = 4;
    private const int ExpandedProgressRefreshMs = 1000;
    private const int CompactProgressRefreshMs = 2000;
    private const int MediaPropertiesSettleDelayMs = 220;
    private const int LyricsLookupDebounceMs = 300;
    private static readonly TimeSpan RevealFullRefreshFreshness =
        TimeSpan.FromSeconds(30);

    /// <summary>
    /// Grace period before the timeline row collapses for a session that
    /// publishes no timeline: players burst empty timeline events around
    /// track changes, and the row must not flash hidden mid-transition.
    /// </summary>
    internal static readonly TimeSpan TimelineRowHideDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Grace window after an accepted Play/Pause transport command during which
    /// pre-transition or transient <see cref="MusicPlaybackState.Unknown"/> SMTC
    /// status reads do not bounce the optimistic playback state back to the prior state.
    /// </summary>
    internal static readonly TimeSpan PendingPlayPauseSettleWindow = TimeSpan.FromMilliseconds(900);
    internal static readonly TimeSpan PlaceholderCoverHoldGrace = TimeSpan.FromMilliseconds(950);
    private static readonly int[] LateCoverProbeDelaysMs = [450, 750, 1400];
    private static readonly TimeSpan CoverSettleWindow = TimeSpan.FromMilliseconds(2600);
    internal static readonly TimeSpan TrackEndReconcileGrace = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan TrackEndReconcileCooldown = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan PlayingTimelineProbeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlayingFullReconcileFallbackInterval = TimeSpan.FromSeconds(60);
    private const int PostCommandVerificationDelayMs = 220;
    private const int PostCommandFinalReconcileDelayMs = 720;

    private readonly IMusicSessionController _musicSessionService;
    private readonly Func<byte[]?, Task<DecodedCoverResult>> _coverDecoder;
    private readonly MusicVolumeService _musicVolumeService;
    private readonly IMusicVolumeController _volumeController;
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly LyricsService _lyricsService;
    private readonly WidgetConfig _config;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _progressTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _lyricTimer;
    private readonly MusicTimelineExtrapolator _timelineExtrapolator = new();
    private readonly Action _memoryReclaimCompletedHandler;

    private string? _preferredSessionId;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
    private string _sourceAppUserModelId = string.Empty;
    private string _sourceDisplayName = string.Empty;
    private MusicPlaybackState _playbackState = MusicPlaybackState.Unknown;
    private TimeSpan _position;
    private TimeSpan _duration;
    private string _displayedPositionText = "--:--";
    private BitmapImage? _thumbnailImage;
    private bool _canPlay;
    private bool _canPause;
    private bool _canGoPrevious;
    private bool _canGoNext;
    private bool _canSeek;
    private bool _canChangeShuffle;
    private bool _canChangeRepeat;
    private bool _isRefreshing;
    private Task? _activeRefreshTask;
    private bool _hasCurrentMediaInfo;
    private int _hiddenMediaStateDirty;
    private long _lastFullRefreshCompletedUtcTicks;
    private int _timelineRefreshGeneration;
    private int _playbackRefreshGeneration;
    private int _playPauseCommandGeneration;
    private MusicPlaybackState? _pendingCommandTargetState;
    private DateTimeOffset _pendingCommandTargetUntilUtc;
    private int _coverGeneration;
    private int _lateCoverProbeGeneration;
    private bool _hasAppliedCover;
    private bool _hasAppliedCoverForCurrentTrack;
    private int _decodedCoverApplyCount;
    private string? _lastCoverSignature;
    private ulong _lastCoverFingerprint;
    private string? _placeholderCoverSessionId;
    private ulong _sessionPlaceholderFingerprint;
    private string? _currentTrackCoverSignature;
    private ulong _currentTrackInitialCoverFingerprint;
    private ulong _previousTrackFinalCoverFingerprint;
    private DateTimeOffset _currentTrackCoverStartedUtc;
    private string? _confirmedEmptyCoverSignature;
    private string? _coverRetrySignature;
    private int _coverRetryCount;
    private bool _coverRetryInFlight;
    private string? _lastTrackIdentitySignature;
    private TimeSpan _previousTrackNativeDuration;
    private TimeSpan _currentTrackInitialNativeDuration;
    private bool _currentTrackDurationUntrusted;
    private TimeSpan _lastLyricsQueryDuration;
    private bool _hasRetriedLyricsWithUpdatedDuration;
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
    private Task<bool>? _activeVolumeRefreshTask;
    private bool _hasReadSystemVolume;
    private int _debouncedMediaRefreshInFlightCount;
    private int _timelineRefreshInFlightCount;
    private int _playbackRefreshInFlightCount;
    private int _liveReconcileInFlight;
    private int _liveTimelineProbeInFlight;
    private DateTimeOffset _lastLiveReconcileUtc;
    private DateTimeOffset _lastLiveTimelineProbeUtc;
    private long _lastObservedReclaimEpoch;
    private long _lastObservedWorkingSetTrimEpoch;
    private double? _pendingSystemVolume;
    private double? _pendingSessionVolume;
    private bool _isDisposed;
    private double _textSize = SettingsService.DefaultTextSize;
    private double _seekValue;
    private double _systemVolume = 0.5;
    private double _sessionVolume = 1.0;
    private double _preMuteSystemVolume = 0.5;
    private double _preMuteSessionVolume = 0.8;
    private bool _hasSessionVolume;
    private bool _useArtworkBackdrop = true;
    private bool _enableCoverHoverMotion = true;
    private bool _hideTimelineWhenUnavailable = true;
    private bool _showLyrics = true;
    private bool _showCapsuleLyrics = true;
    private string _localLyricsFolder = string.Empty;
    private bool _timelineRowHidden;
    private DateTimeOffset? _timelineUnavailableSince;
    private string _displayMode = SettingsService.MusicDisplayModeAuto;
    private string _spectrumStyle = SettingsService.MusicSpectrumStyleAuto;
    private Color _artworkColor = AccentColorHelper.DefaultAccentColor;
    private Color _backdropColor = AccentColorHelper.DefaultAccentColor;
    private bool _hasArtworkColor;
    private SolidColorBrush? _cachedMusicAccentBrush;
    private Color _cachedMusicAccentBrushColor;
    private SolidColorBrush? _cachedPlayPauseBrush;
    private Color _cachedPlayPauseBrushColor;
    private SolidColorBrush? _cachedLyricsWashBrush;
    private Color _cachedLyricsWashBrushColor;
    private ImageSource? _sourceAppIcon;
    private string _resolvedSourceIconKey = string.Empty;
    private int _sourceIconRequestVersion;
    private MusicPlaybackMode _playbackMode = MusicPlaybackMode.Normal;

    // Synchronized lyrics state (event-driven, zero polling)
    private CancellationTokenSource? _lyricsFetchCts;
    private string? _lastLyricsTrackSignature;
    private LrcDocument _currentLyrics = LrcDocument.Empty;
    private int _activeLyricIndex = -1;
    private string _currentLyricLine = string.Empty;
    private string? _currentLyricSecondaryLine;
    private bool _isLoadingLyrics;
    private bool _isLyricIntroPreview;
    private bool _isLyricsViewOpen;
    private string _cachedSubtitleText = string.Empty;
    private Visibility _cachedSubtitleVisibility = Visibility.Visible;
    private double _cachedSubtitleOpacity = 0.82;
    private string _cachedInlineLyricText = string.Empty;
    private Visibility _cachedInlineLyricVisibility = Visibility.Collapsed;
    private double _cachedInlineLyricOpacity = 0.96;
    private Visibility _cachedActiveLyricAccentVisibility = Visibility.Collapsed;

    public MusicWidgetViewModel(
        WidgetConfig config,
        MusicSessionService musicSessionService,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = null,
        MusicVolumeService? musicVolumeService = null,
        LyricsService? lyricsService = null)
        : this(
            config,
            (IMusicSessionController)musicSessionService,
            localizationService,
            settingsService,
            dispatcherQueue,
            musicVolumeService,
            lyricsService)
    {
    }

    internal MusicWidgetViewModel(
        WidgetConfig config,
        IMusicSessionController musicSessionService,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = null,
        MusicVolumeService? musicVolumeService = null,
        LyricsService? lyricsService = null,
        Func<byte[]?, Task<DecodedCoverResult>>? coverDecoder = null,
        IMusicVolumeController? volumeController = null)
    {
        _config = config;
        _musicSessionService = musicSessionService;
        _coverDecoder = coverDecoder ?? DecodeCoverCoreAsync;
        _musicVolumeService = musicVolumeService ?? new MusicVolumeService();
        _volumeController = volumeController ?? _musicVolumeService;
        _localizationService = localizationService;
        _settingsService = settingsService;
        _lyricsService = lyricsService ?? LyricsService.Shared;
        _dispatcherQueue = dispatcherQueue ?? TryGetCurrentDispatcherQueue();
        _lastObservedReclaimEpoch = MemoryReclaimer.CompletedReclaimEpoch;
        _lastObservedWorkingSetTrimEpoch = App.WorkingSetTrimEpoch;
        _memoryReclaimCompletedHandler = OnMemoryReclaimCompleted;
        MemoryReclaimer.ReclaimCompleted += _memoryReclaimCompletedHandler;

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

            _lyricTimer = _dispatcherQueue.CreateTimer();
            _lyricTimer.IsRepeating = false;
            _lyricTimer.Tick += LyricTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }

        RefreshSubtitleState(notify: false);
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
        ShouldRunProgressTimer(playbackState, hasCurrentMediaInfo, isUnknownBufferingExpired: false);

    internal static bool ShouldRunProgressTimer(
        MusicPlaybackState playbackState,
        bool hasCurrentMediaInfo,
        bool isUnknownBufferingExpired) =>
        hasCurrentMediaInfo &&
        (playbackState == MusicPlaybackState.Playing ||
         (playbackState == MusicPlaybackState.Unknown && !isUnknownBufferingExpired));

    /// <summary>
    /// Gate for the "hide the timeline row when the player publishes no
    /// timeline" preference. Players that never sync SMTC timeline data
    /// leave the duration at zero; live-style sessions advance a position
    /// without a duration and must keep their elapsed-time row, so both
    /// values have to be missing before the row may collapse.
    /// </summary>
    internal static bool ShouldHideTimelineRow(
        bool hideWhenUnavailable,
        bool hasSeekableTimeline,
        TimeSpan position) =>
        hideWhenUnavailable && !hasSeekableTimeline && position <= TimeSpan.Zero;

    public ObservableCollection<string> SessionDisplayNames { get; } = [];

    public ObservableCollection<string> SessionIds { get; } = [];

    public ObservableCollection<LrcLine> LyricLines { get; } = [];

    public bool IsFollowingSystemSession => string.IsNullOrWhiteSpace(_preferredSessionId);

    public string? PreferredSessionId => _preferredSessionId;

    public string DisplayName => _config.IsDefaultTitle
        ? _localizationService.T("Music.Title")
        : _config.Name;

    public string Title
    {
        get => string.IsNullOrWhiteSpace(_title) ? _localizationService.T("Music.EmptyTitle") : _title;
        private set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(LyricsHeaderSubtitle));
                RefreshSubtitleState();
            }
        }
    }

    public string Artist
    {
        get => string.IsNullOrWhiteSpace(_artist) ? SourceDisplayName : _artist;
        private set
        {
            if (SetProperty(ref _artist, value))
            {
                OnPropertyChanged(nameof(LyricsHeaderSubtitle));
                OnPropertyChanged(nameof(SourceBadgeTextVisibility));
            }
        }
    }

    public string Album
    {
        get => _album;
        private set
        {
            if (SetProperty(ref _album, value))
            {
                RefreshSubtitleState();
            }
        }
    }

    public string SourceDisplayName
    {
        get => string.IsNullOrWhiteSpace(_sourceDisplayName) ? _localizationService.T("Music.SourceUnknown") : _sourceDisplayName;
        private set
        {
            if (SetProperty(ref _sourceDisplayName, value))
            {
                OnPropertyChanged(nameof(Artist));
                OnPropertyChanged(nameof(LyricsHeaderSubtitle));
                OnPropertyChanged(nameof(SourceBadgeText));
                OnPropertyChanged(nameof(SourceBadgeTextVisibility));
                RefreshSubtitleState();
            }
        }
    }

    public string SourceBadgeText => !string.IsNullOrWhiteSpace(_sourceDisplayName)
        ? _sourceDisplayName
        : string.Empty;

    internal string SourceAppUserModelId => _sourceAppUserModelId;

    internal string RawSourceDisplayName => _sourceDisplayName;

    public Visibility SourceBadgeTextVisibility =>
        HasSession &&
        !string.IsNullOrWhiteSpace(_sourceDisplayName) &&
        !string.IsNullOrWhiteSpace(_artist) &&
        !string.Equals(_artist.Trim(), _sourceDisplayName.Trim(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public ImageSource? SourceAppIcon => HasSession ? _sourceAppIcon : null;

    public Visibility SourceAppIconVisibility =>
        SourceAppIcon is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SourceFallbackIconVisibility =>
        SourceAppIcon is null ? Visibility.Visible : Visibility.Collapsed;

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

    public string SpectrumStyle
    {
        get => _spectrumStyle;
        private set => SetProperty(ref _spectrumStyle, SettingsService.NormalizeMusicSpectrumStyle(value));
    }

    public bool ShowLyrics
    {
        get => _showLyrics;
        private set
        {
            if (SetProperty(ref _showLyrics, value))
            {
                if (!value)
                {
                    IsLyricsViewOpen = false;
                    ClearLyricsState();
                    _lyricsService.ClearMemoryCache();
                    if (Microsoft.UI.Xaml.Application.Current is App app)
                    {
                        app.RequestDeferredFeatureMemoryReclaim("music-lyrics-disabled");
                    }
                }

                OnPropertyChanged(nameof(LyricsOverlayVisibility));
                OnPropertyChanged(nameof(ArtworkBackdropVisibility));
                OnPropertyChanged(nameof(LyricsToggleVisibility));
                RefreshSubtitleState();
                UpdateLyricActiveLineAndScheduleTimer();
            }
        }
    }

    public bool ShowCapsuleLyrics
    {
        get => _showCapsuleLyrics;
        private set
        {
            if (SetProperty(ref _showCapsuleLyrics, value))
            {
                UpdateLyricActiveLineAndScheduleTimer();
            }
        }
    }

    public bool IsLyricsViewOpen
    {
        get => _isLyricsViewOpen;
        private set
        {
            if (SetProperty(ref _isLyricsViewOpen, value))
            {
                OnPropertyChanged(nameof(LyricsOverlayVisibility));
                OnPropertyChanged(nameof(ArtworkBackdropVisibility));
            }
        }
    }

    public int ActiveLyricIndex
    {
        get => _activeLyricIndex;
        private set
        {
            if (SetProperty(ref _activeLyricIndex, value))
            {
                OnPropertyChanged(nameof(CurrentLyricDurationSeconds));
            }
        }
    }

    public double CurrentLyricDurationSeconds
    {
        get
        {
            if (!_currentLyrics.HasSyncedLines)
            {
                return 0;
            }

            int idx = _activeLyricIndex;
            if (idx >= 0 && idx < _currentLyrics.Lines.Count)
            {
                TimeSpan start = _currentLyrics.Lines[idx].Timestamp;
                if (idx + 1 < _currentLyrics.Lines.Count)
                {
                    return Math.Max(0, (_currentLyrics.Lines[idx + 1].Timestamp - start).TotalSeconds);
                }

                if (Duration > start)
                {
                    return Math.Max(0, (Duration - start).TotalSeconds);
                }

                return 0;
            }

            return Math.Max(0, _currentLyrics.Lines[0].Timestamp.TotalSeconds);
        }
    }

    public string CurrentLyricLine
    {
        get => _currentLyricLine;
        private set
        {
            if (SetProperty(ref _currentLyricLine, value))
            {
                RefreshSubtitleState();
            }
        }
    }

    public string? CurrentLyricSecondaryLine
    {
        get => _currentLyricSecondaryLine;
        private set
        {
            if (SetProperty(ref _currentLyricSecondaryLine, value))
            {
                OnPropertyChanged(nameof(InlineLyricTooltip));
            }
        }
    }

    public string SubtitleText => _cachedSubtitleText;

    public Visibility SubtitleVisibility => _cachedSubtitleVisibility;

    public double SubtitleOpacity => _cachedSubtitleOpacity;

    public string InlineLyricText => _cachedInlineLyricText;

    public Visibility InlineLyricVisibility => _cachedInlineLyricVisibility;

    public double InlineLyricOpacity => _cachedInlineLyricOpacity;

    public Visibility HasActiveSyncedLyricVisibility => _cachedActiveLyricAccentVisibility;

    public string? InlineLyricTooltip => string.IsNullOrWhiteSpace(_cachedInlineLyricText)
        ? null
        : string.IsNullOrWhiteSpace(_currentLyricSecondaryLine)
            ? _cachedInlineLyricText
            : $"{_cachedInlineLyricText}\n{_currentLyricSecondaryLine}";

    public Visibility LyricsOverlayVisibility =>
        ShowLyrics && IsLyricsViewOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LyricsToggleVisibility =>
        ShowLyrics && HasSession ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LyricsListVisibility =>
        _currentLyrics.HasSyncedLines ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LyricsPlaceholderVisibility =>
        _currentLyrics.HasSyncedLines ? Visibility.Collapsed : Visibility.Visible;

    public string LyricsPlaceholderText => _currentLyrics.IsInstrumental
        ? _localizationService.T("Music.Lyrics.Instrumental")
        : _localizationService.T("Music.Lyrics.Empty");

    public string LyricsToggleTooltip => _localizationService.T("Music.Lyrics.Toggle");

    public string LyricsOffsetEarlierTooltip => _localizationService.T("Music.Lyrics.OffsetEarlier");

    public string LyricsOffsetLaterTooltip => _localizationService.T("Music.Lyrics.OffsetLater");

    public string LyricsOffsetResetTooltip => _localizationService.T("Music.Lyrics.OffsetReset");

    public string LyricsOffsetText => _currentLyrics.UserOffsetMs == 0
        ? "0.0s"
        : $"{_currentLyrics.UserOffsetMs / 1000.0:+0.0;-0.0;0.0}s";

    public bool HasNonZeroLyricsOffset => _currentLyrics.UserOffsetMs != 0;

    public string LyricsHeaderSubtitle => string.IsNullOrWhiteSpace(_artist)
        ? Title
        : $"{Title} · {Artist}";

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
                OnPropertyChanged(nameof(HasSession));
                OnPropertyChanged(nameof(LyricsToggleVisibility));
                RefreshSubtitleState();

                // Start or stop the progress timer based on playback state.
                // Also start for Unknown when a current media snapshot exists — many
                // players report "Changing" (buffering) at song start, which maps to
                // Unknown. A missing session also maps to Unknown and must stay idle.
                UpdateProgressTimerState();
                UpdateLyricActiveLineAndScheduleTimer();
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

                RefreshPositionText();
                UpdateTimelineRowEligibility();
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
                RefreshPositionText();
                UpdateTimelineRowEligibility();
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
                OnPropertyChanged(nameof(LyricTextSize));
                OnPropertyChanged(nameof(MinimalLyricTextSize));
                OnPropertyChanged(nameof(CaptionTextSize));
                OnPropertyChanged(nameof(VolumeTextSize));
            }
        }
    }

    public double TitleTextSize => Math.Min(SettingsService.MaxTextSize + 3, TextSize + 4);

    public double MinimalTitleTextSize => Math.Min(SettingsService.MaxTextSize + 1, TextSize + 1.5);

    public double SecondaryTextSize => Math.Max(SettingsService.MinTextSize, TextSize - 1);

    public double LyricTextSize => Math.Min(SettingsService.MaxTextSize + 1.5, TextSize + 0.5);

    public double MinimalLyricTextSize => Math.Min(SettingsService.MaxTextSize + 1.5, TextSize + 0.5);

    public double CaptionTextSize => Math.Max(SettingsService.MinTextSize - 1, TextSize - 2);

    public bool IsPlaying => PlaybackState == MusicPlaybackState.Playing;

    internal bool ShouldAdvancePlaybackClock => ShouldRunProgressTimer(
        PlaybackState,
        _hasCurrentMediaInfo,
        _timelineExtrapolator.IsUnknownBufferingExpired(DateTimeOffset.UtcNow));

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

    public Visibility ArtworkBackdropVisibility =>
        UseArtworkBackdrop && LyricsOverlayVisibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// Collapses the progress bar and both time labels in every layout when
    /// the player publishes no timeline and the user opted into hiding it.
    /// </summary>
    public Visibility TimelineRowVisibility => _timelineRowHidden ? Visibility.Collapsed : Visibility.Visible;

    public CornerRadius ArtworkBackdropCornerRadius => new(GetArtworkBackdropCornerRadius());

    public Color ArtworkBackdropStartColor => Color.FromArgb(0x5A, _backdropColor.R, _backdropColor.G, _backdropColor.B);

    public Color ArtworkBackdropMidColor => Color.FromArgb(0x20, _backdropColor.R, _backdropColor.G, _backdropColor.B);

    public Color ArtworkBackdropEndColor => Color.FromArgb(0x00, _backdropColor.R, _backdropColor.G, _backdropColor.B);

    public SolidColorBrush MusicAccentBrush
    {
        get
        {
            Color target = GetMusicAccentColor();
            if (_cachedMusicAccentBrush is null || !_cachedMusicAccentBrushColor.Equals(target))
            {
                _cachedMusicAccentBrushColor = target;
                _cachedMusicAccentBrush = new SolidColorBrush(target);
            }

            return _cachedMusicAccentBrush;
        }
    }

    public SolidColorBrush PlayPauseButtonBackgroundBrush
    {
        get
        {
            Color target = GetMusicAccentColor();
            if (_cachedPlayPauseBrush is null || !_cachedPlayPauseBrushColor.Equals(target))
            {
                _cachedPlayPauseBrushColor = target;
                _cachedPlayPauseBrush = new SolidColorBrush(target);
            }

            return _cachedPlayPauseBrush;
        }
    }

    public SolidColorBrush LyricsAmbientWashBrush
    {
        get
        {
            Color target = UseArtworkBackdrop && _hasArtworkColor
                ? Color.FromArgb(0x3C, _backdropColor.R, _backdropColor.G, _backdropColor.B)
                : Color.FromArgb(0x00, 0, 0, 0);
            if (_cachedLyricsWashBrush is null || !_cachedLyricsWashBrushColor.Equals(target))
            {
                _cachedLyricsWashBrushColor = target;
                _cachedLyricsWashBrush = new SolidColorBrush(target);
            }

            return _cachedLyricsWashBrush;
        }
    }

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
        MusicPlaybackMode.RepeatOne => "\uE8ED",
        _ => "\uE8AB"
    };

    public string PlaybackModeTooltip => PlaybackMode switch
    {
        MusicPlaybackMode.Shuffle => _localizationService.T("Music.Control.Mode.Shuffle"),
        MusicPlaybackMode.Repeat => _localizationService.T("Music.Control.Mode.Repeat"),
        MusicPlaybackMode.RepeatOne => _localizationService.T("Music.Control.Mode.RepeatOne"),
        _ => _localizationService.T("Music.Control.Mode.Normal")
    };

    public double PlaybackModeOpacity => PlaybackMode == MusicPlaybackMode.Normal ? 0.74 : 1.0;

    public string VolumeTooltip => _localizationService.T("Music.Control.Volume");

    public string VolumeGlyph => SystemVolume switch
    {
        <= 0.005 => "\uE74F",
        < 0.34 => "\uE993",
        < 0.67 => "\uE994",
        _ => "\uE995"
    };

    public string SessionVolumeGlyph => SessionVolume <= 0.005 ? "\uE74F" : "\uEC4F";

    public double SystemVolume
    {
        get => _systemVolume;
        set
        {
            double normalizedValue = NormalizeVolume(value);
            if (normalizedValue > 0.005)
            {
                _preMuteSystemVolume = normalizedValue;
            }

            if (SetProperty(ref _systemVolume, normalizedValue))
            {
                OnPropertyChanged(nameof(SystemVolumeText));
                OnPropertyChanged(nameof(VolumeGlyph));
            }
        }
    }

    public double SessionVolume
    {
        get => _sessionVolume;
        set
        {
            double normalizedValue = NormalizeVolume(value);
            if (normalizedValue > 0.005)
            {
                _preMuteSessionVolume = normalizedValue;
            }

            if (SetProperty(ref _sessionVolume, normalizedValue))
            {
                OnPropertyChanged(nameof(SessionVolumeText));
                OnPropertyChanged(nameof(SessionVolumeGlyph));
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
                OnPropertyChanged(nameof(SessionVolumeRowVisibility));
                OnPropertyChanged(nameof(SessionVolumeText));
            }
        }
    }

    public Visibility SessionVolumeRowVisibility =>
        HasSessionVolume ? Visibility.Visible : Visibility.Collapsed;

    public double SessionVolumeSliderOpacity => HasSessionVolume ? 1.0 : 0.45;

    public string SystemVolumeLabel => _localizationService.T("Music.Volume.System");

    public string SessionVolumeLabel => _localizationService.T("Music.Volume.App");

    public string SystemVolumeText => FormatPercent(SystemVolume);

    public double VolumeTextSize => CaptionTextSize + 2;

    public string SessionVolumeText => HasSessionVolume
        ? FormatPercent(SessionVolume)
        : _localizationService.T("Music.Volume.Unavailable");

    public string RefreshTooltip => _localizationService.T("Common.Refresh");

    public string PositionText => _displayedPositionText;

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
        ++_lateCoverProbeGeneration;
        ++_transientEmptyInfoGeneration;
        ++_timelineRefreshGeneration;
        ++_playbackRefreshGeneration;
        ++_playPauseCommandGeneration;
        ClearPendingPlayPauseIntent();
        CancelPendingLyricsLookup();
        MemoryReclaimer.ReclaimCompleted -= _memoryReclaimCompletedHandler;
        DetachServiceEvents();
        if (_progressTimer is not null)
        {
            _progressTimer.Stop();
            _progressTimer.Tick -= ProgressTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerReleased();
        }

        if (_lyricTimer is not null)
        {
            _lyricTimer.Stop();
            _lyricTimer.Tick -= LyricTimer_Tick;
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

    private void RefreshPositionText()
    {
        string formatted = FormatPositionText(Position, Duration);
        if (!string.Equals(_displayedPositionText, formatted, StringComparison.Ordinal))
        {
            _displayedPositionText = formatted;
            OnPropertyChanged(nameof(PositionText));
        }
    }

    private void RefreshSubtitleState(bool notify = true)
    {
        string nextInlineText;
        Visibility nextInlineVis;
        double nextInlineOpacity;
        Visibility nextAccentVis;

        if (!ShowLyrics || !HasSession)
        {
            nextInlineText = string.Empty;
            nextInlineVis = Visibility.Collapsed;
            nextInlineOpacity = 0.0;
            nextAccentVis = Visibility.Collapsed;
        }
        else
        {
            nextInlineVis = Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(_currentLyricLine))
            {
                nextInlineText = _currentLyricLine;
                nextInlineOpacity = _isLyricIntroPreview ? 0.76 : 0.96;
                nextAccentVis = Visibility.Visible;
            }
            else if (_currentLyrics.IsInstrumental)
            {
                nextInlineText = _localizationService.T("Music.Lyrics.Instrumental");
                nextInlineOpacity = 0.74;
                nextAccentVis = Visibility.Visible;
            }
            else if (_isLoadingLyrics)
            {
                nextInlineText = string.Empty;
                nextInlineOpacity = 0.56;
                nextAccentVis = Visibility.Collapsed;
            }
            else
            {
                nextInlineText = _localizationService.T("Music.Lyrics.Empty");
                nextInlineOpacity = 0.56;
                nextAccentVis = Visibility.Collapsed;
            }
        }

        string nextSubtitleText;
        Visibility nextSubtitleVis;
        double nextSubtitleOpacity;

        if (!HasSession)
        {
            nextSubtitleText = StatusText;
            nextSubtitleVis = Visibility.Visible;
            nextSubtitleOpacity = 0.82;
        }
        else if (ShowLyrics)
        {
            nextSubtitleText = nextInlineText;
            nextSubtitleVis = Visibility.Visible;
            nextSubtitleOpacity = nextInlineOpacity;
        }
        else
        {
            nextSubtitleText = string.Empty;
            nextSubtitleVis = Visibility.Collapsed;
            nextSubtitleOpacity = 0.0;
        }

        bool inlineTextChanged = !string.Equals(_cachedInlineLyricText, nextInlineText, StringComparison.Ordinal);
        bool inlineVisChanged = _cachedInlineLyricVisibility != nextInlineVis;
        bool inlineOpacityChanged = Math.Abs(_cachedInlineLyricOpacity - nextInlineOpacity) > 0.001;
        bool accentVisChanged = _cachedActiveLyricAccentVisibility != nextAccentVis;
        bool subtitleTextChanged = !string.Equals(_cachedSubtitleText, nextSubtitleText, StringComparison.Ordinal);
        bool subtitleVisChanged = _cachedSubtitleVisibility != nextSubtitleVis;
        bool subtitleOpacityChanged = Math.Abs(_cachedSubtitleOpacity - nextSubtitleOpacity) > 0.001;

        _cachedInlineLyricText = nextInlineText;
        _cachedInlineLyricVisibility = nextInlineVis;
        _cachedInlineLyricOpacity = nextInlineOpacity;
        _cachedActiveLyricAccentVisibility = nextAccentVis;
        _cachedSubtitleText = nextSubtitleText;
        _cachedSubtitleVisibility = nextSubtitleVis;
        _cachedSubtitleOpacity = nextSubtitleOpacity;

        if (notify)
        {
            if (inlineTextChanged)
            {
                OnPropertyChanged(nameof(InlineLyricText));
                OnPropertyChanged(nameof(InlineLyricTooltip));
            }

            if (inlineVisChanged)
            {
                OnPropertyChanged(nameof(InlineLyricVisibility));
            }

            if (inlineOpacityChanged)
            {
                OnPropertyChanged(nameof(InlineLyricOpacity));
            }

            if (accentVisChanged)
            {
                OnPropertyChanged(nameof(HasActiveSyncedLyricVisibility));
            }

            if (subtitleTextChanged)
            {
                OnPropertyChanged(nameof(SubtitleText));
            }

            if (subtitleVisChanged)
            {
                OnPropertyChanged(nameof(SubtitleVisibility));
            }

            if (subtitleOpacityChanged)
            {
                OnPropertyChanged(nameof(SubtitleOpacity));
            }
        }
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

    private int _mediaPropertiesPendingGeneration;
    private int? _pendingFullRefreshMediaGeneration;
    private void OnSessionsChanged(object? sender, EventArgs e) => ScheduleFullRefresh();
    private void OnCurrentSessionChanged(object? sender, EventArgs e) => ScheduleFullRefresh();
    private void OnMediaPropertiesChanged(object? sender, EventArgs e)
    {
        ScheduleDebouncedMediaRefresh();
    }

    private void ScheduleDebouncedMediaRefresh(bool isUserInitiated = false)
    {
        int gen = Interlocked.Increment(ref _mediaPropertiesPendingGeneration);
        if (!CanRunMediaRefresh(isUserInitiated))
        {
            Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = RunDebouncedMediaRefreshAsync(gen, isUserInitiated));
            return;
        }

        _ = RunDebouncedMediaRefreshAsync(gen, isUserInitiated);
    }

    private async Task RunDebouncedMediaRefreshAsync(int generation, bool isUserInitiated)
    {
        Interlocked.Increment(ref _debouncedMediaRefreshInFlightCount);
        try
        {
            await Task.Delay(MediaPropertiesSettleDelayMs);
            if (_isDisposed)
            {
                return;
            }

            if (!CanRunMediaRefresh(isUserInitiated))
            {
                if (!isUserInitiated)
                {
                    Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
                }

                return;
            }

            if (generation != Volatile.Read(ref _mediaPropertiesPendingGeneration))
            {
                return;
            }

            _ = RefreshAsync(generation);
        }
        finally
        {
            Interlocked.Decrement(ref _debouncedMediaRefreshInFlightCount);
        }
    }

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

    private async Task RefreshTimelineAsync(bool isUserInitiated = false)
    {
        if (!CanRunMediaRefresh(isUserInitiated))
        {
            if (!_isDisposed && !isUserInitiated)
            {
                Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
            }

            return;
        }

        int gen = ++_timelineRefreshGeneration;
        Interlocked.Increment(ref _timelineRefreshInFlightCount);
        try
        {
            MusicTimelineSnapshot? info = await _musicSessionService.GetCurrentTimelineAsync(_preferredSessionId);
            if (_isDisposed)
            {
                return;
            }

            if (!CanRunMediaRefresh(isUserInitiated))
            {
                if (!isUserInitiated)
                {
                    Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
                }

                return;
            }

            if (gen != _timelineRefreshGeneration || info is null)
            {
                return;
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            MusicPlaybackState effectiveState = ResolvePlaybackStateWithPendingIntent(info.PlaybackState, nowUtc);
            (TimeSpan extrapolatedPos, TimeSpan effectiveDur) = _timelineExtrapolator.ApplyTimelineSnapshot(
                effectiveState,
                info.Position,
                info.Duration,
                info.PositionUpdatedUtc,
                info.PlaybackRate,
                nowUtc);

            if (effectiveState is MusicPlaybackState.Playing or MusicPlaybackState.Paused or MusicPlaybackState.Stopped)
            {
                PlaybackState = effectiveState;
            }

            Duration = effectiveDur;
            Position = ShouldExposeTimelinePosition() ? extrapolatedPos : TimeSpan.Zero;
            CanSeek = CanSeek || (_timelineExtrapolator.HasNativeDuration && CanInteractWithProgress);
            if (_timelineExtrapolator.HasNativeDuration)
            {
                NotifyTimelineDurationUpdated(info.Duration, info.Position, info.PositionUpdatedUtc, nowUtc);
            }

            UpdateLyricActiveLineAndScheduleTimer();
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                App.Log($"[MusicWidget] Timeline refresh failed: {ex.Message}");
            }
        }
        finally
        {
            Interlocked.Decrement(ref _timelineRefreshInFlightCount);
        }
    }

    private async Task RefreshPlaybackAsync(bool isUserInitiated = false)
    {
        if (!CanRunMediaRefresh(isUserInitiated))
        {
            if (!_isDisposed && !isUserInitiated)
            {
                Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
            }

            return;
        }

        int gen = ++_playbackRefreshGeneration;
        Interlocked.Increment(ref _playbackRefreshInFlightCount);
        try
        {
            MusicPlaybackSnapshot? info = await _musicSessionService.GetCurrentPlaybackAsync(_preferredSessionId);
            if (_isDisposed)
            {
                return;
            }

            if (!CanRunMediaRefresh(isUserInitiated))
            {
                if (!isUserInitiated)
                {
                    Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
                }

                return;
            }

            if (gen != _playbackRefreshGeneration || info is null)
            {
                return;
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            MusicPlaybackState effectiveState = ResolvePlaybackStateWithPendingIntent(info.PlaybackState, nowUtc);
            TimeSpan anchoredPos = _timelineExtrapolator.ApplyPlaybackStateChange(
                effectiveState,
                info.PlaybackRate,
                nowUtc);

            PlaybackState = effectiveState;
            if (ShouldExposeTimelinePosition() && !_isSeeking)
            {
                Position = anchoredPos;
            }

            if (_pendingCommandTargetState is { } pendingTarget &&
                !info.CanPlay &&
                !info.CanPause)
            {
                CanPlay = pendingTarget != MusicPlaybackState.Playing;
                CanPause = pendingTarget == MusicPlaybackState.Playing;
            }
            else
            {
                CanPlay = info.CanPlay;
                CanPause = info.CanPause;
            }

            CanGoPrevious = info.CanGoPrevious;
            CanGoNext = info.CanGoNext;
            CanSeek = info.CanSeek && _timelineExtrapolator.HasNativeDuration;
            CanChangeShuffle = info.CanChangeShuffle;
            CanChangeRepeat = info.CanChangeRepeat;
            PlaybackMode = info.PlaybackMode;
            OnPropertyChanged(nameof(HasSession));
            UpdateLyricActiveLineAndScheduleTimer();
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                App.Log($"[MusicWidget] Playback refresh failed: {ex.Message}");
            }
        }
        finally
        {
            Interlocked.Decrement(ref _playbackRefreshInFlightCount);
        }
    }

    private bool ShouldExposeTimelinePosition() =>
        !_timelineExtrapolator.UsesLocalFallbackClock ||
        _timelineExtrapolator.EffectiveDuration > TimeSpan.Zero ||
        !_hideTimelineWhenUnavailable;

    private void ProgressTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        bool unknownBufferingExpired = _timelineExtrapolator.IsUnknownBufferingExpired(nowUtc);
        if (_isDisposed ||
            !_isWindowVisible ||
            !_isWindowRevealCompleted ||
            !ShouldRunProgressTimer(
                PlaybackState,
                _hasCurrentMediaInfo,
                unknownBufferingExpired))
        {
            sender.Stop();
            UpdateMusicTimerDiagnostics();
            if (!_isDisposed &&
                PlaybackState == MusicPlaybackState.Unknown &&
                unknownBufferingExpired)
            {
                OnPropertyChanged(nameof(IsPlaying));
                _ = RefreshPlaybackAsync();
            }

            return;
        }

        if (!_isSeeking && ShouldExposeTimelinePosition())
        {
            Position = _timelineExtrapolator.GetPosition(nowUtc);
        }

        if (PlaybackState == MusicPlaybackState.Unknown)
        {
            _ = RefreshPlaybackAsync();
            return;
        }

        if (ConsumeMemoryEpochChanges())
        {
            _lastLiveReconcileUtc = nowUtc;
            _lastLiveTimelineProbeUtc = nowUtc;
            _ = ReconcileLiveSessionStateAsync();
        }
        else if (PlaybackState == MusicPlaybackState.Playing &&
                 _timelineExtrapolator.HasExceededNativeDuration(nowUtc, TrackEndReconcileGrace) &&
                 nowUtc - _lastLiveReconcileUtc >= TrackEndReconcileCooldown)
        {
            _lastLiveReconcileUtc = nowUtc;
            _lastLiveTimelineProbeUtc = nowUtc;
            _ = ReconcileLiveSessionStateAsync();
        }
        else if (PlaybackState == MusicPlaybackState.Playing &&
                 nowUtc - _lastLiveReconcileUtc >= PlayingFullReconcileFallbackInterval)
        {
            _lastLiveReconcileUtc = nowUtc;
            _lastLiveTimelineProbeUtc = nowUtc;
            _ = ReconcileLiveSessionStateAsync();
        }
        else if (PlaybackState == MusicPlaybackState.Playing &&
                 nowUtc - _lastLiveTimelineProbeUtc >= PlayingTimelineProbeInterval)
        {
            _lastLiveTimelineProbeUtc = nowUtc;
            _ = ProbeLiveTimelineStateAsync();
        }
    }

    private void OnMemoryReclaimCompleted()
    {
        if (_isDisposed)
        {
            return;
        }

        Interlocked.Exchange(ref _lastObservedReclaimEpoch, MemoryReclaimer.CompletedReclaimEpoch);
        if (!CanRunWindowMediaRefresh())
        {
            Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (CanRunWindowMediaRefresh())
                {
                    _ = ReconcileLiveSessionStateAsync();
                }
                else if (!_isDisposed)
                {
                    Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
                }
            });
            return;
        }

        _ = ReconcileLiveSessionStateAsync();
    }

    private bool ConsumeMemoryEpochChanges()
    {
        long currentReclaimEpoch = MemoryReclaimer.CompletedReclaimEpoch;
        long currentWorkingSetEpoch = App.WorkingSetTrimEpoch;
        long previousReclaimEpoch = Interlocked.Exchange(ref _lastObservedReclaimEpoch, currentReclaimEpoch);
        long previousWorkingSetEpoch = Interlocked.Exchange(ref _lastObservedWorkingSetTrimEpoch, currentWorkingSetEpoch);
        return currentReclaimEpoch != previousReclaimEpoch ||
            currentWorkingSetEpoch != previousWorkingSetEpoch;
    }

    internal Task ReconcileLiveSessionStateForTestingAsync() =>
        ReconcileLiveSessionStateAsync();

    internal Task ProbeLiveTimelineStateForTestingAsync() =>
        ProbeLiveTimelineStateAsync();

    internal static bool ShouldEscalateTimelineProbeToFullReconcile(
        bool hasCurrentMediaInfo,
        MusicPlaybackState currentPlaybackState,
        MusicPlaybackState resolvedLivePlaybackState,
        TimeSpan currentEffectiveDuration,
        TimeSpan extrapolatedPosition,
        bool isSeeking,
        MusicTimelineSnapshot? liveTimeline)
    {
        if (liveTimeline is null)
        {
            return hasCurrentMediaInfo;
        }

        if (!hasCurrentMediaInfo)
        {
            return liveTimeline.PlaybackState != MusicPlaybackState.Unknown ||
                liveTimeline.Duration > TimeSpan.Zero;
        }

        if (resolvedLivePlaybackState != currentPlaybackState)
        {
            return true;
        }

        if (liveTimeline.Duration > TimeSpan.Zero &&
            Math.Abs((liveTimeline.Duration - currentEffectiveDuration).TotalSeconds) > 1.0)
        {
            return true;
        }

        if (!isSeeking &&
            liveTimeline.Duration > TimeSpan.Zero &&
            extrapolatedPosition - liveTimeline.Position > TimeSpan.FromSeconds(4.5))
        {
            return true;
        }

        return false;
    }

    private async Task ProbeLiveTimelineStateAsync()
    {
        if (!CanRunWindowMediaRefresh() ||
            _isRefreshing ||
            Volatile.Read(ref _liveReconcileInFlight) != 0 ||
            Interlocked.CompareExchange(ref _liveTimelineProbeInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            MusicTimelineSnapshot? liveTimeline =
                await _musicSessionService.GetCurrentTimelineAsync(_preferredSessionId);
            if (_isDisposed)
            {
                return;
            }

            if (!CanRunWindowMediaRefresh())
            {
                Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
                return;
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            MusicPlaybackState resolvedPlaybackState = liveTimeline is null
                ? MusicPlaybackState.Unknown
                : ResolvePlaybackStateWithPendingIntent(liveTimeline.PlaybackState, nowUtc);
            if (ShouldEscalateTimelineProbeToFullReconcile(
                    _hasCurrentMediaInfo,
                    PlaybackState,
                    resolvedPlaybackState,
                    _timelineExtrapolator.EffectiveDuration,
                    _timelineExtrapolator.GetPosition(nowUtc),
                    _isSeeking,
                    liveTimeline))
            {
                _lastLiveReconcileUtc = nowUtc;
                await ReconcileLiveSessionStateAsync();
            }
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                App.LogVerbose($"[MusicWidget] Live timeline probe skipped: {ex.Message}");
            }
        }
        finally
        {
            Volatile.Write(ref _liveTimelineProbeInFlight, 0);
        }
    }

    private async Task ReconcileLiveSessionStateAsync()
    {
        if (!CanRunWindowMediaRefresh() ||
            _isRefreshing ||
            Interlocked.CompareExchange(ref _liveReconcileInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            MusicSessionInfo? liveInfo =
                await _musicSessionService.GetCurrentSessionInfoAsync(_preferredSessionId);
            if (_isDisposed)
            {
                return;
            }

            if (!CanRunWindowMediaRefresh())
            {
                Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
                return;
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            if (HasLiveSessionStateDiverged(liveInfo, nowUtc))
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                App.LogVerbose($"[MusicWidget] Live session reconcile skipped: {ex.Message}");
            }
        }
        finally
        {
            Volatile.Write(ref _liveReconcileInFlight, 0);
        }
    }

    private bool HasLiveSessionStateDiverged(MusicSessionInfo? liveInfo, DateTimeOffset nowUtc)
    {
        if (liveInfo is null)
        {
            return _hasCurrentMediaInfo;
        }

        if (!_hasCurrentMediaInfo)
        {
            return !string.IsNullOrWhiteSpace(liveInfo.Title) ||
                !string.IsNullOrWhiteSpace(liveInfo.SourceAppUserModelId) ||
                liveInfo.PlaybackState != MusicPlaybackState.Unknown;
        }

        if (!string.Equals(_placeholderCoverSessionId, liveInfo.SessionId, StringComparison.Ordinal) ||
            !string.Equals(_sourceAppUserModelId, liveInfo.SourceAppUserModelId, StringComparison.Ordinal) ||
            !string.Equals(Title, liveInfo.Title, StringComparison.Ordinal) ||
            !string.Equals(Artist, liveInfo.Artist, StringComparison.Ordinal) ||
            !string.Equals(Album, liveInfo.Album, StringComparison.Ordinal))
        {
            return true;
        }

        MusicPlaybackState effectivePlaybackState =
            ResolvePlaybackStateWithPendingIntent(liveInfo.PlaybackState, nowUtc);
        if (effectivePlaybackState != PlaybackState)
        {
            return true;
        }

        if (!_hasAppliedCoverForCurrentTrack && liveInfo.Thumbnail is not null)
        {
            return true;
        }

        if (liveInfo.Duration > TimeSpan.Zero &&
            Math.Abs((liveInfo.Duration - _timelineExtrapolator.EffectiveDuration).TotalSeconds) > 1.0)
        {
            return true;
        }

        if (_timelineExtrapolator.HasExceededNativeDuration(nowUtc, TrackEndReconcileGrace) &&
            liveInfo.Duration > TimeSpan.Zero &&
            liveInfo.Position < liveInfo.Duration - TimeSpan.FromSeconds(3))
        {
            return true;
        }

        return false;
    }

    private void LyricTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        UpdateLyricActiveLineAndScheduleTimer();
    }

    private bool CanRunWindowMediaRefresh() =>
        !_isDisposed && _isWindowVisible && _isWindowRevealCompleted;

    private bool CanRunMediaRefresh(bool isUserInitiated) =>
        !_isDisposed && (isUserInitiated || (_isWindowVisible && _isWindowRevealCompleted));

    private MusicPlaybackState ResolvePlaybackStateWithPendingIntent(
        MusicPlaybackState reportedState,
        DateTimeOffset nowUtc)
    {
        if (_pendingCommandTargetState is not { } targetState)
        {
            return reportedState;
        }

        if (reportedState == targetState)
        {
            _pendingCommandTargetState = null;
            return reportedState;
        }

        if (nowUtc < _pendingCommandTargetUntilUtc)
        {
            return targetState;
        }

        _pendingCommandTargetState = null;
        return reportedState;
    }

    private void ClearPendingPlayPauseIntent()
    {
        _pendingCommandTargetState = null;
        _pendingCommandTargetUntilUtc = default;
    }

    private void UpdateMusicTimerDiagnostics()
    {
        int activeTimers = (_progressTimer?.IsRunning == true ? 1 : 0) +
                           (_lyricTimer?.IsRunning == true ? 1 : 0);
        PerformanceLogger.ActiveMusicTimerCount = activeTimers;
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
            ShouldRunProgressTimer(
                PlaybackState,
                _hasCurrentMediaInfo,
                _timelineExtrapolator.IsUnknownBufferingExpired(DateTimeOffset.UtcNow)))
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

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(LyricsHeaderSubtitle));
        OnPropertyChanged(nameof(SourceDisplayName));
        OnPropertyChanged(nameof(SourcePickerTooltip));
        OnPropertyChanged(nameof(FollowSystemSourceLabel));
        OnPropertyChanged(nameof(NoAvailableSourcesLabel));
        OnPropertyChanged(nameof(PlayPauseTooltip));
        OnPropertyChanged(nameof(PreviousTooltip));
        OnPropertyChanged(nameof(NextTooltip));
        OnPropertyChanged(nameof(PlaybackModeTooltip));
        OnPropertyChanged(nameof(VolumeTooltip));
        OnPropertyChanged(nameof(SystemVolumeLabel));
        OnPropertyChanged(nameof(SessionVolumeLabel));
        OnPropertyChanged(nameof(SessionVolumeText));
        OnPropertyChanged(nameof(RefreshTooltip));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LyricsPlaceholderText));
        OnPropertyChanged(nameof(LyricsToggleTooltip));
        OnPropertyChanged(nameof(LyricsOffsetEarlierTooltip));
        OnPropertyChanged(nameof(LyricsOffsetLaterTooltip));
        OnPropertyChanged(nameof(LyricsOffsetResetTooltip));
        RefreshSubtitleState();
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
        SpectrumStyle = settings.Music.MusicSpectrumStyle;
        _hideTimelineWhenUnavailable = settings.Music.MusicHideTimelineWhenUnavailable;
        ShowLyrics = settings.Music.MusicShowLyrics;
        ShowCapsuleLyrics = settings.Music.MusicShowCapsuleLyrics;

        string nextLocalFolder = settings.Music.MusicLocalLyricsFolder ?? string.Empty;
        bool folderChanged = !string.Equals(_localLyricsFolder, nextLocalFolder, StringComparison.Ordinal);
        _localLyricsFolder = nextLocalFolder;

        OnPropertyChanged(nameof(ArtworkBackdropCornerRadius));
        if (!_isSeeking && _hasCurrentMediaInfo)
        {
            Position = ShouldExposeTimelinePosition()
                ? _timelineExtrapolator.GetPosition(DateTimeOffset.UtcNow)
                : TimeSpan.Zero;
        }

        UpdateTimelineRowEligibility();

        if ((folderChanged || (_showLyrics && _lastLyricsTrackSignature is null)) && _hasCurrentMediaInfo)
        {
            _lastLyricsTrackSignature = null;
            ScheduleLyricsLookupForCurrentTrack();
        }
    }

    private void UpdateTimelineRowEligibility()
    {
        bool eligible = ShouldHideTimelineRow(_hideTimelineWhenUnavailable, HasSeekableTimeline, Position);
        if (!eligible)
        {
            _timelineUnavailableSince = null;
            SetTimelineRowHidden(false);
            return;
        }

        if (!_hasCurrentMediaInfo)
        {
            _timelineUnavailableSince = null;
            SetTimelineRowHidden(true);
            return;
        }

        if (_timelineUnavailableSince is null)
        {
            _timelineUnavailableSince = DateTimeOffset.UtcNow;
            ScheduleTimelineRowHideCheck(_timelineUnavailableSince.Value);
        }
    }

    private void ScheduleTimelineRowHideCheck(DateTimeOffset episodeStarted)
    {
        if (_dispatcherQueue is null)
        {
            SetTimelineRowHidden(true);
            return;
        }

        _ = HideTimelineRowAfterDelayAsync(episodeStarted);
    }

    private async Task HideTimelineRowAfterDelayAsync(DateTimeOffset episodeStarted)
    {
        try
        {
            await Task.Delay(TimelineRowHideDelay);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (_isDisposed || _timelineUnavailableSince != episodeStarted)
        {
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => ApplyTimelineRowHideIfStillUnavailable(episodeStarted));
            return;
        }

        ApplyTimelineRowHideIfStillUnavailable(episodeStarted);
    }

    private void ApplyTimelineRowHideIfStillUnavailable(DateTimeOffset episodeStarted)
    {
        if (_isDisposed || _timelineUnavailableSince != episodeStarted)
        {
            return;
        }

        if (ShouldHideTimelineRow(_hideTimelineWhenUnavailable, HasSeekableTimeline, Position))
        {
            SetTimelineRowHidden(true);
        }
    }

    private void SetTimelineRowHidden(bool value)
    {
        if (_timelineRowHidden == value)
        {
            return;
        }

        _timelineRowHidden = value;
        OnPropertyChanged(nameof(TimelineRowVisibility));
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
            SettingsService.WidgetCornerPreferenceLarge => 16,
            _ => 16
        };
    }

    private void RaiseDisplayPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(SourceAppUserModelId));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LyricsToggleVisibility));
        OnPropertyChanged(nameof(HasSeekableTimeline));
        OnPropertyChanged(nameof(CanInteractWithProgress));
        OnPropertyChanged(nameof(SourceBadgeTextVisibility));
        OnPropertyChanged(nameof(SourceAppIcon));
        OnPropertyChanged(nameof(SourceAppIconVisibility));
        OnPropertyChanged(nameof(SourceFallbackIconVisibility));
        RefreshSubtitleState();
    }

    private MusicPlaybackMode GetNextPlaybackMode()
    {
        return PlaybackMode switch
        {
            MusicPlaybackMode.Normal when CanChangeRepeat => MusicPlaybackMode.Repeat,
            MusicPlaybackMode.Normal when CanChangeShuffle => MusicPlaybackMode.Shuffle,
            MusicPlaybackMode.Repeat when CanChangeRepeat => MusicPlaybackMode.RepeatOne,
            MusicPlaybackMode.Repeat when CanChangeShuffle => MusicPlaybackMode.Shuffle,
            MusicPlaybackMode.RepeatOne when CanChangeShuffle => MusicPlaybackMode.Shuffle,
            MusicPlaybackMode.RepeatOne => MusicPlaybackMode.Normal,
            MusicPlaybackMode.Shuffle => MusicPlaybackMode.Normal,
            _ => MusicPlaybackMode.Normal
        };
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";
        }

        return $"{value.Minutes:00}:{value.Seconds:00}";
    }

    internal static string FormatPositionText(TimeSpan position, TimeSpan duration) =>
        duration > TimeSpan.Zero || position > TimeSpan.Zero
            ? FormatTime(position)
            : "--:--";

    private static double NormalizeVolume(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(Math.Round(value, 3), 0.0, 1.0)
            : 0.0;
    }

    private static string FormatPercent(double value)
    {
        return $"{Math.Clamp((int)Math.Round(NormalizeVolume(value) * 100), 0, 100)}";
    }
}
