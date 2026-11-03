using CommunityToolkit.WinUI.Controls;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WidgetSurfaceRow = DeskBox.Services.WidgetSurfacePreviewItem;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// The "displays & widgets" settings page, modeled on the Windows display
/// settings page: a frameless arrangement preview of monitor cards (big
/// centered number, proportional mini-widget blocks, the selected card fully
/// accent-filled like Windows with luminance-derived contrast foreground),
/// the selected display's settings card below it, scaling/disconnect policy,
/// and filterable inline "show on" combos per widget surface. Pure code-behind
/// on purpose — runtime state, not persisted settings (DesktopOrganization
/// section precedent).
/// </summary>
public sealed partial class DisplaySettingsSection : UserControl
{
    private const double PreviewPaddingPx = 16;
    private const string FilterTagAll = "all";
    private const string FilterTagSelected = "selected";
    private const string FilterTagBorrowed = "borrowed";

    private IReadOnlyList<WidgetScreenInfo> _screens = [];
    private WidgetScreenInfo? _selectedScreen;
    private string _widgetFilterTag = FilterTagAll;
    private bool _isRefreshing;
    private ISettingsWidgetActions _widgetActions = null!;
    private ISettingsAppActions _appActions = null!;

    public DisplaySettingsSection()
    {
        InitializeComponent();
        ApplyPreviewHintForeground();
        Loaded += OnSectionLoaded;
        Unloaded += OnSectionUnloaded;
        ActualThemeChanged += (_, _) =>
        {
            ApplyPreviewHintForeground();
            Refresh();
        };
    }

    /// <summary>
    /// Injected by the settings window when the deferred section is created,
    /// before the first Loaded (host commands and the host-raised events).
    /// </summary>
    public void Configure(ISettingsWidgetActions widgetActions, ISettingsAppActions appActions)
    {
        _widgetActions = widgetActions;
        _appActions = appActions;
    }

    // H5: the app raises this when the display topology changes outside the
    // settings page (hot-plug, resolution change), so the preview and combos
    // do not keep showing a stale arrangement.
    private void OnSectionLoaded(object sender, RoutedEventArgs e)
    {
        _appActions.DisplayTopologyChanged += OnExternalDisplayTopologyChanged;
        _appActions.ScreenHomeChanged += OnExternalScreenHomeChanged;
        Refresh();
    }

    private void OnSectionUnloaded(object sender, RoutedEventArgs e)
    {
        _appActions.DisplayTopologyChanged -= OnExternalDisplayTopologyChanged;
        _appActions.ScreenHomeChanged -= OnExternalScreenHomeChanged;
        _refreshDebounce?.Stop();
    }

    private void OnExternalDisplayTopologyChanged()
    {
        // Topology changes arrive in bursts while Windows re-enumerates
        // monitors (a primary switch fires several signature flips); the
        // debounce also skips the transient mid-states whose rasterization
        // scale mismatch crashed the XAML layout pass.
        ScheduleRefresh();
    }

    private void OnExternalScreenHomeChanged() => ScheduleRefresh();

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshDebounce;

    /// <summary>
    /// Coalesced UI refresh: screen-home changes arrive in bursts (move-all
    /// fires one per surface) and topology changes flip through transient
    /// states, so every external trigger funnels into one short debounce
    /// instead of rebuilding the page back-to-back mid-layout.
    /// </summary>
    private void ScheduleRefresh()
    {
        _refreshDebounce ??= DispatcherQueue.CreateTimer();
        _refreshDebounce.Interval = TimeSpan.FromMilliseconds(300);
        _refreshDebounce.IsRepeating = false;
        _refreshDebounce.Tick -= DebouncedRefresh;
        _refreshDebounce.Tick += DebouncedRefresh;
        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }

    private void DebouncedRefresh(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        if (!_isRefreshing)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Preview hint keeps the secondary text color instead of raw opacity,
    /// resolved in code-behind (SettingsWindow.Feedback precedent) because a
    /// ThemeResource brush here can break DataTemplate.LoadContent. The
    /// lookup must follow the section's own effective theme: an
    /// application-scope lookup resolves against the startup system theme,
    /// which pinned the hint to the dark dictionary's white in a Light
    /// settings window.
    /// </summary>
    private void ApplyPreviewHintForeground()
    {
        if (Helpers.NeutralInteractionBrush.ResolveThemedResource(
                "TextFillColorSecondaryBrush",
                PreviewHintText) is Brush hintBrush)
        {
            PreviewHintText.Foreground = hintBrush;
        }

        if (Helpers.NeutralInteractionBrush.ResolveThemedResource(
                "CardBackgroundFillColorDefaultBrush",
                PreviewArea) is Brush stageBg)
        {
            PreviewArea.Background = stageBg;
        }

        if (Helpers.NeutralInteractionBrush.ResolveThemedResource(
                "CardStrokeColorDefaultBrush",
                PreviewArea) is Brush stageStroke)
        {
            PreviewArea.BorderBrush = stageStroke;
        }
    }

    public void Refresh()
    {
        if (global::DeskBox.App.Current?.SettingsService is null)
        {
            return;
        }

        // try/finally + single reset point: the flag guards the combo
        // SelectionChanged handlers, and a rebuild can raise those events
        // after the sub-builders finish — resetting inside them re-armed the
        // page for re-entrant rebuilds during layout (the primary-switch
        // crash family).
        _isRefreshing = true;
        try
        {
            _screens = WidgetScreenCatalog.Capture();
            if (_selectedScreen is { } currentScreen)
            {
                _selectedScreen = WidgetScreenCatalog.TryFindScreen(_screens, currentScreen.StableId);
            }

            // Windows opens the display page with the primary monitor selected.
            if (_selectedScreen is null)
            {
                _selectedScreen = _screens.FirstOrDefault(screen => screen.IsPrimary) ?? _screens.FirstOrDefault();
            }

            // Windows shows the arrangement even for a single monitor — the page
            // keeps a compact preview so "identify" and the default badge stay
            // meaningful; multi-monitor layouts get the full-size arrangement.
            bool hasScreens = _screens.Count > 0;
            PreviewArea.Visibility = hasScreens ? Visibility.Visible : Visibility.Collapsed;
            PreviewViewbox.MaxHeight = _screens.Count > 1 ? 280 : 210;

            List<WidgetSurfaceRow> surfaces = EnumerateSurfaces();
            RebuildPreview(surfaces);
            UpdateScreenDetailCard(surfaces);
            RefreshPlacementCombos();
            RefreshWidgetFilterCombo(surfaces);
            RefreshWidgetCards(surfaces);
        }
        catch (Exception ex)
        {
            // A topology change can land mid-rebuild (rasterization scale
            // moving, monitors re-enumerating); losing one refresh beats a
            // stowed-exception crash. The next event re-runs a full refresh.
            global::DeskBox.App.Log($"[Displays] Refresh failed: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    // ── Arrangement preview ────────────────────────────────────

    private void RebuildPreview(IReadOnlyList<WidgetSurfaceRow> surfaces)
    {
        Canvas canvas = DisplayPreviewCanvas;
        canvas.Children.Clear();
        if (_screens.Count == 0)
        {
            canvas.Width = 0;
            canvas.Height = 0;
            return;
        }

        // Design-size canvas in physical pixels; the wrapping Viewbox scales
        // the whole arrangement uniformly. Typography keys off scaleRef so
        // numbers and badges stay at native Windows 11 proportions across
        // single, dual, and triple-monitor arrangements.
        int unionLeft = _screens.Min(screen => screen.Monitor.X);
        int unionTop = _screens.Min(screen => screen.Monitor.Y);
        int unionRight = _screens.Max(screen => screen.Monitor.X + screen.Monitor.Width);
        int unionBottom = _screens.Max(screen => screen.Monitor.Y + screen.Monitor.Height);
        int unionWidth = Math.Max(1, unionRight - unionLeft);
        int unionHeight = Math.Max(1, unionBottom - unionTop);
        double scaleRef = Math.Clamp(
            Math.Max(unionHeight, unionWidth * 0.28),
            unionHeight,
            unionHeight * 1.45);
        canvas.Width = unionWidth + PreviewPaddingPx * 2;
        canvas.Height = unionHeight + PreviewPaddingPx * 2;

        // When the user hides all widgets globally via tray / shortcut (F7),
        // every surface reports IsVisible=false in persisted state; keep
        // normal region visibility in that global-hide state.
        bool allHidden = surfaces.Count > 0 && surfaces.All(surface => !surface.IsVisible);

        foreach (WidgetScreenInfo screen in _screens)
        {
            canvas.Children.Add(CreateScreenCard(
                screen,
                unionLeft,
                unionTop,
                unionHeight,
                scaleRef,
                surfaces,
                allHidden));
        }
    }

    private Button CreateScreenCard(
        WidgetScreenInfo screen,
        int unionLeft,
        int unionTop,
        int unionHeight,
        double scaleRef,
        IReadOnlyList<WidgetSurfaceRow> surfaces,
        bool allHidden)
    {
        bool isSelected = _selectedScreen is not null &&
            string.Equals(_selectedScreen.StableId, screen.StableId, StringComparison.OrdinalIgnoreCase);

        WidgetLayoutSettingsSlice layoutSlice = global::DeskBox.App.Current.SettingsService.Settings.WidgetLayout;
        string placementTarget = SettingsService.NormalizeWidgetNewPlacementTarget(
            layoutSlice.WidgetNewPlacementTarget);
        bool isDefault = placementTarget switch
        {
            SettingsService.WidgetNewPlacementMainDisplay => screen.IsPrimary,
            SettingsService.WidgetNewPlacementSpecificDisplay =>
                layoutSlice.WidgetDefaultBoundScreenId is { } defaultId &&
                string.Equals(defaultId.Trim(), screen.StableId.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        bool isDarkTheme = Helpers.NeutralInteractionBrush.IsDarkTheme(this);
        double bezelInset = _screens.Count > 1
            ? Math.Round(Math.Clamp(unionHeight * 0.012, 8, 28))
            : 0;
        double cardW = Math.Max(48, screen.Monitor.Width - (bezelInset * 2));
        double cardH = Math.Max(48, screen.Monitor.Height - (bezelInset * 2));
        double monitorRadius = Math.Clamp(unionHeight * 0.028, 16, 64);
        double borderPx = Math.Clamp(unionHeight * 0.005, 4, 12);

        Windows.UI.Color accentFill = ResolveAccentFillColor(isDarkTheme);
        Windows.UI.Color foregroundColor = isSelected
            ? ResolveOnAccentForegroundColor(accentFill)
            : (ResolveThemeColor("TextFillColorPrimaryBrush")
                ?? (isDarkTheme ? Colors.White : Windows.UI.Color.FromArgb(0xE4, 0x00, 0x00, 0x00)));

        List<WidgetSurfaceRow> onScreen = surfaces
            .Where(surface =>
                surface.ActualScreen is { } actual &&
                string.Equals(actual.StableId, screen.StableId, StringComparison.OrdinalIgnoreCase) &&
                surface.PreviewBounds is { Width: > 0, Height: > 0 })
            .ToList();

        var root = new Grid
        {
            Width = cardW,
            Height = cardH,
            CornerRadius = new CornerRadius(monitorRadius)
        };

        // 1. Pure borderless 1:1 widget & capsule regions (no borders, no text,
        //    no inner chrome — calm geometric silhouette behind the number).
        root.Children.Add(CreateMiniWidgetOverlay(
            screen,
            cardW,
            cardH,
            unionHeight,
            isSelected,
            isDarkTheme,
            accentFill,
            foregroundColor,
            onScreen,
            allHidden));

        // 2. Native Windows 11 centered monitor number ("1", "2").
        root.Children.Add(new TextBlock
        {
            Text = screen.Number.ToString(),
            FontSize = Math.Clamp(scaleRef * 0.25, 60, 340),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush(foregroundColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        });

        // 3. Clean Windows 11 bottom attribute pills (主显示器 / 新格子).
        if (screen.IsPrimary || isDefault)
        {
            double pillFontSize = Math.Clamp(scaleRef * 0.068, 30, 148);
            var badges = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = pillFontSize * 0.36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, cardH * 0.058),
                IsHitTestVisible = false
            };

            if (screen.IsPrimary)
            {
                badges.Children.Add(CreateMonitorBadge(
                    T("Settings.Displays.PrimaryBadge"),
                    pillFontSize,
                    filled: true,
                    isSelected,
                    isDarkTheme,
                    foregroundColor));
            }

            if (isDefault)
            {
                badges.Children.Add(CreateMonitorBadge(
                    T("Settings.Displays.DefaultBadge"),
                    pillFontSize,
                    filled: false,
                    isSelected,
                    isDarkTheme,
                    foregroundColor));
            }

            root.Children.Add(badges);
        }

        var card = new Button
        {
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(isSelected ? 0 : borderPx),
            Width = cardW,
            Height = cardH,
            CornerRadius = new CornerRadius(monitorRadius),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = root,
            Tag = screen
        };
        ApplyMonitorCardStyle(card, isSelected, isDarkTheme, accentFill, foregroundColor);
        card.Click += ScreenCard_Click;

        Canvas.SetLeft(card, PreviewPaddingPx + screen.Monitor.X - unionLeft + bezelInset);
        Canvas.SetTop(card, PreviewPaddingPx + screen.Monitor.Y - unionTop + bezelInset);
        return card;
    }

    private static Canvas CreateMiniWidgetOverlay(
        WidgetScreenInfo screen,
        double cardW,
        double cardH,
        int unionHeight,
        bool isSelected,
        bool isDarkTheme,
        Windows.UI.Color accentFill,
        Windows.UI.Color foregroundColor,
        IReadOnlyList<WidgetSurfaceRow> onScreenSurfaces,
        bool allHidden)
    {
        var miniCanvas = new Canvas
        {
            Width = cardW,
            Height = cardH,
            IsHitTestVisible = false
        };

        if (screen.Monitor.Width <= 0 || screen.Monitor.Height <= 0 || onScreenSurfaces.Count == 0)
        {
            return miniCanvas;
        }

        double scaleX = cardW / screen.Monitor.Width;
        double scaleY = cardH / screen.Monitor.Height;
        double edgePad = Math.Clamp(unionHeight * 0.006, 4, 14);
        double cardCorner = Math.Clamp(unionHeight * 0.015, 6, 22);

        // Selected monitor fills with the Windows accent color: lift regions
        // gently with white frost on mid/dark accents (keeping dark text high-
        // contrast in Dark mode), and step to a soft dark tonal shade only on
        // very high-luminance accents so blocks never clip to white.
        Windows.UI.Color normalToneBase;
        byte visibleAlpha;
        byte hiddenAlpha;
        if (isSelected)
        {
            bool useLightFrost = RelativeLuminance(accentFill) < 0.72;
            normalToneBase = useLightFrost ? Colors.White : Colors.Black;
            visibleAlpha = useLightFrost
                ? (RelativeLuminance(foregroundColor) < 0.25 ? (byte)0x38 : (byte)0x26)
                : (byte)0x1E;
            hiddenAlpha = (byte)0x12;
        }
        else
        {
            normalToneBase = isDarkTheme ? Colors.White : Colors.Black;
            visibleAlpha = isDarkTheme ? (byte)0x18 : (byte)0x11;
            hiddenAlpha = isDarkTheme ? (byte)0x0B : (byte)0x08;
        }

        foreach (WidgetSurfaceRow surface in onScreenSurfaces.OrderBy(item => item.IsCompact ? 1 : 0))
        {
            if (surface.PreviewBounds is not { Width: > 0, Height: > 0 } rect)
            {
                continue;
            }

            double w = Math.Min(rect.Width * scaleX, Math.Max(16, cardW - (edgePad * 2)));
            double h = Math.Min(rect.Height * scaleY, Math.Max(12, cardH - (edgePad * 2)));
            double left = Math.Clamp(
                (rect.X - screen.Monitor.X) * scaleX,
                edgePad,
                Math.Max(edgePad, cardW - w - edgePad));
            double top = Math.Clamp(
                (rect.Y - screen.Monitor.Y) * scaleY,
                edgePad,
                Math.Max(edgePad, cardH - h - edgePad));

            bool individuallyHidden = !surface.IsVisible && !allHidden;
            double radius = surface.IsCompact
                ? Math.Min(h * 0.5, cardCorner * 1.4)
                : cardCorner;

            Windows.UI.Color regionColor = surface.IsBorrowedFallback
                ? Windows.UI.Color.FromArgb(
                    individuallyHidden ? (byte)0x1A : (byte)0x36,
                    0xF5,
                    0x9E,
                    0x0B)
                : Windows.UI.Color.FromArgb(
                    individuallyHidden ? hiddenAlpha : visibleAlpha,
                    normalToneBase.R,
                    normalToneBase.G,
                    normalToneBase.B);

            var block = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(radius),
                Background = Brush(regionColor)
            };
            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, top);
            miniCanvas.Children.Add(block);
        }

        return miniCanvas;
    }

    private static Border CreateMonitorBadge(
        string text,
        double pillFontSize,
        bool filled,
        bool isSelected,
        bool isDarkTheme,
        Windows.UI.Color foregroundColor)
    {
        double cornerRadius = Math.Clamp(pillFontSize * 0.30, 8, 26);
        double strokeThickness = Math.Clamp(pillFontSize * 0.085, 2.2, 5.5);

        Windows.UI.Color bgColor;
        Windows.UI.Color borderColor;
        if (isSelected)
        {
            bgColor = Windows.UI.Color.FromArgb(
                filled ? (byte)0x24 : (byte)0x0E,
                foregroundColor.R,
                foregroundColor.G,
                foregroundColor.B);
            borderColor = Windows.UI.Color.FromArgb(
                0x68,
                foregroundColor.R,
                foregroundColor.G,
                foregroundColor.B);
        }
        else
        {
            Windows.UI.Color neutral = isDarkTheme ? Colors.White : Colors.Black;
            bgColor = Windows.UI.Color.FromArgb(
                filled ? (isDarkTheme ? (byte)0x1E : (byte)0x14) : (byte)0x06,
                neutral.R,
                neutral.G,
                neutral.B);
            borderColor = Windows.UI.Color.FromArgb(
                isDarkTheme ? (byte)0x42 : (byte)0x34,
                neutral.R,
                neutral.G,
                neutral.B);
        }

        return new Border
        {
            Background = Brush(bgColor),
            BorderBrush = filled ? null : Brush(borderColor),
            BorderThickness = new Thickness(filled ? 0 : strokeThickness),
            CornerRadius = new CornerRadius(cornerRadius),
            Padding = new Thickness(
                pillFontSize * 0.56,
                pillFontSize * 0.16,
                pillFontSize * 0.56,
                pillFontSize * 0.22),
            Child = new TextBlock
            {
                Text = text,
                FontSize = pillFontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush(foregroundColor)
            }
        };
    }

    private void ApplyMonitorCardStyle(
        Button card,
        bool isSelected,
        bool isDarkTheme,
        Windows.UI.Color accentFill,
        Windows.UI.Color foregroundColor)
    {
        Brush fgBrush = Brush(foregroundColor);
        if (isSelected)
        {
            Brush normalBrush = Helpers.NeutralInteractionBrush.ResolveThemedResource(
                    "AccentFillColorDefaultBrush",
                    this)
                ?? Brush(accentFill);
            Brush hoverBrush = Helpers.NeutralInteractionBrush.ResolveThemedResource(
                    "AccentFillColorSecondaryBrush",
                    this)
                ?? Brush(Blend(accentFill, isDarkTheme ? Colors.White : Colors.Black, 0.08));
            Brush pressedBrush = Helpers.NeutralInteractionBrush.ResolveThemedResource(
                    "AccentFillColorTertiaryBrush",
                    this)
                ?? Brush(Blend(accentFill, Colors.Black, 0.14));
            Brush transparent = Brush(Colors.Transparent);

            card.Background = normalBrush;
            card.Foreground = fgBrush;
            card.BorderBrush = transparent;
            card.Resources["ButtonBackground"] = normalBrush;
            card.Resources["ButtonBackgroundPointerOver"] = hoverBrush;
            card.Resources["ButtonBackgroundPressed"] = pressedBrush;
            card.Resources["ButtonBorderBrush"] = transparent;
            card.Resources["ButtonBorderBrushPointerOver"] = transparent;
            card.Resources["ButtonBorderBrushPressed"] = transparent;
            card.Resources["ButtonForeground"] = fgBrush;
            card.Resources["ButtonForegroundPointerOver"] = fgBrush;
            card.Resources["ButtonForegroundPressed"] = fgBrush;
            return;
        }

        Brush restBg = Helpers.NeutralInteractionBrush.ResolveThemedResource(
                "ControlFillColorDefaultBrush",
                this)
            ?? Brush(isDarkTheme
                ? Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
                : Colors.White);
        Brush hoverBg = Helpers.NeutralInteractionBrush.ResolveThemedResource(
                "ControlFillColorSecondaryBrush",
                this)
            ?? Brush(isDarkTheme
                ? Windows.UI.Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0xFF, 0xF6, 0xF6, 0xF6));
        Brush pressBg = Helpers.NeutralInteractionBrush.ResolveThemedResource(
                "ControlFillColorTertiaryBrush",
                this)
            ?? Brush(isDarkTheme
                ? Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE));
        Brush borderBrush = Helpers.NeutralInteractionBrush.ResolveThemedResource(
                "ControlStrokeColorDefaultBrush",
                this)
            ?? Brush(isDarkTheme
                ? Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x22, 0x00, 0x00, 0x00));

        card.Background = restBg;
        card.Foreground = fgBrush;
        card.BorderBrush = borderBrush;
        card.Resources["ButtonBackground"] = restBg;
        card.Resources["ButtonBackgroundPointerOver"] = hoverBg;
        card.Resources["ButtonBackgroundPressed"] = pressBg;
        card.Resources["ButtonBorderBrush"] = borderBrush;
        card.Resources["ButtonBorderBrushPointerOver"] = borderBrush;
        card.Resources["ButtonBorderBrushPressed"] = borderBrush;
        card.Resources["ButtonForeground"] = fgBrush;
        card.Resources["ButtonForegroundPointerOver"] = fgBrush;
        card.Resources["ButtonForegroundPressed"] = fgBrush;
    }

    private Windows.UI.Color? ResolveThemeColor(string key) =>
        Helpers.NeutralInteractionBrush.ResolveThemedResource(key, this) is SolidColorBrush brush
            ? brush.Color
            : null;

    private Windows.UI.Color ResolveAccentFillColor(bool isDarkTheme)
    {
        if (ResolveThemeColor("AccentFillColorDefaultBrush") is { A: > 0 } themed)
        {
            return themed;
        }

        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            return isDarkTheme
                ? ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight2)
                : ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            return Windows.UI.Color.FromArgb(0xFF, 0x00, 0x67, 0xC0);
        }
    }

    private Windows.UI.Color ResolveOnAccentForegroundColor(Windows.UI.Color accentFill)
    {
        if (ResolveThemeColor("TextOnAccentFillColorPrimaryBrush") is { A: > 0 } themed)
        {
            return themed;
        }

        // WCAG crossover (~0.18 relative luminance) matches WinUI's dark text
        // on light-pastel Dark-theme accents and white text on rich Light-theme
        // accents.
        return RelativeLuminance(accentFill) > 0.24
            ? Windows.UI.Color.FromArgb(0xE4, 0x00, 0x00, 0x00)
            : Colors.White;
    }

    private static Brush Brush(Windows.UI.Color color) => new SolidColorBrush(color);

    private static Windows.UI.Color Blend(
        Windows.UI.Color source,
        Windows.UI.Color overlay,
        double amount) => Windows.UI.Color.FromArgb(
            0xFF,
            (byte)Math.Round(source.R + (overlay.R - source.R) * amount),
            (byte)Math.Round(source.G + (overlay.G - source.G) * amount),
            (byte)Math.Round(source.B + (overlay.B - source.B) * amount));

    private static double RelativeLuminance(Windows.UI.Color color)
    {
        static double Channel(double value) =>
            value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        return (0.2126 * Channel(color.R / 255.0))
            + (0.7152 * Channel(color.G / 255.0))
            + (0.0722 * Channel(color.B / 255.0));
    }

    // ── Selected screen card ───────────────────────────────────

    private void ScreenCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WidgetScreenInfo screen })
        {
            return;
        }

        _selectedScreen = screen;
        _isRefreshing = true;
        try
        {
            List<WidgetSurfaceRow> surfaces = EnumerateSurfaces();
            RebuildPreview(surfaces);
            UpdateScreenDetailCard(surfaces);
            RefreshWidgetFilterCombo(surfaces);
            if (string.Equals(_widgetFilterTag, FilterTagSelected, StringComparison.Ordinal))
            {
                RefreshWidgetCards(surfaces);
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void UpdateScreenDetailCard(IReadOnlyList<WidgetSurfaceRow> surfaces)
    {
        if (_selectedScreen is not { } screen)
        {
            ScreenDetailCard.Visibility = Visibility.Collapsed;
            return;
        }

        ScreenDetailCard.Visibility = Visibility.Visible;
        MoveAllWidgetsButton.Visibility = _screens.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RecalibrateLayoutButton.IsEnabled = _widgetActions.IsHostAvailable;

        string title = Format(
            "Widget.ScreenBinding.MonitorFormat",
            screen.Number,
            screen.DisplayName);
        ScreenDetailCard.Header = screen.IsPrimary
            ? title + T("Widget.ScreenBinding.PrimarySuffix")
            : title;

        int hereCount = 0;
        int borrowedCount = 0;
        foreach (WidgetSurfaceRow surface in surfaces)
        {
            if (surface.ActualScreen is { } actual &&
                string.Equals(
                    actual.StableId.Trim(),
                    screen.StableId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                hereCount++;
                if (surface.IsBorrowedFallback)
                {
                    borrowedCount++;
                }
            }
        }

        ScreenDetailCard.Description = borrowedCount > 0
            ? Format(
                "Settings.Displays.ScreenSummaryWithBorrowed",
                screen.DisplayName,
                Math.Round(screen.EffectiveDpiScale * 100),
                hereCount,
                borrowedCount)
            : Format(
                "Settings.Displays.ScreenSummary",
                screen.DisplayName,
                Math.Round(screen.EffectiveDpiScale * 100),
                hereCount);
    }

    private async void RecalibrateLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_widgetActions.IsHostAvailable)
        {
            return;
        }

        RecalibrateLayoutButton.IsEnabled = false;
        try
        {
            int recalibrated = await _widgetActions.RecalibrateDisplayLayoutAsync();
            ShowStatus(
                InfoBarSeverity.Success,
                Format("Settings.Displays.Recalibrate.Done", recalibrated),
                string.Empty);
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[Displays] Recalibrate layout failed: {ex}");
            ShowStatus(
                InfoBarSeverity.Error,
                T("Settings.Displays.Recalibrate.Failed"),
                string.Empty);
        }
        finally
        {
            RecalibrateLayoutButton.IsEnabled = _widgetActions.IsHostAvailable;
            Refresh();
        }
    }

    private WidgetMoveAllUndoToken? _lastMoveAllToken;

    private async void MoveAllWidgetsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScreen is not { } screen ||
            !_widgetActions.IsHostAvailable)
        {
            return;
        }

        // Confirm dialog (spec 6.3/6.4): counts how many widgets sit on other
        // displays right now.
        List<WidgetSurfaceRow> surfaces = EnumerateSurfaces();
        int elsewhere = surfaces.Count(surface =>
            surface.ActualScreen is { } actual
                ? !string.Equals(
                    actual.StableId.Trim(),
                    screen.StableId.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                : surface.Mode == WidgetScreenBindingMode.Pinned &&
                  !string.Equals(
                      surface.BoundScreenId?.Trim(),
                      screen.StableId.Trim(),
                      StringComparison.OrdinalIgnoreCase));
        var dialog = new ContentDialog
        {
            Title = Format("Settings.Displays.MoveAll.ConfirmTitle", screen.Number),
            Content = elsewhere == 0
                ? Format("Settings.Displays.MoveAll.ConfirmBodyAllHere", surfaces.Count)
                : Format("Settings.Displays.MoveAll.ConfirmBody", surfaces.Count, elsewhere),
            PrimaryButtonText = T("Settings.Displays.MoveAll.ConfirmButton"),
            CloseButtonText = T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        MoveAllWidgetsButton.IsEnabled = false;
        try
        {
            (int moved, WidgetMoveAllUndoToken token) =
                await _widgetActions.MoveAllWidgetSurfacesToDisplayAsync(screen.StableId);
            _lastMoveAllToken = token;
            ShowMoveAllDone(moved, screen.Number, token);
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[Displays] Move-all failed: {ex}");
            ShowStatus(
                InfoBarSeverity.Error,
                T("Settings.Displays.MoveAll.Failed"),
                string.Empty);
        }
        finally
        {
            MoveAllWidgetsButton.IsEnabled = true;
            Refresh();
        }
    }

    private void ShowMoveAllDone(int moved, int displayNumber, WidgetMoveAllUndoToken token)
    {
        var undoButton = new Button { Content = T("Settings.Displays.MoveAll.Undo") };
        undoButton.Click += async (_, _) =>
        {
            undoButton.IsEnabled = false;
            try
            {
                if (_widgetActions.IsHostAvailable)
                {
                    // New signature: false means the snapshot went stale
                    // (widgets moved again after the move-all) and nothing
                    // was restored.
                    bool undone = await _widgetActions.UndoMoveAllWidgetSurfacesAsync(token);
                    _lastMoveAllToken = null;
                    ShowStatus(
                        undone ? InfoBarSeverity.Informational : InfoBarSeverity.Warning,
                        undone
                            ? T("Settings.Displays.MoveAll.Undone")
                            : T("Settings.Displays.MoveAll.Stale"),
                        string.Empty);
                }
            }
            catch (Exception ex)
            {
                global::DeskBox.App.Log($"[Displays] Move-all undo failed: {ex}");
            }
            finally
            {
                Refresh();
            }
        };
        DisplaysStatusInfo.ActionButton = undoButton;
        // token.Persisted=false: the batch's single physical save failed.
        // Every binding already applied in memory and the undo token is
        // valid, so the undo button stays, but the InfoBar downgrades to an
        // error with the dedicated failure wording instead of claiming done.
        ShowStatus(
            token.Persisted ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            token.Persisted
                ? Format("Settings.Displays.MoveAll.Done", moved, displayNumber)
                : T("Settings.Displays.MoveAll.Failed"),
            string.Empty);
    }

    private void IdentifyButton_Click(object sender, RoutedEventArgs e)
    {
        DisplayIdentifyOverlayWindow.IdentifyAll(_screens);
    }

    // ── 新格子出现在 / 屏幕缩放 / 显示器断开时 (spec 6.3) ────────

    private void RefreshPlacementCombos()
    {
        var settings = global::DeskBox.App.Current.SettingsService.Settings;
        _isRefreshing = true;

        // 新格子出现在: cursor / main / per-display (+ offline "specific"
        // display keeps selection with a disabled marker row).
        NewPlacementCombo.Items.Clear();
        NewPlacementCombo.Items.Add(new ComboBoxItem
        {
            Content = T("Settings.Displays.NewWidgets.Cursor"),
            Tag = SettingsService.WidgetNewPlacementCursorDisplay
        });
        NewPlacementCombo.Items.Add(new ComboBoxItem
        {
            Content = T("Settings.Displays.NewWidgets.Main"),
            Tag = SettingsService.WidgetNewPlacementMainDisplay
        });
        string currentTarget = SettingsService.NormalizeWidgetNewPlacementTarget(
            settings.WidgetLayout.WidgetNewPlacementTarget);
        int selectedIndex = currentTarget switch
        {
            SettingsService.WidgetNewPlacementMainDisplay => 1,
            SettingsService.WidgetNewPlacementSpecificDisplay => -1,
            _ => 0
        };
        foreach (WidgetScreenInfo screen in _screens)
        {
            bool isSpecific = currentTarget == SettingsService.WidgetNewPlacementSpecificDisplay &&
                string.Equals(
                    settings.WidgetLayout.WidgetDefaultBoundScreenId?.Trim(),
                    screen.StableId.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            var item = new ComboBoxItem
            {
                Content = Format(
                    "Widget.ScreenBinding.MonitorFormat",
                    screen.Number,
                    screen.DisplayName),
                Tag = screen.StableId
            };
            NewPlacementCombo.Items.Add(item);
            if (isSpecific)
            {
                selectedIndex = NewPlacementCombo.Items.Count - 1;
            }
        }

        if (selectedIndex < 0)
        {
            // Specific display offline: keep a disabled marker selected. The
            // default-screen id has no stored label, so the compact generic
            // name fills {0} in the NewWidgets.Offline wording.
            var offline = new ComboBoxItem
            {
                Content = Format(
                    "Settings.Displays.NewWidgets.Offline",
                    T("Widget.ScreenBinding.HomeOfflineFallbackName")),
                IsEnabled = false,
                Tag = "offline-specific"
            };
            NewPlacementCombo.Items.Add(offline);
            selectedIndex = NewPlacementCombo.Items.Count - 1;
        }

        NewPlacementCombo.SelectedIndex = selectedIndex;

        // 屏幕尺寸或缩放变化时: fixed size / proportional elastic scale.
        ScalingModeCombo.Items.Clear();
        ScalingModeCombo.Items.Add(new ComboBoxItem
        {
            Content = T("Settings.Displays.ScalingMode.FixedSize"),
            Tag = SettingsService.WidgetDisplayScalingFixedSize
        });
        ScalingModeCombo.Items.Add(new ComboBoxItem
        {
            Content = T("Settings.Displays.ScalingMode.ElasticScale"),
            Tag = SettingsService.WidgetDisplayScalingElasticScale
        });
        ScalingModeCombo.SelectedIndex = string.Equals(
            SettingsService.NormalizeWidgetDisplayScalingMode(
                settings.WidgetLayout.WidgetDisplayScalingMode),
            SettingsService.WidgetDisplayScalingElasticScale,
            StringComparison.Ordinal)
            ? 1
            : 0;

        // 显示器断开时: move / collapse.
        DisconnectBehaviorCombo.Items.Clear();
        DisconnectBehaviorCombo.Items.Add(new ComboBoxItem
        {
            Content = T("Settings.Displays.Disconnect.Move"),
            Tag = SettingsService.WidgetDisplayDisconnectMoveToRemaining
        });
        DisconnectBehaviorCombo.Items.Add(new ComboBoxItem
        {
            Content = T("Settings.Displays.Disconnect.Collapse"),
            Tag = SettingsService.WidgetDisplayDisconnectCollapseToCapsule
        });
        DisconnectBehaviorCombo.SelectedIndex = string.Equals(
            SettingsService.NormalizeWidgetDisplayDisconnectBehavior(
                settings.WidgetLayout.WidgetDisplayDisconnectBehavior),
            SettingsService.WidgetDisplayDisconnectCollapseToCapsule,
            StringComparison.Ordinal)
            ? 1
            : 0;
        // _isRefreshing stays held: only the outer Refresh() releases it, so
        // deferred SelectionChanged events from these rebuilds cannot trigger
        // re-entrant page rebuilds during layout.
    }

    private void NewPlacementCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isRefreshing ||
            NewPlacementCombo.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            string.Equals(tag, "offline-specific", StringComparison.Ordinal))
        {
            return;
        }

        var service = global::DeskBox.App.Current.SettingsService;
        string newPlacementTarget;
        string? defaultBoundScreenId = null;
        bool setDefaultBoundScreenId = false;
        if (tag is SettingsService.WidgetNewPlacementCursorDisplay or
            SettingsService.WidgetNewPlacementMainDisplay)
        {
            newPlacementTarget = tag;
        }
        else
        {
            newPlacementTarget = SettingsService.WidgetNewPlacementSpecificDisplay;
            defaultBoundScreenId = tag;
            setDefaultBoundScreenId = true;
        }

        service.Settings.WidgetLayout.WidgetNewPlacementTarget = newPlacementTarget;
        // Mirror the pre-split behavior exactly: the bound screen id only
        // changes when the specific-display mode is chosen; cursor/main
        // leave the previous value (the payload's null = untouched matches
        // this on the main-process side).
        if (setDefaultBoundScreenId)
        {
            service.Settings.WidgetLayout.WidgetDefaultBoundScreenId = defaultBoundScreenId;
        }

        // The layout domain never rides snapshots: the preference write
        // travels as its own command and the response merges the
        // authoritative slice (batch R2).
        _ = _widgetActions.ApplyLayoutPreferences(new LayoutPreferencesChange(
            NewPlacementTarget: newPlacementTarget,
            DefaultBoundScreenId: defaultBoundScreenId));
        // The preview's "新格子" badge mirrors this setting — schedule a
        // refresh instead of calling Refresh() inline: this handler runs
        // inside the combo's own SelectionChanged, and a synchronous rebuild
        // here re-enters the selector while it is still settling (the
        // primary-switch crash family).
        ScheduleRefresh();
    }

    private void ScalingModeCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isRefreshing ||
            ScalingModeCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        var service = global::DeskBox.App.Current.SettingsService;
        string normalizedScaling = SettingsService.NormalizeWidgetDisplayScalingMode(tag);
        service.Settings.WidgetLayout.WidgetDisplayScalingMode = normalizedScaling;
        _ = ApplyScalingModeAndRefreshAsync(normalizedScaling);
    }

    private async Task ApplyScalingModeAndRefreshAsync(string normalizedScaling)
    {
        try
        {
            await _widgetActions.ApplyLayoutPreferences(new LayoutPreferencesChange(
                DisplayScalingMode: normalizedScaling));
            if (_widgetActions.IsHostAvailable)
            {
                _ = await _widgetActions.RecalibrateDisplayLayoutAsync();
            }
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[Displays] Apply scaling mode failed: {ex}");
        }

        ScheduleRefresh();
    }

    private void DisconnectBehaviorCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isRefreshing ||
            DisconnectBehaviorCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        var service = global::DeskBox.App.Current.SettingsService;
        string normalizedDisconnect =
            SettingsService.NormalizeWidgetDisplayDisconnectBehavior(tag);
        service.Settings.WidgetLayout.WidgetDisplayDisconnectBehavior = normalizedDisconnect;
        _ = _widgetActions.ApplyLayoutPreferences(new LayoutPreferencesChange(
            DisconnectBehavior: normalizedDisconnect));
    }

    // ── Widget rows with filter & inline "show on" combos ──────

    private void RefreshWidgetFilterCombo(IReadOnlyList<WidgetSurfaceRow> surfaces)
    {
        _isRefreshing = true;
        WidgetFilterCombo.Items.Clear();

        WidgetFilterCombo.Items.Add(new ComboBoxItem
        {
            Content = Format("Settings.Displays.Filter.All", surfaces.Count),
            Tag = FilterTagAll
        });

        if (_selectedScreen is { } selected && _screens.Count > 1)
        {
            int onSelected = surfaces.Count(surface =>
                surface.ActualScreen is { } actual &&
                string.Equals(actual.StableId, selected.StableId, StringComparison.OrdinalIgnoreCase));
            WidgetFilterCombo.Items.Add(new ComboBoxItem
            {
                Content = Format("Settings.Displays.Filter.SelectedScreen", selected.Number, onSelected),
                Tag = FilterTagSelected
            });
        }

        int borrowedCount = surfaces.Count(surface => surface.IsBorrowedFallback);
        if (borrowedCount > 0)
        {
            WidgetFilterCombo.Items.Add(new ComboBoxItem
            {
                Content = Format("Settings.Displays.Filter.Borrowed", borrowedCount),
                Tag = FilterTagBorrowed
            });
        }

        int selectedFilterIndex = 0;
        for (int i = 0; i < WidgetFilterCombo.Items.Count; i++)
        {
            if (WidgetFilterCombo.Items[i] is ComboBoxItem { Tag: string itemTag } &&
                string.Equals(itemTag, _widgetFilterTag, StringComparison.Ordinal))
            {
                selectedFilterIndex = i;
                break;
            }
        }

        if (selectedFilterIndex == 0)
        {
            _widgetFilterTag = FilterTagAll;
        }

        WidgetFilterCombo.SelectedIndex = selectedFilterIndex;
        WidgetFilterCombo.Visibility = WidgetFilterCombo.Items.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void WidgetFilterCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isRefreshing ||
            WidgetFilterCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        _widgetFilterTag = tag;
        ScheduleRefresh();
    }

    private void RefreshWidgetCards(IReadOnlyList<WidgetSurfaceRow> surfaces)
    {
        WidgetBindingCards.Children.Clear();
        foreach (WidgetSurfaceRow surface in FilterSurfaces(surfaces))
        {
            WidgetBindingCards.Children.Add(CreateWidgetRow(surface));
        }

        if (WidgetBindingCards.Children.Count == 0)
        {
            WidgetBindingCards.Children.Add(new TextBlock
            {
                Text = T("Settings.Displays.Widgets.Empty"),
                Margin = new Thickness(8, 12, 8, 4),
                Opacity = 0.6
            });
        }
    }

    private IEnumerable<WidgetSurfaceRow> FilterSurfaces(IReadOnlyList<WidgetSurfaceRow> surfaces)
    {
        if (string.Equals(_widgetFilterTag, FilterTagBorrowed, StringComparison.Ordinal))
        {
            return surfaces.Where(surface => surface.IsBorrowedFallback);
        }

        if (string.Equals(_widgetFilterTag, FilterTagSelected, StringComparison.Ordinal) &&
            _selectedScreen is { } selected)
        {
            return surfaces.Where(surface =>
                surface.ActualScreen is { } actual &&
                string.Equals(actual.StableId, selected.StableId, StringComparison.OrdinalIgnoreCase));
        }

        return surfaces;
    }

    private FrameworkElement CreateWidgetRow(WidgetSurfaceRow surface)
    {
        var combo = new ComboBox
        {
            MinWidth = 240,
            Tag = surface.ApplyWidgetId
        };
        _isRefreshing = true;
        int selectedIndex = surface.Mode == WidgetScreenBindingMode.FollowPrimary ? 0 : -1;
        combo.Items.Add(new ComboBoxItem
        {
            Content = T("Widget.ScreenBinding.FollowPrimary"),
            Tag = "follow"
        });
        foreach (WidgetScreenInfo screen in _screens)
        {
            string label = Format(
                "Widget.ScreenBinding.MonitorFormat",
                screen.Number,
                screen.DisplayName);
            if (screen.IsPrimary)
            {
                label += T("Widget.ScreenBinding.PrimarySuffix");
            }

            combo.Items.Add(new ComboBoxItem { Content = label, Tag = screen.StableId });
            if (surface.Mode == WidgetScreenBindingMode.Pinned &&
                string.Equals(
                    surface.BoundScreenId?.Trim(),
                    screen.StableId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = combo.Items.Count - 1;
            }
        }

        if (selectedIndex < 0)
        {
            // Pinned to a monitor that is not attached right now: the combo
            // item stays compact ("DELL U2720Q（未连接）"); the explanatory
            // "returns when reconnected" sentence lives in the row
            // description instead of bloating the dropdown.
            var offline = new ComboBoxItem
            {
                Content = Format(
                    "Widget.ScreenBinding.HomeOfflineCompact",
                    OfflineHomeDisplayName(surface.BoundScreenLabel)),
                IsEnabled = false,
                Tag = "offline"
            };
            combo.Items.Add(offline);
            selectedIndex = combo.Items.Count - 1;
        }

        combo.SelectedIndex = selectedIndex;
        // SelectionChanged is wired only after the initial selection, and
        // _isRefreshing stays held for the same reason as the placement
        // combos: the outer Refresh() is the single release point.
        combo.SelectionChanged += WidgetBindingCombo_SelectionChanged;

        return new SettingsCard
        {
            HeaderIcon = new FontIcon { Glyph = "\uE8A9" },
            Header = surface.DisplayName,
            Description = BuildRowDescription(surface),
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Content = combo
        };
    }

    private void WidgetBindingCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isRefreshing ||
            sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } } combo ||
            !_widgetActions.IsHostAvailable)
        {
            return;
        }

        string widgetId = (combo.Tag as string)!;
        (WidgetScreenBindingMode mode, string? boundId) = string.Equals(tag, "follow", StringComparison.Ordinal)
            ? (WidgetScreenBindingMode.FollowPrimary, null)
            : (WidgetScreenBindingMode.Pinned, tag);

        _ = ApplyBindingAndRefreshAsync(widgetId, mode, boundId);
    }

    private async Task ApplyBindingAndRefreshAsync(
        string widgetId,
        WidgetScreenBindingMode mode,
        string? boundId)
    {
        try
        {
            await _widgetActions.ApplyScreenBindingAsync(widgetId, mode, boundId);
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[Displays] Apply binding failed: {ex}");
        }

        Refresh();
    }

    private string BuildRowDescription(WidgetSurfaceRow surface)
    {
        var parts = new List<string>(4);
        string bindingSummary = BuildBindingSummary(surface);
        if (!string.IsNullOrWhiteSpace(bindingSummary))
        {
            parts.Add(bindingSummary);
        }

        if (surface.IsBorrowedFallback && surface.ActualScreen is { } borrowedOn)
        {
            parts.Add(Format("Settings.Displays.Widgets.BorrowedOnFormat", borrowedOn.Number));
        }

        if (surface.IsSizeLocked)
        {
            parts.Add(T("Settings.Displays.Widgets.SizeLockedSuffix"));
        }

        if (!string.IsNullOrWhiteSpace(surface.MembersText))
        {
            parts.Add(surface.MembersText);
        }

        return string.Join(" · ", parts);
    }

    private string BuildBindingSummary(WidgetSurfaceRow surface)
    {
        return surface.Mode switch
        {
            WidgetScreenBindingMode.FollowPrimary => T("Widget.ScreenBinding.FollowPrimary"),
            WidgetScreenBindingMode.Pinned when WidgetScreenCatalog.TryFindScreen(
                _screens, surface.BoundScreenId) is { } screen =>
                Format("Widget.ScreenBinding.MonitorFormat", screen.Number, screen.DisplayName),
            // Row description carries the full explanatory sentence; without
            // a stored label the pre-composed unnamed wording is used
            // directly (never nested through Format).
            WidgetScreenBindingMode.Pinned when !string.IsNullOrWhiteSpace(surface.BoundScreenLabel) =>
                Format("Widget.ScreenBinding.HomeOffline", surface.BoundScreenLabel),
            WidgetScreenBindingMode.Pinned =>
                T("Widget.ScreenBinding.HomeOfflineUnnamed"),
            _ => string.Empty
        };
    }

    /// <summary>
    /// Compact display name for a detached home: the friendly label captured
    /// at bind time, or the generic "原显示器" fallback — never the raw
    /// stable id, never a full sentence (that belongs to the description).
    /// </summary>
    private static string OfflineHomeDisplayName(string? storedLabel) =>
        string.IsNullOrWhiteSpace(storedLabel)
            ? T("Widget.ScreenBinding.HomeOfflineFallbackName")
            : storedLabel;

    // ── Shared helpers ─────────────────────────────────────────

    /// <summary>
    /// Every placeable surface: standalone widgets plus one row per group,
    /// filtered by session availability and resolved with exact expanded vs
    /// capsule geometry via <see cref="WidgetPlacementPreviewResolver"/>.
    /// </summary>
    private List<WidgetSurfaceRow> EnumerateSurfaces() =>
        WidgetPlacementPreviewResolver.ResolveSurfaces(
            global::DeskBox.App.Current.SettingsService.Settings,
            _screens,
            count => Format("Settings.Displays.GroupHeader", count),
            names => Format("Settings.Displays.GroupMembers", names));

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        // Every status switch drops the action button: the undo affordance is
        // only (re)attached right after a successful move-all.
        DisplaysStatusInfo.ActionButton = null;
        DisplaysStatusInfo.Severity = severity;
        DisplaysStatusInfo.Title = title;
        DisplaysStatusInfo.Message = message;
        DisplaysStatusInfo.IsOpen = true;
    }

    private static string T(string key) =>
        global::DeskBox.App.Current.LocalizationService.T(key);

    private static string Format(string key, params object[] values) =>
        global::DeskBox.App.Current.LocalizationService.Format(key, values);
}
