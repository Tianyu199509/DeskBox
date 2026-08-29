using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Launcher = Windows.System.Launcher;

namespace DeskBox.ViewModels;

public sealed partial class GlanceWidgetViewModel : ObservableObject, IDisposable
{
    private const double CalendarPanelMaximumWidth = 360;
    private const double CalendarPanelHorizontalInset = 28;

    private readonly GlanceWidgetStore _store;
    private readonly GlanceImageService _imageService;
    private readonly ICalendarPresentationSource _calendarSource;
    private readonly GlanceTraditionalCalendarService _traditionalCalendarService = new();
    private readonly GlanceFestivalService _festivalService = new();
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly DispatcherQueueTimer _rotationTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private GlanceWidgetData _settings = new();
    private IReadOnlyList<GlanceImageInfo> _images = [];
    private GlanceImageInfo? _currentImage;
    private string _timeText = string.Empty;
    private string _dateText = string.Empty;
    private string _compactCalendarDateText = string.Empty;
    private string _weekdayText = string.Empty;
    private string _traditionalCalendarTitle = string.Empty;
    private string _statusText = string.Empty;
    private bool _isLoading;
    private bool _isPaused;
    private bool _isWindowVisible = true;
    private bool _isCompact;
    private bool _onlineRefreshCompletedForSession;
    private CancellationTokenSource? _onlineRefreshCts;
    private bool _isDisposed;
    private double _availableWidth = 360;
    private double _availableHeight = 260;
    private int _currentIndex = -1;
    private int _calendarLoadVersion;
    private DateOnly _displayedCalendarMonth = new(
        DateTime.Today.Year,
        DateTime.Today.Month,
        1);

    public GlanceWidgetViewModel(
        WidgetConfig config,
        LocalizationService localizationService,
        GlanceWidgetStore? store = null,
        GlanceImageService? imageService = null,
        ICalendarPresentationSource? calendarSource = null,
        DispatcherQueue? dispatcherQueue = null,
        SettingsService? settingsService = null)
    {
        if (config.WidgetKind != WidgetKind.Glance)
        {
            throw new ArgumentException("Glance content requires a Glance widget config.", nameof(config));
        }

        Config = config;
        _localizationService = localizationService;
        _store = store ?? GlanceWidgetStore.ForWidget(config.Id);
        _imageService = imageService ?? new GlanceImageService();
        _calendarSource = calendarSource ?? new LocalCalendarPresentationSource();
        _settingsService = settingsService;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Glance content must be created on a UI thread.");

        _clockTimer = _dispatcherQueue.CreateTimer();
        _clockTimer.IsRepeating = false;
        _clockTimer.Tick += ClockTimer_Tick;
        _rotationTimer = _dispatcherQueue.CreateTimer();
        _rotationTimer.IsRepeating = true;
        _rotationTimer.Tick += RotationTimer_Tick;

        _store.Changed += Store_Changed;
        _localizationService.LanguageChanged += LocalizationService_LanguageChanged;
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged += SettingsService_SettingsChanged;
        }
    }

    public WidgetConfig Config { get; }
    public ObservableCollection<string> WeekdayHeaders { get; } = [];
    public ObservableCollection<GlanceCalendarDay> CalendarDays { get; } = [];
    public DateOnly DisplayedCalendarMonth => _displayedCalendarMonth;
    public string CalendarLanguage => GetCulture().Name;

    public GlanceWidgetData Settings => _settings;
    public string TimeText { get => _timeText; private set => SetProperty(ref _timeText, value); }
    public string DateText { get => _dateText; private set => SetProperty(ref _dateText, value); }
    public string CompactCalendarDateText { get => _compactCalendarDateText; private set => SetProperty(ref _compactCalendarDateText, value); }
    public string WeekdayText { get => _weekdayText; private set => SetProperty(ref _weekdayText, value); }
    public string TraditionalCalendarTitle
    {
        get => _traditionalCalendarTitle;
        private set
        {
            if (SetProperty(ref _traditionalCalendarTitle, value))
            {
                OnPropertyChanged(nameof(HasTraditionalCalendar));
                OnPropertyChanged(nameof(ShowCalendarTraditionalDetails));
                OnPropertyChanged(nameof(CalendarPanelHeight));
            }
        }
    }
    public bool HasTraditionalCalendar => !string.IsNullOrWhiteSpace(TraditionalCalendarTitle);
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(CanAdvanceImage));
            }
        }
    }
    public bool IsPaused { get => _isPaused; private set { if (SetProperty(ref _isPaused, value)) { OnPropertyChanged(nameof(PauseToolTip)); UpdateRotationTimer(); } } }
    public string PauseToolTip => _localizationService.T(IsPaused ? "Glance.Actions.Resume" : "Glance.Actions.Pause");
    public string NextToolTip => _localizationService.T("Glance.Actions.Next");
    public GlanceImageInfo? CurrentImage
    {
        get => _currentImage;
        private set
        {
            if (SetProperty(ref _currentImage, value))
            {
                OnPropertyChanged(nameof(CurrentImagePath));
                OnPropertyChanged(nameof(PhotoInfoText));
                OnPropertyChanged(nameof(HasPhotoInfo));
                OnPropertyChanged(nameof(HasCurrentImageContextAction));
                OnPropertyChanged(nameof(IsCurrentImageOnline));
                OnPropertyChanged(nameof(HasCurrentImage));
                OnPropertyChanged(nameof(HasVisibleCurrentImage));
                OnPropertyChanged(nameof(ShowPhotoControls));
                OnPropertyChanged(nameof(ShowNonCalendarImageReadability));
                OnPropertyChanged(nameof(ShowCalendarImageReadability));
                OnPropertyChanged(nameof(ShowExpandedCalendarImageReadability));
            }
        }
    }
    public string? CurrentImagePath => CurrentImage?.LocalPath;
    public string PhotoInfoText => string.Join(" · ", new[] { CurrentImage?.Author, CurrentImage?.License }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public bool HasPhotoInfo => CurrentImage?.IsOnline == true;
    public int ImageCount => _images.Count;
    public bool CanAdvanceImage => !IsLoading && ImageCount > 1;
    public bool CanPauseRotation => _settings.RotationIntervalMinutes > 0 && ImageCount > 1;
    public bool IsCurrentImageOnline => CurrentImage?.IsOnline == true;
    public bool HasCurrentImage => CurrentImage is not null;
    public double BackgroundImageTransparency =>
        Math.Clamp(_settings.BackgroundImageTransparency, 0.0, 1.0);
    public double BackgroundImageOpacity => 1.0 - BackgroundImageTransparency;
    public bool HasVisibleCurrentImage => HasCurrentImage && BackgroundImageOpacity > 0.001;
    public bool HasCurrentImageContextAction => IsCurrentImageOnline ||
        (!string.IsNullOrWhiteSpace(CurrentImagePath) && File.Exists(CurrentImagePath));

    public bool ShowTime => _settings.ShowTime;
    public bool ShowDate => _settings.ShowDate;
    public bool ShowYear => _settings.ShowYear;
    public bool ShowWeekday => _settings.ShowWeekday;
    public bool ShowCalendar => _settings.ShowCalendar && _availableWidth >= 300 && _availableHeight >= 280;
    public bool ShowPhotoControls => _settings.ShowPhotoControls && HasCurrentImage;
    public bool IsForegroundVisible => ShowTime || ShowDate || ShowWeekday || ShowCalendar;
    public bool IsImmersiveLayout => _settings.Layout == GlanceLayoutMode.Immersive ||
        (_settings.Layout == GlanceLayoutMode.Calendar && !ShowCalendar);
    public bool IsCenteredLayout => _settings.Layout == GlanceLayoutMode.Centered;
    public bool IsEditorialLayout => _settings.Layout == GlanceLayoutMode.Editorial;
    public bool IsCalendarLayout => _settings.Layout == GlanceLayoutMode.Calendar && ShowCalendar;
    public bool IsNonCalendarForeground => IsForegroundVisible && !IsCalendarLayout;
    public bool IsCompactCalendarPresentation =>
        IsCalendarLayout && GlanceCalendarLayoutCalculator.IsCompact(_availableHeight);
    public bool IsExpandedCalendarPresentation => IsCalendarLayout && !IsCompactCalendarPresentation;
    public bool ShowNonCalendarImageReadability => HasVisibleCurrentImage && IsNonCalendarForeground;
    public bool ShowCalendarImageReadability => HasVisibleCurrentImage && IsCalendarLayout;
    public bool ShowExpandedCalendarImageReadability => HasVisibleCurrentImage && IsExpandedCalendarPresentation;
    public double ReadabilityStrengthOpacity => _settings.Readability switch
    {
        GlanceReadabilityMode.None => 0,
        GlanceReadabilityMode.Strong => 0.5,
        _ => 0.28
    };
    public double ReadabilityOpacity => IsForegroundVisible ? ReadabilityStrengthOpacity : 0;
    public FontFamily TimeFontFamily => new(string.IsNullOrWhiteSpace(_settings.TimeFontFamily)
        ? "XamlAutoFontFamily"
        : _settings.TimeFontFamily);
    public double TimeFontSize => RoundFontSize(
        Math.Clamp(Math.Min(_availableWidth * 0.18, _availableHeight * 0.28), 38, 78) * _settings.TimeScale);
    public double CompactTimeFontSize => RoundFontSize(
        Math.Clamp(Math.Min(_availableWidth * 0.13, _availableHeight * 0.2), 30, 60) * _settings.TimeScale);
    public double CalendarCompactTimeFontSize => RoundFontSize(Math.Clamp(
        Math.Min(_availableWidth * 0.078, _availableHeight * 0.095),
        22,
        28) * _settings.TimeScale);
    public double CalendarPanelHeight
        => Math.Round(GlanceCalendarLayoutCalculator.CalculatePanelHeight(
            _availableHeight,
            IsCompactCalendarPresentation,
            HasTraditionalCalendar));
    public double CalendarPanelMaxWidth => CalendarPanelMaximumWidth;
    public double CalendarPanelWidth => Math.Round(Math.Clamp(
        _availableWidth - CalendarPanelHorizontalInset,
        272,
        CalendarPanelMaximumWidth));
    public double CalendarDayItemMinimumHeight =>
        Math.Round(GlanceCalendarLayoutCalculator.CalculateDayHeight(
            CalendarPanelHeight,
            IsCompactCalendarPresentation,
            HasTraditionalCalendar) * 2) / 2;
    public bool ShowCalendarTraditionalDetails =>
        GlanceCalendarLayoutCalculator.ShouldShowTraditionalDetails(
            CalendarPanelWidth,
            CalendarDayItemMinimumHeight,
            IsCompactCalendarPresentation,
            HasTraditionalCalendar);
    public CornerRadius CalendarCornerRadius => new(
        WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
            WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                _settingsService?.Settings.WidgetCornerPreference)));
    public string CalendarMaterialType =>
        WindowsCompatibilityService.ResolveWidgetMaterialType(
            _settingsService?.Settings.WidgetMaterialType ??
            SettingsService.WidgetMaterialTypeMica);
    public double CalendarMaterialOpacity => Math.Clamp(
        _settingsService?.Settings.WidgetOpacity ?? SettingsService.DefaultWidgetOpacity,
        SettingsService.MinWidgetOpacity,
        SettingsService.MaxWidgetOpacity);
    public double CalendarMaterialIntensity => Math.Clamp(
        _settingsService?.Settings.WidgetMaterialIntensity ?? SettingsService.DefaultWidgetMaterialIntensity,
        SettingsService.MinWidgetMaterialIntensity,
        SettingsService.MaxWidgetMaterialIntensity);
    public GlanceCalendarMaterialMode CalendarMaterialMode => _settings.CalendarMaterialMode;
    public double CalendarImageMaterialTransparency =>
        Math.Clamp(_settings.CalendarImageMaterialTransparency, 0.0, 1.0);
    public GlanceTraditionalCalendarMode TraditionalCalendarMode =>
        _settings.TraditionalCalendarMode;
    public bool ShowChineseFestivals => _settings.ShowChineseFestivals;
    public GlanceTransitionMode Transition => _settings.Transition;
    public GlanceTransitionSpeed TransitionSpeed => _settings.TransitionSpeed;
    public GlanceImageFitMode ImageFit => _settings.ImageFit;
    public GlanceImageFocus ImageFocus => _settings.ImageFocus;

    public async Task InitializeAsync()
    {
        _settings = await _store.LoadAsync();
        ApplySettingsProperties();
        UpdateDateAndTime();
        await UpdateCalendarAsync();
        await ReloadImagesAsync(refreshOnline: false);
        UpdateTimers();

        if (IsOnlineSource(_settings.BackgroundSource))
        {
            _ = RefreshOnlineInBackgroundAsync();
        }
    }

    public async Task RefreshAsync()
    {
        await ReloadImagesAsync(refreshOnline: IsOnlineSource(_settings.BackgroundSource));
    }

    public void ApplyAppearance()
    {
        OnPropertyChanged(nameof(ReadabilityStrengthOpacity));
        OnPropertyChanged(nameof(ReadabilityOpacity));
        OnPropertyChanged(nameof(CalendarCornerRadius));
        RaiseCalendarMaterialProperties();
    }

    public void OnActivated()
    {
        UpdateDateAndTime();
    }

    public void OnDeactivated()
    {
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (!visible)
        {
            CancelOnlineRefresh();
        }
        UpdateTimers();
        if (visible)
        {
            UpdateDateAndTime();
            if (!_isCompact && IsOnlineSource(_settings.BackgroundSource) && !_onlineRefreshCompletedForSession)
            {
                _ = RefreshOnlineInBackgroundAsync();
            }
        }
    }

    public void OnCompactStateChanged(bool collapsed)
    {
        _isCompact = collapsed;
        if (collapsed)
        {
            CancelOnlineRefresh();
        }
        UpdateTimers();
        if (!collapsed && _isWindowVisible && IsOnlineSource(_settings.BackgroundSource) && !_onlineRefreshCompletedForSession)
        {
            _ = RefreshOnlineInBackgroundAsync();
        }
    }

    public void OnWindowRevealCompleted()
    {
        if (_isWindowVisible && !_isCompact &&
            IsOnlineSource(_settings.BackgroundSource) &&
            !_onlineRefreshCompletedForSession &&
            _images.Count == 0)
        {
            _ = RefreshOnlineInBackgroundAsync();
        }
    }

    public void UpdateAvailableSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (Math.Abs(_availableWidth - width) < 0.01 &&
            Math.Abs(_availableHeight - height) < 0.01)
        {
            return;
        }

        bool oldShowCalendar = ShowCalendar;
        bool oldCompactCalendar = IsCompactCalendarPresentation;
        double oldTimeFontSize = TimeFontSize;
        double oldCompactTimeFontSize = CompactTimeFontSize;
        double oldCalendarCompactTimeFontSize = CalendarCompactTimeFontSize;
        double oldPanelHeight = CalendarPanelHeight;
        double oldPanelWidth = CalendarPanelWidth;
        double oldDayItemHeight = CalendarDayItemMinimumHeight;
        bool oldShowTraditionalDetails = ShowCalendarTraditionalDetails;
        _availableWidth = width;
        _availableHeight = height;
        if (oldTimeFontSize != TimeFontSize)
        {
            OnPropertyChanged(nameof(TimeFontSize));
        }
        if (oldCompactTimeFontSize != CompactTimeFontSize)
        {
            OnPropertyChanged(nameof(CompactTimeFontSize));
        }
        if (oldCalendarCompactTimeFontSize != CalendarCompactTimeFontSize)
        {
            OnPropertyChanged(nameof(CalendarCompactTimeFontSize));
        }
        if (oldPanelHeight != CalendarPanelHeight)
        {
            OnPropertyChanged(nameof(CalendarPanelHeight));
        }
        if (oldPanelWidth != CalendarPanelWidth)
        {
            OnPropertyChanged(nameof(CalendarPanelWidth));
        }
        if (oldDayItemHeight != CalendarDayItemMinimumHeight)
        {
            OnPropertyChanged(nameof(CalendarDayItemMinimumHeight));
        }
        if (oldShowTraditionalDetails != ShowCalendarTraditionalDetails)
        {
            OnPropertyChanged(nameof(ShowCalendarTraditionalDetails));
        }
        if (oldShowCalendar != ShowCalendar)
        {
            OnPropertyChanged(nameof(ShowCalendar));
            OnPropertyChanged(nameof(IsForegroundVisible));
        }
        if (oldShowCalendar != ShowCalendar || oldCompactCalendar != IsCompactCalendarPresentation)
        {
            RaiseLayoutProperties();
        }
    }

    private static double RoundFontSize(double value) => Math.Round(value * 2) / 2;

    public async Task SetDisplayedCalendarMonthAsync(DateOnly month)
    {
        DateOnly normalized = new(month.Year, month.Month, 1);
        if (_displayedCalendarMonth == normalized)
        {
            return;
        }

        _displayedCalendarMonth = normalized;
        OnPropertyChanged(nameof(DisplayedCalendarMonth));
        await UpdateCalendarAsync();
    }

    internal GlanceCalendarDay? FindCalendarDay(DateOnly date) =>
        CalendarDays.FirstOrDefault(day => day.Date == date);

    public void NextImage() => AdvanceImage(resetRotationTimer: true);

    private void AdvanceImage(bool resetRotationTimer)
    {
        if (_images.Count == 0)
        {
            if (IsOnlineSource(_settings.BackgroundSource))
            {
                _ = RefreshOnlineInBackgroundAsync();
            }
            return;
        }

        int nextIndex;
        if (_images.Count == 1)
        {
            nextIndex = 0;
        }
        else if (_settings.RandomOrder)
        {
            do
            {
                nextIndex = Random.Shared.Next(_images.Count);
            }
            while (nextIndex == _currentIndex);
        }
        else
        {
            nextIndex = (_currentIndex + 1 + _images.Count) % _images.Count;
        }

        _currentIndex = nextIndex;
        CurrentImage = _images[nextIndex];
        if (resetRotationTimer)
        {
            UpdateRotationTimer();
        }
    }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
    }

    public async Task OpenPhotoInfoAsync()
    {
        if (Uri.TryCreate(CurrentImage?.SourcePageUrl, UriKind.Absolute, out Uri? uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    public async Task OpenCurrentImageContextAsync()
    {
        GlanceImageInfo? image = CurrentImage;
        if (Uri.TryCreate(image?.SourcePageUrl, UriKind.Absolute, out Uri? uri))
        {
            await Launcher.LaunchUriAsync(uri);
            return;
        }

        if (!string.IsNullOrWhiteSpace(image?.LocalPath) && File.Exists(image.LocalPath))
        {
            Win32Helper.ShowInExplorer(image.LocalPath);
        }
    }

    public Task SetDisplayElementAsync(
        GlanceDisplayElement element,
        bool isVisible)
    {
        return _store.UpdateAsync(settings =>
            GlanceWidgetSettingsPolicy.SetDisplayElement(
                settings,
                element,
                isVisible));
    }

    public Task SetLayoutAsync(GlanceLayoutMode layout)
    {
        return _store.UpdateAsync(settings =>
            GlanceWidgetSettingsPolicy.SetLayout(settings, layout));
    }

    public Task SetLocalImageFilesAsync(IEnumerable<string> imagePaths)
    {
        return _store.UpdateAsync(settings =>
            GlanceWidgetSettingsPolicy.SetLocalImageFiles(settings, imagePaths));
    }

    public Task SetBackgroundImageTransparencyAsync(double transparency)
    {
        return _store.UpdateAsync(settings =>
            settings.BackgroundImageTransparency = Math.Clamp(transparency, 0.0, 1.0));
    }

    public Task SetPhotoPlaybackAsync(
        double rotationIntervalMinutes,
        bool randomOrder,
        GlanceTransitionMode transition,
        GlanceTransitionSpeed transitionSpeed,
        GlanceReadabilityMode readability,
        bool showPhotoControls)
    {
        return _store.UpdateAsync(settings =>
            GlanceWidgetSettingsPolicy.SetPhotoPlayback(
                settings,
                rotationIntervalMinutes,
                randomOrder,
                transition,
                transitionSpeed,
                readability,
                showPhotoControls));
    }

    private async Task ReloadSettingsAsync()
    {
        GlanceBackgroundSource previousSource = _settings.BackgroundSource;
        GlanceOnlineImageCategory previousOnlineCategory = _settings.OnlineImageCategory;
        _settings = await _store.LoadAsync();
        if (previousSource != _settings.BackgroundSource ||
            previousOnlineCategory != _settings.OnlineImageCategory)
        {
            CancelOnlineRefresh();
            _onlineRefreshCompletedForSession = false;
        }
        ApplySettingsProperties();
        UpdateDateAndTime();
        await UpdateCalendarAsync();
        await ReloadImagesAsync(refreshOnline: false);
        UpdateTimers();
        if (IsOnlineSource(_settings.BackgroundSource) && !_onlineRefreshCompletedForSession)
        {
            _ = RefreshOnlineInBackgroundAsync();
        }
    }

    private async Task ReloadImagesAsync(bool refreshOnline)
    {
        IsLoading = true;
        try
        {
            string? previousId = CurrentImage?.Id;
            _images = refreshOnline
                ? await _imageService.RefreshOnlineImagesAsync(_settings, _lifetimeCts.Token)
                : await _imageService.GetAvailableImagesAsync(_settings, _lifetimeCts.Token);
            _images = _images.Where(image => File.Exists(image.LocalPath)).ToArray();
            RaiseImageCollectionProperties();
            if (_images.Count == 0)
            {
                _currentIndex = -1;
                CurrentImage = null;
                StatusText = IsOnlineSource(_settings.BackgroundSource)
                    ? _localizationService.T("Glance.Status.OnlineFallback")
                    : _localizationService.T("Glance.Status.NoLocalImages");
                return;
            }

            _currentIndex = previousId is null
                ? -1
                : _images.ToList().FindIndex(image => string.Equals(image.Id, previousId, StringComparison.Ordinal));
            if (_currentIndex < 0)
            {
                _currentIndex = _settings.RandomOrder ? Random.Shared.Next(_images.Count) : 0;
            }

            CurrentImage = _images[_currentIndex];
            StatusText = string.Empty;
        }
        catch (OperationCanceledException) when (_isDisposed || _lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[GlanceWidgetViewModel] Image reload failed: {ex}");
            StatusText = _localizationService.T(IsOnlineSource(_settings.BackgroundSource)
                ? "Glance.Status.OnlineFallback"
                : "Glance.Status.NoLocalImages");
        }
        finally
        {
            IsLoading = false;
            UpdateRotationTimer();
        }
    }

    private async Task RefreshOnlineInBackgroundAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        CancelOnlineRefresh();
        GlanceBackgroundSource requestedSource = _settings.BackgroundSource;
        GlanceOnlineImageCategory requestedCategory = _settings.OnlineImageCategory;
        if (!IsOnlineSource(requestedSource))
        {
            return;
        }

        var refreshSettings = new GlanceWidgetData
        {
            BackgroundSource = requestedSource,
            OnlineImageCategory = requestedCategory
        };
        var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _onlineRefreshCts = refreshCts;
        bool showLoading = _images.Count == 0;
        if (showLoading)
        {
            IsLoading = true;
        }

        try
        {
            IReadOnlyList<GlanceImageInfo> refreshed = await _imageService.RefreshOnlineImagesAsync(
                refreshSettings,
                refreshCts.Token);
            if (_isDisposed ||
                !MatchesOnlineRequest(requestedSource, requestedCategory) ||
                refreshed.Count == 0)
            {
                return;
            }

            void Apply()
            {
                if (_isDisposed || !MatchesOnlineRequest(requestedSource, requestedCategory))
                {
                    return;
                }

                string? previousId = CurrentImage?.Id;
                _images = refreshed.Where(image => File.Exists(image.LocalPath)).ToArray();
                RaiseImageCollectionProperties();
                int existingIndex = previousId is null
                    ? -1
                    : _images.ToList().FindIndex(image => string.Equals(image.Id, previousId, StringComparison.Ordinal));
                if (existingIndex >= 0)
                {
                    _currentIndex = existingIndex;
                }
                else if (_images.Count > 0)
                {
                    _currentIndex = _settings.RandomOrder ? Random.Shared.Next(_images.Count) : 0;
                    CurrentImage = _images[_currentIndex];
                }
                StatusText = string.Empty;
                IsLoading = false;
                UpdateRotationTimer();
            }
            if (MatchesOnlineRequest(requestedSource, requestedCategory))
            {
                _onlineRefreshCompletedForSession = refreshed.Count > 0;
            }

            if (_dispatcherQueue.HasThreadAccess)
            {
                Apply();
            }
            else
            {
                _dispatcherQueue.TryEnqueue(Apply);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[GlanceWidgetViewModel] Background refresh failed: {ex}");
        }
        finally
        {
            if (showLoading && !_isDisposed)
            {
                if (_dispatcherQueue.HasThreadAccess)
                {
                    IsLoading = false;
                }
                else
                {
                    _dispatcherQueue.TryEnqueue(() => IsLoading = false);
                }
            }
            if (ReferenceEquals(_onlineRefreshCts, refreshCts))
            {
                _onlineRefreshCts = null;
            }
            refreshCts.Dispose();
        }
    }

    private void ApplySettingsProperties()
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(ShowTime));
        OnPropertyChanged(nameof(ShowDate));
        OnPropertyChanged(nameof(ShowYear));
        OnPropertyChanged(nameof(ShowWeekday));
        OnPropertyChanged(nameof(ShowCalendar));
        OnPropertyChanged(nameof(ShowPhotoControls));
        OnPropertyChanged(nameof(IsForegroundVisible));
        OnPropertyChanged(nameof(BackgroundImageTransparency));
        OnPropertyChanged(nameof(BackgroundImageOpacity));
        OnPropertyChanged(nameof(HasVisibleCurrentImage));
        OnPropertyChanged(nameof(ReadabilityStrengthOpacity));
        OnPropertyChanged(nameof(ReadabilityOpacity));
        OnPropertyChanged(nameof(TimeFontFamily));
        OnPropertyChanged(nameof(TimeFontSize));
        OnPropertyChanged(nameof(CompactTimeFontSize));
        OnPropertyChanged(nameof(CalendarCompactTimeFontSize));
        OnPropertyChanged(nameof(CalendarPanelHeight));
        OnPropertyChanged(nameof(CalendarPanelWidth));
        OnPropertyChanged(nameof(CalendarDayItemMinimumHeight));
        OnPropertyChanged(nameof(ShowCalendarTraditionalDetails));
        OnPropertyChanged(nameof(Transition));
        OnPropertyChanged(nameof(TransitionSpeed));
        OnPropertyChanged(nameof(ImageFit));
        OnPropertyChanged(nameof(ImageFocus));
        OnPropertyChanged(nameof(CalendarMaterialMode));
        OnPropertyChanged(nameof(CalendarImageMaterialTransparency));
        OnPropertyChanged(nameof(TraditionalCalendarMode));
        OnPropertyChanged(nameof(ShowChineseFestivals));
        OnPropertyChanged(nameof(CanPauseRotation));
        RaiseLayoutProperties();
    }

    private void RaiseLayoutProperties()
    {
        OnPropertyChanged(nameof(IsImmersiveLayout));
        OnPropertyChanged(nameof(IsCenteredLayout));
        OnPropertyChanged(nameof(IsEditorialLayout));
        OnPropertyChanged(nameof(IsCalendarLayout));
        OnPropertyChanged(nameof(IsNonCalendarForeground));
        OnPropertyChanged(nameof(IsCompactCalendarPresentation));
        OnPropertyChanged(nameof(IsExpandedCalendarPresentation));
        OnPropertyChanged(nameof(CalendarDayItemMinimumHeight));
        OnPropertyChanged(nameof(ShowCalendarTraditionalDetails));
        OnPropertyChanged(nameof(ShowNonCalendarImageReadability));
        OnPropertyChanged(nameof(ShowCalendarImageReadability));
        OnPropertyChanged(nameof(ShowExpandedCalendarImageReadability));
    }

    private void RaiseImageCollectionProperties()
    {
        OnPropertyChanged(nameof(ImageCount));
        OnPropertyChanged(nameof(CanAdvanceImage));
        OnPropertyChanged(nameof(CanPauseRotation));
    }

    private void UpdateDateAndTime()
    {
        CultureInfo culture = GetCulture();
        DateTime now = DateTime.Now;
        bool uses24Hour = culture.DateTimeFormat.ShortTimePattern.Contains('H');
        TimeText = now.ToString(uses24Hour ? "HH:mm" : "h:mm", culture);
        DateText = FormatDateText(now, culture, _settings.ShowYear);
        CompactCalendarDateText = FormatCompactCalendarDateText(now, culture);
        WeekdayText = now.ToString("dddd", culture);
    }

    internal static string FormatCompactCalendarDateText(
        DateTime date,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        string day = date.Day.ToString(culture);
        return culture.TwoLetterISOLanguageName switch
        {
            "zh" or "ja" => $"{day}日",
            "ko" => $"{day}일",
            _ => day
        };
    }

    internal static string FormatDateText(
        DateTime date,
        CultureInfo culture,
        bool includeYear)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (!includeYear)
        {
            return date.ToString("M", culture);
        }

        // LongDatePattern already carries the locale's natural year/month/day
        // order. Remove its weekday token because weekday is an independent
        // Glance display option.
        string pattern = Regex.Replace(
            culture.DateTimeFormat.LongDatePattern,
            @"(?<!d)d{3,4}(?!d)",
            string.Empty,
            RegexOptions.CultureInvariant);
        pattern = pattern.Trim().Trim(',', '，', '،').Trim();
        return date.ToString(pattern, culture);
    }

    private async Task UpdateCalendarAsync()
    {
        int loadVersion = Interlocked.Increment(ref _calendarLoadVersion);
        CultureInfo culture = GetCulture();
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly requestedMonth = _displayedCalendarMonth;
        try
        {
            GlanceCalendarMonth month = await _calendarSource.GetMonthAsync(
                requestedMonth,
                culture,
                _lifetimeCts.Token);
            DateOnly currentMonth = new(today.Year, today.Month, 1);
            DateOnly traditionalTitleDate = requestedMonth == currentMonth
                ? today
                : requestedMonth.AddDays(14);
            month = _traditionalCalendarService.Apply(
                month,
                _settings.TraditionalCalendarMode,
                culture,
                traditionalTitleDate);
            month = _festivalService.Apply(
                month,
                _settings.ShowChineseFestivals,
                _settings.TraditionalCalendarMode,
                culture);
            if (loadVersion != _calendarLoadVersion || requestedMonth != _displayedCalendarMonth)
            {
                return;
            }

            TraditionalCalendarTitle = month.TraditionalTitle;
            WeekdayHeaders.Clear();
            foreach (string header in month.WeekdayHeaders)
            {
                WeekdayHeaders.Add(header);
            }

            CalendarDays.Clear();
            foreach (GlanceCalendarDay day in month.Days)
            {
                CalendarDays.Add(day);
            }

            OnPropertyChanged(nameof(CalendarDays));
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
    }

    private CultureInfo GetCulture()
    {
        try
        {
            return CultureInfo.GetCultureInfo(_localizationService.CurrentCultureName);
        }
        catch
        {
            return CultureInfo.CurrentCulture;
        }
    }

    private void UpdateTimers()
    {
        UpdateClockTimer();
        UpdateRotationTimer();
    }

    private void UpdateClockTimer()
    {
        _clockTimer.Stop();
        bool needsCalendarClock = _settings.ShowDate ||
            _settings.ShowWeekday ||
            _settings.ShowCalendar ||
            _settings.TraditionalCalendarMode != GlanceTraditionalCalendarMode.None;
        if (!_isWindowVisible || (!_settings.ShowTime && !needsCalendarClock) || _isDisposed)
        {
            return;
        }

        DateTime now = DateTime.Now;
        _clockTimer.Interval = _settings.ShowTime
            ? TimeSpan.FromSeconds(Math.Max(1, 60 - now.Second)) - TimeSpan.FromMilliseconds(now.Millisecond)
            : now.Date.AddDays(1).AddMilliseconds(100) - now;
        _clockTimer.Start();
    }

    private void UpdateRotationTimer()
    {
        _rotationTimer.Stop();
        if (!_isWindowVisible ||
            _isCompact ||
            IsPaused ||
            _isDisposed ||
            !GlanceImageAutoRotationEnabled() ||
            _settings.RotationIntervalMinutes <= 0 ||
            _images.Count < 2)
        {
            return;
        }

        _rotationTimer.Interval = TimeSpan.FromMinutes(_settings.RotationIntervalMinutes);
        _rotationTimer.Start();
    }

    private bool GlanceImageAutoRotationEnabled()
    {
        return _settingsService is null ||
            PerformanceSettingsPolicy.Resolve(_settingsService.Settings)
                .AllowGlanceImageAutoRotation;
    }

    private void ClockTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        DateOnly previousDate = DateOnly.FromDateTime(DateTime.Now.AddMinutes(-1));
        DateOnly currentDate = DateOnly.FromDateTime(DateTime.Now);
        UpdateDateAndTime();
        if (previousDate != currentDate)
        {
            DateOnly previousMonth = new(previousDate.Year, previousDate.Month, 1);
            DateOnly currentMonth = new(currentDate.Year, currentDate.Month, 1);
            if (_displayedCalendarMonth == previousMonth && previousMonth != currentMonth)
            {
                _displayedCalendarMonth = currentMonth;
                OnPropertyChanged(nameof(DisplayedCalendarMonth));
            }

            _ = UpdateCalendarAsync();
        }
        UpdateClockTimer();
    }

    private void RotationTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        AdvanceImage(resetRotationTimer: false);
    }

    private void Store_Changed(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            _ = ReloadSettingsAsync();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => _ = ReloadSettingsAsync());
        }
    }

    private void LocalizationService_LanguageChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            UpdateDateAndTime();
            OnPropertyChanged(nameof(CalendarLanguage));
            _ = UpdateCalendarAsync();
            OnPropertyChanged(nameof(PauseToolTip));
            OnPropertyChanged(nameof(NextToolTip));
        });
    }

    private void SettingsService_SettingsChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            UpdateRotationTimer();
            OnPropertyChanged(nameof(CalendarCornerRadius));
            RaiseCalendarMaterialProperties();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                UpdateRotationTimer();
                OnPropertyChanged(nameof(CalendarCornerRadius));
                RaiseCalendarMaterialProperties();
            });
        }
    }

    private void RaiseCalendarMaterialProperties()
    {
        OnPropertyChanged(nameof(CalendarMaterialType));
        OnPropertyChanged(nameof(CalendarMaterialOpacity));
        OnPropertyChanged(nameof(CalendarMaterialIntensity));
    }

    private static bool IsOnlineSource(GlanceBackgroundSource source)
    {
        return source is GlanceBackgroundSource.Online or GlanceBackgroundSource.Bing;
    }

    private bool MatchesOnlineRequest(
        GlanceBackgroundSource source,
        GlanceOnlineImageCategory category)
    {
        return _settings.BackgroundSource == source &&
            (source != GlanceBackgroundSource.Online || _settings.OnlineImageCategory == category);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _store.Changed -= Store_Changed;
        _localizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged -= SettingsService_SettingsChanged;
        }
        _clockTimer.Stop();
        _clockTimer.Tick -= ClockTimer_Tick;
        _rotationTimer.Stop();
        _rotationTimer.Tick -= RotationTimer_Tick;
        CancelOnlineRefresh();
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }

    private void CancelOnlineRefresh()
    {
        try
        {
            _onlineRefreshCts?.Cancel();
        }
        catch
        {
        }
    }
}
