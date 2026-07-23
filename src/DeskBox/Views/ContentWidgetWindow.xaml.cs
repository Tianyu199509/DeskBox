using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Lightweight host window for future non-file widget content.
/// User-facing creation remains gated by WidgetRegistry.
/// </summary>
public sealed partial class ContentWidgetWindow : WidgetWindowBase, IDesktopWidgetWindow
{
    private readonly WidgetConfig _config;
    private readonly WidgetContentDescriptor _descriptor;
    private readonly WidgetChromeModeResolver _chromeModeResolver;
    private readonly WidgetShellContentHost _contentHost;
    private readonly ContentWidgetTitleViewModel _titleViewModel;

    private bool _isHidePrepared;
    private bool _isCommittingTitleRename;
    private bool _isCancellingTitleRename;
    private INotifyPropertyChanged? _compactPresentationSource;

    private bool _isVisibleOnDesktop;
    private bool _searchHistorySubscribed;

    // Safety-net timer that re-evaluates the music compact presentation every
    // 500 ms while the music widget is active. The music ViewModel only raises
    // PropertyChanged when Position / Duration / IsPlaying actually change, so
    // if a player keeps Position at 0 for tens of seconds (very common at
    // song-start) and never reports a Duration change, the capsule can stay
    // stuck on a stale Progress=0 / ScaleX=0 render and look completely empty.
    // A forced refresh on a timer guarantees IsProgressIndeterminate is
    // re-evaluated continuously so the sweeping+pulsing bar keeps showing
    // during that window.
    private DispatcherTimer? _musicProgressRefreshTimer;

    public ContentWidgetWindow(
        WidgetConfig config,
        IWidgetContent content,
        SettingsService settingsService,
        WidgetContentDescriptor descriptor)
    {
        _config = config;
        _descriptor = descriptor;
        _chromeModeResolver = new WidgetChromeModeResolver(settingsService);

        InitializeComponent();

        SettingsService = settingsService;
        HWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(HWnd);
        AppWindow = AppWindow.GetFromWindowId(windowId);
        Diagnostics = new WidgetWindowDiagnostics("Content", _config, () => HWnd);
        TrayAnimation = new WidgetTrayAnimationController(
            AppWindow,
            RootGrid,
            DispatcherQueue,
            HWnd,
            GetCurrentAnimationBounds,
            LogTrayWindow);
        _contentHost = new WidgetShellContentHost(ContentWidgetShell);

        _titleViewModel = new ContentWidgetTitleViewModel(_config, settingsService);
        ContentWidgetShell.DataContext = _titleViewModel;
        ContentWidgetShell.TitleGlyph = descriptor.DefaultGlyph;
        ContentWidgetShell.TitleIconKind = WidgetTitleIconKindNames.FromWidgetKind(_config.WidgetKind);
        ContentWidgetShell.ShowHoverButtons = settingsService.Settings.ShowHoverButtons;
        ContentWidgetShell.IsTitleEditable = true;
        ApplyLocalizedTitleActionTooltips();

        ConfigureWindowCore();
        ApplyTitleBarLayout();
        SetupEventHandlers();
        
        // ✅ Set initial title
        this.Title = App.Current.LocalizationService.T("Window.ContentWidget.Title");
        
        _ = LoadContentAsync(content);

        App.Current.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    // ── Abstract member overrides ──────────────────────────────

    public override WidgetConfig Config => _config;
    protected override double WidgetOpacity => SettingsService.Settings.WidgetOpacity;
    protected override FrameworkElement RootElement => RootGrid;
    protected override WidgetShell WidgetShellControl => ContentWidgetShell;
    protected override string LogPrefix => "Content";
    protected override bool IsSizeLocked => _config.IsSizeLocked;
    protected override bool IsPositionLocked => _config.IsPositionLocked;

    protected override WidgetCompactPresentation CreateCompactPresentation()
    {
        var localization = App.Current.LocalizationService;
        string contentMode = ResolveEffectiveCompactContentMode();
        return CurrentContent switch
        {
            TodoWidgetContentAdapter todo => CreateTodoCompactPresentation(todo, contentMode, localization),
            MusicWidgetContentAdapter music =>
                CreateMusicCompactPresentation(music, contentMode),
            WeatherWidgetContentAdapter weather => CreateWeatherCompactPresentation(weather, contentMode),
            SearchWidgetContentAdapter => CreateSearchCompactPresentation(contentMode, localization),
            _ => new WidgetCompactPresentation(
                _titleViewModel.DisplayName,
                string.Empty,
                _descriptor.DefaultGlyph,
                localization.T("Widget.Compact.DropHint"),
                EnableMarquee: true,
                LiveStateKey: _titleViewModel.DisplayName)
        };
    }

    private WidgetCompactPresentation CreateSearchCompactPresentation(
        string contentMode,
        LocalizationService localization)
    {
        // Smart mode gets a dynamic subtitle so the search capsule matches the
        // height/style of the weather/quick-capture capsules instead of looking
        // bare. The subtitle shows the most recent query ("最近：xxx"); when there
        // is no history (or history is disabled / sensitive content is hidden) it
        // falls back to a static hint so the line never appears empty.
        bool stacked = contentMode == SettingsService.WidgetCompactContentModeSmart;

        string summary = string.Empty;
        string recentKey = string.Empty;
        if (stacked)
        {
            string? recent = null;
            if (SettingsService.Settings.SearchSaveHistory &&
                !SettingsService.Settings.WidgetCompactHideSensitiveContent)
            {
                recent = App.Current.SearchHistoryService?.RecentQueries.FirstOrDefault();
            }

            summary = string.IsNullOrWhiteSpace(recent)
                ? localization.T("Search.Compact.Hint")
                : localization.Format("Search.Compact.Recent", recent);
            recentKey = recent ?? string.Empty;
        }

        return new WidgetCompactPresentation(
            _titleViewModel.DisplayName,
            summary,
            _descriptor.DefaultGlyph,
            localization.T("Widget.Compact.DropHint"),
            ShowPrimaryAction: true,
            PrimaryActionGlyph: "\uE721",
            UseStackedText: stacked,
            EnableMarquee: true,
            LiveStateKey: string.Join("|", _titleViewModel.DisplayName, recentKey));
    }

    private WidgetCompactPresentation CreateMusicCompactPresentation(
        MusicWidgetContentAdapter music,
        string contentMode)
    {
        bool hidesSensitiveContent = WidgetCompactPrivacyPolicy.HidesSensitiveContent(
            SettingsService.Settings.WidgetCompactHideSensitiveContent,
            Config.WidgetKind);
        string title = hidesSensitiveContent
            ? _titleViewModel.DisplayName
            : music.ViewModel.Title;
        string summary = contentMode == SettingsService.WidgetCompactContentModeMinimal
            ? string.Empty
            : hidesSensitiveContent
                ? music.ViewModel.StatusText
                : music.ViewModel.Artist;

        // Plain, determinate progress shown below the artist name inside the
        // capsule. Mirrors the EXPANDED music view: the track is always
        // visible while a session exists, and the fill grows from 0. Uses
        // SeekValue/SeekMaximum (same source as the expanded view), so when
        // the player hasn't reported a duration yet (common for streaming in
        // the first ~40 s) SeekMaximum falls back to 1 and the fill reads 0
        // — i.e. an empty-but-visible track, identical to the expanded view.
        // Only when there is no session at all do we pass null (hide the bar).
        double? musicProgress = null;
        if (music.ViewModel.HasSession)
        {
            double max = music.ViewModel.SeekMaximum;
            musicProgress = max > 0
                ? Math.Clamp(music.ViewModel.SeekValue / max, 0, 1)
                : 0;
        }

        return new WidgetCompactPresentation(
            title,
            summary,
            _descriptor.DefaultGlyph,
            string.Empty,
            hidesSensitiveContent ? null : music.ViewModel.ThumbnailImage,
            ShowMediaControls: contentMode == SettingsService.WidgetCompactContentModeSmart,
            IsPlaying: music.ViewModel.IsPlaying,
            CanGoPrevious: music.ViewModel.CanGoPrevious,
            CanGoNext: music.ViewModel.CanGoNext,
            UseStackedText: contentMode == SettingsService.WidgetCompactContentModeSmart,
            EnableMarquee: !hidesSensitiveContent,
            Progress: null,
            IsProgressIndeterminate: false,
            UseFullBleedBackground: !hidesSensitiveContent,
            ShowSpectrum: !hidesSensitiveContent,
            LiveStateKey: hidesSensitiveContent
                ? string.Join(
                    "|",
                    music.ViewModel.PlaybackState,
                    music.ViewModel.Duration.Ticks)
                : string.Join(
                    "|",
                    music.ViewModel.Title,
                    music.ViewModel.Artist,
                    music.ViewModel.PlaybackState,
                    music.ViewModel.Duration.Ticks),
            ShowVinyl: !hidesSensitiveContent,
            MusicProgress: musicProgress);
    }

    private WidgetCompactPresentation CreateWeatherCompactPresentation(
        WeatherWidgetContentAdapter weather,
        string contentMode)
    {
        var condition = weather.ViewModel.CurrentCondition;
        bool isDay = weather.ViewModel.IsDay;
        bool isAttention = IsCompactWeatherAttentionRequired(weather);

        // Color field based on weather condition
        var (colorStart, colorEnd) = condition switch
        {
            Helpers.WeatherCodeMapper.WeatherCondition.Clear => isDay
                ? (Windows.UI.Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B), Windows.UI.Color.FromArgb(0xFF, 0xF9, 0x73, 0x16))
                : (Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x3A, 0x5F), Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A)),
            Helpers.WeatherCodeMapper.WeatherCondition.Cloudy or
            Helpers.WeatherCodeMapper.WeatherCondition.Fog =>
                (Windows.UI.Color.FromArgb(0xFF, 0x64, 0x74, 0x8B), Windows.UI.Color.FromArgb(0xFF, 0x47, 0x55, 0x69)),
            Helpers.WeatherCodeMapper.WeatherCondition.Rain or
            Helpers.WeatherCodeMapper.WeatherCondition.Drizzle =>
                (Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x40, 0xAF), Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x3A, 0x5F)),
            Helpers.WeatherCodeMapper.WeatherCondition.Snow =>
                (Windows.UI.Color.FromArgb(0xFF, 0x93, 0xC5, 0xFD), Windows.UI.Color.FromArgb(0xFF, 0xBF, 0xDB, 0xFE)),
            Helpers.WeatherCodeMapper.WeatherCondition.Thunderstorm =>
                (Windows.UI.Color.FromArgb(0xFF, 0x4C, 0x1D, 0x95), Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x1B, 0x4B)),
            _ => ((Windows.UI.Color?)null, (Windows.UI.Color?)null)
        };

        // Particles based on condition
        var particleKind = condition switch
        {
            Helpers.WeatherCodeMapper.WeatherCondition.Rain or
            Helpers.WeatherCodeMapper.WeatherCondition.Drizzle or
            Helpers.WeatherCodeMapper.WeatherCondition.Thunderstorm => CompactParticleKind.Rain,
            Helpers.WeatherCodeMapper.WeatherCondition.Snow => CompactParticleKind.Snow,
            _ => CompactParticleKind.None
        };

        return new WidgetCompactPresentation(
            string.IsNullOrWhiteSpace(weather.ViewModel.CurrentTemperatureText)
                ? _titleViewModel.DisplayName
                : weather.ViewModel.CurrentTemperatureText,
            BuildWeatherCompactSummary(weather, contentMode),
            _descriptor.DefaultGlyph,
            string.Empty,
            UseStackedText: contentMode == SettingsService.WidgetCompactContentModeSmart,
            EnableMarquee: true,
            Progress: isAttention ? 1 : null,
            IsAttention: isAttention,
            EmojiIcon: Helpers.WeatherCodeMapper.GetEmoji(
                weather.ViewModel.CurrentWeatherCode, isDay),
            BackgroundColorStart: colorStart,
            BackgroundColorEnd: colorEnd,
            ParticleKind: particleKind,
            LiveStateKey: string.Join(
                "|",
                weather.ViewModel.CurrentTemperatureText,
                weather.ViewModel.CurrentDescription,
                weather.ViewModel.PrecipitationText));
    }

    private WidgetCompactPresentation CreateTodoCompactPresentation(
        TodoWidgetContentAdapter todo,
        string contentMode,
        LocalizationService localization)
    {
        if (SettingsService.Settings.WidgetCompactHideSensitiveContent &&
            contentMode == SettingsService.WidgetCompactContentModeSmart)
        {
            contentMode = SettingsService.WidgetCompactContentModeSummary;
        }

        var nextItem = GetNextCompactTodoItem(todo);
        int overdueCount = todo.ViewModel.Items.Count(item => item.IsOverdue);
        string countSummary = localization.Format(
            "Widget.Compact.TodoSummary",
            todo.ViewModel.TodayFilterCount,
            overdueCount);

        WidgetCompactPresentation presentation = contentMode switch
        {
            SettingsService.WidgetCompactContentModeMinimal => new WidgetCompactPresentation(
                _titleViewModel.DisplayName,
                string.Empty,
                _descriptor.DefaultGlyph,
                localization.T("Widget.Compact.TodoDropHint")),
            SettingsService.WidgetCompactContentModeSmart when nextItem is not null =>
                new WidgetCompactPresentation(
                    NormalizeCompactSingleLine(nextItem.Text),
                    BuildCompactTodoDueSummary(nextItem, countSummary, localization),
                    _descriptor.DefaultGlyph,
                    localization.T("Widget.Compact.TodoDropHint"),
                    ShowPrimaryAction: true),
            _ => new WidgetCompactPresentation(
                _titleViewModel.DisplayName,
                countSummary,
                _descriptor.DefaultGlyph,
                localization.T("Widget.Compact.TodoDropHint"))
        };

        int totalCount = todo.ViewModel.Items.Count;
        int completedCount = todo.ViewModel.CompletedCount;

        return presentation with
        {
            EnableMarquee = true,
            Progress = totalCount > 0
                ? completedCount / (double)totalCount
                : null,
            IsAttention = overdueCount > 0,
            LiveStateKey = string.Join(
                "|",
                nextItem?.Id ?? string.Empty,
                nextItem?.Text ?? string.Empty,
                completedCount,
                totalCount,
                overdueCount)
        };
    }

    private static double? GetMusicCompactProgress(MusicWidgetContentAdapter music)
    {
        double duration = music.ViewModel.Duration.TotalSeconds;
        // Return 0 (not null) when Duration is unknown so the determinate branch
        // keeps the track visible — matching the expanded view, which always
        // shows the track even at 0% fill. Returning null would hit the hide
        // branch and make the whole bar disappear until Duration arrives.
        return duration > 0
            ? Math.Clamp(music.ViewModel.Position.TotalSeconds / duration, 0, 1)
            : 0;
    }

    private static bool IsCompactWeatherAttentionRequired(WeatherWidgetContentAdapter weather)
    {
        int code = weather.ViewModel.CurrentWeatherCode;
        return code is >= 51 and <= 67 or
            >= 71 and <= 86 or
            >= 95 and <= 99;
    }

    private static string NormalizeCompactSingleLine(string? text)
    {
        return string.Join(
            " ",
            (text ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string BuildCompactTodoDueSummary(
        TodoItemViewModel item,
        string fallback,
        LocalizationService localization)
    {
        if (item.DueDate is not { } dueDate)
        {
            return fallback;
        }

        if (item.IsOverdue)
        {
            return localization.T("Todo.Due.OverdueSuffix");
        }

        DateTimeOffset localDueDate = dueDate.ToLocalTime();
        DateTime today = DateTime.Today;
        if (localDueDate.Date == today)
        {
            return localization.Format("Todo.Due.TodayAt", localDueDate.ToString("HH:mm"));
        }

        if (localDueDate.Date == today.AddDays(1))
        {
            return localization.Format("Todo.Due.TomorrowAt", localDueDate.ToString("HH:mm"));
        }

        return localDueDate.ToString("M/d");
    }

    private static TodoItemViewModel? GetNextCompactTodoItem(TodoWidgetContentAdapter todo) =>
        todo.ViewModel.Items
            .Where(item => !item.IsCompleted)
            .OrderByDescending(item => item.IsOverdue)
            .ThenByDescending(item => item.IsImportant)
            .ThenBy(item => item.DueDate ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();

    private static string BuildWeatherCompactSummary(
        WeatherWidgetContentAdapter weather,
        string contentMode)
    {
        if (contentMode == SettingsService.WidgetCompactContentModeMinimal)
        {
            return string.Empty;
        }

        string description = weather.ViewModel.CurrentDescription;
        if (contentMode != SettingsService.WidgetCompactContentModeSmart ||
            string.IsNullOrWhiteSpace(weather.ViewModel.PrecipitationText))
        {
            return description;
        }

        return string.IsNullOrWhiteSpace(description)
            ? weather.ViewModel.PrecipitationText
            : $"{description} · {weather.ViewModel.PrecipitationText}";
    }

    protected override async Task OnCompactPrimaryActionRequestedAsync()
    {
        if (CurrentContent is SearchWidgetContentAdapter)
        {
            App.Current.OpenSearchPopup();
            return;
        }

        if (CurrentContent is not TodoWidgetContentAdapter todo ||
            GetNextCompactTodoItem(todo) is not { } item)
        {
            return;
        }

        await todo.ViewModel.SetCompletedAsync(item.Id, true);
    }

    protected override Task OnCompactPreviousRequestedAsync()
    {
        return CurrentContent is MusicWidgetContentAdapter music
            ? music.ViewModel.PreviousAsync()
            : Task.CompletedTask;
    }

    protected override Task OnCompactPlayPauseRequestedAsync()
    {
        return CurrentContent is MusicWidgetContentAdapter music
            ? music.ViewModel.TogglePlayPauseAsync()
            : Task.CompletedTask;
    }

    protected override Task OnCompactNextRequestedAsync()
    {
        return CurrentContent is MusicWidgetContentAdapter music
            ? music.ViewModel.NextAsync()
            : Task.CompletedTask;
    }

    protected override void UpdateConfigBoundsFromPhysical(
        int x, int y, int width, int height, bool persist)
    {
        if (IsCompactBoundsStateActive)
        {
            if (persist)
            {
                SettingsService.UpdateWidget(_config, notifySubscribers: false);
                SettingsService.SaveDebounced(notifySubscribers: false);
            }
            return;
        }

        var bounds = new RectInt32(x, y, width, height);
        // Use center point for consistent monitor determination across drag/resize.
        var center = new PointInt32(
            x + Math.Max(1, width) / 2,
            y + Math.Max(1, height) / 2);
        var workArea = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest).WorkArea;
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(_config, bounds, workArea);
        if (persist)
        {
            SettingsService.UpdateWidget(_config, notifySubscribers: false);
            SettingsService.SaveDebounced();
        }
    }

    protected override Windows.UI.Color BuildNativeBackdropTintColor(bool isDark)
    {
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var baseColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BuildAccentSurfaceColor(
            isDark,
            accentColor,
            baseColor,
            accentMix: isDark ? 0.08 : 0.16,
            overlayMix: isDark ? 0.04 : 0.08);
    }

    // ── Virtual hooks ──────────────────────────────────────────

    protected override void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(SettingsService.Settings.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        string materialType = SettingsService.Settings.WidgetMaterialType;

        // Simplified layering: only apply surface color overlay for Solid mode.
        if (materialType is SettingsService.WidgetMaterialTypeSolid)
        {
            var surfaceColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);
            ContentWidgetShell.BackgroundSurface.Background = GetOrUpdateSolidColorBrush(
                ContentWidgetShell.BackgroundSurface.Background,
                surfaceColor);
        }
        else
        {
            ContentWidgetShell.BackgroundSurface.Background = GetOrUpdateSolidColorBrush(
                ContentWidgetShell.BackgroundSurface.Background,
                Colors.Transparent);
        }

        var (borderThickness, borderColor, dividerColor) = GetWidgetBorderVisuals(isDark, accentColor);
        var iconForeground = ColorHelper.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);

        ContentWidgetShell.BackgroundSurface.BorderThickness = new Thickness(borderThickness);
        ContentWidgetShell.BackgroundSurface.BorderBrush = GetOrUpdateSolidColorBrush(
            ContentWidgetShell.BackgroundSurface.BorderBrush,
            borderColor);
        ContentWidgetShell.BackgroundSurface.CornerRadius = new CornerRadius(GetCurrentSurfaceCornerRadius());
        ContentWidgetShell.Divider.Background = GetOrUpdateSolidColorBrush(
            ContentWidgetShell.Divider.Background,
            dividerColor);
        ContentWidgetShell.TitleIconAccentColor = iconForeground;
        ContentWidgetShell.TitleIconMode = SettingsService.Settings.WidgetTitleIconMode;
    }

    protected override void OnRootElementLoaded()
    {
        RootGrid.Focus(FocusState.Programmatic);
    }

    // ── IDesktopWidgetWindow implementation ────────────────────

    public IntPtr WindowHandle => HWnd;
    public WidgetWindowIdentity Identity => Diagnostics.Identity;
    public Windows.Foundation.Rect AnimationBounds => GetCurrentAnimationBounds();
        public Windows.Foundation.Rect RestingAnimationBounds => TrayAnimation.GetRestingAnimationBounds();

    public new bool Visible
    {
        get => _isVisibleOnDesktop;
        private set => _isVisibleOnDesktop = value;
    }

    internal IWidgetContent? CurrentContent => _contentHost.CurrentContent;

    public void ApplyAppearancePreview()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyAppearancePreview);
            return;
        }

        if (IsClosing)
        {
            return;
        }

        ApplyWindowCornerPreference();
        ApplyBackdropPreference();
        ContentWidgetShell.ShowHoverButtons = SettingsService.Settings.ShowHoverButtons;
        ApplyTitleBarLayout();
        _contentHost.ApplyAppearance();
    }

    public void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY)
    {
        TrayAnimation.SetOffsetOverride(offsetX, offsetY);
    }

public void PrepareTrayShowAnimation()
{
TrayAnimation.NextGeneration();
TrayAnimation.StopAndRestoreWindowPosition();
TrayAnimation.CloakWindowForTrayShow();
_isHidePrepared = false;
IsHideAnimationRunning = false;

        var profile = GetTrayAnimationProfile();
        LogTrayWindow(
            $"PrepareShow gen={TrayAnimation.Generation} effect={SettingsService.Settings.WidgetAnimationEffect} " +
            $"speed={SettingsService.Settings.WidgetAnimationSpeed} enabled={profile.IsEnabled} durationMs={profile.DurationMs}");
        TrayAnimation.PrepareVisualState(
            profile.ShowOffsetX,
            profile.ShowOffsetY,
            profile.ShowStartOpacity,
            profile.ShowStartScale);
    }

    public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)
    {
        ShowWithoutActivation(persistVisibility);
        TrayAnimation.PrepareHiddenState();
        TrayAnimation.RevealWindowForTrayShow();
        PushToBottom();
    }

    public void ShowPreparedRaisedFromTray(bool persistVisibility = true)
    {
        ShowWithoutActivation(persistVisibility);
        TrayAnimation.PrepareHiddenState();
        TrayAnimation.RevealWindowForTrayShow();
        HoldTemporaryTopMost();
    }

    public void PlayTrayShowAnimation()
    {
        PlayTrayRaiseAnimationAfterFirstFrame();
    }

    public void CompleteTrayShowWithoutAnimation()
    {
        TrayAnimation.NextGeneration();
        LogTrayWindow($"CompleteShowWithoutAnimation gen={TrayAnimation.Generation}");
        TrayAnimation.Stop();
        SetTrayAnimationOffsetOverride(null, null);
        TrayAnimation.RestoreVisualState();
        TrayAnimation.RestoreWindowPosition();
        TrayAnimation.RevealWindowForTrayShow();
    }

    public bool PrepareTrayHideAnimation(bool persistVisibility = true)
    {
        if (!Visible || IsHideAnimationRunning)
        {
            LogTrayWindow($"PrepareHide skipped visible={Visible} hideRunning={IsHideAnimationRunning}");
            return false;
        }

TrayAnimation.NextGeneration();
TrayAnimation.RevealWindowForTrayShow();
TrayAnimation.Stop();
IsHideAnimationRunning = true;
        _isHidePrepared = true;
        Visible = false;
        _config.IsVisible = false;
        if (persistVisibility)
        {
            SettingsService.SaveDebounced();
        }

        LogTrayWindow($"PrepareHide gen={TrayAnimation.Generation}");
        TrayAnimation.PrepareVisualState(0, 0, WidgetTrayAnimationController.RestingOpacity, WidgetTrayAnimationController.RestingScale);
        return true;
    }

    public void PlayPreparedTrayHideAnimation()
    {
        if (!_isHidePrepared || !IsHideAnimationRunning)
        {
            return;
        }

        PlayTrayHideAnimation(CompleteTrayHideAnimation);
    }

    public void ActivateRaisedFromTrayBatch()
    {
        if (!Visible)
        {
            return;
        }

        HoldTemporaryTopMost();
        base.Activate();
        Win32Helper.SetForegroundWindow(HWnd);
        RootGrid.Focus(FocusState.Programmatic);
        _contentHost.OnActivated();
    }

    public void EnsureRaisedFromTrayTopMost()
    {
        if (!Visible)
        {
            return;
        }

        AppWindow.Show();
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_SHOWNORMAL);
        WidgetLayerService.BringToFront(HWnd);
        HoldTemporaryTopMost();
    }

    public void ForceRestoreDesktopLayerFromManager()
    {
        RestoreDesktopLayerFromManager();
    }

    public void RestoreDesktopLayerFromManager()
    {
        if (!Visible)
        {
            return;
        }

        RestoreDesktopLayer(force: true);
        _contentHost.OnDeactivated();
    }

    public void HideWindow()
    {
        TrayAnimation.Stop();
        TrayAnimation.RevealWindowForTrayShow();
        IsHideAnimationRunning = false;
        _isHidePrepared = false;
        Visible = false;
        _config.IsVisible = false;
        SettingsService.SaveDebounced();
        WidgetLayerService.ClearTopMost(HWnd);
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_HIDE);
        AppWindow.Hide();
        TrayAnimation.RestoreVisualState();
        TrayAnimation.RestoreWindowPosition();
        _contentHost.OnDeactivated();
        _contentHost.OnWindowVisibilityChanged(false);
    }

    public void CloseWindow()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(CloseWindow);
            return;
        }

        if (IsClosing)
        {
            return;
        }

        IsClosing = true;
        TrayAnimation.RevealWindowForTrayShow();
        WidgetLayerService.ReleaseWindow(HWnd);
        Close();
    }

    // ── Event setup ────────────────────────────────────────────

    private void OnLanguageChanged()
    {
        _titleViewModel.RefreshDisplayName();
        ApplyLocalizedTitleActionTooltips();
        RefreshCompactPresentation();
    }

    private async Task LoadContentAsync(IWidgetContent content)
    {
        await _contentHost.SetContentAsync(content);
        AttachCompactPresentationSource(content);
        RefreshCompactPresentation();
        ApplyTitleActionButtonConfiguration();
    }

    private void AttachCompactPresentationSource(IWidgetContent content)
    {
        if (_compactPresentationSource is not null)
        {
            _compactPresentationSource.PropertyChanged -= CompactPresentationSource_PropertyChanged;
        }

        _compactPresentationSource = content switch
        {
            TodoWidgetContentAdapter todo => todo.ViewModel,
            MusicWidgetContentAdapter music => music.ViewModel,
            WeatherWidgetContentAdapter weather => weather.ViewModel,
            _ => null
        };

        if (_compactPresentationSource is not null)
        {
            _compactPresentationSource.PropertyChanged += CompactPresentationSource_PropertyChanged;
        }

        // Start (or restart) the music progress refresh timer whenever a music
        // widget becomes the active compact source. It ticks at 500 ms and just
        // calls RefreshCompactPresentation, which is a no-op cost-wise if the
        // presentation is already up to date but guarantees the indeterminate
        // branch is re-evaluated every tick during the song-start window.
        if (content is MusicWidgetContentAdapter)
        {
            _musicProgressRefreshTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _musicProgressRefreshTimer.Tick -= OnMusicProgressRefreshTick;
            _musicProgressRefreshTimer.Tick += OnMusicProgressRefreshTick;
            _musicProgressRefreshTimer.Start();
        }
        else
        {
            StopMusicProgressRefreshTimer();
        }

        // The search capsule's dynamic subtitle ("最近：xxx") tracks the recent-query
        // list, which has no INotifyPropertyChanged surface, so subscribe to the
        // history service's change event to refresh the compact presentation live.
        if (content is SearchWidgetContentAdapter &&
            !_searchHistorySubscribed &&
            App.Current.SearchHistoryService is { } historyService)
        {
            historyService.RecentQueriesChanged += OnRecentQueriesChanged;
            _searchHistorySubscribed = true;
        }
    }

    private void OnRecentQueriesChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshCompactPresentation);
            return;
        }

        RefreshCompactPresentation();
    }

    private void OnMusicProgressRefreshTick(object? sender, object e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshCompactPresentation);
            return;
        }

        RefreshCompactPresentation();
    }

    private void StopMusicProgressRefreshTimer()
    {
        if (_musicProgressRefreshTimer is not null)
        {
            _musicProgressRefreshTimer.Stop();
            _musicProgressRefreshTimer.Tick -= OnMusicProgressRefreshTick;
        }
    }

    private void CompactPresentationSource_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshCompactPresentation);
            return;
        }

        RefreshCompactPresentation();
    }

    private void ApplyLocalizedTitleActionTooltips()
    {
        var localization = App.Current.LocalizationService;
        ToolTipService.SetToolTip(ContentWidgetShell.PositionLockActionButton, localization.T("Widget.LockPosition"));
        ToolTipService.SetToolTip(ContentWidgetShell.SizeLockActionButton, localization.T("Widget.LockSize"));
        ToolTipService.SetToolTip(ContentWidgetShell.AddActionButton, localization.T("Widget.Tooltip.Add"));
        ToolTipService.SetToolTip(ContentWidgetShell.MoreActionButton, localization.T("Widget.Tooltip.More"));
        ToolTipService.SetToolTip(ContentWidgetShell.CloseActionButton, localization.T("Widget.FeatureWidget.Disable"));
    }

    private void SetupEventHandlers()
    {
        SettingsService.SettingsChanged += OnSettingsChanged;
        Activated += ContentWidgetWindow_Activated;
        AppWindow.Changed += OnAppWindowChanged;
        DisplayChangeWatcher = new WidgetDisplayChangeWatcher(HWnd, DispatcherQueue, RestoreBoundsAfterDisplayChange);
        ContentWidgetShell.RightTapped += ContentWidgetShell_RightTapped;
        ContentWidgetShell.TitleDoubleTapped += ContentWidgetShell_TitleDoubleTapped;

        foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                child.PointerPressed += ResizeBorder_PointerPressed;
                child.PointerMoved += ResizeBorder_PointerMoved;
                child.PointerReleased += ResizeBorder_PointerReleased;
                child.PointerEntered += ResizeBorder_PointerEntered;
                child.PointerCaptureLost += ResizeBorder_PointerCaptureLost;
            }
        }

        Closed += (_, _) =>
        {
            IsClosing = true;
            Visible = false;
            App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
            SettingsService.SettingsChanged -= OnSettingsChanged;
            AppWindow.Changed -= OnAppWindowChanged;
            ContentWidgetShell.RightTapped -= ContentWidgetShell_RightTapped;
            ContentWidgetShell.TitleDoubleTapped -= ContentWidgetShell_TitleDoubleTapped;
            if (_compactPresentationSource is not null)
            {
                _compactPresentationSource.PropertyChanged -= CompactPresentationSource_PropertyChanged;
                _compactPresentationSource = null;
            }
            if (_searchHistorySubscribed && App.Current.SearchHistoryService is { } historyService)
            {
                historyService.RecentQueriesChanged -= OnRecentQueriesChanged;
                _searchHistorySubscribed = false;
            }
            StopMusicProgressRefreshTimer();
            try { TrayAnimation.RevealWindowForTrayShow(); } catch { }
            try { CleanupBase(); } catch (Exception ex) { App.Log($"[ContentWidget] CleanupBase failed during close: {ex.Message}"); }
            try { _contentHost.DisposeContent(); } catch (Exception ex) { App.Log($"[ContentWidget] DisposeContent failed during close: {ex.Message}"); }

            foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
            {
                if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                {
                    child.PointerPressed -= ResizeBorder_PointerPressed;
                    child.PointerMoved -= ResizeBorder_PointerMoved;
                    child.PointerReleased -= ResizeBorder_PointerReleased;
                    child.PointerEntered -= ResizeBorder_PointerEntered;
                    child.PointerCaptureLost -= ResizeBorder_PointerCaptureLost;
                }
            }
        };
    }

    private void OnSettingsChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnSettingsChanged);
            return;
        }

        ContentWidgetShell.ShowHoverButtons = SettingsService.Settings.ShowHoverButtons;
        _titleViewModel.RefreshMetrics();
        ApplyAppearancePreview();

        // Search capsule subtitle depends on SearchSaveHistory / hide-sensitive flags.
        if (CurrentContent is SearchWidgetContentAdapter)
        {
            RefreshCompactPresentation();
        }
    }

    // ── Drag handlers (delegate to base) ───────────────────────

    private static Windows.UI.Color BuildFrostedSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        double surfaceOpacity)
    {
        double materialOpacity = isDark
            ? Math.Clamp(surfaceOpacity * 0.78, 0.10, 0.82)
            : Math.Clamp(surfaceOpacity * 0.78, 0.0, 0.78);

        return ApplySurfaceOpacity(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x21, 0x24, 0x2A)
                    : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.18 : 0.18,
                overlayMix: isDark ? 0.15 : 0.04),
            materialOpacity);
    }

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        var mixed = BlendColors(baseColor, accentColor, accentMix);
        var overlay = isDark
            ? ColorHelper.FromArgb(0xFF, 0x2B, 0x2F, 0x36)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        return BlendColors(mixed, overlay, overlayMix);
    }

    private static Windows.UI.Color ApplySurfaceOpacity(Windows.UI.Color color, double opacity)
    {
        return Windows.UI.Color.FromArgb(
            (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255),
            color.R,
            color.G,
            color.B);
    }

    private static Windows.UI.Color BlendColors(Windows.UI.Color from, Windows.UI.Color to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Windows.UI.Color.FromArgb(
            0xFF,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    // ── Nested: title view model ───────────────────────────────

    private sealed class ContentWidgetTitleViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly SettingsService _settingsService;

        public ContentWidgetTitleViewModel(WidgetConfig config, SettingsService settingsService)
        {
            Config = config;
            _settingsService = settingsService;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public WidgetConfig Config { get; }

        public string DisplayName
        {
            get
            {
                if (Config.IsDefaultTitle)
                {
                    var localization = App.Current.LocalizationService;
                    var key = Config.WidgetKind switch
                    {
                        WidgetKind.Todo => "Todo.Title",
                        WidgetKind.Weather => "Weather.Title",
                        WidgetKind.Tags => "Tags.Title",
                        WidgetKind.Music => "Music.Title",
                        WidgetKind.Search => "Search.Title",
                        WidgetKind.SystemMonitor => "SystemMonitor.Title",
                        _ => ""
                    };
                    if (!string.IsNullOrEmpty(key))
                    {
                        var localized = localization.T(key);
                        if (!string.IsNullOrEmpty(localized))
                            return localized;
                    }
                }

                return string.IsNullOrWhiteSpace(Config.Name)
                    ? Config.WidgetKind.ToString()
                    : Config.Name;
            }
        }

        public double TitleIconSize
        {
            get
            {
                double iconSize = SettingsService.NormalizeIconSize(_settingsService.Settings.IconSize);
                return Math.Clamp(Math.Round(iconSize * 0.72 * 0.56 * 0.54), 11, 18);
            }
        }

        public double TitleTextSize
        {
            get
            {
                double textSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
                return Math.Min(SettingsService.MaxTextSize + 2, textSize + 3);
            }
        }

        public void RefreshDisplayName()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
        }

        public void RefreshMetrics()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TitleIconSize)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TitleTextSize)));
        }
    }
}
