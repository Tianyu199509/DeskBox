using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
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
        return CurrentContent switch
        {
            TodoWidgetContentAdapter todo => new WidgetCompactPresentation(
                _titleViewModel.DisplayName,
                localization.Format(
                    "Widget.Compact.TodoSummary",
                    todo.ViewModel.TodayFilterCount,
                    todo.ViewModel.Items.Count(item => item.IsOverdue)),
                _descriptor.DefaultGlyph,
                localization.T("Widget.Compact.TodoDropHint")),
            MusicWidgetContentAdapter music => new WidgetCompactPresentation(
                music.ViewModel.Title,
                music.ViewModel.Artist,
                _descriptor.DefaultGlyph,
                string.Empty,
                music.ViewModel.ThumbnailImage,
                ShowMediaControls: true,
                IsPlaying: music.ViewModel.IsPlaying,
                CanGoPrevious: music.ViewModel.CanGoPrevious,
                CanGoNext: music.ViewModel.CanGoNext),
            WeatherWidgetContentAdapter weather => new WidgetCompactPresentation(
                _titleViewModel.DisplayName,
                $"{weather.ViewModel.CurrentTemperatureText} · {weather.ViewModel.CurrentDescription}".Trim(' ', '·'),
                _descriptor.DefaultGlyph,
                string.Empty),
            _ => new WidgetCompactPresentation(
                _titleViewModel.DisplayName,
                string.Empty,
                _descriptor.DefaultGlyph,
                localization.T("Widget.Compact.DropHint"))
        };
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
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
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
        _isHidePrepared = false;
        IsHideAnimationRunning = false;

        var profile = GetTrayAnimationProfile();
        LogTrayWindow(
            $"PrepareShow gen={TrayAnimation.Generation} effect={SettingsService.Settings.WidgetAnimationEffect} " +
            $"speed={SettingsService.Settings.WidgetAnimationSpeed} enabled={profile.IsEnabled} durationMs={profile.DurationMs}");
        TrayAnimation.PrepareVisualState(profile.ShowOffsetX, profile.ShowOffsetY, profile.ShowStartOpacity, profile.ShowStartScale);
    }

    public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)
    {
        ShowWithoutActivation(persistVisibility);
        PushToBottom();
    }

    public void ShowPreparedRaisedFromTray(bool persistVisibility = true)
    {
        ShowWithoutActivation(persistVisibility);
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
    }

    public bool PrepareTrayHideAnimation(bool persistVisibility = true)
    {
        if (!Visible || IsHideAnimationRunning)
        {
            LogTrayWindow($"PrepareHide skipped visible={Visible} hideRunning={IsHideAnimationRunning}");
            return false;
        }

        TrayAnimation.NextGeneration();
        TrayAnimation.StopAndRestoreWindowPosition();
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
