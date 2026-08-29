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
    private WidgetConfig _config;
    private WidgetContentDescriptor _descriptor;
    private readonly WidgetChromeModeResolver _chromeModeResolver;
    private readonly WidgetShellContentHost _contentHost;
    private readonly ContentWidgetTitleViewModel _titleViewModel;
    private readonly Task _contentLoadTask;
    private const int RevealCompletedBackgroundDelayMs = 240;
    private readonly Dictionary<string, IWidgetContent> _cachedGroupContents =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _cachedGroupContentOrder = [];

    private bool _isHidePrepared;
    private bool _isCommittingTitleRename;
    private bool _isCancellingTitleRename;
    private bool _compactPresentationRefreshQueued;
    private INotifyPropertyChanged? _compactPresentationSource;
    private IWidgetFeedbackSource? _feedbackSource;
    private IWidgetHostContextMenuSource? _hostContextMenuSource;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoRestoreTimer;

    private bool _isVisibleOnDesktop;
    private int _contentVisibilityGeneration;
    private int _queuedContentRevealGeneration = -1;
    private SearchHistoryService? _subscribedSearchHistoryService;

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
        _contentHost = new WidgetShellContentHost(
            ContentWidgetShell,
            TryRetainGroupContent);

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
        
        _contentLoadTask = LoadContentAsync(content);

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
    protected override bool IsCompactExpansionWarmupContentReady =>
        _contentHost.CurrentContent is not null;

    protected override void OnResizeStart()
    {
        if (CurrentContent is IWidgetInteractiveResizeContent resizeContent)
        {
            double titleHeight = Math.Max(0, ContentWidgetShell.ActualTitleBarHeight);
            resizeContent.BeginInteractiveResize(
                Math.Max(1, RootGrid.ActualWidth),
                Math.Max(1, RootGrid.ActualHeight - titleHeight));
        }
    }

    protected override WidgetCompactPresentation CreateCompactPresentation()
    {
        var localization = App.Current.LocalizationService;
        string contentMode = ResolveEffectiveCompactContentMode();
        return CurrentContent switch
        {
            FileSurfaceContent file =>
                CreateFileCompactPresentation(file, contentMode),
            TodoWidgetContentAdapter todo => CreateTodoCompactPresentation(todo, contentMode, localization),
            GlanceWidgetContentAdapter glance =>
                CreateGlanceCompactPresentation(glance),
            MusicWidgetContentAdapter music =>
                CreateMusicCompactPresentation(music, contentMode),
            QuickCaptureSurfaceContent quickCapture =>
                CreateQuickCaptureCompactPresentation(quickCapture, contentMode),
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

    private WidgetCompactPresentation CreateGlanceCompactPresentation(
        GlanceWidgetContentAdapter glance)
    {
        GlanceWidgetViewModel viewModel = glance.ViewModel;
        string title = viewModel.ShowTime && !string.IsNullOrWhiteSpace(viewModel.TimeText)
            ? viewModel.TimeText
            : viewModel.ShowDate && !string.IsNullOrWhiteSpace(viewModel.DateText)
                ? viewModel.DateText
                : viewModel.ShowWeekday && !string.IsNullOrWhiteSpace(viewModel.WeekdayText)
                    ? viewModel.WeekdayText
                    : viewModel.HasTraditionalCalendar
                        ? viewModel.TraditionalCalendarTitle
                        : string.Empty;

        var summaryParts = new List<string>(3);
        if (viewModel.ShowDate &&
            !string.IsNullOrWhiteSpace(viewModel.DateText) &&
            !string.Equals(title, viewModel.DateText, StringComparison.Ordinal))
        {
            summaryParts.Add(viewModel.DateText);
        }
        if (viewModel.ShowWeekday && !string.IsNullOrWhiteSpace(viewModel.WeekdayText))
        {
            if (!string.Equals(title, viewModel.WeekdayText, StringComparison.Ordinal))
            {
                summaryParts.Add(viewModel.WeekdayText);
            }
        }
        if (viewModel.HasTraditionalCalendar &&
            !string.IsNullOrWhiteSpace(viewModel.TraditionalCalendarTitle) &&
            !string.Equals(title, viewModel.TraditionalCalendarTitle, StringComparison.Ordinal))
        {
            summaryParts.Add(viewModel.TraditionalCalendarTitle);
        }

        string summary = string.Join(" · ", summaryParts);
        bool hasText = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(summary);
        ImageSource? backgroundImage = viewModel.HasVisibleCurrentImage
            ? glance.GetCompactBackgroundImage()
            : null;
        return new WidgetCompactPresentation(
            title,
            summary,
            _descriptor.DefaultGlyph,
            string.Empty,
            Thumbnail: backgroundImage,
            UseStackedText: true,
            EnableMarquee: true,
            UseFullBleedBackground: backgroundImage is not null,
            LiveStateKey: string.Join(
                "|",
                viewModel.TimeText,
                viewModel.DateText,
                viewModel.WeekdayText,
                viewModel.TraditionalCalendarTitle,
                viewModel.CurrentImagePath),
            FullBleedOverlayOpacity: hasText ? viewModel.ReadabilityStrengthOpacity : 0,
            UseUniformFullBleedOverlay: true,
            FullBleedBackgroundOpacity: viewModel.BackgroundImageOpacity);
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
        bool isDay = weather.ViewModel.IsDay;
        bool isAttention = IsCompactWeatherAttentionRequired(weather);
        bool usesRichSkin = weather.ViewModel.UsesRichSkin;
        Windows.UI.Color? colorStart = usesRichSkin
            ? weather.ViewModel.RichBackdropTopColor
            : null;
        Windows.UI.Color? colorEnd = usesRichSkin
            ? weather.ViewModel.RichBackdropBottomColor
            : null;

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
            // Avoid the former 50 ms UI-thread particle timer (20 Canvas writes
            // per tick). The condition-aware color field and emoji retain the
            // weather identity without a permanent rendering tax.
            ParticleKind: CompactParticleKind.None,
            LiveStateKey: string.Join(
                "|",
                usesRichSkin,
                weather.ViewModel.CurrentTemperatureText,
                weather.ViewModel.CurrentDescription,
                weather.ViewModel.PrecipitationText),
            UseLightForeground: usesRichSkin
                ? weather.ViewModel.RichSkinUsesLightText
                : null);
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
        if (persist && !CanPersistBoundsChange(persist))
        {
            return;
        }

        if (IsCompactBoundsStateActive)
        {
            if (persist)
            {
                SettingsService.UpdateWidget(_config, notifySubscribers: false);
                SettingsService.SaveDebounced(notifySubscribers: false);
                SynchronizeWidgetGroupLayout();
            }
            return;
        }

        var bounds = CollapseHostBoundsToContent(
            new RectInt32(x, y, width, height));
        // Use center point for consistent monitor determination across drag/resize.
        var center = new PointInt32(
            bounds.X + Math.Max(1, bounds.Width) / 2,
            bounds.Y + Math.Max(1, bounds.Height) / 2);
        var workArea = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest).WorkArea;
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(_config, bounds, workArea);
        if (persist)
        {
            SettingsService.UpdateWidget(_config, notifySubscribers: false);
            SettingsService.SaveDebounced();
            SynchronizeWidgetGroupLayout();
        }
    }

    protected override Windows.UI.Color BuildNativeBackdropTintColor(bool isDark)
    {
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        return WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accentColor);
    }

    protected override Windows.UI.Color BuildSolidColorBackdropTintColor(
        bool isDark,
        double surfaceOpacity)
    {
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        return WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
            isDark,
            accentColor,
            surfaceOpacity);
    }

    // ── Virtual hooks ──────────────────────────────────────────

    protected override void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(SettingsService.Settings.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        string materialType = WindowsCompatibilityService.ResolveWidgetMaterialType(
            SettingsService.Settings.WidgetMaterialType);

        // Simplified layering: only apply surface color overlay for Solid mode.
        if (materialType is SettingsService.WidgetMaterialTypeSolid && !IsSolidColorBackdropActive)
        {
            var surfaceColor = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                isDark,
                accentColor,
                surfaceOpacity);
            ContentWidgetShell.BackgroundSurface.Background = GetOrUpdateSolidColorBrush(
                ContentWidgetShell.BackgroundSurface.Background,
                surfaceColor);
        }
        else if (WindowsCompatibilityService.UsesLegacyWindowAcrylic &&
            SettingsService.IsAcrylicMaterial(materialType))
        {
            var overlayColor = WidgetMaterialVisualCalculator.BuildLegacyAcrylicSurfaceOverlayColor(
                isDark,
                accentColor,
                materialType == SettingsService.WidgetMaterialTypeAcrylicBase,
                surfaceOpacity,
                SettingsService.Settings.WidgetMaterialIntensity);
            ContentWidgetShell.BackgroundSurface.Background = GetOrUpdateSolidColorBrush(
                ContentWidgetShell.BackgroundSurface.Background,
                overlayColor);
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
        QueueNativeFileDropTargetRegistration();
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

    internal Task ContentReadyTask => _contentLoadTask;

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
        ApplyWidgetForegroundAppearance();
        ApplyBackdropPreference();
        ContentWidgetShell.ShowHoverButtons = SettingsService.Settings.ShowHoverButtons;
        ApplyTitleBarLayout();
        _contentHost.ApplyAppearance();
    }

    public void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY)
    {
        TrayAnimation.SetOffsetOverride(offsetX, offsetY);
    }

    public void CancelTrayAnimationAndRestorePosition()
    {
        if (!Visible && IsHideAnimationRunning)
        {
            CompleteTrayHideAnimation();
            return;
        }

        long animationGeneration = TrayAnimation.NextGeneration();
        TrayAnimation.Stop();
        SetTrayAnimationOffsetOverride(null, null);
        TrayAnimation.RestoreVisualState();
        TrayAnimation.RestoreWindowPosition();
        TrayAnimation.RevealWindowForTrayShow();
        SetTrayHideInputSuppressed(false);
        IsHideAnimationRunning = false;
        _isHidePrepared = false;
        LogTrayWindow($"CancelAnimationAndRestore gen={animationGeneration}");
    }

public void PrepareTrayShowAnimation()
{
PrepareTrayShowAnimationCore(restoreBoundsForCurrentTopology: false);
}

internal bool PrepareTrayShowAnimationForCurrentTopology()
{
return PrepareTrayShowAnimationCore(restoreBoundsForCurrentTopology: true);
}

private bool PrepareTrayShowAnimationCore(bool restoreBoundsForCurrentTopology)
{
SetTrayHideInputSuppressed(false);
TrayAnimation.NextGeneration();
TrayAnimation.StopAndRestoreWindowPosition();
TrayAnimation.CloakWindowForTrayShow();
_isHidePrepared = false;
IsHideAnimationRunning = false;

        // A group detach changes this persistent HWND's topology identity.
        // Retarget it only after DWM cloak is active and before preparing the
        // animation offset; otherwise TryRestoreBounds sees an active position
        // transition and intentionally skips the move.
        bool boundsRestored = !restoreBoundsForCurrentTopology ||
            TryRestoreBoundsForCurrentTopology(allowHidden: true);

        var profile = GetTrayAnimationProfile();
        LogTrayWindow(
            $"PrepareShow gen={TrayAnimation.Generation} topologyRetarget={restoreBoundsForCurrentTopology} " +
            $"boundsRestored={boundsRestored} effect={SettingsService.Settings.WidgetAnimationEffect} " +
            $"speed={SettingsService.Settings.WidgetAnimationSpeed} enabled={profile.IsEnabled} durationMs={profile.DurationMs}");
        TrayAnimation.PrepareVisualState(
            profile.ShowOffsetX,
            profile.ShowOffsetY,
            profile.ShowStartOpacity,
            profile.ShowStartScale);
        return boundsRestored;
    }

    public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)
    {
        ShowWithoutActivation(persistVisibility);
        TrayAnimation.PrepareHiddenState();
        PushToBottom(showWindow: false);
        TrayAnimation.RevealWindowForTrayShow();
    }

    public void ShowPreparedRaisedFromTray(bool persistVisibility = true)
    {
        ShowWithoutActivation(persistVisibility);
        TrayAnimation.PrepareHiddenState();
        HoldTemporaryTopMost(showWindow: false);
        TrayAnimation.RevealWindowForTrayShow();
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
        NotifyVisibleContentRevealCompleted();
    }

    public void RevealFromTray(bool autoRestore = true)
    {
        PrepareTrayShowAnimation();
        ShowPreparedRaisedFromTray();
        ActivateRaisedFromTrayBatch();
        PlayTrayShowAnimation();

        if (!autoRestore)
        {
            _autoRestoreTimer?.Stop();
            return;
        }

        if (_autoRestoreTimer is null)
        {
            _autoRestoreTimer = DispatcherQueue.CreateTimer();
            _autoRestoreTimer.IsRepeating = false;
            _autoRestoreTimer.Tick += AutoRestoreTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }
        else
        {
            _autoRestoreTimer.Stop();
        }

        _autoRestoreTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _autoRestoreTimer.Start();
    }

    private void AutoRestoreTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (Visible &&
            !IsHideAnimationRunning &&
            !IsDragging &&
            !IsResizing)
        {
            RestoreDesktopLayer(force: true);
        }
    }

    private void ReleaseAutoRestoreTimer()
    {
        Microsoft.UI.Dispatching.DispatcherQueueTimer? timer =
            _autoRestoreTimer;
        if (timer is null)
        {
            return;
        }

        _autoRestoreTimer = null;
        timer.Stop();
        timer.Tick -= AutoRestoreTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    public bool PrepareTrayHideAnimation(bool persistVisibility = true)
    {
        if (!Visible || IsHideAnimationRunning)
        {
            LogTrayWindow($"PrepareHide skipped visible={Visible} hideRunning={IsHideAnimationRunning}");
            return false;
        }

        PrepareCompactHostForTrayHide();
        _autoRestoreTimer?.Stop();
        CancelPendingDesktopLayerRestore();
TrayAnimation.NextGeneration();
TrayAnimation.RevealWindowForTrayShow();
if (TrayAnimation.IsPositionTransitionActive)
{
    TrayAnimation.StopAndRestoreWindowPosition();
}
else
{
    TrayAnimation.Stop();
}
IsHideAnimationRunning = true;
        _isHidePrepared = true;
        SetTrayHideInputSuppressed(true);
        Visible = false;
        NotifyCompactHostVisibilityChanged(false);
        UpdatePersistedVisibility(isVisible: false, persistVisibility);

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
        App.Current?.WidgetManager?.CancelWidgetSurfaceSwitch(_config.Id);
        TrayAnimation.Stop();
        TrayAnimation.RevealWindowForTrayShow();
        IsHideAnimationRunning = false;
        _isHidePrepared = false;
        SetTrayHideInputSuppressed(false);
        Visible = false;
        _autoRestoreTimer?.Stop();
        CancelPendingDesktopLayerRestore();
        UpdatePersistedVisibility(isVisible: false, persistVisibility: true);
        WidgetLayerService.ClearTopMost(HWnd);
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_HIDE);
        AppWindow.Hide();
        WidgetShellControl.SuspendVisualActivity();
        NotifyCompactHostVisibilityChanged(false);
        TrayAnimation.RestoreVisualState();
        TrayAnimation.RestoreWindowPosition();
        _contentHost.OnDeactivated();
        NotifyVisibleContentSuspended();
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
        if (!ReferenceEquals(_contentHost.CurrentContent, content))
        {
            return;
        }

        AttachCompactPresentationSource(content);
        AttachFeedbackSource(content);
        AttachHostContextMenuSource(content);
        RefreshCompactPresentation();
        ApplyTitleActionButtonConfiguration();
    }

    private void AttachFeedbackSource(IWidgetContent content)
    {
        if (_feedbackSource is not null)
        {
            _feedbackSource.FeedbackRequested -= FeedbackSource_FeedbackRequested;
        }

        _feedbackSource = content as IWidgetFeedbackSource;
        if (_feedbackSource is not null)
        {
            _feedbackSource.FeedbackRequested += FeedbackSource_FeedbackRequested;
        }
    }

    private void FeedbackSource_FeedbackRequested(
        object? sender,
        WidgetFeedbackRequestedEventArgs e)
    {
        ContentWidgetShell.ShowFeedback(e.Request);
    }

    private void AttachHostContextMenuSource(IWidgetContent content)
    {
        if (content is FileSurfaceContent fileSurface)
        {
            fileSurface.SetHostWindowHandle(HWnd);
        }

        if (_hostContextMenuSource is not null)
        {
            _hostContextMenuSource.HostContextMenuOpening -=
                HostContextMenuSource_HostContextMenuOpening;
        }

        _hostContextMenuSource = content as IWidgetHostContextMenuSource;
        if (_hostContextMenuSource is not null)
        {
            _hostContextMenuSource.HostContextMenuOpening +=
                HostContextMenuSource_HostContextMenuOpening;
        }
    }

    private void HostContextMenuSource_HostContextMenuOpening(
        object? sender,
        WidgetHostContextMenuOpeningEventArgs e)
    {
        if (ReferenceEquals(sender, _contentHost.CurrentContent))
        {
            ProvideWidgetActionsForContentMenu(e);
        }
    }

    private void AttachCompactPresentationSource(IWidgetContent content)
    {
        if (_compactPresentationSource is not null)
        {
            _compactPresentationSource.PropertyChanged -= CompactPresentationSource_PropertyChanged;
        }

        _compactPresentationSource = content switch
        {
            FileSurfaceContent file => file.ViewModel,
            TodoWidgetContentAdapter todo => todo.ViewModel,
            GlanceWidgetContentAdapter glance => glance.ViewModel,
            MusicWidgetContentAdapter music => music.ViewModel,
            WeatherWidgetContentAdapter weather => weather.ViewModel,
            QuickCaptureSurfaceContent quickCapture => quickCapture.ViewModel,
            _ => null
        };

        if (_compactPresentationSource is not null)
        {
            _compactPresentationSource.PropertyChanged += CompactPresentationSource_PropertyChanged;
        }

        // The search capsule's dynamic subtitle ("最近：xxx") tracks the recent-query
        // list, which has no INotifyPropertyChanged surface, so subscribe to the
        // history service's change event to refresh the compact presentation live.
        if (content is not SearchWidgetContentAdapter &&
            _subscribedSearchHistoryService is { } previousHistoryService)
        {
            previousHistoryService.RecentQueriesChanged -= OnRecentQueriesChanged;
            _subscribedSearchHistoryService = null;
        }

        if (content is SearchWidgetContentAdapter &&
            App.Current.SearchHistoryService is { } historyService &&
            !ReferenceEquals(_subscribedSearchHistoryService, historyService))
        {
            if (_subscribedSearchHistoryService is { } previousService)
            {
                previousService.RecentQueriesChanged -= OnRecentQueriesChanged;
            }

            historyService.RecentQueriesChanged += OnRecentQueriesChanged;
            _subscribedSearchHistoryService = historyService;
        }
    }

    private void OnRecentQueriesChanged()
    {
        QueueCompactPresentationRefresh();
    }

    private void CompactPresentationSource_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsCompactPresentationPropertyRelevant(Config.WidgetKind, e.PropertyName))
        {
            return;
        }

        QueueCompactPresentationRefresh();
    }

    private void QueueCompactPresentationRefresh()
    {
        // The compact surface is refreshed synchronously when a collapse begins,
        // so expanded widgets do not need to keep rewriting their hidden capsule.
        if (IsClosing || !IsWidgetCollapsed || _compactPresentationRefreshQueued)
        {
            return;
        }

        _compactPresentationRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _compactPresentationRefreshQueued = false;
            if (!IsClosing && IsWidgetCollapsed)
            {
                RefreshCompactPresentation();
            }
        }))
        {
            _compactPresentationRefreshQueued = false;
        }
    }

    internal static bool IsCompactPresentationPropertyRelevant(
        WidgetKind widgetKind,
        string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return true;
        }

        return widgetKind switch
        {
            WidgetKind.Weather => propertyName is
                nameof(WeatherWidgetViewModel.CurrentCondition) or
                nameof(WeatherWidgetViewModel.IsDay) or
                nameof(WeatherWidgetViewModel.UsesRichSkin) or
                nameof(WeatherWidgetViewModel.RichSkinUsesLightText) or
                nameof(WeatherWidgetViewModel.RichBackdropTopColor) or
                nameof(WeatherWidgetViewModel.RichBackdropBottomColor) or
                nameof(WeatherWidgetViewModel.CurrentWeatherCode) or
                nameof(WeatherWidgetViewModel.CurrentTemperatureText) or
                nameof(WeatherWidgetViewModel.CurrentDescription) or
                nameof(WeatherWidgetViewModel.PrecipitationText),
            WidgetKind.Music => propertyName is
                nameof(MusicWidgetViewModel.Title) or
                nameof(MusicWidgetViewModel.Artist) or
                nameof(MusicWidgetViewModel.StatusText) or
                nameof(MusicWidgetViewModel.ThumbnailImage) or
                nameof(MusicWidgetViewModel.HasSession) or
                nameof(MusicWidgetViewModel.IsPlaying) or
                nameof(MusicWidgetViewModel.CanGoPrevious) or
                nameof(MusicWidgetViewModel.CanGoNext) or
                nameof(MusicWidgetViewModel.PlaybackState) or
                nameof(MusicWidgetViewModel.Duration) or
                nameof(MusicWidgetViewModel.SeekMaximum) or
                nameof(MusicWidgetViewModel.SeekValue),
            WidgetKind.Glance => propertyName is
                nameof(GlanceWidgetViewModel.TimeText) or
                nameof(GlanceWidgetViewModel.DateText) or
                nameof(GlanceWidgetViewModel.WeekdayText) or
                nameof(GlanceWidgetViewModel.TraditionalCalendarTitle) or
                nameof(GlanceWidgetViewModel.HasTraditionalCalendar) or
                nameof(GlanceWidgetViewModel.CurrentImagePath) or
                nameof(GlanceWidgetViewModel.BackgroundImageOpacity) or
                nameof(GlanceWidgetViewModel.HasVisibleCurrentImage) or
                nameof(GlanceWidgetViewModel.ReadabilityStrengthOpacity) or
                nameof(GlanceWidgetViewModel.ReadabilityOpacity) or
                nameof(GlanceWidgetViewModel.ShowTime) or
                nameof(GlanceWidgetViewModel.ShowDate) or
                nameof(GlanceWidgetViewModel.ShowWeekday),
            _ => true
        };
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
        InstallGroupFileDropFallbackHandlers();

        foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
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
            ReleaseAutoRestoreTimer();
            try { RemoveGroupFileDropFallbackHandlers(); } catch (Exception ex) { App.Log($"[ContentWidget] Remove group drop fallback failed during close: {ex.Message}"); }
            try { RemoveNativeFileDropBridge(); } catch (Exception ex) { App.Log($"[ContentWidget] Remove native file drop bridge failed during close: {ex.Message}"); }
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
            if (_subscribedSearchHistoryService is { } historyService)
            {
                historyService.RecentQueriesChanged -= OnRecentQueriesChanged;
                _subscribedSearchHistoryService = null;
            }
            try { TrayAnimation.RevealWindowForTrayShow(); } catch { }
            try { CleanupBase(); } catch (Exception ex) { App.Log($"[ContentWidget] CleanupBase failed during close: {ex.Message}"); }
            try { _contentHost.DisposeContent(); } catch (Exception ex) { App.Log($"[ContentWidget] DisposeContent failed during close: {ex.Message}"); }
            try { DisposeCachedGroupContents(); } catch (Exception ex) { App.Log($"[ContentWidget] Dispose cached group content failed during close: {ex.Message}"); }

            foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
            {
                if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                {
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
        bool appearanceOnly =
            SettingsService.LastNotifiedChangeKind == SettingsChangeKind.Appearance;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ApplySettingsChanged(appearanceOnly));
            return;
        }

        ApplySettingsChanged(appearanceOnly);
    }

    private void ApplySettingsChanged(bool appearanceOnly)
    {
        if (appearanceOnly)
        {
            // The appearance preview channel already applied corner,
            // foreground, backdrop, title layout, and content appearance to
            // this window; the settings refreshed here are unchanged.
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

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    // ── Nested: title view model ───────────────────────────────

#if DESKBOX_NATIVE_AOT
    [WinRT.GeneratedBindableCustomProperty([
        nameof(DisplayName),
        nameof(TitleIconSize),
        nameof(TitleTextSize)
    ], [])]
#endif
    private sealed partial class ContentWidgetTitleViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly SettingsService _settingsService;

        public ContentWidgetTitleViewModel(WidgetConfig config, SettingsService settingsService)
        {
            Config = config;
            _settingsService = settingsService;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public WidgetConfig Config { get; private set; }

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
                        WidgetKind.Glance => "Glance.Title",
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

        public void SetConfig(WidgetConfig config)
        {
            Config = config;
            RefreshDisplayName();
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
