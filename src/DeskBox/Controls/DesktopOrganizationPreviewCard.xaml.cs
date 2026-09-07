using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskBox.Controls;

/// <summary>A side-effect-free preview: only selection and destination choices are edited.</summary>
public sealed partial class DesktopOrganizationPreviewCard : UserControl, IDisposable
{
    private readonly DesktopOrganizationTargetPlan _target;
    private readonly DesktopOrganizationTargetSelection _selection;
    private readonly HashSet<string> _excludedPaths;
    private readonly HashSet<string>? _retainedSelection;
    private readonly bool _canSelectItems;
    private readonly Func<DesktopOrganizationFileSnapshot, string>? _itemDescription;
    private readonly List<PreviewTile> _tiles = [];
    private FileWidgetIconLayout _layout;
    private PreviewTileAppearance? _tileAppearance;
    private XamlRoot? _observedXamlRoot;
    private bool _updating;
    private bool _expanded;
    private bool _disposed;
    private int _columns;
    private int _visibleCount = -1;
    private int _iconGeneration;
    private int _selectionAnchor = -1;
    private IReadOnlySet<string> _newPaths = new HashSet<string>();
    private readonly List<DesktopOrganizationDestinationOption> _destinations = [];

    public DesktopOrganizationPreviewCard(
        DesktopOrganizationTargetPlan target,
        DesktopOrganizationTargetSelection selection,
        HashSet<string> excludedPaths,
        IReadOnlyList<DesktopOrganizationDestinationOption> destinations)
        : this(target, selection, excludedPaths, destinations, null, true, null)
    {
    }

    internal DesktopOrganizationPreviewCard(
        string groupId, string title, string description,
        IReadOnlyList<DesktopOrganizationFileSnapshot> items,
        HashSet<string> selectedPaths, bool canSelect,
        Func<DesktopOrganizationFileSnapshot, string>? itemDescription = null)
        : this(new DesktopOrganizationTargetPlan
        {
            SourceBucketId = groupId, TargetWidgetId = groupId, SuggestedDisplayName = title, Items = items.ToList()
        }, new DesktopOrganizationTargetSelection(), [], [], selectedPaths, canSelect, itemDescription)
    {
        DestinationButton.Visibility = Visibility.Collapsed;
        GroupDescriptionText.Text = description;
        GroupDescriptionText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
        if (!canSelect)
        {
            GroupCheckBox.Visibility = Visibility.Collapsed;
            ReadOnlyTitleText.Text = title;
            ReadOnlyTitleText.Visibility = Visibility.Visible;
        }
        _expanded = true;
        LayoutItems(force: true);
    }

    private DesktopOrganizationPreviewCard(
        DesktopOrganizationTargetPlan target,
        DesktopOrganizationTargetSelection selection,
        HashSet<string> excludedPaths,
        IReadOnlyList<DesktopOrganizationDestinationOption> destinations,
        HashSet<string>? retainedSelection, bool canSelect,
        Func<DesktopOrganizationFileSnapshot, string>? itemDescription)
    {
        _target = target;
        _selection = selection;
        _excludedPaths = excludedPaths;
        _retainedSelection = retainedSelection;
        _canSelectItems = canSelect;
        _itemDescription = itemDescription;
        InitializeComponent();
        TitleText.Text = target.SuggestedDisplayName;
        AutomationProperties.SetName(GroupCheckBox, target.SuggestedDisplayName);
        ToolTipService.SetToolTip(GroupCheckBox, target.SuggestedDisplayName + "\n" +
            T("DesktopOrganization.Layout.SelectionHelp"));
        AutomationProperties.SetHelpText(GroupCheckBox, T("DesktopOrganization.Layout.SelectionHelp"));
        _updating = true;
        if (target.CreatesWidget)
        {
            AddDestinationMenuItem(null);
        }
        else
        {
            AddDestination(new DesktopOrganizationDestinationOption(target.TargetWidgetId,
                target.SuggestedDisplayName, target.TargetDirectoryPath, false));
        }
        foreach (var destination in destinations.Where(item => target.CreatesWidget || item.Id != target.TargetWidgetId))
            AddDestination(destination);
        _updating = false;
        Loaded += Card_Loaded;
        Unloaded += Card_Unloaded;
        SizeChanged += (_, _) => LayoutItems();
        ActualThemeChanged += (_, _) => RefreshAppearance();
        AddHandler(KeyDownEvent, new KeyEventHandler(Card_KeyDown), true);
        RefreshAppearance();
    }

    public event EventHandler? SelectionChanged;
    internal double MinimumPreviewWidth => Math.Max(220, _layout.CellWidth * 2 + 26);
    internal bool IsExpanded { get => _expanded; set { _expanded = value; LayoutItems(force: true); } }
    internal string SourceBucketId => _target.SourceBucketId;

    internal void SetNewItems(IReadOnlySet<string> paths)
    {
        _newPaths = paths;
        _tileAppearance = null;
        RefreshAppearance();
    }

    private void AddDestination(DesktopOrganizationDestinationOption option)
    {
        _destinations.Add(option);
        AddDestinationMenuItem(option);
    }

    private void AddDestinationMenuItem(DesktopOrganizationDestinationOption? option)
    {
        if (_target.CreatesWidget && option is not null && DestinationFlyout.Items.Count == 1)
            DestinationFlyout.Items.Add(new MenuFlyoutSeparator());

        var item = new RadioMenuFlyoutItem
        {
            // A dynamic destination shares the card title's name; the quotes
            // distinguish "creates a new widget" from an existing widget pick.
            Text = option is null ? Format("DesktopOrganization.Layout.NewDestination", _target.SuggestedDisplayName) : option.DisplayName,
            Icon = new FontIcon
            {
                Glyph = option is null ? "\uE710" : "\uE8B8",
                FontSize = 16,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            },
            Tag = option, GroupName = _target.SourceBucketId
        };
        ToolTipService.SetToolTip(item, option?.DirectoryPath ?? _target.TargetDirectoryPath);
        item.Click += (_, _) =>
        {
            _selection.DestinationMode = option is null ? DesktopOrganizationDestinationMode.Dynamic : DesktopOrganizationDestinationMode.ExistingWidget;
            _selection.ExistingWidgetId = option?.Id;
            RefreshAppearance();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        DestinationFlyout.Items.Add(item);
    }

    internal void RefreshAppearance()
    {
        if (_disposed) return;
        var settings = App.Current.SettingsService.Settings;
        WidgetConfig? config = settings.Widgets.FirstOrDefault(item =>
            item.Id == (_selection.ExistingWidgetId ?? _target.TargetWidgetId));
        _layout = FileWidgetIconLayout.Calculate(settings, config?.IconSizeOverride,
            WindowsCompatibilityService.ResolveSystemTextScaleFactor());
        var tileAppearance = new PreviewTileAppearance(_layout, settings.HideShortcutArrowOverlay,
            settings.ShowImageFilesAsIcons, settings.ShowFileExtensions,
            settings.HideShortcutExtensionWhenShowingFileExtensions, settings.ShowFileItemPathTooltips,
            XamlRoot?.RasterizationScale ?? 1);
        if (_tileAppearance != tileAppearance)
        {
            _tileAppearance = tileAppearance;
            _iconGeneration++;
            foreach (PreviewTile tile in _tiles) tile.Image.Source = null;
            _tiles.Clear();
            ItemsPanel.Children.Clear();
            _visibleCount = -1;
        }
        ApplyMaterial(settings);
        LayoutItems();
        UpdateSelectionVisuals();
        var destination = _selection.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget
            ? _destinations.FirstOrDefault(item => item.Id == _selection.ExistingWidgetId) : null;
        string destinationName = destination?.DisplayName ??
            Format("DesktopOrganization.Layout.NewDestination", _target.SuggestedDisplayName);
        DestinationText.Text = destinationName;
        string destinationHint = T("DesktopOrganization.Layout.ChangeDestination") + "\n" +
            (destination?.DirectoryPath ?? _target.TargetDirectoryPath);
        ToolTipService.SetToolTip(DestinationButton, destinationHint);
        AutomationProperties.SetName(DestinationButton, T("DesktopOrganization.Layout.ChangeDestination") + " · " +
            DestinationText.Text);
        foreach (var menuItem in DestinationFlyout.Items.OfType<RadioMenuFlyoutItem>())
            menuItem.IsChecked = (menuItem.Tag as DesktopOrganizationDestinationOption)?.Id == destination?.Id;
    }

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        if (_observedXamlRoot is not null) _observedXamlRoot.Changed -= Card_XamlRootChanged;
        _observedXamlRoot = XamlRoot;
        if (_observedXamlRoot is not null) _observedXamlRoot.Changed += Card_XamlRootChanged;
        RefreshAppearance();
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachXamlRoot();
    }

    private void Card_XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshAppearance();

    private void DetachXamlRoot()
    {
        if (_observedXamlRoot is not null) _observedXamlRoot.Changed -= Card_XamlRootChanged;
        _observedXamlRoot = null;
    }

    private void ApplyMaterial(AppSettings settings)
    {
        // Planning-surface cards use the Fluent control-layer radius, not the
        // desktop widgets' corner preference: this is a task dialog, and its
        // cards must match the buttons and InfoBars around them.
        var corner = new CornerRadius(8);
        MaterialSurface.CornerRadius = FallbackSurface.CornerRadius = CardOutline.CornerRadius = corner;
        // A planning dialog needs a calm, neutral surface. Widget material and the
        // user's accent belong to the desktop widgets; applying them here makes the
        // preview read like a themed canvas instead of a Fluent task surface.
        MaterialSurface.Visibility = Visibility.Collapsed;
        FallbackSurface.Visibility = Visibility.Visible;
        FallbackSurface.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        CardOutline.BorderThickness = new Thickness(1);
        CardOutline.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        HeaderDivider.BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
    }

    private void LayoutItems(bool force = false)
    {
        if (_disposed || _layout.CellWidth <= 0) return;
        // A Grid with fixed columns can report its overflowing desired width.
        // Always size the icon region from the card's allocated width instead.
        double cardWidth = double.IsFinite(Width) ? Width : ActualWidth;
        if (cardWidth <= 0) cardWidth = MinimumPreviewWidth;
        double width = Math.Max(1, cardWidth - ItemsPanel.Margin.Left - ItemsPanel.Margin.Right -
            CardOutline.BorderThickness.Left - CardOutline.BorderThickness.Right);
        if (!double.IsFinite(ItemsPanel.Width) || Math.Abs(ItemsPanel.Width - width) > .5)
            ItemsPanel.Width = width;
        int columns = Math.Max(1, (int)Math.Floor(width / _layout.CellWidth));
        // Keep every item in the card's own scroll surface. This makes cards in the
        // plan grid the same height while still keeping the complete plan visible.
        int visible = _target.Items.Count;
        if (!force && columns == _columns && visible == _visibleCount) return;
        _columns = columns;
        _visibleCount = visible;
        while (_tiles.Count < visible) _tiles.Add(CreateTile(_target.Items[_tiles.Count]));
        ItemsPanel.Children.Clear();
        ItemsPanel.ColumnDefinitions.Clear();
        ItemsPanel.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++)
            ItemsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_layout.CellWidth) });
        for (int i = 0; i < (int)Math.Ceiling(visible / (double)columns); i++)
            ItemsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_layout.CellHeight) });
        for (int i = 0; i < visible; i++)
        {
            var button = _tiles[i].Button;
            Grid.SetColumn(button, i % columns);
            Grid.SetRow(button, i / columns);
            ItemsPanel.Children.Add(button);
        }
        ExpandButton.Visibility = Visibility.Collapsed;
        UpdateSelectionVisuals();
    }

    private PreviewTile CreateTile(DesktopOrganizationFileSnapshot item)
    {
        var settings = App.Current.SettingsService.Settings;
        var image = new Image { Width = _layout.ImageSize, Height = _layout.ImageSize, Stretch = Stretch.Uniform };
        var fallback = new FontIcon
        {
            Glyph = item.IsDirectory ? "\uE8B8" : "\uE7C3", FontSize = _layout.ImageSize * .85,
            Width = _layout.ImageSize, Height = _layout.ImageSize
        };
        var iconHost = new Grid();
        iconHost.Children.Add(image);
        iconHost.Children.Add(fallback);
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = _layout.ContentSpacing,
            Padding = _layout.TilePadding
        };
        content.Children.Add(iconHost);
        content.Children.Add(new TextBlock
        {
            Text = FileService.GetDisplayName(item.SourcePath, item.IsDirectory,
                settings.ShowFileExtensions, settings.HideShortcutExtensionWhenShowingFileExtensions),
            FontSize = _layout.LabelFontSize, MaxWidth = _layout.LabelMaxWidth,
            MaxLines = _layout.LabelMaxLines, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 0, 2, 0),
            Visibility = _layout.ShowLabel ? Visibility.Visible : Visibility.Collapsed
        });
        var tileRoot = new Grid();
        tileRoot.Children.Add(content);
        if (item.SourceScope == DesktopOrganizationSourceScope.Public)
            tileRoot.Children.Add(new FontIcon
            {
                Glyph = "\uE716", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 2, _newPaths.Contains(item.SourcePath) ? 26 : 2, 2), IsHitTestVisible = false
            });
        if (_newPaths.Contains(item.SourcePath))
            tileRoot.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(2), Padding = new Thickness(2, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Child = new TextBlock { Text = T("DesktopOrganization.Layout.New"), FontSize = 10,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] }
            });
        var button = new ToggleButton
        {
            Content = tileRoot, Style = (Style)Resources["PreviewFileToggleStyle"],
            Width = _layout.TileWidth, Height = _layout.TileHeight, Margin = _layout.TileMargin,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top
        };
        string label = item.Name + (item.SourceScope == DesktopOrganizationSourceScope.Public
            ? " · " + T("DesktopOrganization.Public.Badge") : string.Empty);
        string description = _itemDescription?.Invoke(item) ?? string.Empty;
        AutomationProperties.SetName(button, string.IsNullOrWhiteSpace(description) ? label : label + " · " + description);
        string tooltip = settings.ShowFileItemPathTooltips ? item.SourcePath : label;
        if (!string.IsNullOrWhiteSpace(description)) tooltip += "\n" + description;
        ToolTipService.SetToolTip(button, tooltip);
        if (!_canSelectItems)
        {
            // Build the details flyout lazily on click; pre-attaching one per
            // tile made preview-card memory scale with desktop file count.
            button.Click += (_, _) => CreateItemDetails(item, description).ShowAt(button);
        }
        var contextMenu = new MenuFlyout();
        var detailsItem = new MenuFlyoutItem { Text = T("DesktopOrganization.Layout.Details") };
        detailsItem.Click += (_, _) => CreateItemDetails(item, description).ShowAt(button);
        contextMenu.Items.Add(detailsItem);
        button.ContextFlyout = contextMenu;
        var tile = new PreviewTile(item, button, image, fallback, content);
        button.Checked += (_, _) => ChangeItemSelection(tile, true);
        button.Unchecked += (_, _) => ChangeItemSelection(tile, false);
        _ = LoadIconAsync(tile, _iconGeneration, settings.HideShortcutArrowOverlay, settings.ShowImageFilesAsIcons);
        return tile;
    }

    private async Task LoadIconAsync(PreviewTile tile, int generation, bool hideArrow, bool imagesAsIcons)
    {
        try
        {
            int pixels = Math.Max(_layout.DecodePixelWidth,
                (int)Math.Ceiling(_layout.ImageSize * (XamlRoot?.RasterizationScale ?? 1)));
            var icon = await IconHelper.GetIconAsync(tile.Item.SourcePath,
                hideShortcutArrowOverlay: hideArrow, showImageFilesAsIcons: imagesAsIcons,
                decodePixelWidth: pixels, cacheScope: "desktop-organization-preview");
            if (_disposed || generation != _iconGeneration || icon is null) return;
            tile.Image.Source = icon;
            tile.Fallback.Visibility = Visibility.Collapsed;
        }
        catch { /* Removed/offline files keep the fallback glyph. */ }
    }

    private void ChangeItemSelection(PreviewTile tile, bool selected)
    {
        if (_updating || _disposed) return;
        if (!_canSelectItems) { UpdateSelectionVisuals(); return; }
        int index = _target.Items.IndexOf(tile.Item);
        bool range = IsKeyDown(VirtualKey.Shift) && _selectionAnchor >= 0;
        if (range)
        {
            for (int i = Math.Min(index, _selectionAnchor); i <= Math.Max(index, _selectionAnchor); i++)
                SetItemSelected(_target.Items[i], selected);
        }
        else { _selectionAnchor = index; SetItemSelected(tile.Item, selected); }
        _selection.IsSelected = _target.Items.Any(item => !_excludedPaths.Contains(item.SourcePath));
        UpdateSelectionVisuals();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetItemSelected(DesktopOrganizationFileSnapshot item, bool selected)
    {
        if (_retainedSelection is not null)
        {
            if (selected) _retainedSelection.Add(item.SourcePath);
            else _retainedSelection.Remove(item.SourcePath);
        }
        else
        {
            if (selected) _excludedPaths.Remove(item.SourcePath);
            else _excludedPaths.Add(item.SourcePath);
        }
    }

    private void Card_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_disposed || !_canSelectItems) return;
        if (e.Key == VirtualKey.A && IsKeyDown(VirtualKey.Control))
        {
            foreach (var item in _target.Items) SetItemSelected(item, true);
            _selection.IsSelected = true;
            UpdateSelectionVisuals();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            int current = _tiles.FindIndex(tile => ReferenceEquals(tile.Button, FocusManager.GetFocusedElement(XamlRoot)));
            if (current < 0) return;
            int delta = e.Key switch { VirtualKey.Left => -1, VirtualKey.Right => 1, VirtualKey.Up => -_columns, _ => _columns };
            int next = Math.Clamp(current + delta, 0, _target.Items.Count - 1);
            if (next >= _visibleCount) { _expanded = true; LayoutItems(force: true); }
            _tiles[next].Button.Focus(FocusState.Keyboard);
            if (IsKeyDown(VirtualKey.Shift))
            {
                if (_selectionAnchor < 0) _selectionAnchor = current;
                for (int i = Math.Min(next, _selectionAnchor); i <= Math.Max(next, _selectionAnchor); i++)
                    SetItemSelected(_target.Items[i], true);
                _selection.IsSelected = true;
                UpdateSelectionVisuals();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static Flyout CreateItemDetails(DesktopOrganizationFileSnapshot item, string description)
    {
        var content = new StackPanel { Spacing = 8, MaxWidth = 360 };
        content.Children.Add(new TextBlock { Text = item.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(description))
            content.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = item.SourcePath, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        return new Flyout { Content = content };
    }

    private void GroupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || _disposed || !_canSelectItems) return;
        if (_retainedSelection is not null)
        {
            foreach (var item in _target.Items)
            {
                if (GroupCheckBox.IsChecked == true) _retainedSelection.Add(item.SourcePath);
                else _retainedSelection.Remove(item.SourcePath);
            }
            UpdateSelectionVisuals();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _selection.IsSelected = GroupCheckBox.IsChecked == true;
        foreach (var item in _target.Items)
        {
            if (_selection.IsSelected) _excludedPaths.Remove(item.SourcePath);
            else _excludedPaths.Add(item.SourcePath);
        }
        UpdateSelectionVisuals();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSelectionVisuals()
    {
        _updating = true;
        try
        {
            int selected = _target.Items.Count(IsItemSelected);
            GroupCheckBox.IsChecked = selected == 0 ? false : selected == _target.Items.Count ? true : null;
            CountText.Text = !_canSelectItems
                ? Format("DesktopOrganization.Preview.ItemCount", _target.Items.Count)
                : Format("DesktopOrganization.Layout.SelectedCount", selected, _target.Items.Count);
            foreach (var tile in _tiles)
            {
                bool include = IsItemSelected(tile.Item);
                tile.Button.IsChecked = include;
                // Selection reads through opacity alone: chosen tiles stay at
                // full strength, the rest fade. Fills read as hover noise.
                tile.Button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                tile.Content.Opacity = !_canSelectItems || include ? 1 : 0.25;
            }
        }
        finally { _updating = false; }
    }

    private bool IsItemSelected(DesktopOrganizationFileSnapshot item) => _canSelectItems &&
        (_retainedSelection is not null ? _retainedSelection.Contains(item.SourcePath)
            : _selection.IsSelected && !_excludedPaths.Contains(item.SourcePath));

    private void ItemsPanel_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutItems();
    private void ExpandButton_Click(object sender, RoutedEventArgs e) { _expanded = !_expanded; LayoutItems(force: true); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _iconGeneration++;
        DetachXamlRoot();
        foreach (var tile in _tiles) tile.Image.Source = null;
        _tiles.Clear();
        ItemsPanel.Children.Clear();
    }

    private static string T(string key) => App.Current.LocalizationService.T(key);
    private static string Format(string key, params object[] args) => App.Current.LocalizationService.Format(key, args);

    private sealed record PreviewTile(DesktopOrganizationFileSnapshot Item, ToggleButton Button,
        Image Image, FontIcon Fallback, StackPanel Content);
    private sealed record PreviewTileAppearance(FileWidgetIconLayout Layout, bool HideShortcutArrow,
        bool ImagesAsIcons, bool ShowFileExtensions, bool HideShortcutExtension, bool ShowPathTooltips,
        double RasterizationScale);
}
