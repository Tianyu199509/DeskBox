using CommunityToolkit.WinUI.Controls;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// The "displays & widgets" settings page, modeled on the Windows display
/// settings page: a frameless arrangement preview of monitor cards (big
/// centered number, the selected card fully accent-filled like Windows,
/// with the number and badges flipped to a luminance-derived contrast
/// color), the selected display's settings card below it, and one inline
/// "show on" combo per widget surface. Pure code-behind on purpose —
/// runtime state, not persisted settings (DesktopOrganization section
/// precedent).
/// </summary>
public sealed partial class DisplaySettingsSection : UserControl
{
    private const double PreviewPaddingPx = 16;

    private IReadOnlyList<WidgetScreenInfo> _screens = [];
    private WidgetScreenInfo? _selectedScreen;
    private bool _isRefreshing;

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

    // H5: the app raises this when the display topology changes outside the
    // settings page (hot-plug, resolution change), so the preview and combos
    // do not keep showing a stale arrangement.
    private void OnSectionLoaded(object sender, RoutedEventArgs e)
    {
        global::DeskBox.App.Current.DisplayTopologyChanged += OnExternalDisplayTopologyChanged;
        global::DeskBox.App.Current.ScreenHomeChanged += OnExternalScreenHomeChanged;
        Refresh();
    }

    private void OnSectionUnloaded(object sender, RoutedEventArgs e)
    {
        global::DeskBox.App.Current.DisplayTopologyChanged -= OnExternalDisplayTopologyChanged;
        global::DeskBox.App.Current.ScreenHomeChanged -= OnExternalScreenHomeChanged;
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
    }

    public void Refresh()
    {
        if (global::DeskBox.App.Current?.SettingsService is not { } settingsService)
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
            PreviewViewbox.MaxHeight = _screens.Count > 1 ? 280 : 200;

            RebuildPreview();
            UpdateScreenDetailCard();
            RefreshPlacementCombos();
            RefreshWidgetCards();
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

    private void RebuildPreview()
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
        // the whole arrangement uniformly, so card and label sizes stay
        // proportional without any manual scale math. Label sizes key off the
        // union height so their rendered size stays stable whether the
        // arrangement is shown compact (single monitor) or full-size.
        int unionLeft = _screens.Min(screen => screen.Monitor.X);
        int unionTop = _screens.Min(screen => screen.Monitor.Y);
        int unionRight = _screens.Max(screen => screen.Monitor.X + screen.Monitor.Width);
        int unionBottom = _screens.Max(screen => screen.Monitor.Y + screen.Monitor.Height);
        int unionHeight = unionBottom - unionTop;
        canvas.Width = unionRight - unionLeft + PreviewPaddingPx * 2;
        canvas.Height = unionHeight + PreviewPaddingPx * 2;

        foreach (WidgetScreenInfo screen in _screens)
        {
            canvas.Children.Add(CreateScreenCard(screen, unionLeft, unionTop, unionHeight));
        }
    }

    private Button CreateScreenCard(
        WidgetScreenInfo screen,
        int unionLeft,
        int unionTop,
        int unionHeight)
    {
        bool isSelected = _selectedScreen is not null &&
            string.Equals(_selectedScreen.StableId, screen.StableId, StringComparison.OrdinalIgnoreCase);
        bool isDefault = global::DeskBox.App.Current.SettingsService.Settings.WidgetDefaultBoundScreenId
            is { } defaultId &&
            string.Equals(defaultId.Trim(), screen.StableId.Trim(), StringComparison.OrdinalIgnoreCase);

        double pillFontSize = Math.Clamp(unionHeight * 0.075, 18, 72);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Foreground stays unset on purpose: the number inherits the button's
        // ContentPresenter foreground, which flips to the luminance-derived
        // contrast color on the accent-filled (selected) card and keeps the
        // theme default everywhere else.
        var number = new TextBlock
        {
            Text = screen.Number.ToString(),
            FontSize = Math.Clamp(unionHeight * 0.26, 48, 320),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(number, 0);
        content.Children.Add(number);

        // Selection fill has to be resolved before the badges so they can
        // repaint against the actual background they sit on.
        Windows.UI.Color? selectionFill = isSelected ? ResolveSelectionFillColor() : null;

        // Win10/11 mark the primary monitor with a small filled badge inside
        // the rectangle; the widget-default badge stays an outline pill so the
        // two attributes read at a glance without competing. On the selected
        // (accent-filled) card both switch to contrast-derived colors so they
        // never drown in the theme color.
        if (screen.IsPrimary || isDefault)
        {
            var badges = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = pillFontSize * 0.35,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, unionHeight * 0.035)
            };
            if (screen.IsPrimary)
            {
                badges.Children.Add(CreateBadge(
                    T("Settings.Displays.PrimaryBadge"),
                    pillFontSize,
                    filled: true,
                    selectionFill));
            }

            if (isDefault)
            {
                badges.Children.Add(CreateBadge(
                    T("Settings.Displays.DefaultBadge"),
                    pillFontSize,
                    filled: false,
                    selectionFill));
            }

            Grid.SetRow(badges, 1);
            content.Children.Add(badges);
        }

        var card = new Button
        {
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Width = screen.Monitor.Width,
            Height = screen.Monitor.Height,
            CornerRadius = new CornerRadius(8),
            Content = content,
            Tag = screen
        };
        if (isSelected && selectionFill is { } fill)
        {
            ApplySelectionFill(card, fill);
        }
        card.Click += ScreenCard_Click;

        Canvas.SetLeft(card, PreviewPaddingPx + screen.Monitor.X - unionLeft);
        Canvas.SetTop(card, PreviewPaddingPx + screen.Monitor.Y - unionTop);
        return card;
    }

    /// <summary>
    /// Native display-settings selection: the whole rectangle fills with the
    /// accent color instead of just a ring. The default Fluent template's
    /// resting/hover/pressed brushes are overridden in the card's own
    /// resources so those states stay inside the accent family (a touch
    /// lighter/darker) instead of flashing to the neutral gray, and the
    /// foreground flips to a luminance-derived contrast color so the number
    /// and badges stay readable on any accent.
    /// </summary>
    private void ApplySelectionFill(Button card, Windows.UI.Color fill)
    {
        Windows.UI.Color contrast = ContrastForegroundColor(fill);
        bool isDarkTheme = ActualTheme == ElementTheme.Dark;
        Windows.UI.Color overlay = isDarkTheme ? Colors.White : Colors.Black;
        double hoverAmount = isDarkTheme ? 0.10 : 0.08;
        double pressAmount = isDarkTheme ? 0.18 : 0.16;
        Windows.UI.Color hover = Blend(fill, overlay, hoverAmount);
        Windows.UI.Color pressed = Blend(fill, overlay, pressAmount);

        card.Background = Brush(fill);
        card.Foreground = Brush(contrast);
        card.BorderBrush = Brush(fill);
        card.Resources["ButtonBackground"] = Brush(fill);
        card.Resources["ButtonBackgroundPointerOver"] = Brush(hover);
        card.Resources["ButtonBackgroundPressed"] = Brush(pressed);
        card.Resources["ButtonBorderBrush"] = Brush(fill);
        card.Resources["ButtonBorderBrushPointerOver"] = Brush(hover);
        card.Resources["ButtonBorderBrushPressed"] = Brush(pressed);
        card.Resources["ButtonForeground"] = Brush(contrast);
        card.Resources["ButtonForegroundPointerOver"] = Brush(contrast);
        card.Resources["ButtonForegroundPressed"] = Brush(contrast);

        // L1: a fill whose luminance sits in the ambiguous band around the
        // 0.45 black/white threshold (0.35–0.55) gives the number weak
        // contrast against the fill; a 1px inner contrast stroke keeps the
        // selection edge readable on those mid-luminance accents.
        if (RelativeLuminance(fill) is >= 0.35 and <= 0.55 &&
            card.Content is Grid content)
        {
            card.Content = new Border
            {
                BorderBrush = Brush(contrast),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(1),
                Child = content
            };
        }
    }

    private Border CreateBadge(
        string text,
        double pillFontSize,
        bool filled,
        Windows.UI.Color? selectionFill)
    {
        // Subtle rounding, not a stadium pill — the badge should read as a
        // compact label, not a capsule. Filled badges always pair a solid
        // background with a luminance-derived contrast text so the label
        // never vanishes into its own pill (the accent family can be a pale
        // tint, e.g. salmon on a red accent): the unselected card paints the
        // theme accent with contrast text, the accent-filled selected card
        // inverts to a contrast chip carrying the fill color. Outline badges
        // stay single-color lines over the card.
        double cornerRadius = Math.Clamp(pillFontSize * 0.25, 4, 18);
        Windows.UI.Color fillColor;
        Windows.UI.Color textColor;
        Windows.UI.Color outlineColor;
        if (selectionFill is { } fill)
        {
            Windows.UI.Color onFill = ContrastForegroundColor(fill);
            fillColor = onFill;
            textColor = fill;
            outlineColor = onFill;
        }
        else
        {
            fillColor = ResolveSelectionFillColor();
            textColor = ContrastForegroundColor(fillColor);
            outlineColor = ResolveAccentColor();
        }
        return new Border
        {
            Background = filled ? Brush(fillColor) : null,
            BorderBrush = filled ? null : Brush(outlineColor),
            BorderThickness = new Thickness(filled ? 0 : Math.Clamp(pillFontSize * 0.08, 1, 3)),
            CornerRadius = new CornerRadius(cornerRadius),
            Padding = new Thickness(pillFontSize * 0.6, pillFontSize * 0.18, pillFontSize * 0.6, pillFontSize * 0.26),
            Child = new TextBlock
            {
                Text = text,
                FontSize = pillFontSize,
                Foreground = Brush(filled ? textColor : outlineColor)
            }
        };
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

    private static Windows.UI.Color ContrastForegroundColor(Windows.UI.Color background) =>
        RelativeLuminance(background) > 0.45 ? Colors.Black : Colors.White;

    private Windows.UI.Color ResolveSelectionFillColor()
    {
        try
        {
            // Light theme fills with the raw accent like the native display
            // page; dark theme steps to Light1 so the fill keeps white text
            // readable instead of dropping to the washed-out Light2 shade.
            var ui = new Windows.UI.ViewManagement.UISettings();
            return ActualTheme == ElementTheme.Dark
                ? ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight1)
                : ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            return Windows.UI.Color.FromArgb(0xFF, 0x00, 0x5C, 0xE6);
        }
    }

    private static Windows.UI.Color ResolveAccentColor()
    {
        try
        {
            // Match the accent shade the theme's controls actually paint
            // with (toggle/button fills), not the raw saturated accent: the
            // raw color reads harshly next to them (e.g. deep red vs the
            // lighter red the switches use).
            var ui = new Windows.UI.ViewManagement.UISettings();
            return ui.GetColorValue(
                Windows.UI.ViewManagement.UIColorType.AccentLight2);
        }
        catch
        {
            return Windows.UI.Color.FromArgb(0xFF, 0x00, 0x5C, 0xE6);
        }
    }

    // ── Selected screen card ───────────────────────────────────

    private void ScreenCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WidgetScreenInfo screen })
        {
            _selectedScreen = screen;
            RebuildPreview();
            UpdateScreenDetailCard();
        }
    }

    private void UpdateScreenDetailCard()
    {
        if (_selectedScreen is not { } screen)
        {
            ScreenDetailCard.Visibility = Visibility.Collapsed;
            return;
        }

        ScreenDetailCard.Visibility = _screens.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        string title = Format(
            "Widget.ScreenBinding.MonitorFormat",
            screen.Number,
            screen.DisplayName);
        ScreenDetailCard.Header = screen.IsPrimary
            ? title + T("Widget.ScreenBinding.PrimarySuffix")
            : title;

        int boundCount = 0;
        foreach (WidgetSurfaceRow surface in EnumerateSurfaces())
        {
            if (surface.Mode == WidgetScreenBindingMode.Pinned &&
                string.Equals(
                    surface.BoundScreenId?.Trim(),
                    screen.StableId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                boundCount++;
            }
        }

        ScreenDetailCard.Description = Format(
            "Settings.Displays.ScreenSummary",
            screen.DisplayName,
            Math.Round(screen.EffectiveDpiScale * 100),
            boundCount);
    }

    private WidgetMoveAllUndoToken? _lastMoveAllToken;

    private async void MoveAllWidgetsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScreen is not { } screen ||
            global::DeskBox.App.Current.WidgetManager is not { } manager)
        {
            return;
        }

        // Confirm dialog (spec 6.3/6.4): counts how many widgets sit on other
        // displays right now.
        var surfaces = EnumerateSurfaces().ToList();
        int elsewhere = surfaces.Count(surface =>
            surface.Mode == WidgetScreenBindingMode.Pinned &&
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
                await manager.MoveAllWidgetSurfacesToDisplayAsync(screen.StableId);
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
                if (global::DeskBox.App.Current.WidgetManager is { } manager)
                {
                    // New signature: false means the snapshot went stale
                    // (widgets moved again after the move-all) and nothing
                    // was restored.
                    bool undone = await manager.UndoMoveAllWidgetSurfacesAsync(token);
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
        ShowStatus(
            InfoBarSeverity.Success,
            Format("Settings.Displays.MoveAll.Done", moved, displayNumber),
            string.Empty);
    }

    private void IdentifyButton_Click(object sender, RoutedEventArgs e)
    {
        DisplayIdentifyOverlayWindow.IdentifyAll(_screens);
    }

    // ── 新格子出现在 / 显示器断开时 (spec 6.3) ────────────────

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
            settings.WidgetNewPlacementTarget);
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
                    settings.WidgetDefaultBoundScreenId?.Trim(),
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
            SettingsService.NormalizeWidgetDisplayDisconnectBehavior(settings.WidgetDisplayDisconnectBehavior),
            SettingsService.WidgetDisplayDisconnectCollapseToCapsule) ? 1 : 0;
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
        if (tag is SettingsService.WidgetNewPlacementCursorDisplay or
            SettingsService.WidgetNewPlacementMainDisplay)
        {
            service.Settings.WidgetNewPlacementTarget = tag;
        }
        else
        {
            service.Settings.WidgetNewPlacementTarget =
                SettingsService.WidgetNewPlacementSpecificDisplay;
            service.Settings.WidgetDefaultBoundScreenId = tag;
        }

        _ = service.SaveAsync();
        // The preview's "新格子" badge mirrors this setting — schedule a
        // refresh instead of calling Refresh() inline: this handler runs
        // inside the combo's own SelectionChanged, and a synchronous rebuild
        // here re-enters the selector while it is still settling (the
        // primary-switch crash family).
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
        service.Settings.WidgetDisplayDisconnectBehavior =
            SettingsService.NormalizeWidgetDisplayDisconnectBehavior(tag);
        _ = service.SaveAsync();
    }

    // ── Widget rows with inline "show on" combos ───────────────

    private void RefreshWidgetCards()
    {
        WidgetBindingCards.Children.Clear();
        foreach (WidgetSurfaceRow surface in EnumerateSurfaces())
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
            Description = surface.MembersText ?? BuildBindingSummary(surface),
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
            global::DeskBox.App.Current.WidgetManager is not { } manager)
        {
            return;
        }

        string widgetId = (combo.Tag as string)!;
        (WidgetScreenBindingMode mode, string? boundId) = string.Equals(tag, "follow", StringComparison.Ordinal)
            ? (WidgetScreenBindingMode.FollowPrimary, null)
            : (WidgetScreenBindingMode.Pinned, tag);

        _ = ApplyBindingAndRefreshAsync(manager, widgetId, mode, boundId);
    }

    private async Task ApplyBindingAndRefreshAsync(
        WidgetManager manager,
        string widgetId,
        WidgetScreenBindingMode mode,
        string? boundId)
    {
        try
        {
            await manager.ApplyScreenBindingAsync(widgetId, mode, boundId);
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[Displays] Apply binding failed: {ex}");
        }

        Refresh();
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

    private readonly record struct WidgetSurfaceRow(
        string DisplayName,
        string ApplyWidgetId,
        WidgetScreenBindingMode Mode,
        string? BoundScreenId,
        string? MembersText = null,
        string? BoundScreenLabel = null);

    /// <summary>
    /// Every placeable surface: standalone widgets plus one row per group.
    /// Group rows expose the group's binding for display but apply through
    /// the representative member, whose ApplyScreenBindingAsync syncs the
    /// group surface and every member.
    /// </summary>
    private List<WidgetSurfaceRow> EnumerateSurfaces()
    {
        var settings = global::DeskBox.App.Current.SettingsService.Settings;
        var grouped = new HashSet<string>(
            settings.WidgetGroups.SelectMany(group => group.MemberIds),
            StringComparer.Ordinal);

        var surfaces = new List<WidgetSurfaceRow>();
        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (widget.IsDisabled ||
                settings.DeletedWidgetIds.Contains(widget.Id) ||
                grouped.Contains(widget.Id))
            {
                continue;
            }

            surfaces.Add(new WidgetSurfaceRow(
                widget.Name,
                widget.Id,
                widget.ScreenBindingMode,
                widget.BoundScreenId,
                BoundScreenLabel: widget.BoundScreenLabel));
        }

        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            List<WidgetConfig> members = group.MemberIds
                .Select(id => settings.Widgets.FirstOrDefault(widget =>
                    string.Equals(widget.Id, id, StringComparison.Ordinal)))
                .Where(member => member is not null)
                .Cast<WidgetConfig>()
                .ToList();
            if (members.Count == 0)
            {
                continue;
            }

            // Groups get their own identity plus the member list — a bare
            // member name does not identify a multi-widget surface.
            surfaces.Add(new WidgetSurfaceRow(
                Format("Settings.Displays.GroupHeader", members.Count),
                members[0].Id,
                group.ScreenBindingMode,
                group.BoundScreenId,
                BoundScreenLabel: group.BoundScreenLabel,
                MembersText: Format(
                    "Settings.Displays.GroupMembers",
                    string.Join(", ", members.Select(member => member.Name)))));
        }

        return surfaces;
    }

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
