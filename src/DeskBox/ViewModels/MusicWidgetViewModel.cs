using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace DeskBox.ViewModels;

public sealed partial class MusicWidgetViewModel : ObservableObject, IDisposable
{
    private const int ProgressRefreshMs = 1000;
    private const int VisualizerRefreshMs = 140;
    private const int VisualizerTransitionRefreshMs = 33;
    private const double VisualizerTransitionDurationMs = 420;
    private static readonly double[] s_visualizerSeeds =
    [
        0.28, 0.52, 0.36, 0.68, 0.44, 0.58, 0.32, 0.74,
        0.40, 0.62, 0.48, 0.82, 0.34, 0.56, 0.70, 0.46,
        0.30, 0.64, 0.42, 0.76, 0.50, 0.60, 0.38, 0.86,
        0.54, 0.72, 0.36, 0.66, 0.44, 0.80, 0.32, 0.58,
        0.46, 0.68, 0.40, 0.78, 0.52, 0.62, 0.34, 0.84,
        0.48, 0.70, 0.42, 0.60, 0.30, 0.74, 0.56, 0.88,
        0.38, 0.64, 0.50, 0.76, 0.44, 0.58, 0.36, 0.82,
        0.54, 0.72, 0.40, 0.66, 0.32, 0.78, 0.46, 0.60
    ];

    private readonly MusicSessionService _musicSessionService;
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly WidgetConfig _config;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _progressTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _visualizerTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _visualizerTransitionTimer;
    private readonly Random _random = new();

    private string? _preferredSessionId;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
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
    private bool _isRefreshing;
    private bool _isSeeking;
    private bool _isDisposed;
    private DateTimeOffset _visualizerTransitionStartedAt;
    private double[] _visualizerTransitionStartHeights = [];
    private double[] _visualizerTransitionStartDotSizes = [];
    private double[] _visualizerTransitionStartOpacities = [];
    private double _textSize = SettingsService.DefaultTextSize;
    private double _seekValue;
    private bool _useArtworkBackdrop = true;
    private bool _showRhythmBars = true;
    private string _rhythmStyle = SettingsService.MusicRhythmStyleSoftWave;
    private bool _enableCoverHoverMotion = true;
    private Color _artworkColor = AccentColorHelper.DefaultAccentColor;

    public MusicWidgetViewModel(
        WidgetConfig config,
        MusicSessionService musicSessionService,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = null)
    {
        _config = config;
        _musicSessionService = musicSessionService;
        _localizationService = localizationService;
        _settingsService = settingsService;
        _dispatcherQueue = dispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

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
            _progressTimer.Interval = TimeSpan.FromMilliseconds(ProgressRefreshMs);
            _progressTimer.IsRepeating = true;
            _progressTimer.Tick += ProgressTimer_Tick;

            _visualizerTimer = _dispatcherQueue.CreateTimer();
            _visualizerTimer.Interval = TimeSpan.FromMilliseconds(VisualizerRefreshMs);
            _visualizerTimer.IsRepeating = true;
            _visualizerTimer.Tick += VisualizerTimer_Tick;

            _visualizerTransitionTimer = _dispatcherQueue.CreateTimer();
            _visualizerTransitionTimer.Interval = TimeSpan.FromMilliseconds(VisualizerTransitionRefreshMs);
            _visualizerTransitionTimer.IsRepeating = true;
            _visualizerTransitionTimer.Tick += VisualizerTransitionTimer_Tick;
        }

        foreach (var seed in s_visualizerSeeds)
        {
            VisualizerBars.Add(new MusicBarViewModel(ScaleBar(seed)));
        }

        AttachServiceEvents();
    }

    public ObservableCollection<MusicBarViewModel> VisualizerBars { get; } = [];

    public ObservableCollection<string> SessionDisplayNames { get; } = [];

    public ObservableCollection<string> SessionIds { get; } = [];

    public string DisplayName => _config.IsDefaultTitle
        ? _localizationService.T("Music.Title")
        : _config.Name;

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
            }
        }
    }

    public bool ShowRhythmBars
    {
        get => _showRhythmBars;
        private set
        {
            if (SetProperty(ref _showRhythmBars, value))
            {
                OnPropertyChanged(nameof(RhythmBarsVisibility));
                OnPropertyChanged(nameof(SoftWaveVisibility));
                OnPropertyChanged(nameof(GlassSpectrumVisibility));
                OnPropertyChanged(nameof(DotPulseVisibility));
                OnPropertyChanged(nameof(LineSpectrumVisibility));
                OnPropertyChanged(nameof(RhythmBarsOpacity));
                UpdateVisualizerTimer();
            }
        }
    }

    public string RhythmStyle
    {
        get => _rhythmStyle;
        private set
        {
            string normalizedValue = SettingsService.NormalizeMusicRhythmStyle(value);
            if (SetProperty(ref _rhythmStyle, normalizedValue))
            {
                OnPropertyChanged(nameof(SoftWaveVisibility));
                OnPropertyChanged(nameof(GlassSpectrumVisibility));
                OnPropertyChanged(nameof(DotPulseVisibility));
                OnPropertyChanged(nameof(LineSpectrumVisibility));
                UpdateVisualizerTimer();
            }
        }
    }

    public bool EnableCoverHoverMotion
    {
        get => _enableCoverHoverMotion;
        private set => SetProperty(ref _enableCoverHoverMotion, value);
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
                OnPropertyChanged(nameof(RhythmBarsOpacity));
                UpdateVisualizerTimer();
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
                OnPropertyChanged(nameof(SecondaryTextSize));
                OnPropertyChanged(nameof(CaptionTextSize));
            }
        }
    }

    public double TitleTextSize => Math.Min(SettingsService.MaxTextSize + 3, TextSize + 4);

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

    public bool HasSeekableTimeline => Duration > TimeSpan.Zero && SeekMaximum > 1;

    public bool CanInteractWithProgress => CanSeek && HasSeekableTimeline;

    public Visibility ThumbnailVisibility => ThumbnailImage is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ThumbnailPlaceholderVisibility => ThumbnailImage is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SessionPickerVisibility => SessionIds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ArtworkBackdropVisibility => UseArtworkBackdrop ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RhythmBarsVisibility => ShowRhythmBars ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SoftWaveVisibility => ShowRhythmBars && RhythmStyle == SettingsService.MusicRhythmStyleSoftWave
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility GlassSpectrumVisibility => ShowRhythmBars && RhythmStyle == SettingsService.MusicRhythmStyleGlassSpectrum
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility DotPulseVisibility => ShowRhythmBars && RhythmStyle == SettingsService.MusicRhythmStyleDotPulse
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LineSpectrumVisibility => ShowRhythmBars && RhythmStyle == SettingsService.MusicRhythmStyleLineSpectrum
        ? Visibility.Visible
        : Visibility.Collapsed;

    public double RhythmBarsOpacity => ShowRhythmBars
        ? IsPlaying ? 0.92 : 0.48
        : 0.0;

    public Color ArtworkBackdropStartColor => Color.FromArgb(0x5A, _artworkColor.R, _artworkColor.G, _artworkColor.B);

    public Color ArtworkBackdropMidColor => Color.FromArgb(0x20, _artworkColor.R, _artworkColor.G, _artworkColor.B);

    public Color ArtworkBackdropEndColor => Color.FromArgb(0x00, _artworkColor.R, _artworkColor.G, _artworkColor.B);

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

    public async Task InitializeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await RefreshAsync();
        if (_isDisposed)
        {
            return;
        }

        _progressTimer?.Start();
        UpdateVisualizerTimer();
    }

    public async Task RefreshAsync()
    {
        if (_isDisposed || _isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            await _musicSessionService.InitializeAsync();
            if (_isDisposed)
            {
                return;
            }

            RefreshSessionList();
            var info = await _musicSessionService.GetCurrentSessionInfoAsync(_preferredSessionId);
            if (_isDisposed)
            {
                return;
            }

            await ApplyInfoAsync(info);
        }
        catch (Exception ex)
        {
            if (_isDisposed)
            {
                return;
            }

            App.Log($"[MusicWidget] Refresh failed: {ex}");
            await ApplyInfoAsync(null);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public async Task SelectSessionAsync(int index)
    {
        if (_isDisposed)
        {
            return;
        }

        if (index < 0 || index >= SessionIds.Count)
        {
            return;
        }

        _preferredSessionId = SessionIds[index];
        await _musicSessionService.TrySetPreferredSessionAsync(_preferredSessionId);
        OnPropertyChanged(nameof(SelectedSessionIndex));
        await RefreshAsync();
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        if (IsPlaying)
        {
            await _musicSessionService.TryPauseAsync(_preferredSessionId);
        }
        else
        {
            await _musicSessionService.TryPlayAsync(_preferredSessionId);
        }

        await RefreshAsync();
        await Task.Delay(180);
        await RefreshAsync();
    }

    public async Task PreviousAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await _musicSessionService.TryPreviousAsync(_preferredSessionId);
        await RefreshAsync();
    }

    public async Task NextAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await _musicSessionService.TryNextAsync(_preferredSessionId);
        await RefreshAsync();
    }

    public void BeginSeek()
    {
        _isSeeking = true;
    }

    public async Task CommitSeekAsync()
    {
        if (_isDisposed || !_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        if (!CanInteractWithProgress)
        {
            SeekValue = Math.Clamp(Position.TotalSeconds, 0, SeekMaximum);
            return;
        }

        var targetPosition = TimeSpan.FromSeconds(SeekValue);
        bool didSeek = await _musicSessionService.TrySeekAsync(_preferredSessionId, targetPosition);
        if (!didSeek)
        {
            App.Log("[MusicWidget] Seek request was rejected by the active media session.");
            await RefreshAsync();
            return;
        }

        Position = targetPosition;
        await Task.Delay(260);
        await RefreshAsync();
    }

    public void ApplyAppearance()
    {
        if (_settingsService is null)
        {
            return;
        }

        TextSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
        ApplyMusicSettings(_settingsService.Settings);
    }

    public void OnActivated()
    {
        if (_isDisposed)
        {
            return;
        }

        _progressTimer?.Start();
        UpdateVisualizerTimer();
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DetachServiceEvents();
        if (_progressTimer is not null)
        {
            _progressTimer.Stop();
            _progressTimer.Tick -= ProgressTimer_Tick;
        }

        if (_visualizerTimer is not null)
        {
            _visualizerTimer.Stop();
            _visualizerTimer.Tick -= VisualizerTimer_Tick;
        }

        if (_visualizerTransitionTimer is not null)
        {
            _visualizerTransitionTimer.Stop();
            _visualizerTransitionTimer.Tick -= VisualizerTransitionTimer_Tick;
        }
        _localizationService.LanguageChanged -= OnLanguageChanged;
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }

        _musicSessionService.Dispose();
    }

    private async Task ApplyInfoAsync(MusicSessionInfo? info)
    {
        if (info is null)
        {
            Title = string.Empty;
            Artist = string.Empty;
            Album = string.Empty;
            SourceDisplayName = string.Empty;
            PlaybackState = MusicPlaybackState.Unknown;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            ThumbnailImage = null;
            ArtworkColor = AccentColorHelper.DefaultAccentColor;
            CanPlay = false;
            CanPause = false;
            CanGoPrevious = false;
            CanGoNext = false;
            CanSeek = false;
            RaiseDisplayPropertiesChanged();
            return;
        }

        _preferredSessionId ??= info.SessionId;
        Title = info.Title;
        Artist = info.Artist;
        Album = info.Album;
        SourceDisplayName = info.SourceDisplayName;
        PlaybackState = info.PlaybackState;
        Position = info.Position;
        Duration = info.Duration;
        CanPlay = info.CanPlay;
        CanPause = info.CanPause;
        CanGoPrevious = info.CanGoPrevious;
        CanGoNext = info.CanGoNext;
        CanSeek = info.CanSeek && info.Duration > TimeSpan.Zero;
        ThumbnailImage = await CreateThumbnailImageAsync(info);
        ArtworkColor = await TryReadArtworkColorAsync(info) ?? AccentColorHelper.DefaultAccentColor;
        RaiseDisplayPropertiesChanged();
    }

    private Color ArtworkColor
    {
        get => _artworkColor;
        set
        {
            if (_artworkColor.Equals(value))
            {
                return;
            }

            _artworkColor = value;
            OnPropertyChanged(nameof(ArtworkBackdropStartColor));
            OnPropertyChanged(nameof(ArtworkBackdropMidColor));
            OnPropertyChanged(nameof(ArtworkBackdropEndColor));
        }
    }

    private async Task<BitmapImage?> CreateThumbnailImageAsync(MusicSessionInfo info)
    {
        if (info.Thumbnail is null)
        {
            return null;
        }

        try
        {
            using var stream = await info.Thumbnail.OpenReadAsync();
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Failed to load thumbnail: {ex.Message}");
            return null;
        }
    }

    private static async Task<Color?> TryReadArtworkColorAsync(MusicSessionInfo info)
    {
        if (info.Thumbnail is null)
        {
            return null;
        }

        try
        {
            using var stream = await info.Thumbnail.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            uint targetWidth = Math.Max(1, Math.Min(32, decoder.PixelWidth));
            uint targetHeight = Math.Max(1, Math.Min(32, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = data.DetachPixelData();
            if (pixels.Length < 4)
            {
                return null;
            }

            double totalR = 0;
            double totalG = 0;
            double totalB = 0;
            double weightTotal = 0;

            for (int i = 0; i <= pixels.Length - 4; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 24)
                {
                    continue;
                }

                double saturationWeight = 0.55 + (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) / 255.0;
                double alphaWeight = a / 255.0;
                double weight = saturationWeight * alphaWeight;
                totalR += r * weight;
                totalG += g * weight;
                totalB += b * weight;
                weightTotal += weight;
            }

            if (weightTotal <= 0.01)
            {
                return null;
            }

            byte averageR = ClampByte(totalR / weightTotal);
            byte averageG = ClampByte(totalG / weightTotal);
            byte averageB = ClampByte(totalB / weightTotal);
            return Color.FromArgb(0xFF, averageR, averageG, averageB);
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Failed to sample thumbnail color: {ex.Message}");
            return null;
        }
    }

    private void RefreshSessionList()
    {
        var ids = _musicSessionService.GetSessionIds();
        SessionIds.Clear();
        SessionDisplayNames.Clear();

        foreach (var id in ids)
        {
            SessionIds.Add(id);
            SessionDisplayNames.Add(string.IsNullOrWhiteSpace(id) ? _localizationService.T("Music.SourceUnknown") : id);
        }

        OnPropertyChanged(nameof(SessionPickerVisibility));
        OnPropertyChanged(nameof(SelectedSessionIndex));
    }

    private void AttachServiceEvents()
    {
        _musicSessionService.SessionsChanged += MusicSessionService_Changed;
        _musicSessionService.CurrentSessionChanged += MusicSessionService_Changed;
        _musicSessionService.PlaybackInfoChanged += MusicSessionService_Changed;
        _musicSessionService.MediaPropertiesChanged += MusicSessionService_Changed;
        _musicSessionService.TimelinePropertiesChanged += MusicSessionService_Changed;
    }

    private void DetachServiceEvents()
    {
        _musicSessionService.SessionsChanged -= MusicSessionService_Changed;
        _musicSessionService.CurrentSessionChanged -= MusicSessionService_Changed;
        _musicSessionService.PlaybackInfoChanged -= MusicSessionService_Changed;
        _musicSessionService.MediaPropertiesChanged -= MusicSessionService_Changed;
        _musicSessionService.TimelinePropertiesChanged -= MusicSessionService_Changed;
    }

    private void MusicSessionService_Changed(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = RefreshAsync());
            return;
        }

        _ = RefreshAsync();
    }

    private void ProgressTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        if (IsPlaying && Duration > TimeSpan.Zero && !_isSeeking)
        {
            Position = TimeSpan.FromSeconds(Math.Min(Duration.TotalSeconds, Position.TotalSeconds + ProgressRefreshMs / 1000.0));
        }
    }

    private void VisualizerTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        for (int i = 0; i < VisualizerBars.Count; i++)
        {
            double seed = s_visualizerSeeds[i % s_visualizerSeeds.Length];
            double jitter = _random.NextDouble() * 0.28;
            double value = Math.Clamp(seed + jitter - 0.1, 0.12, 1.0);
            VisualizerBars[i].Height = ScaleBar(value);
            VisualizerBars[i].DotSize = ScaleDot(value);
            VisualizerBars[i].Opacity = 0.68 + _random.NextDouble() * 0.32;
        }
    }

    private void UpdateVisualizerTimer()
    {
        if (IsPlaying && ShowRhythmBars)
        {
            _visualizerTransitionTimer?.Stop();
            _visualizerTimer?.Start();
            return;
        }

        _visualizerTimer?.Stop();
        if (!ShowRhythmBars)
        {
            _visualizerTransitionTimer?.Stop();
            return;
        }

        StartVisualizerTransitionToIdle();
    }

    private void StartVisualizerTransitionToIdle()
    {
        if (VisualizerBars.Count == 0)
        {
            return;
        }

        _visualizerTransitionStartHeights = VisualizerBars.Select(bar => bar.Height).ToArray();
        _visualizerTransitionStartDotSizes = VisualizerBars.Select(bar => bar.DotSize).ToArray();
        _visualizerTransitionStartOpacities = VisualizerBars.Select(bar => bar.Opacity).ToArray();
        _visualizerTransitionStartedAt = DateTimeOffset.UtcNow;
        _visualizerTransitionTimer?.Start();
        ApplyVisualizerTransitionFrame(0);
    }

    private void VisualizerTransitionTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        double elapsedMs = (DateTimeOffset.UtcNow - _visualizerTransitionStartedAt).TotalMilliseconds;
        double progress = Math.Clamp(elapsedMs / VisualizerTransitionDurationMs, 0.0, 1.0);
        double easedProgress = 1 - Math.Pow(1 - progress, 3);
        ApplyVisualizerTransitionFrame(easedProgress);

        if (progress >= 1.0)
        {
            sender.Stop();
        }
    }

    private void ApplyVisualizerTransitionFrame(double progress)
    {
        for (int i = 0; i < VisualizerBars.Count; i++)
        {
            double targetValue = s_visualizerSeeds[i % s_visualizerSeeds.Length] * 0.45;
            double targetHeight = ScaleBar(targetValue);
            double targetDotSize = ScaleDot(targetValue);
            const double targetOpacity = 0.54;

            double startHeight = i < _visualizerTransitionStartHeights.Length
                ? _visualizerTransitionStartHeights[i]
                : targetHeight;
            double startDotSize = i < _visualizerTransitionStartDotSizes.Length
                ? _visualizerTransitionStartDotSizes[i]
                : targetDotSize;
            double startOpacity = i < _visualizerTransitionStartOpacities.Length
                ? _visualizerTransitionStartOpacities[i]
                : targetOpacity;

            VisualizerBars[i].Height = Lerp(startHeight, targetHeight, progress);
            VisualizerBars[i].DotSize = Lerp(startDotSize, targetDotSize, progress);
            VisualizerBars[i].Opacity = Lerp(startOpacity, targetOpacity, progress);
        }
    }

    private void OnLanguageChanged()
    {
        if (_isDisposed)
        {
            return;
        }

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
        ShowRhythmBars = settings.MusicShowRhythmBars;
        RhythmStyle = SettingsService.NormalizeMusicRhythmStyle(settings.MusicRhythmStyle);
        EnableCoverHoverMotion = settings.MusicEnableCoverHoverMotion;
    }

    private void RaiseDisplayPropertiesChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(SourceDisplayName));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PlayIconVisibility));
        OnPropertyChanged(nameof(PauseIconVisibility));
        OnPropertyChanged(nameof(PlayPauseTooltip));
        OnPropertyChanged(nameof(PreviousTooltip));
        OnPropertyChanged(nameof(NextTooltip));
        OnPropertyChanged(nameof(RefreshTooltip));
        OnPropertyChanged(nameof(ThumbnailVisibility));
        OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
        OnPropertyChanged(nameof(ArtworkBackdropVisibility));
        OnPropertyChanged(nameof(HasSeekableTimeline));
        OnPropertyChanged(nameof(CanInteractWithProgress));
        OnPropertyChanged(nameof(RhythmBarsVisibility));
        OnPropertyChanged(nameof(SoftWaveVisibility));
        OnPropertyChanged(nameof(GlassSpectrumVisibility));
        OnPropertyChanged(nameof(DotPulseVisibility));
        OnPropertyChanged(nameof(LineSpectrumVisibility));
        OnPropertyChanged(nameof(RhythmBarsOpacity));
    }

    private static double ScaleBar(double value)
    {
        return Math.Round(3 + value * 22);
    }

    private static double ScaleDot(double value)
    {
        return Math.Round(2.5 + value * 4.5);
    }

    private static double Lerp(double start, double end, double progress)
    {
        return start + (end - start) * Math.Clamp(progress, 0.0, 1.0);
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
}
