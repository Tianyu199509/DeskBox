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
    // Preview accents follow the user's system accent color, resolved fresh
    // on every rebuild so theme/accent changes apply on the next refresh.
    private Brush AccentBrush() => new SolidColorBrush(ResolveAccentColor());

    private const double PreviewPaddingPx = 16;

    private IReadOnlyList<WidgetScreenInfo> _screens = [];
    private WidgetScreenInfo? _selectedScreen;
    private bool _isRefreshing;

    public DisplaySettingsSection()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        ActualThemeChanged += (_, _) => Refresh();
    }

    public void Refresh()
    {
        if (global::DeskBox.App.Current?.SettingsService is not { } settingsService)
        {
            return;
        }

        _isRefreshing = true;
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
        RefreshWidgetCards();
        _isRefreshing = false;
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

        ScreenDetailCard.Visibility = Visibility.Visible;
        string title = Format(
            "Widget.ScreenBinding.MonitorFormat",
            screen.Number,
            screen.PhysicalSizeText);
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
            screen.PhysicalSizeText,
            Math.Round(screen.EffectiveDpiScale * 100),
            boundCount);

        string? defaultId = global::DeskBox.App.Current.SettingsService.Settings
            .WidgetDefaultBoundScreenId;
        bool screenIsDefault = string.Equals(
            defaultId?.Trim(),
            screen.StableId.Trim(),
            StringComparison.OrdinalIgnoreCase);

        // Two-state action: the selected screen that is already the widget
        // default offers "clear default" instead of a dead disabled button.
        // The caption resolves in code-behind (not a Localized attached
        // property) because it flips per selected screen at runtime.
        SetDefaultScreenButton.IsEnabled = true;
        SetDefaultScreenButton.Content = screenIsDefault
            ? T("Settings.Displays.SetDefault.Clear.Title")
            : T("Settings.Displays.SetDefault");
    }

    private void SetDefaultScreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScreen is not { } screen)
        {
            return;
        }

        var service = global::DeskBox.App.Current.SettingsService;
        bool clearing = string.Equals(
            service.Settings.WidgetDefaultBoundScreenId?.Trim(),
            screen.StableId.Trim(),
            StringComparison.OrdinalIgnoreCase);
        service.Settings.WidgetDefaultBoundScreenId = clearing ? null : screen.StableId;
        _ = service.SaveAsync();
        ShowStatus(
            InfoBarSeverity.Success,
            clearing
                ? T("Settings.Displays.SetDefault.Clear.Title")
                : Format("Settings.Displays.SetDefault.Done", screen.Number),
            string.Empty);
        Refresh();
    }

    private async void PinAllWidgetsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScreen is not { } screen ||
            global::DeskBox.App.Current.WidgetManager is not { } manager)
        {
            return;
        }

        PinAllWidgetsButton.IsEnabled = false;
        try
        {
            int changed = await manager.PinAllWidgetSurfacesToScreenAsync(screen.StableId);
            ShowStatus(
                InfoBarSeverity.Success,
                Format("Settings.Displays.PinAll.Done", changed, screen.Number),
                string.Empty);
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[Displays] Pin-all failed: {ex}");
            ShowStatus(
                InfoBarSeverity.Error,
                T("Settings.Displays.PinAll.Failed"),
                string.Empty);
        }
        finally
        {
            PinAllWidgetsButton.IsEnabled = true;
            Refresh();
        }
    }

    private void IdentifyButton_Click(object sender, RoutedEventArgs e)
    {
        DisplayIdentifyOverlayWindow.IdentifyAll(_screens);
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
            MinWidth = 200,
            Tag = surface.ApplyWidgetId
        };
        _isRefreshing = true;
        int selectedIndex = surface.Mode switch
        {
            WidgetScreenBindingMode.FollowPrimary => 1,
            WidgetScreenBindingMode.Pinned => -1,
            _ => 0
        };
        combo.Items.Add(new ComboBoxItem { Content = T("Widget.ScreenBinding.Auto"), Tag = "auto" });
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
                screen.PhysicalSizeText);
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
            // Pinned to a monitor that is not attached right now: show the
            // state in place instead of silently snapping the combo to 自动.
            var offline = new ComboBoxItem
            {
                Content = Format(
                    "Settings.Displays.Widgets.Offline",
                    T("Settings.Displays.Widgets.Pinned")),
                IsEnabled = false,
                Tag = "offline"
            };
            combo.Items.Add(offline);
            selectedIndex = combo.Items.Count - 1;
        }

        combo.SelectedIndex = selectedIndex;
        _isRefreshing = false;
        combo.SelectionChanged += WidgetBindingCombo_SelectionChanged;

        return new SettingsCard
        {
            HeaderIcon = new FontIcon { Glyph = "\uE8A5" },
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
        (WidgetScreenBindingMode mode, string? boundId) = tag switch
        {
            "follow" => (WidgetScreenBindingMode.FollowPrimary, null),
            string stableId when !string.Equals(stableId, "auto", StringComparison.Ordinal) &&
                !string.Equals(stableId, "offline", StringComparison.Ordinal) =>
                (WidgetScreenBindingMode.Pinned, stableId),
            _ => (WidgetScreenBindingMode.Unbound, null)
        };

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
                Format("Widget.ScreenBinding.MonitorFormat", screen.Number, screen.PhysicalSizeText),
            WidgetScreenBindingMode.Pinned =>
                Format(
                    "Settings.Displays.Widgets.Offline",
                    T("Settings.Displays.Widgets.Pinned")),
            _ => T("Widget.ScreenBinding.Auto")
        };
    }

    // ── Shared helpers ─────────────────────────────────────────

    private readonly record struct WidgetSurfaceRow(
        string DisplayName,
        string ApplyWidgetId,
        WidgetScreenBindingMode Mode,
        string? BoundScreenId,
        string? MembersText = null);

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
                widget.BoundScreenId));
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
                Format(
                    "Settings.Displays.GroupMembers",
                    string.Join(", ", members.Select(member => member.Name)))));
        }

        return surfaces;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
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
