using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeskBox.Controls;

public enum DesktopOrganizationItemViewMode
{
    Tiles,
    CompactGrid
}

/// <summary>A fixed-size (320x216) mini-box preview matching the user's real DeskBox file widget appearance, with inner vertical scrolling when > 8 items.</summary>
public sealed partial class DesktopOrganizationPreviewCard : UserControl, IDisposable
{
    internal const double FixedBoxWidth = 320;
    internal const double FixedBoxHeight = 216;
    private const int FixedGridColumns = 4;
    private const double RestMoreButtonOpacity = 0.52;

    internal static string? ActiveDraggedSourcePath { get; set; }

    internal static IReadOnlyList<string> ActiveDraggedSourcePaths { get; set; } = [];

    internal static readonly HashSet<string> ActiveSelectedSourcePaths = new(StringComparer.OrdinalIgnoreCase);

    internal static event EventHandler<bool>? DragSessionStateChanged;

    internal static event EventHandler? GlobalSelectionVisualsChanged;

    internal static event EventHandler<PointerRoutedEventArgs>? PointerDragMoved;

    internal static event EventHandler<PointerRoutedEventArgs>? PointerDragReleased;

    private readonly DesktopOrganizationTargetPlan _target;
    private readonly DesktopOrganizationTargetSelection _selection;
    private readonly HashSet<string> _excludedPaths;
    private readonly HashSet<string>? _retainedSelection;
    private readonly bool _canSelectItems;
    private readonly Func<DesktopOrganizationFileSnapshot, string>? _itemDescription;
    private readonly List<PreviewTile> _tiles = [];
    private readonly List<RadioMenuFlyoutItem> _destinationRadioItems = [];
    private FileWidgetIconLayout _layout;
    private PreviewTileAppearance? _tileAppearance;
    private DesktopOrganizationItemViewMode _viewMode = DesktopOrganizationItemViewMode.CompactGrid;
    private XamlRoot? _observedXamlRoot;
    private bool _updating;
    private bool _disposed;
    private bool _inlineIncludeEnabled;
    private bool _isCommittingRename;
    private bool _isPointerOverCard;
    private bool _isDestinationFlyoutOpen;
    private PreviewTile? _pressedTile;
    private Windows.Foundation.Point _tileDragStartPoint;
    private bool _isTilePointerDragging;
    private string? _lastClickedSourcePath;
    private int _columns = FixedGridColumns;
    private int _visibleCount = -1;
    private int _iconGeneration;
    internal const string PreviewIconCacheScope = "desktop-organization-preview";
    private const int IconLoadBatchSize = 8;
    private readonly SemaphoreSlim _iconLoadGate = new(IconLoadBatchSize, IconLoadBatchSize);
    private IReadOnlySet<string> _newPaths = new HashSet<string>();
    private readonly List<DesktopOrganizationDestinationOption> _destinations = [];

    public DesktopOrganizationPreviewCard(
        DesktopOrganizationTargetPlan target,
        DesktopOrganizationTargetSelection selection,
        HashSet<string> excludedPaths,
        IReadOnlyList<DesktopOrganizationDestinationOption> destinations,
        DesktopOrganizationItemViewMode viewMode = DesktopOrganizationItemViewMode.CompactGrid)
        : this(target, selection, excludedPaths, destinations, null, true, null, DesktopOrganizationItemViewMode.CompactGrid)
    {
    }

    internal DesktopOrganizationPreviewCard(
        string groupId,
        string title,
        string description,
        IReadOnlyList<DesktopOrganizationFileSnapshot> items,
        HashSet<string> selectedPaths,
        bool canSelect,
        Func<DesktopOrganizationFileSnapshot, string>? itemDescription = null,
        DesktopOrganizationItemViewMode viewMode = DesktopOrganizationItemViewMode.CompactGrid)
        : this(
            new DesktopOrganizationTargetPlan
            {
                SourceBucketId = groupId,
                CategoryId = groupId,
                TargetWidgetId = groupId,
                SuggestedDisplayName = title,
                Items = items.ToList()
            },
            new DesktopOrganizationTargetSelection(),
            [],
            [],
            selectedPaths,
            canSelect,
            itemDescription,
            DesktopOrganizationItemViewMode.CompactGrid)
    {
        DestinationPanel.Visibility = Visibility.Collapsed;
        GroupDescriptionText.Text = description;
        GroupDescriptionText.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(GroupDescriptionText, description);
        if (!canSelect)
        {
            GroupCheckBox.Visibility = Visibility.Collapsed;
            ReadOnlyTitleText.Text = title;
            ReadOnlyHeaderPanel.Visibility = Visibility.Collapsed;
        }
        LayoutItems(force: true);
    }

    private DesktopOrganizationPreviewCard(
        DesktopOrganizationTargetPlan target,
        DesktopOrganizationTargetSelection selection,
        HashSet<string> excludedPaths,
        IReadOnlyList<DesktopOrganizationDestinationOption> destinations,
        HashSet<string>? retainedSelection,
        bool canSelect,
        Func<DesktopOrganizationFileSnapshot, string>? itemDescription,
        DesktopOrganizationItemViewMode viewMode)
    {
        _target = target;
        _selection = selection;
        _excludedPaths = excludedPaths;
        _retainedSelection = retainedSelection;
        _canSelectItems = canSelect;
        _itemDescription = itemDescription;
        _viewMode = DesktopOrganizationItemViewMode.CompactGrid;
        InitializeComponent();

        TitleText.Text = target.SuggestedDisplayName;
        RoutePrefixText.Text = T("DesktopOrganization.Layout.RoutePrefix");
        string categoryGlyph = GetCategoryGlyph(target.CategoryId);
        CategoryIcon.Glyph = categoryGlyph;
        ReadOnlyCategoryIcon.Glyph = categoryGlyph;

        AutomationProperties.SetName(GroupCheckBox, target.SuggestedDisplayName);
        ToolTipService.SetToolTip(
            GroupCheckBox,
            target.SuggestedDisplayName + "\n" + T("DesktopOrganization.Layout.SelectionHelp"));
        AutomationProperties.SetHelpText(GroupCheckBox, T("DesktopOrganization.Layout.SelectionHelp"));
        ToolTipService.SetToolTip(TitleDisplayPanel, target.SuggestedDisplayName);

        foreach (var destination in destinations.Where(item => target.CreatesWidget || item.Id != target.TargetWidgetId))
        {
            _destinations.Add(destination);
        }

        if (!target.CreatesWidget && _destinations.All(item => item.Id != target.TargetWidgetId))
        {
            _destinations.Insert(0, new DesktopOrganizationDestinationOption(
                target.TargetWidgetId,
                target.SuggestedDisplayName,
                target.TargetDirectoryPath,
                false));
        }

        RebuildDestinationFlyout();

        Loaded += Card_Loaded;
        Unloaded += Card_Unloaded;
        ActualThemeChanged += (_, _) => RefreshAppearance();
        GlobalSelectionVisualsChanged += OnGlobalSelectionVisualsChanged;
        AddHandler(KeyDownEvent, new KeyEventHandler(Card_KeyDown), true);
        RefreshAppearance();
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler<bool>? InlineIncludeRequested;

    internal event EventHandler<IReadOnlyList<string>>? FilesDroppedIntoCard;

    internal event EventHandler<string>? BoxTitleRenamed;

    internal event EventHandler? DestinationRouteChanged;

    internal string SourceBucketId => _target.SourceBucketId;

    internal DesktopOrganizationTargetPlan TargetPlan => _target;

    internal static void NotifyDragSessionState(bool isDragging) =>
        DragSessionStateChanged?.Invoke(null, isDragging);

    internal static void NotifyPointerDragMoved(object? sender, PointerRoutedEventArgs e) =>
        PointerDragMoved?.Invoke(sender, e);

    internal static void NotifyPointerDragReleased(object? sender, PointerRoutedEventArgs e) =>
        PointerDragReleased?.Invoke(sender, e);

    internal string EffectiveBoxTitle => GetEffectiveBoxTitle();

    internal void SetDropTargetHighlight(bool highlighted)
    {
        if (_disposed)
        {
            return;
        }

        if (highlighted)
        {
            CardOutline.BorderBrush = ResolveThemeBrush("AccentFillColorDefaultBrush");
            CardOutline.BorderThickness = new Thickness(1.5);
        }
        else
        {
            ApplyMaterial();
        }
    }

    internal static void ClearActiveTileSelection()
    {
        if (ActiveSelectedSourcePaths.Count == 0)
        {
            return;
        }

        ActiveSelectedSourcePaths.Clear();
        GlobalSelectionVisualsChanged?.Invoke(null, EventArgs.Empty);
    }

    internal void EnableInlineIncludeAction()
    {
        _inlineIncludeEnabled = true;
        InlineIncludeButton.Visibility = Visibility.Visible;
        UpdateSelectionVisuals();
    }

    internal void SetViewMode(DesktopOrganizationItemViewMode mode)
    {
        _viewMode = DesktopOrganizationItemViewMode.CompactGrid;
    }

    internal void SetNewItems(IReadOnlySet<string> paths)
    {
        _newPaths = paths;
        _tileAppearance = null;
        RefreshAppearance();
    }

    private long _titleRenameOpenedAtTick;

    internal void BeginInlineRename()
    {
        if (_disposed || !_canSelectItems || _retainedSelection is not null)
        {
            return;
        }

        TitleRenameBox.Text = GetEffectiveBoxTitle();
        TitleDisplayPanel.Visibility = Visibility.Collapsed;
        TitleRenameBox.Visibility = Visibility.Visible;
        _titleRenameOpenedAtTick = Environment.TickCount64;
        DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(TitleRenameBox);
        DeskBox.Helpers.InlineEditorFocus.FocusWhenLoaded(
            TitleRenameBox,
            static box => box.SelectAll(),
            DispatcherQueue,
            "DesktopOrganizationCardRename");
    }

    internal string GetEffectiveBoxTitle()
    {
        var destination = _selection.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget
            ? _destinations.FirstOrDefault(item => item.Id == _selection.ExistingWidgetId)
            : null;
        return destination?.DisplayName ?? _target.SuggestedDisplayName;
    }

    private void OnGlobalSelectionVisualsChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            UpdateSelectionVisuals();
        }
    }

    private void RebuildDestinationFlyout()
    {
        DestinationFlyout.Items.Clear();
        _destinationRadioItems.Clear();

        if (_canSelectItems && _retainedSelection is null)
        {
            var renameItem = new MenuFlyoutItem
            {
                Text = T("Common.Rename"),
                Icon = new FontIcon
                {
                    Glyph = "\uE70F",
                    FontSize = 14
                }
            };
            renameItem.Click += (_, _) => BeginInlineRename();
            DestinationFlyout.Items.Add(renameItem);
        }

        var destinationSubItem = new MenuFlyoutSubItem
        {
            Text = T("DesktopOrganization.Layout.ChangeDestination"),
            Icon = new FontIcon
            {
                Glyph = "\uE8B7",
                FontSize = 14
            }
        };

        if (_target.CreatesWidget)
        {
            AddDestinationRadioItem(destinationSubItem, null);
        }

        foreach (var destination in _destinations)
        {
            AddDestinationRadioItem(destinationSubItem, destination);
        }

        if (destinationSubItem.Items.Count > 0)
        {
            DestinationFlyout.Items.Add(destinationSubItem);
        }

        if (_canSelectItems && _retainedSelection is null)
        {
            DestinationFlyout.Items.Add(new MenuFlyoutSeparator());
            var excludeBoxItem = new MenuFlyoutItem
            {
                Text = T("DesktopOrganization.Layout.KeptTitle"),
                Icon = new FontIcon
                {
                    Glyph = "\uE7F8",
                    FontSize = 14
                }
            };
            excludeBoxItem.Click += (_, _) => ExcludeAllItemsInBox();
            DestinationFlyout.Items.Add(excludeBoxItem);
        }
    }

    private void ExcludeAllItemsInBox()
    {
        if (_disposed || !_canSelectItems || _retainedSelection is not null)
        {
            return;
        }

        foreach (var item in _target.Items)
        {
            _excludedPaths.Add(item.SourcePath);
            ActiveSelectedSourcePaths.Remove(item.SourcePath);
        }

        _selection.IsSelected = false;
        LayoutItems(force: true);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddDestinationRadioItem(MenuFlyoutSubItem parentSubItem, DesktopOrganizationDestinationOption? option)
    {
        if (_target.CreatesWidget && option is not null &&
            parentSubItem.Items.OfType<RadioMenuFlyoutItem>().Count() == 1)
        {
            parentSubItem.Items.Add(new MenuFlyoutSeparator());
        }

        var item = new RadioMenuFlyoutItem
        {
            Text = option is null
                ? Format("DesktopOrganization.Layout.NewDestination", _target.SuggestedDisplayName)
                : option.DisplayName,
            Tag = option,
            GroupName = _target.SourceBucketId
        };
        ToolTipService.SetToolTip(item, option?.DirectoryPath ?? _target.TargetDirectoryPath);
        item.Click += (_, _) =>
        {
            _selection.DestinationMode = option is null
                ? DesktopOrganizationDestinationMode.Dynamic
                : DesktopOrganizationDestinationMode.ExistingWidget;
            _selection.ExistingWidgetId = option?.Id;
            _tileAppearance = null;
            RefreshAppearance();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            DestinationRouteChanged?.Invoke(this, EventArgs.Empty);
        };
        _destinationRadioItems.Add(item);
        parentSubItem.Items.Add(item);
    }

    internal void RefreshAppearance()
    {
        if (_disposed)
        {
            return;
        }

        var settings = App.Current.SettingsService.Settings;
        WidgetConfig? config = settings.Widgets.FirstOrDefault(item =>
            item.Id == (_selection.ExistingWidgetId ?? _target.TargetWidgetId));
        _layout = FileWidgetIconLayout.Calculate(
            settings,
            config?.IconSizeOverride,
            WindowsCompatibilityService.ResolveSystemTextScaleFactor());
        var tileAppearance = new PreviewTileAppearance(
            _viewMode,
            _layout,
            settings.HideShortcutArrowOverlay,
            settings.ShowImageFilesAsIcons,
            settings.ShowFileExtensions,
            settings.HideShortcutExtensionWhenShowingFileExtensions,
            settings.ShowFileItemPathTooltips,
            XamlRoot?.RasterizationScale ?? 1,
            ActualTheme);
        if (_tileAppearance != tileAppearance)
        {
            _tileAppearance = tileAppearance;
            _iconGeneration++;
            foreach (PreviewTile tile in _tiles)
            {
                tile.Image.Source = null;
            }

            _tiles.Clear();
            ItemsPanel.Children.Clear();
            _visibleCount = -1;
        }

        ApplyMaterial();
        UpdateDestinationVisuals();
        LayoutItems(force: true);
    }

    private bool ResolveCreatesNewWidget()
    {
        var destination = _selection.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget
            ? _destinations.FirstOrDefault(item => item.Id == _selection.ExistingWidgetId)
            : null;
        return destination is null && _target.CreatesWidget;
    }

    private void UpdateDestinationVisuals()
    {
        if (DestinationPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var destination = _selection.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget
            ? _destinations.FirstOrDefault(item => item.Id == _selection.ExistingWidgetId)
            : null;
        string targetTitle = destination?.DisplayName ?? _target.SuggestedDisplayName;
        TitleText.Text = targetTitle;
        DestinationText.Text = targetTitle;
        ToolTipService.SetToolTip(TitleDisplayPanel, targetTitle);

        string targetDirectory = destination?.DirectoryPath ?? _target.TargetDirectoryPath;
        ToolTipService.SetToolTip(DestinationButton, T("DesktopOrganization.Layout.More") + "\n" + targetDirectory);
        AutomationProperties.SetName(DestinationButton, targetTitle);

        foreach (var menuItem in _destinationRadioItems)
        {
            menuItem.IsChecked = (menuItem.Tag as DesktopOrganizationDestinationOption)?.Id == destination?.Id;
        }
    }

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_observedXamlRoot is not null)
        {
            _observedXamlRoot.Changed -= Card_XamlRootChanged;
        }

        _observedXamlRoot = XamlRoot;
        if (_observedXamlRoot is not null)
        {
            _observedXamlRoot.Changed += Card_XamlRootChanged;
        }

        RefreshAppearance();
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachXamlRoot();
    }

    private void Card_XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshAppearance();

    private void DetachXamlRoot()
    {
        if (_observedXamlRoot is not null)
        {
            _observedXamlRoot.Changed -= Card_XamlRootChanged;
        }

        _observedXamlRoot = null;
    }

    private Brush ResolveThemeBrush(string resourceKey) =>
        NeutralInteractionBrush.ResolveThemedResource(resourceKey, this) ??
        new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

    private bool IsDarkEffectiveTheme() =>
        ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => App.Current.ThemeService.EffectiveTheme == ElementTheme.Dark
        };

    private void ApplyMaterial()
    {
        AppSettings appSettings = App.Current.SettingsService.Settings;
        bool isDark = IsDarkEffectiveTheme();
        Windows.UI.Color accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;

        double cornerRadius = WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
            WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                appSettings.WidgetCornerPreference));
        var corner = new CornerRadius(cornerRadius);
        MaterialSurface.CornerRadius = FallbackSurface.CornerRadius = CardOutline.CornerRadius = corner;
        MaterialSurface.Visibility = Visibility.Collapsed;
        FallbackSurface.Visibility = Visibility.Visible;

        string materialType = appSettings.WidgetMaterialType;
        double surfaceOpacity = appSettings.WidgetOpacity;
        double materialIntensity = appSettings.WidgetMaterialIntensity;

        Windows.UI.Color surfaceColor;
        if (SettingsService.IsMicaMaterial(materialType))
        {
            surfaceColor = WidgetMaterialVisualCalculator.BuildEmbeddedMicaTintOverlayColor(
                isDark,
                accentColor,
                materialType == SettingsService.WidgetMaterialTypeMicaAlt,
                materialIntensity);
        }
        else if (SettingsService.IsAcrylicMaterial(materialType))
        {
            surfaceColor = WidgetMaterialVisualCalculator.BuildLegacyAcrylicSurfaceOverlayColor(
                isDark,
                accentColor,
                materialType == SettingsService.WidgetMaterialTypeAcrylicBase,
                surfaceOpacity,
                materialIntensity);
        }
        else
        {
            surfaceColor = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                isDark,
                accentColor,
                surfaceOpacity);
        }

        CardOutline.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        FallbackSurface.Background = new SolidColorBrush(surfaceColor);

        WidgetBorderVisuals borderVisuals = WidgetBorderVisualCalculator.Resolve(
            appSettings.WidgetBorderStyle,
            appSettings.WidgetBorderColorMode,
            isDark,
            accentColor);
        if (borderVisuals.Thickness <= 0 || borderVisuals.BorderColor.A == 0)
        {
            byte ch = isDark ? (byte)0xFF : (byte)0x00;
            byte alpha = isDark ? (byte)0x24 : (byte)0x18;
            borderVisuals = new WidgetBorderVisuals(
                1.0,
                Windows.UI.Color.FromArgb(alpha, ch, ch, ch),
                borderVisuals.DividerColor);
        }

        CardOutline.BorderThickness = new Thickness(borderVisuals.Thickness);
        CardOutline.BorderBrush = new SolidColorBrush(borderVisuals.BorderColor);
        HeaderDivider.BorderBrush = borderVisuals.DividerColor.A > 0
            ? new SolidColorBrush(borderVisuals.DividerColor)
            : ResolveThemeBrush("DividerStrokeColorDefaultBrush");
    }

    private static string GetCategoryGlyph(string categoryId) =>
        categoryId switch
        {
            DesktopOrganizationCategoryIds.Shortcuts => "\uE71D",
            DesktopOrganizationCategoryIds.Design => "\uE790",
            DesktopOrganizationCategoryIds.Documents => "\uE8A5",
            DesktopOrganizationCategoryIds.Images => "\uEB9F",
            DesktopOrganizationCategoryIds.Media => "\uE768",
            DesktopOrganizationCategoryIds.Packages => "\uE7B8",
            _ => "\uE8B7"
        };

    private void LayoutItems(bool force = false)
    {
        if (_disposed || _layout.CellWidth <= 0)
        {
            return;
        }

        CardRootGrid.Width = FixedBoxWidth;
        CardRootGrid.Height = FixedBoxHeight;

        bool isTargetBox = _canSelectItems && _retainedSelection is null;
        IReadOnlyList<DesktopOrganizationFileSnapshot> activeIncoming = isTargetBox
            ? _target.Items.Where(IsItemSelected).ToList()
            : _target.Items;

        int totalCells = activeIncoming.Count;
        const int columns = FixedGridColumns;

        if (!force && columns == _columns && totalCells == _visibleCount && _tiles.Count == totalCells)
        {
            UpdateSelectionVisuals();
            return;
        }

        _columns = columns;
        _visibleCount = totalCells;
        _iconGeneration++;
        foreach (PreviewTile tile in _tiles)
        {
            tile.Image.Source = null;
        }

        _tiles.Clear();
        foreach (var item in activeIncoming)
        {
            _tiles.Add(CreateTile(item));
        }

        ItemsPanel.Children.Clear();
        ItemsPanel.ColumnDefinitions.Clear();
        ItemsPanel.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++)
        {
            ItemsPanel.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }

        int rowCount = Math.Max(1, (int)Math.Ceiling(totalCells / (double)columns));
        for (int i = 0; i < rowCount; i++)
        {
            ItemsPanel.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(_tiles.Count == 0 ? 156 : 80)
            });
        }

        if (_tiles.Count == 0)
        {
            var emptyHint = new TextBlock
            {
                Text = T("DesktopOrganization.Preview.NoItems"),
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ResolveThemeBrush("TextFillColorTertiaryBrush")
            };
            Grid.SetColumnSpan(emptyHint, columns);
            ItemsPanel.Children.Add(emptyHint);
        }

        for (int i = 0; i < _tiles.Count; i++)
        {
            var button = _tiles[i].Button;
            Grid.SetColumn(button, i % columns);
            Grid.SetRow(button, i / columns);
            ItemsPanel.Children.Add(button);
        }

        UpdateSelectionVisuals();
    }

    private PreviewTile CreateTile(DesktopOrganizationFileSnapshot item)
    {
        var settings = App.Current.SettingsService.Settings;
        const double iconSize = 32;
        var image = new Image
        {
            Width = iconSize,
            Height = iconSize,
            Stretch = Stretch.Uniform
        };
        var fallback = new FontIcon
        {
            Glyph = item.IsDirectory ? "\uE8B8" : "\uE7C3",
            FontSize = iconSize * 0.80,
            Width = iconSize,
            Height = iconSize,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush")
        };
        var iconHost = new Grid
        {
            Width = iconSize,
            Height = iconSize,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconHost.Children.Add(image);
        iconHost.Children.Add(fallback);

        string displayName = FileService.GetDisplayName(
            item.SourcePath,
            item.IsDirectory,
            settings.ShowFileExtensions,
            settings.HideShortcutExtensionWhenShowingFileExtensions);
        string description = _itemDescription?.Invoke(item) ?? string.Empty;

        var checkIcon = new FontIcon { Glyph = "\uE73E", FontSize = 10, Visibility = Visibility.Collapsed };
        var checkIndicator = new Border { Width = 18, Height = 18, Visibility = Visibility.Collapsed, Child = checkIcon };
        var subtitleText = new TextBlock { Visibility = Visibility.Collapsed };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Spacing = 5,
            Padding = new Thickness(4, 6, 4, 4)
        };
        stack.Children.Add(iconHost);
        stack.Children.Add(new TextBlock
        {
            Text = displayName,
            FontSize = 12,
            LineHeight = 16,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = ResolveThemeBrush("TextFillColorPrimaryBrush")
        });

        var gridCell = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        gridCell.Children.Add(stack);
        gridCell.Children.Add(checkIndicator);
        FrameworkElement contentRoot = gridCell;

        var button = new ToggleButton
        {
            Content = contentRoot,
            Style = (Style)Resources["PreviewFileToggleStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CanDrag = false
        };

        string label = item.Name + (item.SourceScope == DesktopOrganizationSourceScope.Public
            ? " · " + T("DesktopOrganization.Public.Badge")
            : string.Empty);
        AutomationProperties.SetName(
            button,
            string.IsNullOrWhiteSpace(description) ? label : label + " · " + description);
        string tooltip = settings.ShowFileItemPathTooltips ? item.SourcePath : label;
        if (!string.IsNullOrWhiteSpace(description))
        {
            tooltip += "\n" + description;
        }
        ToolTipService.SetToolTip(button, tooltip);

        var tile = new PreviewTile(
            item,
            description,
            button,
            image,
            fallback,
            contentRoot,
            subtitleText,
            checkIndicator,
            checkIcon,
            iconSize);

        if (!_canSelectItems)
        {
            button.Click += (_, _) =>
            {
                button.IsChecked = false;
                CreateItemDetails(item, description).ShowAt(button);
            };
        }
        else if (_retainedSelection is null)
        {
            button.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler((_, e) => OnTilePointerPressed(tile, e)),
                handledEventsToo: true);
            button.AddHandler(
                UIElement.PointerMovedEvent,
                new PointerEventHandler((_, e) => OnTilePointerMoved(tile, e)),
                handledEventsToo: true);
            button.AddHandler(
                UIElement.PointerReleasedEvent,
                new PointerEventHandler((_, e) => OnTilePointerReleased(tile, e)),
                handledEventsToo: true);
            button.AddHandler(
                UIElement.PointerCanceledEvent,
                new PointerEventHandler((_, _) => CancelTilePointerDrag()),
                handledEventsToo: true);
            button.AddHandler(
                UIElement.PointerCaptureLostEvent,
                new PointerEventHandler((_, _) => CancelTilePointerDrag()),
                handledEventsToo: true);
            button.Click += (_, _) => UpdateSelectionVisuals();

            var contextMenu = new MenuFlyout();
            var excludeItem = new MenuFlyoutItem
            {
                Text = T("DesktopOrganization.Layout.KeptTitle"),
                Icon = new FontIcon { Glyph = "\uE7F8", FontSize = 14 }
            };
            excludeItem.Click += (_, _) => ExcludeTileOrSelectedBatch(tile);
            contextMenu.Items.Add(excludeItem);

            var detailsItem = new MenuFlyoutItem
            {
                Text = T("DesktopOrganization.Layout.Details"),
                Icon = new FontIcon { Glyph = "\uE946", FontSize = 14 }
            };
            detailsItem.Click += (_, _) => CreateItemDetails(item, description).ShowAt(button);
            contextMenu.Items.Add(detailsItem);
            button.ContextFlyout = contextMenu;
        }

        _ = LoadIconAsync(tile, _iconGeneration, settings.HideShortcutArrowOverlay, settings.ShowImageFilesAsIcons);
        return tile;
    }

    private void OnTilePointerPressed(PreviewTile tile, PointerRoutedEventArgs e)
    {
        if (_updating || _disposed || !_canSelectItems || _retainedSelection is not null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressedTile = tile;
        _tileDragStartPoint = point.Position;
        _isTilePointerDragging = false;
    }

    private void OnTilePointerMoved(PreviewTile tile, PointerRoutedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(_pressedTile, tile))
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            CancelTilePointerDrag();
            return;
        }

        if (!_isTilePointerDragging)
        {
            double dx = point.Position.X - _tileDragStartPoint.X;
            double dy = point.Position.Y - _tileDragStartPoint.Y;
            if ((dx * dx) + (dy * dy) < 25)
            {
                return;
            }

            _isTilePointerDragging = true;
            tile.Button.CapturePointer(e.Pointer);

            if (!ActiveSelectedSourcePaths.Contains(tile.Item.SourcePath))
            {
                ActiveSelectedSourcePaths.Clear();
                ActiveSelectedSourcePaths.Add(tile.Item.SourcePath);
                GlobalSelectionVisualsChanged?.Invoke(this, EventArgs.Empty);
            }

            List<string> batch = ActiveSelectedSourcePaths.ToList();
            ActiveDraggedSourcePath = tile.Item.SourcePath;
            ActiveDraggedSourcePaths = batch;
            NotifyDragSessionState(true);
        }

        NotifyPointerDragMoved(this, e);
    }

    private void OnTilePointerReleased(PreviewTile tile, PointerRoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!ReferenceEquals(_pressedTile, tile))
        {
            UpdateSelectionVisuals();
            return;
        }

        bool wasDragging = _isTilePointerDragging;
        _pressedTile = null;
        _isTilePointerDragging = false;
        tile.Button.ReleasePointerCapture(e.Pointer);

        if (wasDragging)
        {
            NotifyPointerDragReleased(this, e);
            ActiveDraggedSourcePath = null;
            ActiveDraggedSourcePaths = [];
            NotifyDragSessionState(false);
            UpdateSelectionVisuals();
            return;
        }

        HandleTileClickSelection(tile);
    }

    private void CancelTilePointerDrag()
    {
        bool wasDragging = _isTilePointerDragging;
        _pressedTile = null;
        _isTilePointerDragging = false;
        if (wasDragging)
        {
            ActiveDraggedSourcePath = null;
            ActiveDraggedSourcePaths = [];
            NotifyDragSessionState(false);
        }

        UpdateSelectionVisuals();
    }

    private void CardRootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_disposed || !_canSelectItems || _retainedSelection is not null)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source)
        {
            DependencyObject? current = source;
            while (current is not null && !ReferenceEquals(current, CardRootGrid))
            {
                if (current is ButtonBase or TextBox)
                {
                    return;
                }

                current = VisualTreeHelper.GetParent(current);
            }
        }

        if (ActiveSelectedSourcePaths.Count > 0)
        {
            ClearActiveTileSelection();
        }
    }

    private void HandleTileClickSelection(PreviewTile tile)
    {
        if (_updating || _disposed || !_canSelectItems || _retainedSelection is not null)
        {
            return;
        }

        bool shift = IsKeyDown(VirtualKey.Shift);
        if (shift && !string.IsNullOrEmpty(_lastClickedSourcePath))
        {
            int anchorIdx = _tiles.FindIndex(t =>
                string.Equals(t.Item.SourcePath, _lastClickedSourcePath, StringComparison.OrdinalIgnoreCase));
            int currentIdx = _tiles.IndexOf(tile);
            if (anchorIdx >= 0 && currentIdx >= 0)
            {
                int start = Math.Min(anchorIdx, currentIdx);
                int end = Math.Max(anchorIdx, currentIdx);
                for (int i = start; i <= end; i++)
                {
                    ActiveSelectedSourcePaths.Add(_tiles[i].Item.SourcePath);
                }

                GlobalSelectionVisualsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        if (!ActiveSelectedSourcePaths.Remove(tile.Item.SourcePath))
        {
            ActiveSelectedSourcePaths.Add(tile.Item.SourcePath);
        }

        _lastClickedSourcePath = tile.Item.SourcePath;
        GlobalSelectionVisualsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExcludeTileOrSelectedBatch(PreviewTile tile)
    {
        if (_updating || _disposed || !_canSelectItems || _retainedSelection is not null)
        {
            return;
        }

        List<string> pathsToExclude = ActiveSelectedSourcePaths.Contains(tile.Item.SourcePath) && ActiveSelectedSourcePaths.Count > 1
            ? ActiveSelectedSourcePaths.ToList()
            : [tile.Item.SourcePath];

        foreach (string path in pathsToExclude)
        {
            _excludedPaths.Add(path);
            ActiveSelectedSourcePaths.Remove(path);
        }

        _selection.IsSelected = _target.Items.Any(item => !_excludedPaths.Contains(item.SourcePath));
        LayoutItems(force: true);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadIconAsync(PreviewTile tile, int generation, bool hideArrow, bool imagesAsIcons)
    {
        try
        {
            await _iconLoadGate.WaitAsync();
            try
            {
                if (_disposed || generation != _iconGeneration)
                {
                    return;
                }

                int pixels = Math.Max(
                    _layout.DecodePixelWidth,
                    (int)Math.Ceiling(tile.IconSize * (XamlRoot?.RasterizationScale ?? 1)));
                var icon = await IconHelper.GetIconAsync(
                    tile.Item.SourcePath,
                    hideShortcutArrowOverlay: hideArrow,
                    showImageFilesAsIcons: imagesAsIcons,
                    decodePixelWidth: pixels,
                    cacheScope: PreviewIconCacheScope);
                if (_disposed || generation != _iconGeneration || icon is null)
                {
                    return;
                }

                tile.Image.Source = icon;
                tile.Fallback.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _iconLoadGate.Release();
            }
        }
        catch
        {
            // Removed/offline files keep the fallback glyph.
        }
    }

    private void Card_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_disposed || !_canSelectItems || TitleRenameBox.Visibility == Visibility.Visible)
        {
            return;
        }

        if (e.Key is VirtualKey.Delete or VirtualKey.Back && ActiveSelectedSourcePaths.Count > 0)
        {
            foreach (string path in ActiveSelectedSourcePaths.ToList())
            {
                _excludedPaths.Add(path);
            }

            ActiveSelectedSourcePaths.Clear();
            _selection.IsSelected = _target.Items.Any(item => !_excludedPaths.Contains(item.SourcePath));
            LayoutItems(force: true);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            int current = _tiles.FindIndex(tile =>
                ReferenceEquals(tile.Button, FocusManager.GetFocusedElement(XamlRoot)));
            if (current < 0 || _columns <= 0 || _tiles.Count == 0)
            {
                return;
            }

            int delta = e.Key switch
            {
                VirtualKey.Left => -1,
                VirtualKey.Right => 1,
                VirtualKey.Up => -_columns,
                _ => _columns
            };
            int next = Math.Clamp(current + delta, 0, _tiles.Count - 1);
            _tiles[next].Button.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    private void CardRootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverCard = true;
        DestinationButton.Opacity = 1;
    }

    private void CardRootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverCard = false;
        if (!_isDestinationFlyoutOpen)
        {
            DestinationButton.Opacity = RestMoreButtonOpacity;
        }
    }

    private void DestinationFlyout_Opened(object? sender, object e)
    {
        _isDestinationFlyoutOpen = true;
        DestinationButton.Opacity = 1;
    }

    private void DestinationFlyout_Closed(object? sender, object e)
    {
        _isDestinationFlyoutOpen = false;
        if (!_isPointerOverCard)
        {
            DestinationButton.Opacity = RestMoreButtonOpacity;
        }
    }

    private void TitleDisplayPanel_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        BeginInlineRename();
    }

    private void TitleRenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DeskBox.Helpers.TransientInputFocusGuard.ShouldDeferInlineEditorBlurCommit(
                sender as Control,
                _titleRenameOpenedAtTick,
                default,
                () => !_disposed && TitleRenameBox.Visibility == Visibility.Visible && !_isCommittingRename,
                () =>
                {
                    CommitInlineRename(cancel: false);
                    return Task.CompletedTask;
                }))
        {
            return;
        }

        CommitInlineRename(cancel: false);
    }

    private void TitleRenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitInlineRename(cancel: false);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CommitInlineRename(cancel: true);
        }
    }

    private void CommitInlineRename(bool cancel)
    {
        if (_isCommittingRename || TitleRenameBox.Visibility != Visibility.Visible)
        {
            return;
        }

        _isCommittingRename = true;
        try
        {
            string candidate = TitleRenameBox.Text.Trim();
            TitleRenameBox.Visibility = Visibility.Collapsed;
            TitleDisplayPanel.Visibility = Visibility.Visible;

            if (!cancel &&
                !string.IsNullOrWhiteSpace(candidate) &&
                !string.Equals(candidate, GetEffectiveBoxTitle(), StringComparison.Ordinal))
            {
                BoxTitleRenamed?.Invoke(this, candidate);
            }
        }
        finally
        {
            _isCommittingRename = false;
        }
    }

    private void CardRootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (_disposed || !_canSelectItems || _retainedSelection is not null ||
            (ActiveDraggedSourcePaths.Count == 0 && string.IsNullOrWhiteSpace(ActiveDraggedSourcePath)))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = GetEffectiveBoxTitle();
        e.DragUIOverride.IsCaptionVisible = true;
        CardOutline.BorderBrush = ResolveThemeBrush("AccentFillColorDefaultBrush");
        CardOutline.BorderThickness = new Thickness(1.5);
    }

    private void CardRootGrid_DragLeave(object sender, DragEventArgs e)
    {
        if (!_disposed)
        {
            ApplyMaterial();
        }
    }

    private async void CardRootGrid_Drop(object sender, DragEventArgs e)
    {
        if (_disposed || !_canSelectItems || _retainedSelection is not null ||
            DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        ApplyMaterial();
        List<string> droppedPaths = ActiveDraggedSourcePaths.Count > 0
            ? ActiveDraggedSourcePaths.ToList()
            : !string.IsNullOrWhiteSpace(ActiveDraggedSourcePath)
                ? [ActiveDraggedSourcePath!]
                : [];

        if (droppedPaths.Count == 0 && e.DataView.Contains(StandardDataFormats.Text))
        {
            try
            {
                string raw = await e.DataView.GetTextAsync();
                droppedPaths = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            catch
            {
                droppedPaths = [];
            }
        }

        ActiveDraggedSourcePath = null;
        ActiveDraggedSourcePaths = [];
        NotifyDragSessionState(false);

        if (droppedPaths.Count > 0)
        {
            FilesDroppedIntoCard?.Invoke(this, droppedPaths);
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static Flyout CreateItemDetails(DesktopOrganizationFileSnapshot item, string description)
    {
        var content = new StackPanel { Spacing = 8, MaxWidth = 360 };
        content.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            content.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = item.SourcePath,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        return new Flyout { Content = content };
    }

    private void GroupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
    }

    private void InlineIncludeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_inlineIncludeEnabled)
        {
            return;
        }

        int selectedCount = _target.Items.Count(IsItemSelected);
        InlineIncludeRequested?.Invoke(this, selectedCount == 0);
    }

    private void UpdateSelectionVisuals()
    {
        _updating = true;
        try
        {
            int selected = _target.Items.Count(IsItemSelected);
            GroupCheckBox.IsChecked = selected == 0
                ? false
                : selected == _target.Items.Count ? true : null;

            CountText.Inlines.Clear();
            if (!_canSelectItems)
            {
                CountText.Text = Format("DesktopOrganization.Preview.ItemCount", _target.Items.Count);
            }
            else if (!ResolveCreatesNewWidget())
            {
                CountText.Inlines.Add(new Run
                {
                    Text = T("DesktopOrganization.Layout.RouteBadgeExisting") + " · "
                });
                CountText.Inlines.Add(new Run
                {
                    Text = $"+{selected}",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ResolveThemeBrush("AccentTextFillColorPrimaryBrush")
                });
            }
            else
            {
                CountText.Text = T("DesktopOrganization.Layout.RouteBadgeNew") + " · " +
                                 Format("DesktopOrganization.Preview.ItemCount", selected);
            }

            if (_inlineIncludeEnabled)
            {
                InlineIncludeText.Text = selected > 0
                    ? Format("DesktopOrganization.Layout.IncludeCount", selected)
                    : Format("DesktopOrganization.Layout.IncludeAllCount", _target.Items.Count);
            }

            bool isDark = IsDarkEffectiveTheme();
            Windows.UI.Color accent = App.Current.ThemeService?.GetEffectiveAccentColor()
                ?? AccentColorHelper.DefaultAccentColor;
            Brush transparentSurface = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Brush selectedTileSurface = new SolidColorBrush(
                Windows.UI.Color.FromArgb(isDark ? (byte)0x2E : (byte)0x22, accent.R, accent.G, accent.B));
            Brush selectedTileBorder = new SolidColorBrush(
                Windows.UI.Color.FromArgb(isDark ? (byte)0x8C : (byte)0x78, accent.R, accent.G, accent.B));

            foreach (var tile in _tiles)
            {
                bool isTileHighlighted = ActiveSelectedSourcePaths.Contains(tile.Item.SourcePath);
                tile.Button.IsChecked = isTileHighlighted;
                tile.Button.Background = isTileHighlighted ? selectedTileSurface : transparentSurface;
                tile.Button.BorderBrush = isTileHighlighted ? selectedTileBorder : transparentSurface;
                tile.ContentRoot.Opacity = 1.0;
                tile.CheckIndicator.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private bool IsItemSelected(DesktopOrganizationFileSnapshot item) =>
        _canSelectItems &&
        (_retainedSelection is not null
            ? _retainedSelection.Contains(item.SourcePath)
            : _selection.IsSelected && !_excludedPaths.Contains(item.SourcePath));

    private void ItemsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_isTilePointerDragging || _pressedTile is not null)
        {
            _pressedTile = null;
            _isTilePointerDragging = false;
            ActiveDraggedSourcePath = null;
            ActiveDraggedSourcePaths = [];
            NotifyDragSessionState(false);
        }

        _iconGeneration++;
        GlobalSelectionVisualsChanged -= OnGlobalSelectionVisualsChanged;
        DetachXamlRoot();
        foreach (var tile in _tiles)
        {
            tile.Image.Source = null;
        }

        _tiles.Clear();
        ItemsPanel.Children.Clear();
    }

    private static string T(string key) => App.Current.LocalizationService.T(key);

    private static string Format(string key, params object[] args) =>
        App.Current.LocalizationService.Format(key, args);

    private sealed record PreviewTile(
        DesktopOrganizationFileSnapshot Item,
        string Description,
        ToggleButton Button,
        Image Image,
        FontIcon Fallback,
        FrameworkElement ContentRoot,
        TextBlock Subtitle,
        Border CheckIndicator,
        FontIcon CheckIcon,
        double IconSize);

    private sealed record PreviewTileAppearance(
        DesktopOrganizationItemViewMode ViewMode,
        FileWidgetIconLayout Layout,
        bool HideShortcutArrow,
        bool ImagesAsIcons,
        bool ShowFileExtensions,
        bool HideShortcutExtension,
        bool ShowPathTooltips,
        double RasterizationScale,
        ElementTheme Theme);
}
