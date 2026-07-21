using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DeskBox.ViewModels;

/// <summary>
/// View model for the weather widget. Manages data fetching, refresh timers,
/// view mode switching, and adaptive layout based on available size.
/// </summary>
public sealed partial class WeatherWidgetViewModel : ObservableObject, IDisposable
{
    private readonly WidgetConfig _config;
    private readonly WeatherService _weatherService;
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;
    private bool _isDisposed;
    private bool _isRefreshing;
    private bool _isWidgetActive;
    private bool _refreshWasUserTriggered;

    // Cached location settings for change detection
    private bool _lastWeatherAutoLocation;
    private double _lastWeatherLatitude;
    private double _lastWeatherLongitude;
    private string _lastWeatherCityName = string.Empty;
    private double _lastAvailableWidth = 300;
    private double _lastAvailableHeight = 200;
    private bool _isResponsiveLayoutTransitionActive;

    // Cached raw data
    private WeatherData? _weatherData;

    // Location
    private double _latitude;
    private double _longitude;
    private string _locationName = string.Empty;

    // Display settings
    private bool _isWeekView;
    private string _temperatureUnit = SettingsService.WeatherTemperatureUnitCelsius;
    private string _windSpeedUnit = SettingsService.WeatherWindSpeedUnitKmh;
    private string _skin = SettingsService.WeatherSkinStandard;
    private bool _showForecast = true;
    private bool _showSunrise = true;
    private bool _showUvIndex = true;
    private bool _showPrecipitation = true;
    private bool _showHumidity = true;
    private bool _showWind = true;
    private bool _showPressure;
    private double _textSize = SettingsService.DefaultTextSize;

    // Current weather display values
    private string _currentTemperatureText = "--\u00B0";
    private string _currentDescription = string.Empty;
    private string _currentEmoji = "\U0001F31E";
    private string _currentIconGlyph = "\uE706";
    private string _apparentTemperatureText = string.Empty;
    private string _humidityText = string.Empty;
    private string _windText = string.Empty;
    private string _pressureText = string.Empty;
    private string _uvIndexText = string.Empty;
    private string _precipitationText = string.Empty;
    private string _sunriseText = string.Empty;
    private string _sunsetText = string.Empty;
    private string _locationDisplay = string.Empty;
    private bool _isDay = true;
    private bool _hasData;
    private int _currentWeatherCode;
    private WeatherCodeMapper.WeatherCondition _currentCondition = WeatherCodeMapper.WeatherCondition.Unknown;

    // Layout
    private string _layoutMode = "Expanded"; // Mini, Compact, Expanded

    // View switch button
    private string _viewSwitchTooltip = string.Empty;
    private string _viewSwitchGlyph = "\uE8B7";

    public WeatherWidgetViewModel(
        WidgetConfig config,
        WeatherService weatherService,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue = null)
    {
        _config = config;
        _weatherService = weatherService;
        _localizationService = localizationService;
        _settingsService = settingsService;
        _dispatcherQueue = dispatcherQueue ?? TryGetCurrentDispatcherQueue();

        if (_settingsService is not null)
        {
            ApplyWeatherSettings(_settingsService.Settings);
            CacheLocationSettings(_settingsService.Settings);
            _settingsService.SettingsChanged += OnSettingsChanged;
        }

        _localizationService.LanguageChanged += OnLanguageChanged;

        if (_dispatcherQueue is not null)
        {
            _refreshTimer = _dispatcherQueue.CreateTimer();
            _refreshTimer.IsRepeating = true;
            _refreshTimer.Tick += RefreshTimer_Tick;
            UpdateRefreshInterval();
        }
    }

    private static Microsoft.UI.Dispatching.DispatcherQueue? TryGetCurrentDispatcherQueue()
    {
        try
        {
            return Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            return null;
        }
    }

    // ─── Observable Properties ─────────────────────────────────

    public bool IsWidgetActive
    {
        get => _isWidgetActive;
        private set => SetProperty(ref _isWidgetActive, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                OnPropertyChanged(nameof(LoadingVisibility));
            }
        }
    }

    public string DisplayName => _config.IsDefaultTitle
        ? _localizationService.T("Weather.Title")
        : _config.Name;

    public ObservableCollection<WeatherDayViewModel> DailyForecast { get; } = [];

    public ObservableCollection<WeatherHourViewModel> HourlyForecast { get; } = [];

    public string CurrentTemperatureText
    {
        get => _currentTemperatureText;
        private set => SetProperty(ref _currentTemperatureText, value);
    }

    public string CurrentDescription
    {
        get => _currentDescription;
        private set => SetProperty(ref _currentDescription, value);
    }

    public string CurrentEmoji
    {
        get => _currentEmoji;
        private set => SetProperty(ref _currentEmoji, value);
    }

    /// <summary>Segoe Fluent Icons glyph for the current weather condition.</summary>
    public string CurrentIconGlyph
    {
        get => _currentIconGlyph;
        private set => SetProperty(ref _currentIconGlyph, value);
    }

    public string ApparentTemperatureText
    {
        get => _apparentTemperatureText;
        private set => SetProperty(ref _apparentTemperatureText, value);
    }

    public string HumidityText
    {
        get => _humidityText;
        private set => SetProperty(ref _humidityText, value);
    }

    public string WindText
    {
        get => _windText;
        private set => SetProperty(ref _windText, value);
    }

    public string PressureText
    {
        get => _pressureText;
        private set => SetProperty(ref _pressureText, value);
    }

    public string UvIndexText
    {
        get => _uvIndexText;
        private set => SetProperty(ref _uvIndexText, value);
    }

    public string PrecipitationText
    {
        get => _precipitationText;
        private set => SetProperty(ref _precipitationText, value);
    }

    public string SunriseText
    {
        get => _sunriseText;
        private set => SetProperty(ref _sunriseText, value);
    }

    public string SunsetText
    {
        get => _sunsetText;
        private set => SetProperty(ref _sunsetText, value);
    }

    public string LocationDisplay
    {
        get => _locationDisplay;
        private set => SetProperty(ref _locationDisplay, value);
    }

    public bool IsDay
    {
        get => _isDay;
        private set => SetProperty(ref _isDay, value);
    }

    public bool HasData
    {
        get => _hasData;
        private set
        {
            if (SetProperty(ref _hasData, value))
            {
                OnPropertyChanged(nameof(LoadingVisibility));
            }
        }
    }

    /// <summary>
    /// Shows the loading overlay when data is being fetched for the first time
    /// (no cached data available yet) or during a user-triggered refresh.
    /// Auto-refresh (timer) with existing data only rotates the refresh icon.
    /// </summary>
    public Visibility LoadingVisibility => _isRefreshing && (!_hasData || _refreshWasUserTriggered)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public int CurrentWeatherCode
    {
        get => _currentWeatherCode;
        private set => SetProperty(ref _currentWeatherCode, value);
    }

    public WeatherCodeMapper.WeatherCondition CurrentCondition
    {
        get => _currentCondition;
        private set => SetProperty(ref _currentCondition, value);
    }

    public bool IsWeekView
    {
        get => _isWeekView;
        set
        {
            if (SetProperty(ref _isWeekView, value))
            {
                UpdateViewSwitchButton();
            }
        }
    }

    public string LayoutMode
    {
        get => _layoutMode;
        private set => SetProperty(ref _layoutMode, value);
    }

    public string ViewSwitchText => _isWeekView
        ? _localizationService.T("Weather.View.Today")
        : _localizationService.T("Weather.View.Week");

    public string ViewSwitchGlyph
    {
        get => _viewSwitchGlyph;
        private set => SetProperty(ref _viewSwitchGlyph, value);
    }

    public string ViewSwitchTooltip
    {
        get => _viewSwitchTooltip;
        private set => SetProperty(ref _viewSwitchTooltip, value);
    }

    public double TextSize
    {
        get => _textSize;
        private set
        {
            if (SetProperty(ref _textSize, value))
            {
                OnPropertyChanged(nameof(TitleTextSize));
                OnPropertyChanged(nameof(BodyTextSize));
                OnPropertyChanged(nameof(CaptionTextSize));
                OnPropertyChanged(nameof(TemperatureTextSize));
                OnPropertyChanged(nameof(ForecastHourTextSize));
                OnPropertyChanged(nameof(ForecastTempTextSize));
                OnPropertyChanged(nameof(WeekDayLabelTextSize));
            }
        }
    }

    public double TitleTextSize => Math.Min(SettingsService.MaxTextSize + 3, TextSize + 4);
    public double BodyTextSize => Math.Max(SettingsService.MinTextSize, TextSize);
    public double CaptionTextSize => Math.Max(SettingsService.MinTextSize - 1, TextSize - 2);
    public double TemperatureTextSize => _layoutMode switch
    {
        "Mini" => Math.Min(26, TextSize + 14),
        "Compact" => Math.Min(34, TextSize + 21),
        _ => Math.Min(44, TextSize + 28)
    };

    // Emoji font size scales with layout
    public double CurrentEmojiSize => _layoutMode == "Mini" ? 26 : _layoutMode == "Compact" ? 28 : 32;
    public double ForecastEmojiSize => _layoutMode == "Expanded" ? 18 : 14;
    public double ForecastHourTextSize => Math.Max(9, TextSize - 3);
    public double ForecastTempTextSize => Math.Max(10, TextSize - 1);
    public double WeekDayLabelTextSize => Math.Max(11, TextSize - 1);
    public double WeekEmojiSize => _layoutMode == "Expanded" ? 18 : 16;
    public double WeekTempMaxSize => _layoutMode == "Expanded" ? 15 : 13;
    public double WeekTempMinSize => _layoutMode == "Expanded" ? 12 : 11;
    public double HourlyCardWidth => 52;

    // Mini layout supplementary info — multiple chips for richer display
    public string MiniHumidityText => _humidityText;
    public string MiniWindText => _windText;
    public string MiniPrecipText => _precipitationText;
    public Visibility MiniHumidityVisibility => !string.IsNullOrEmpty(_humidityText) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MiniWindVisibility => !string.IsNullOrEmpty(_windText) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MiniPrecipVisibility => !string.IsNullOrEmpty(_precipitationText) ? Visibility.Visible : Visibility.Collapsed;

    // Rich skin gradient colors (updated based on weather condition)
    public Color RichBackdropTopColor { get; private set; } = Color.FromArgb(0xFF, 0x4A, 0x90, 0xD9);
    public Color RichBackdropBottomColor { get; private set; } = Color.FromArgb(0xFF, 0x74, 0xB9, 0xFF);
    public Color RichOverlayColor { get; private set; } = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);

    // Visibility helpers for settings-driven content
    public Visibility ForecastVisibility => _showForecast && !_isWeekView ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WeekForecastVisibility => _showForecast && _isWeekView ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SunriseVisibility => _showSunrise && _layoutMode == "Expanded" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UvIndexVisibility => _showUvIndex ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PrecipitationVisibility => _showPrecipitation ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HumidityVisibility => _showHumidity ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WindVisibility => _showWind ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PressureVisibility => _showPressure ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ViewSwitchVisibility => _showForecast ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MiniLayoutVisibility => _layoutMode == "Mini" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MiniLocationVisibility => !string.IsNullOrEmpty(_locationDisplay) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MiniDescriptionVisibility => !string.IsNullOrEmpty(_currentDescription) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompactLayoutVisibility => _layoutMode == "Compact" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExpandedLayoutVisibility => _layoutMode == "Expanded" ? Visibility.Visible : Visibility.Collapsed;

    // Expanded layout: progressive visibility based on available height
    public Visibility ExpandedSunriseVisibility =>
        _layoutMode == "Expanded" && _showSunrise && _lastAvailableHeight >= 260 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExpandedHourlyPrecipVisibility =>
        _layoutMode == "Expanded" && _lastAvailableHeight >= 220 ? Visibility.Visible : Visibility.Collapsed;

    public double ExpandedHourlyCardHeight =>
        _lastAvailableHeight >= 280 ? 92 : (_lastAvailableHeight >= 220 ? 80 : 72);

    public Visibility RichSkinVisibility => _skin == SettingsService.WeatherSkinRich ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// When the Rich skin gradient is dark (night, storms, etc.), text should use
    /// light colors even in light theme mode. This is determined by checking the
    /// average luminance of the gradient colors.
    /// </summary>
    public bool RichSkinUsesLightText { get; private set; }

    private static bool IsColorDark(Color c)
    {
        // Standard luminance formula: 0.299R + 0.587G + 0.114B
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance < 0.45;
    }

    // Animation visibility
    public Visibility RainAnimationVisibility =>
        _skin == SettingsService.WeatherSkinRich &&
        (_currentCondition == WeatherCodeMapper.WeatherCondition.Rain ||
         _currentCondition == WeatherCodeMapper.WeatherCondition.Drizzle)
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SnowAnimationVisibility =>
        _skin == SettingsService.WeatherSkinRich &&
        _currentCondition == WeatherCodeMapper.WeatherCondition.Snow
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ThunderAnimationVisibility =>
        _skin == SettingsService.WeatherSkinRich &&
        _currentCondition == WeatherCodeMapper.WeatherCondition.Thunderstorm
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ClearAnimationVisibility =>
        _skin == SettingsService.WeatherSkinRich &&
        _currentCondition == WeatherCodeMapper.WeatherCondition.Clear
        ? Visibility.Visible : Visibility.Collapsed;

    public string RefreshTooltip => _localizationService.T("Common.Refresh");

    public string LoadingText => _localizationService.T("Weather.Loading");

    // ─── Refresh status toast ───

    private string _refreshStatusText = string.Empty;
    private bool _showRefreshStatus;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshStatusTimer;

    public string RefreshStatusText
    {
        get => _refreshStatusText;
        private set => SetProperty(ref _refreshStatusText, value);
    }

    public bool ShowRefreshStatus
    {
        get => _showRefreshStatus;
        private set
        {
            if (SetProperty(ref _showRefreshStatus, value))
            {
                OnPropertyChanged(nameof(RefreshStatusVisibility));
            }
        }
    }

    public Visibility RefreshStatusVisibility => _showRefreshStatus
        ? Visibility.Visible
        : Visibility.Collapsed;

    private void ShowRefreshStatusToast(bool success)
    {
        RefreshStatusText = success
            ? _localizationService.T("Weather.RefreshSuccess")
            : _localizationService.T("Weather.RefreshFailed");
        ShowRefreshStatus = true;

        _refreshStatusTimer?.Stop();
        if (_dispatcherQueue is not null)
        {
            if (_refreshStatusTimer is null)
            {
                _refreshStatusTimer = _dispatcherQueue.CreateTimer();
                _refreshStatusTimer.Interval = TimeSpan.FromSeconds(2);
                _refreshStatusTimer.Tick += RefreshStatusTimer_Tick;
            }
            _refreshStatusTimer.Start();
        }
    }

    private void RefreshStatusTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        ShowRefreshStatus = false;
        sender.Stop();
    }

    // ─── Lifecycle ─────────────────────────────────────────────

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

        if (_refreshStatusTimer is not null)
        {
            _refreshStatusTimer.Stop();
            _refreshStatusTimer.Tick -= RefreshStatusTimer_Tick;
        }

        _localizationService.LanguageChanged -= OnLanguageChanged;
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }

        _weatherService.Dispose();
    }

    // ─── Private Methods ───────────────────────────────────────

    private void ApplyWeatherSettings(AppSettings settings)
    {
        bool changed = false;

        if (_temperatureUnit != settings.WeatherTemperatureUnit)
        {
            _temperatureUnit = settings.WeatherTemperatureUnit;
            changed = true;
        }

        if (_windSpeedUnit != settings.WeatherWindSpeedUnit)
        {
            _windSpeedUnit = settings.WeatherWindSpeedUnit;
            changed = true;
        }

        if (_skin != settings.WeatherSkin)
        {
            _skin = settings.WeatherSkin;
            OnPropertyChanged(nameof(RichSkinVisibility));
            OnPropertyChanged(nameof(RainAnimationVisibility));
            OnPropertyChanged(nameof(SnowAnimationVisibility));
            OnPropertyChanged(nameof(ThunderAnimationVisibility));
            OnPropertyChanged(nameof(ClearAnimationVisibility));
            // Re-evaluate light text need when skin changes
            UpdateRichSkinColors();
        }

        _showForecast = settings.WeatherShowForecast;
        _showSunrise = settings.WeatherShowSunrise;
        _showUvIndex = settings.WeatherShowUvIndex;
        _showPrecipitation = settings.WeatherShowPrecipitation;
        _showHumidity = settings.WeatherShowHumidity;
        _showWind = settings.WeatherShowWind;
        _showPressure = settings.WeatherShowPressure;
        if (_settingsService is not null)
        {
            TextSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
        }

        if (string.IsNullOrEmpty(_viewSwitchTooltip))
        {
            IsWeekView = settings.WeatherDefaultView == SettingsService.WeatherDefaultViewWeek;
            UpdateViewSwitchButton();
        }

        UpdateRefreshInterval();

        if (changed && _weatherData is not null)
        {
            ApplyWeatherData(_weatherData);
        }

        OnPropertyChanged(nameof(ForecastVisibility));
        OnPropertyChanged(nameof(WeekForecastVisibility));
        OnPropertyChanged(nameof(SunriseVisibility));
        OnPropertyChanged(nameof(UvIndexVisibility));
        OnPropertyChanged(nameof(PrecipitationVisibility));
        OnPropertyChanged(nameof(HumidityVisibility));
        OnPropertyChanged(nameof(WindVisibility));
        OnPropertyChanged(nameof(PressureVisibility));
        OnPropertyChanged(nameof(ViewSwitchVisibility));
    }

    private void UpdateRefreshInterval()
    {
        if (_refreshTimer is null || _settingsService is null)
        {
            return;
        }

        int minutes = Math.Clamp(
            _settingsService.Settings.WeatherRefreshIntervalMinutes,
            SettingsService.WeatherRefreshMinMinutes,
            SettingsService.WeatherRefreshMaxMinutes);
        _refreshTimer.Interval = TimeSpan.FromMinutes(minutes);
    }

    private void UpdateViewSwitchButton()
    {
        ViewSwitchGlyph = _isWeekView ? "\uE8C9" : "\uE8B7";
        ViewSwitchTooltip = _isWeekView
            ? _localizationService.T("Weather.View.Today")
            : _localizationService.T("Weather.View.Week");
        OnPropertyChanged(nameof(ViewSwitchText));
    }

    private void RefreshTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        _ = RefreshAsync();
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

        // Detect location change and auto-refresh
        if (_settingsService is not null)
        {
            var s = _settingsService.Settings;
            bool locationChanged = s.WeatherAutoLocation != _lastWeatherAutoLocation ||
                (!s.WeatherAutoLocation && (
                    Math.Abs(s.WeatherLatitude - _lastWeatherLatitude) > 0.0001 ||
                    Math.Abs(s.WeatherLongitude - _lastWeatherLongitude) > 0.0001 ||
                    !string.Equals(s.WeatherCityName, _lastWeatherCityName, StringComparison.Ordinal)));

            CacheLocationSettings(s);

            if (locationChanged)
            {
                _ = RefreshAsync(forceRefresh: true);
            }
        }
    }

    private void CacheLocationSettings(AppSettings settings)
    {
        _lastWeatherAutoLocation = settings.WeatherAutoLocation;
        _lastWeatherLatitude = settings.WeatherLatitude;
        _lastWeatherLongitude = settings.WeatherLongitude;
        _lastWeatherCityName = settings.WeatherCityName;
    }

    private void OnLanguageChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(LoadingText));
        OnPropertyChanged(nameof(RefreshTooltip));
        UpdateViewSwitchButton();
        if (_weatherData is not null)
        {
            ApplyWeatherData(_weatherData);
        }
    }
}

/// <summary>
/// Represents a single day in the 7-day forecast.
/// </summary>
public sealed partial class WeatherDayViewModel : ObservableObject
{
    public string DayLabel { get; set; } = string.Empty;
    public string Emoji { get; set; } = "\U0001F31E";
    public string IconGlyph { get; set; } = "\uE706";
    public string Description { get; set; } = string.Empty;
    public string TempMaxText { get; set; } = string.Empty;
    public string TempMinText { get; set; } = string.Empty;
    public string PrecipitationText { get; set; } = string.Empty;

    /// <summary>Temperature range bar: left offset as fraction (0..1) of the full weekly range.</summary>
    public double TempBarOffset { get; set; }

    /// <summary>Temperature range bar: width as fraction (0..1) of the full weekly range.</summary>
    public double TempBarWidth { get; set; } = 1.0;
}

/// <summary>
/// Represents a single hour in the 24-hour forecast.
/// </summary>
public sealed partial class WeatherHourViewModel : ObservableObject
{
    public string HourLabel { get; set; } = string.Empty;
    public string TemperatureText { get; set; } = string.Empty;
    public string PrecipitationText { get; set; } = string.Empty;
    public string Emoji { get; set; } = "\U0001F31E";
    public string IconGlyph { get; set; } = "\uE706";

    /// <summary>Whether this is the current hour (used for highlight).</summary>
    public bool IsCurrentHour { get; set; }
}
