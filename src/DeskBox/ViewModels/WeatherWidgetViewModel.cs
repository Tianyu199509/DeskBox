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
    private string _layoutMode = "Standard"; // Mini, Compact, Standard, Detailed

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
    /// (no cached data available yet).
    /// </summary>
    public Visibility LoadingVisibility => !_hasData && _isRefreshing
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
        "Mini" => 26,
        "Detailed" => Math.Min(50, TextSize + 30),
        _ => Math.Min(38, TextSize + 24)
    };

    // Emoji font size scales with layout
    public double CurrentEmojiSize => _layoutMode == "Mini" ? 32 : _layoutMode == "Compact" ? 22 : _layoutMode == "Detailed" ? 40 : 30;
    public double ForecastEmojiSize => _layoutMode == "Detailed" ? 18 : 14;
    public double ForecastHourTextSize => Math.Max(9, TextSize - 3);
    public double ForecastTempTextSize => Math.Max(10, TextSize - 1);
    public double WeekDayLabelTextSize => Math.Max(11, TextSize - 1);
    public double WeekEmojiSize => _layoutMode == "Detailed" ? 20 : 16;
    public double WeekTempMaxSize => _layoutMode == "Detailed" ? 15 : 13;
    public double WeekTempMinSize => _layoutMode == "Detailed" ? 12 : 11;
    public double HourlyCardWidth => _layoutMode == "Detailed" ? 48 : 46;

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
    public Visibility SunriseVisibility => _showSunrise && _layoutMode == "Detailed" ? Visibility.Visible : Visibility.Collapsed;
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
    public Visibility StandardLayoutVisibility => _layoutMode == "Standard" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailedLayoutVisibility => _layoutMode == "Detailed" ? Visibility.Visible : Visibility.Collapsed;
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

        _refreshTimer?.Start();
    }

    public async Task RefreshAsync(bool userTriggered = false)
    {
        if (_isDisposed || _isRefreshing)
        {
            return;
        }

        _refreshWasUserTriggered = userTriggered;
        _isRefreshing = true;
        IsRefreshing = true;
        bool refreshSucceeded = false;
        try
        {
            await EnsureLocationAsync();
            if (_isDisposed)
            {
                return;
            }

            _weatherData = await _weatherService.GetWeatherAsync(_latitude, _longitude, _locationName);
            if (_isDisposed)
            {
                return;
            }

            if (_weatherData is not null)
            {
                ApplyWeatherData(_weatherData);
                HasData = true;
                refreshSucceeded = true;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WeatherWidget] Refresh failed: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
            IsRefreshing = false;

            // Only show the toast for user-triggered refreshes (not auto-timer)
            if (_refreshWasUserTriggered && HasData)
            {
                ShowRefreshStatusToast(refreshSucceeded);
            }
        }
    }

    public void ApplyAppearance()
    {
        if (_settingsService is null)
        {
            return;
        }

        TextSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
        ApplyWeatherSettings(_settingsService.Settings);
    }

    public void OnActivated()
    {
        if (_isDisposed)
        {
            return;
        }

        // Refresh on user interaction, but don't change IsWidgetActive —
        // that is now driven by window visibility (OnWindowVisibilityChanged).
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
        // No-op: animation and timer lifecycle is controlled by window visibility,
        // not activation state. This prevents animations from stopping when the
        // widget is visible at the desktop layer but not foreground-activated.
    }

    /// <summary>
    /// Called when the host window becomes visible or hidden.
    /// Controls animation lifecycle and refresh timer based on actual visibility.
    /// </summary>
    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_isDisposed)
        {
            return;
        }

        IsWidgetActive = visible;

        if (visible)
        {
            _refreshTimer?.Start();
            _ = RefreshAsync();
        }
        else
        {
            _refreshTimer?.Stop();
        }
    }

    public void ToggleViewMode()
    {
        IsWeekView = !IsWeekView;
        OnPropertyChanged(nameof(ForecastVisibility));
        OnPropertyChanged(nameof(WeekForecastVisibility));
    }

    /// <summary>
    /// Called when the widget is resized. Determines the layout mode (Compact/Standard/Detailed).
    /// </summary>
    public void UpdateAvailableSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height))
        {
            return;
        }

        _lastAvailableWidth = width;
        _lastAvailableHeight = height;

        string newLayout = DetermineLayoutMode(width, height);
        if (!string.Equals(newLayout, _layoutMode, StringComparison.Ordinal))
        {
            LayoutMode = newLayout;
            OnPropertyChanged(nameof(MiniLayoutVisibility));
            OnPropertyChanged(nameof(CompactLayoutVisibility));
            OnPropertyChanged(nameof(StandardLayoutVisibility));
            OnPropertyChanged(nameof(DetailedLayoutVisibility));
            OnPropertyChanged(nameof(CurrentEmojiSize));
            OnPropertyChanged(nameof(ForecastEmojiSize));
            OnPropertyChanged(nameof(TemperatureTextSize));
            OnPropertyChanged(nameof(WeekEmojiSize));
            OnPropertyChanged(nameof(WeekDayLabelTextSize));
            OnPropertyChanged(nameof(WeekTempMaxSize));
            OnPropertyChanged(nameof(WeekTempMinSize));
            OnPropertyChanged(nameof(HourlyCardWidth));
            OnPropertyChanged(nameof(SunriseVisibility));
        }
    }

    /// <summary>
    /// Determines layout mode using hysteresis: once in a higher layout, the
    /// widget stays there until size drops significantly below the upgrade
    /// threshold. This prevents flickering and the "almost fits" problem where
    /// a few pixels short would unnecessarily downgrade to a smaller layout.
    /// </summary>
    private string DetermineLayoutMode(double width, double height)
    {
        const double miniUpgradeW = 250, miniUpgradeH = 250;
        const double miniDowngradeW = 210, miniDowngradeH = 210;

        const double compactUpgradeW = 270, compactUpgradeH = 170;
        const double compactDowngradeW = 240, compactDowngradeH = 150;

        const double detailedUpgradeW = 280, detailedUpgradeH = 280;
        const double detailedDowngradeW = 260, detailedDowngradeH = 250;

        // Mini is always forced for very small sizes regardless of hysteresis
        if (width <= miniDowngradeW || height <= miniDowngradeH)
        {
            return "Mini";
        }

        switch (_layoutMode)
        {
            case "Mini":
                // Upgrade to Compact when enough room
                if (width >= miniUpgradeW && height >= miniUpgradeH)
                {
                    goto CheckCompactUp;
                }
                return "Mini";

            case "Compact":
            CheckCompactUp:
                // Upgrade to Standard/Detailed when enough room
                if (width >= compactUpgradeW && height >= compactUpgradeH)
                {
                    // Check if we can go straight to Detailed
                    if (width >= detailedUpgradeW && height >= detailedUpgradeH)
                    {
                        return "Detailed";
                    }
                    return "Standard";
                }
                // Stay in Compact unless we need to downgrade to Mini
                if (width <= miniDowngradeW || height <= miniDowngradeH)
                {
                    return "Mini";
                }
                return "Compact";

            case "Standard":
                // Upgrade to Detailed if enough room
                if (width >= detailedUpgradeW && height >= detailedUpgradeH)
                {
                    return "Detailed";
                }
                // Downgrade to Compact only if significantly smaller
                if (width <= compactDowngradeW || height <= compactDowngradeH)
                {
                    return "Compact";
                }
                return "Standard";

            case "Detailed":
                // Downgrade to Standard if no longer enough room (with hysteresis)
                if (width <= detailedDowngradeW || height <= detailedDowngradeH)
                {
                    // Further check if we should go to Compact
                    if (width <= compactDowngradeW || height <= compactDowngradeH)
                    {
                        return "Compact";
                    }
                    return "Standard";
                }
                return "Detailed";

            default:
                // First-time default: use mid-range thresholds
                if (width >= detailedUpgradeW && height >= detailedUpgradeH)
                {
                    return "Detailed";
                }
                if (width >= compactUpgradeW && height >= compactUpgradeH)
                {
                    return "Standard";
                }
                if (width >= miniUpgradeW && height >= miniUpgradeH)
                {
                    return "Compact";
                }
                return "Mini";
        }
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

    private async Task EnsureLocationAsync()
    {
        if (_settingsService is null)
        {
            _latitude = 39.9042;
            _longitude = 116.4074;
            _locationName = "Beijing";
            return;
        }

        var settings = _settingsService.Settings;
        if (settings.WeatherAutoLocation)
        {
            // Try Windows location API first
            var (lat, lon, name) = await WindowsLocationHelper.GetLocationAsync(_localizationService);
            _latitude = lat;
            _longitude = lon;
            _locationName = name;
        }
        else
        {
            if (settings.WeatherLatitude != 0 || settings.WeatherLongitude != 0)
            {
                _latitude = settings.WeatherLatitude;
                _longitude = settings.WeatherLongitude;
                _locationName = settings.WeatherCityName;
            }
            else
            {
                // Try to resolve the saved city name
                if (!string.IsNullOrWhiteSpace(settings.WeatherCityName))
                {
                    var item = await _weatherService.ResolveCityAsync(
                        settings.WeatherCityName,
                        _localizationService.IsEnglish ? "en" : "zh");
                    if (item is not null)
                    {
                        _latitude = item.Latitude;
                        _longitude = item.Longitude;
                        _locationName = item.DisplayName;
                        settings.WeatherLatitude = _latitude;
                        settings.WeatherLongitude = _longitude;
                        _settingsService.SaveDebounced();
                    }
                }
                else
                {
                    _latitude = 39.9042;
                    _longitude = 116.4074;
                    _locationName = _localizationService.IsEnglish ? "Beijing" : "\u5317\u4EAC";
                }
            }
        }
    }

    private void ApplyWeatherData(WeatherData data)
    {
        if (data.Current is null)
        {
            return;
        }

        var current = data.Current;
        bool isEnglish = _localizationService.IsEnglish;

        IsDay = current.IsDay == 1;
        CurrentWeatherCode = current.WeatherCode;
        CurrentCondition = WeatherCodeMapper.GetCondition(current.WeatherCode);
        CurrentEmoji = WeatherCodeMapper.GetEmoji(current.WeatherCode, IsDay);
        CurrentDescription = isEnglish
            ? WeatherCodeMapper.GetDescriptionEn(current.WeatherCode)
            : WeatherCodeMapper.GetDescriptionZh(current.WeatherCode);
        CurrentTemperatureText = FormatTemperature(current.Temperature);

        ApparentTemperatureText = isEnglish
            ? $"Feels {FormatTemperature(current.ApparentTemperature)}"
            : $"\u4F53\u611F {FormatTemperature(current.ApparentTemperature)}";

        HumidityText = isEnglish
            ? $"{(int)current.Humidity}%"
            : $"\u6E7F\u5EA6 {(int)current.Humidity}%";

        WindText = isEnglish
            ? $"{FormatWindSpeed(current.WindSpeed)} {GetWindDirectionText(current.WindDirection, isEnglish)}"
            : $"{FormatWindSpeed(current.WindSpeed)} {GetWindDirectionText(current.WindDirection, isEnglish)}";

        PressureText = isEnglish
            ? $"{(int)current.Pressure} hPa"
            : $"\u6C14\u538B {(int)current.Pressure} hPa";

        LocationDisplay = string.IsNullOrWhiteSpace(data.LocationName)
            ? _locationName
            : data.LocationName;

        // Daily forecast
        if (data.Daily is not null)
        {
            PopulateDailyForecast(data.Daily, isEnglish);

            if (data.Daily.UvIndexMax.Count > 0)
            {
                double uv = data.Daily.UvIndexMax[0];
                UvIndexText = isEnglish ? $"UV {uv:0}" : $"\u7D2B\u5916 {uv:0}";
            }

            if (data.Daily.PrecipitationProbabilityMax.Count > 0)
            {
                double precip = data.Daily.PrecipitationProbabilityMax[0];
                PrecipitationText = isEnglish
                    ? $"{(int)precip}% rain"
                    : $"\u964D\u6C34\u6982\u7387 {(int)precip}%";
            }

            if (data.Daily.Sunrise.Count > 0)
            {
                SunriseText = FormatTime(data.Daily.Sunrise[0]);
            }

            if (data.Daily.Sunset.Count > 0)
            {
                SunsetText = FormatTime(data.Daily.Sunset[0]);
            }
        }

        // Hourly forecast
        if (data.Hourly is not null)
        {
            PopulateHourlyForecast(data.Hourly);
        }

        // Update rich skin gradient based on condition
        UpdateRichSkinColors();

        // Raise all visibility/animation property changes
        OnPropertyChanged(nameof(ForecastVisibility));
        OnPropertyChanged(nameof(WeekForecastVisibility));
        OnPropertyChanged(nameof(SunriseVisibility));
        OnPropertyChanged(nameof(UvIndexVisibility));
        OnPropertyChanged(nameof(PrecipitationVisibility));
        OnPropertyChanged(nameof(HumidityVisibility));
        OnPropertyChanged(nameof(WindVisibility));
        OnPropertyChanged(nameof(PressureVisibility));
        OnPropertyChanged(nameof(RainAnimationVisibility));
        OnPropertyChanged(nameof(SnowAnimationVisibility));
        OnPropertyChanged(nameof(ThunderAnimationVisibility));
        OnPropertyChanged(nameof(ClearAnimationVisibility));
        OnPropertyChanged(nameof(MiniHumidityText));
        OnPropertyChanged(nameof(MiniWindText));
        OnPropertyChanged(nameof(MiniPrecipText));
        OnPropertyChanged(nameof(MiniHumidityVisibility));
        OnPropertyChanged(nameof(MiniWindVisibility));
        OnPropertyChanged(nameof(MiniPrecipVisibility));
        OnPropertyChanged(nameof(MiniLocationVisibility));
        OnPropertyChanged(nameof(MiniDescriptionVisibility));
    }

    private void UpdateRichSkinColors()
    {
        // Condition-based gradient colors inspired by premium weather apps
        (Color top, Color bottom) = _currentCondition switch
        {
            WeatherCodeMapper.WeatherCondition.Clear when IsDay =>
                (Color.FromArgb(0xFF, 0x2E, 0x86, 0xDE), Color.FromArgb(0xFF, 0x5A, 0xBF, 0xF3)),  // Sky blue
            WeatherCodeMapper.WeatherCondition.Clear when !IsDay =>
                (Color.FromArgb(0xFF, 0x1A, 0x1A, 0x3E), Color.FromArgb(0xFF, 0x2D, 0x35, 0x61)),  // Night dark blue
            WeatherCodeMapper.WeatherCondition.Cloudy =>
                (Color.FromArgb(0xFF, 0x57, 0x60, 0x6F), Color.FromArgb(0xFF, 0x77, 0x8C, 0xA3)),  // Gray
            WeatherCodeMapper.WeatherCondition.Rain or WeatherCodeMapper.WeatherCondition.Drizzle =>
                (Color.FromArgb(0xFF, 0x37, 0x47, 0x4F), Color.FromArgb(0xFF, 0x54, 0x6E, 0x7A)),  // Slate blue
            WeatherCodeMapper.WeatherCondition.Snow =>
                (Color.FromArgb(0xFF, 0x64, 0x7D, 0x8C), Color.FromArgb(0xFF, 0xA5, 0xB4, 0xC4)),  // Light gray-blue
            WeatherCodeMapper.WeatherCondition.Thunderstorm =>
                (Color.FromArgb(0xFF, 0x2A, 0x12, 0x3A), Color.FromArgb(0xFF, 0x3D, 0x1B, 0x5E)),  // Dark purple
            WeatherCodeMapper.WeatherCondition.Fog =>
                (Color.FromArgb(0xFF, 0x6E, 0x7B, 0x8B), Color.FromArgb(0xFF, 0x9C, 0xAA, 0xBC)),  // Foggy gray
            _ => (Color.FromArgb(0xFF, 0x4A, 0x90, 0xD9), Color.FromArgb(0xFF, 0x74, 0xB9, 0xFF))   // Default
        };

        RichBackdropTopColor = top;
        RichBackdropBottomColor = bottom;

        // Determine if the rich skin background is dark enough to need light text.
        // This handles the case where light mode + night/dark-weather conditions
        // would result in dark text on a dark background.
        bool needsLightText = _skin == SettingsService.WeatherSkinRich &&
            (IsColorDark(top) || IsColorDark(bottom));
        if (RichSkinUsesLightText != needsLightText)
        {
            RichSkinUsesLightText = needsLightText;
            OnPropertyChanged(nameof(RichSkinUsesLightText));
        }

        OnPropertyChanged(nameof(RichBackdropTopColor));
        OnPropertyChanged(nameof(RichBackdropBottomColor));
    }

    private void PopulateDailyForecast(WeatherDaily daily, bool isEnglish)
    {
        DailyForecast.Clear();
        int count = Math.Min(daily.Time.Count, 7);
        for (int i = 0; i < count; i++)
        {
            string dateStr = i < daily.Time.Count ? daily.Time[i] : string.Empty;
            int wmoCode = i < daily.WeatherCode.Count ? daily.WeatherCode[i] : 0;
            double tempMax = i < daily.TemperatureMax.Count ? daily.TemperatureMax[i] : 0;
            double tempMin = i < daily.TemperatureMin.Count ? daily.TemperatureMin[i] : 0;
            double precipProb = i < daily.PrecipitationProbabilityMax.Count ? daily.PrecipitationProbabilityMax[i] : 0;

            string dayLabel;
            if (i == 0)
            {
                dayLabel = isEnglish ? "Today" : "\u4ECA\u5929";
            }
            else if (i == 1)
            {
                dayLabel = isEnglish ? "Tomorrow" : "\u660E\u5929";
            }
            else
            {
                dayLabel = ParseDateToDayLabel(dateStr, isEnglish);
            }

            DailyForecast.Add(new WeatherDayViewModel
            {
                DayLabel = dayLabel,
                Emoji = WeatherCodeMapper.GetEmoji(wmoCode, isDay: true),
                IconGlyph = WeatherCodeMapper.GetGlyph(wmoCode, isDay: true),
                Description = isEnglish
                    ? WeatherCodeMapper.GetDescriptionEn(wmoCode)
                    : WeatherCodeMapper.GetDescriptionZh(wmoCode),
                TempMaxText = FormatTemperature(tempMax),
                TempMinText = FormatTemperature(tempMin),
                PrecipitationText = $"{(int)precipProb}%"
            });
        }
    }

    private void PopulateHourlyForecast(WeatherHourly hourly)
    {
        HourlyForecast.Clear();
        int startIndex = FindCurrentHourIndex(hourly.Time);
        int count = Math.Min(24, hourly.Time.Count - startIndex);
        for (int i = 0; i < count; i++)
        {
            int idx = startIndex + i;
            if (idx >= hourly.Time.Count)
            {
                break;
            }

            string timeStr = hourly.Time[idx];
            double temp = idx < hourly.Temperature.Count ? hourly.Temperature[idx] : 0;
            double precip = idx < hourly.PrecipitationProbability.Count ? hourly.PrecipitationProbability[idx] : 0;
            int wmoCode = idx < hourly.WeatherCode.Count ? hourly.WeatherCode[idx] : 0;

            string hourLabel = FormatHourLabel(timeStr);
            bool isDaytime = IsDaytimeHour(timeStr);

            HourlyForecast.Add(new WeatherHourViewModel
            {
                HourLabel = hourLabel,
                TemperatureText = FormatTemperature(temp),
                PrecipitationText = precip > 0 ? $"{(int)precip}%" : "",
                Emoji = WeatherCodeMapper.GetEmoji(wmoCode, isDaytime),
                IconGlyph = WeatherCodeMapper.GetGlyph(wmoCode, isDaytime)
            });
        }
    }

    private static int FindCurrentHourIndex(List<string> times)
    {
        if (times.Count == 0)
        {
            return 0;
        }

        string now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH");
        for (int i = 0; i < times.Count; i++)
        {
            if (times[i].StartsWith(now, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static bool IsDaytimeHour(string isoTime)
    {
        try
        {
            var dt = DateTimeOffset.Parse(isoTime);
            return dt.Hour >= 6 && dt.Hour < 19;
        }
        catch
        {
            return true;
        }
    }

    private static string FormatHourLabel(string isoTime)
    {
        try
        {
            var dt = DateTimeOffset.Parse(isoTime);
            return dt.Hour == 0 ? "0:00" : $"{dt.Hour}:00";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatTime(string isoTime)
    {
        try
        {
            var dt = DateTimeOffset.Parse(isoTime);
            return dt.ToString("HH:mm");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ParseDateToDayLabel(string dateStr, bool isEnglish)
    {
        try
        {
            var dt = DateTimeOffset.Parse(dateStr);
            string[] zhDays = ["\u5468\u65E5", "\u5468\u4E00", "\u5468\u4E8C", "\u5468\u4E09", "\u5468\u56DB", "\u5468\u4E94", "\u5468\u516D"];
            string[] enDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
            int dayOfWeek = ((int)dt.DayOfWeek + 6) % 7;
            return isEnglish ? enDays[dayOfWeek] : zhDays[dayOfWeek];
        }
        catch
        {
            return dateStr;
        }
    }

    private string FormatTemperature(double celsius)
    {
        double value = _temperatureUnit == SettingsService.WeatherTemperatureUnitFahrenheit
            ? celsius * 9 / 5 + 32
            : celsius;
        string unit = _temperatureUnit == SettingsService.WeatherTemperatureUnitFahrenheit ? "\u00B0F" : "\u00B0C";
        return $"{Math.Round(value)}{unit}";
    }

    private string FormatWindSpeed(double kmh)
    {
        double value;
        string unit;
        if (_windSpeedUnit == SettingsService.WeatherWindSpeedUnitMs)
        {
            value = kmh / 3.6;
            unit = "m/s";
        }
        else if (_windSpeedUnit == SettingsService.WeatherWindSpeedUnitMph)
        {
            value = kmh / 1.609;
            unit = "mph";
        }
        else
        {
            value = kmh;
            unit = "km/h";
        }

        return $"{Math.Round(value, 1)} {unit}";
    }

    private static string GetWindDirectionText(double direction, bool isEnglish)
    {
        string[] zhDirs = ["\u5317", "\u4E1C\u5317", "\u4E1C", "\u4E1C\u5357", "\u5357", "\u897F\u5357", "\u897F", "\u897F\u5317"];
        string[] enDirs = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        int index = (int)Math.Round(direction / 45) % 8;
        return isEnglish ? enDirs[index] : zhDirs[index];
    }

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
                _ = RefreshAsync();
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
}
