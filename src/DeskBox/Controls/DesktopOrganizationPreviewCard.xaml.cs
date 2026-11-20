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
using Windows.System;

namespace DeskBox.Controls;

/// <summary>
/// A fixed-size (320x216) mini-box preview matching the user's real DeskBox file
/// widget appearance, with inner vertical scrolling when items exceed one page.
/// </summary>
public sealed partial class DesktopOrganizationPreviewCard : UserControl, IDisposable
{
    internal const double FixedBoxWidth = 320;
    internal const double FixedBoxHeight = 216;
    internal const string PreviewIconCacheScope = "desktop-organization-preview";
    private const int DefaultGridColumns = 4;
    private const double InnerItemsContentWidth = 296;
    private const double RestMoreButtonOpacity = 0.52;
    private const int IconLoadBatchSize = 8;

    private readonly DesktopOrganizationTargetPlan _target;
    private readonly DesktopOrganizationWorkbenchSession _session;
    private readonly DesktopOrganizationPointerDragController _dragController;
    private readonly Func<DesktopOrganizationFileSnapshot, string>? _itemDescription;
    private readonly bool _readOnlyMode;
    private readonly List<PreviewTile> _tiles = [];
    private readonly List<RadioMenuFlyoutItem> _destinationRadioItems = [];
    private readonly List<DesktopOrganizationDestinationOption> _destinations = [];
    private readonly SemaphoreSlim _iconLoadGate = new(IconLoadBatchSize, IconLoadBatchSize);

    private FileWidgetIconLayout _layout;
    private PreviewTileAppearance? _tileAppearance;
    private XamlRoot? _observedXamlRoot;
    private Brush? _selectedTileSurfaceBrush;
    private Brush? _selectedTileBorderBrush;
    private Brush? _hoverTileSurfaceBrush;
    private Brush? _hoverTileBorderBrush;
    private Brush? _transparentBrush;

    private bool _disposed;
    private bool _isCommittingRename;
    private bool _isPointerOverCard;
    private bool _isDestinationFlyoutOpen;
    private long _titleRenameOpenedAtTick;
    private int _columns = DefaultGridColumns;
    private int _visibleCount = -1;
    private int _iconGeneration;

    internal DesktopOrganizationPreviewCard(
        DesktopOrganizationTargetPlan target,
        DesktopOrganizationWorkbenchSession session,
        DesktopOrganizationPointerDragController dragController,
        IReadOnlyList<DesktopOrganizationDestinationOption> destinations,
        Func<DesktopOrganizationFileSnapshot, string>? itemDescription = null,
        bool readOnlyMode = false)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dragController = dragController ?? throw new ArgumentNullException(nameof(dragController));
        _itemDescription = itemDescription;
        _readOnlyMode = readOnlyMode;

        InitializeComponent();

        BucketKey = DesktopOrganizationWorkbenchSession.GetStableBucketKey(target);
        TitleText.Text = target.SuggestedDisplayName;
        ToolTipService.SetToolTip(TitleDisplayPanel, target.SuggestedDisplayName);

        if (_readOnlyMode)
        {
            DestinationPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            DesktopOrganizationTargetSelection? currentSelection =
                _session.TargetSelections.TryGetValue(BucketKey, out DesktopOrganizationTargetSelection? sel)
                    ? sel
                    : null;
            string? selectedExistingWidgetId =
                currentSelection?.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget
                    ? currentSelection.ExistingWidgetId
                    : !target.CreatesWidget
                        ? target.TargetWidgetId
                        : null;

            foreach (DesktopOrganizationDestinationOption destination in destinations.Where(
                         item => target.CreatesWidget || item.Id != target.TargetWidgetId))
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

            RebuildDestinationFlyout(selectedExistingWidgetId);
        }

        Loaded += Card_Loaded;
        Unloaded += Card_Unloaded;
        ActualThemeChanged += (_, _) => RefreshAppearance();
        AddHandler(KeyDownEvent, new KeyEventHandler(Card_KeyDown), true);
        RefreshAppearance();
    }

    internal event EventHandler<IReadOnlyList<string>>? ExcludeItemsRequested;

    internal event EventHandler<string>? BoxTitleRenamed;

    internal event EventHandler? InlineRenameCanceled;

    internal event EventHandler<DesktopOrganizationTargetSelection>? DestinationRouteSelected;

    internal event EventHandler? TileSelectionChanged;

    internal string BucketKey { get; }

    internal string SourceBucketId => _target.SourceBucketId;

    internal DesktopOrganizationTargetPlan TargetPlan => _target;

    internal string EffectiveBoxTitle => _target.SuggestedDisplayName;

    private bool _dropTargetHighlighted;

    internal void SetDropTargetHighlight(bool highlighted)
    {
        if (_disposed || _dropTargetHighlighted == highlighted)
        {
            return;
        }

        _dropTargetHighlighted = highlighted;
        if (highlighted)
        {
            CardOutline.BorderBrush = ResolveThemeBrush("AccentFillColorDefaultBrush");
            CardOutline.BorderThickness = new Thickness(1.5);
        }
        else
        {
            AppSettings appSettings = App.Current.SettingsService.Settings;
            ApplyMaterial(appSettings);
        }
    }

    internal void RefreshSelectionVisuals()
    {
        if (!_disposed)
        {
            UpdateSelectionVisuals();
        }
    }

    internal void BeginInlineRename()
    {
        if (_disposed || _readOnlyMode)
        {
            return;
        }

        TitleRenameBox.Text = EffectiveBoxTitle;
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

    internal void RefreshAppearance()
    {
        if (_disposed)
        {
            return;
        }

        AppSettings settings = App.Current.SettingsService.Settings;
        WidgetConfig? config = settings.WidgetLayout.Widgets.FirstOrDefault(item =>
            string.Equals(item.Id, _target.TargetWidgetId, StringComparison.Ordinal));
        _layout = FileWidgetIconLayout.Calculate(
            settings,
            config?.IconSizeOverride,
            WindowsCompatibilityService.ResolveSystemTextScaleFactor());

        var tileAppearance = new PreviewTileAppearance(
            _layout,
            settings.FileWidget.HideShortcutArrowOverlay,
            settings.FileWidget.ShowImageFilesAsIcons,
            settings.FileWidget.ShowFileExtensions,
            settings.FileWidget.HideShortcutExtensionWhenShowingFileExtensions,
            settings.FileWidget.ShowFileItemPathTooltips,
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

        ApplyMaterial(settings);
        UpdateDestinationVisuals();
        LayoutItems(settings, force: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _iconGeneration++;
        DetachXamlRoot();
        foreach (PreviewTile tile in _tiles)
        {
            tile.Image.Source = null;
        }

        _tiles.Clear();
        ItemsPanel.Children.Clear();
        _iconLoadGate.Dispose();
    }

    private void RebuildDestinationFlyout(string? selectedExistingWidgetId)
    {
        DestinationFlyout.Items.Clear();
        _destinationRadioItems.Clear();

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
            AddDestinationRadioItem(destinationSubItem, null, selectedExistingWidgetId);
        }

        foreach (DesktopOrganizationDestinationOption destination in _destinations)
        {
            AddDestinationRadioItem(destinationSubItem, destination, selectedExistingWidgetId);
        }

        if (destinationSubItem.Items.Count > 0)
        {
            DestinationFlyout.Items.Add(destinationSubItem);
        }

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

    private void AddDestinationRadioItem(
        MenuFlyoutSubItem parentSubItem,
        DesktopOrganizationDestinationOption? option,
        string? selectedExistingWidgetId)
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
            GroupName = BucketKey,
            IsChecked = option is null
                ? string.IsNullOrWhiteSpace(selectedExistingWidgetId)
                : string.Equals(option.Id, selectedExistingWidgetId, StringComparison.Ordinal)
        };
        ToolTipService.SetToolTip(item, option?.DirectoryPath ?? _target.TargetDirectoryPath);
        item.Click += (_, _) =>
        {
            DestinationRouteSelected?.Invoke(
                this,
                new DesktopOrganizationTargetSelection
                {
                    DestinationMode = option is null
                        ? DesktopOrganizationDestinationMode.Dynamic
                        : DesktopOrganizationDestinationMode.ExistingWidget,
                    ExistingWidgetId = option?.Id
                });
        };
        _destinationRadioItems.Add(item);
        parentSubItem.Items.Add(item);
    }

    private void ExcludeAllItemsInBox()
    {
        if (_disposed || _readOnlyMode || _target.Items.Count == 0)
        {
            return;
        }

        ExcludeItemsRequested?.Invoke(this, _target.Items.Select(item => item.SourcePath).ToList());
    }

    private void UpdateDestinationVisuals()
    {
        TitleText.Text = EffectiveBoxTitle;
        ToolTipService.SetToolTip(TitleDisplayPanel, EffectiveBoxTitle);

        if (DestinationPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        ToolTipService.SetToolTip(
            DestinationButton,
            T("DesktopOrganization.Layout.More") + "\n" + _target.TargetDirectoryPath);
        AutomationProperties.SetName(DestinationButton, EffectiveBoxTitle);
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

    private void ApplyMaterial(AppSettings appSettings)
    {
        bool isDark = IsDarkEffectiveTheme();
        Windows.UI.Color accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;

        double cornerRadius = WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
            WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                appSettings.WidgetShell.WidgetCornerPreference));
        var corner = new CornerRadius(cornerRadius);
        MaterialSurface.CornerRadius = FallbackSurface.CornerRadius = CardOutline.CornerRadius = corner;
        MaterialSurface.Visibility = Visibility.Collapsed;
        FallbackSurface.Visibility = Visibility.Visible;

        string materialType = appSettings.WidgetShell.WidgetMaterialType;
        double surfaceOpacity = appSettings.WidgetShell.WidgetOpacity;
        double materialIntensity = appSettings.WidgetShell.WidgetMaterialIntensity;

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
            appSettings.WidgetShell.WidgetBorderStyle,
            appSettings.WidgetShell.WidgetBorderColorMode,
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

        _transparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _selectedTileSurfaceBrush = new SolidColorBrush(
            Windows.UI.Color.FromArgb(isDark ? (byte)0x2E : (byte)0x22, accentColor.R, accentColor.G, accentColor.B));
        _selectedTileBorderBrush = new SolidColorBrush(
            Windows.UI.Color.FromArgb(isDark ? (byte)0x8C : (byte)0x78, accentColor.R, accentColor.G, accentColor.B));
        _hoverTileSurfaceBrush = ResolveThemeBrush("SubtleFillColorSecondaryBrush");
        _hoverTileBorderBrush = _transparentBrush;
    }

    private void LayoutItems(AppSettings settings, bool force = false)
    {
        if (_disposed || _layout.CellWidth <= 0)
        {
            return;
        }

        CardRootGrid.Width = FixedBoxWidth;
        CardRootGrid.Height = FixedBoxHeight;

        IReadOnlyList<DesktopOrganizationFileSnapshot> items = _target.Items;
        int totalCells = items.Count;
        double availableWidth = ItemsPanel.ActualWidth > 120 ? ItemsPanel.ActualWidth : InnerItemsContentWidth;
        double effectiveCellWidth = Math.Max(54, Math.Min(110, _layout.CellWidth));
        int columns = Math.Clamp((int)Math.Floor(availableWidth / effectiveCellWidth), 2, 5);

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
        foreach (DesktopOrganizationFileSnapshot item in items)
        {
            _tiles.Add(CreateTile(item, settings));
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

        double tileRowHeight = ComputeTileRowHeight();
        int rowCount = Math.Max(1, (int)Math.Ceiling(totalCells / (double)columns));
        for (int i = 0; i < rowCount; i++)
        {
            ItemsPanel.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(_tiles.Count == 0 ? 156 : tileRowHeight)
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
            ContentControl host = _tiles[i].Host;
            Grid.SetColumn(host, i % columns);
            Grid.SetRow(host, i / columns);
            ItemsPanel.Children.Add(host);
        }

        UpdateSelectionVisuals();
    }

    private double ComputeTileRowHeight()
    {
        double iconSize = Math.Clamp(_layout.ImageSize, 20, 52);
        if (!_layout.ShowLabel)
        {
            return Math.Max(52, Math.Ceiling(iconSize + 20));
        }

        double fontSize = Math.Clamp(_layout.LabelFontSize, 10, 14);
        int lines = Math.Clamp(_layout.LabelMaxLines, 1, 3);
        double textHeight = Math.Ceiling(fontSize * 1.35 * lines);
        return Math.Max(76, Math.Ceiling(iconSize + textHeight + 18));
    }

    private PreviewTile CreateTile(DesktopOrganizationFileSnapshot item, AppSettings settings)
    {
        double iconSize = Math.Clamp(_layout.ImageSize, 20, 52);
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
            settings.FileWidget.ShowFileExtensions,
            settings.FileWidget.HideShortcutExtensionWhenShowingFileExtensions);
        string description = _itemDescription?.Invoke(item) ?? string.Empty;

        double spacing = Math.Clamp(_layout.ContentSpacing, 2, 7);
        double labelFontSize = Math.Clamp(_layout.LabelFontSize, 10, 14);
        double labelLineHeight = Math.Ceiling(labelFontSize * 1.33);
        int labelMaxLines = Math.Clamp(_layout.LabelMaxLines, 1, 3);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Spacing = spacing,
            Padding = new Thickness(3, 5, 3, 3)
        };
        stack.Children.Add(iconHost);

        if (_layout.ShowLabel)
        {
            stack.Children.Add(new TextBlock
            {
                Text = displayName,
                FontSize = labelFontSize,
                LineHeight = labelLineHeight,
                MaxLines = labelMaxLines,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = ResolveThemeBrush("TextFillColorPrimaryBrush")
            });
        }

        var gridCell = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        gridCell.Children.Add(stack);

        if (_session.NewSourcePaths.Contains(item.SourcePath))
        {
            var newBadge = new Border
            {
                Background = ResolveThemeBrush("AccentFillColorDefaultBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 1, 0),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = T("DesktopOrganization.Layout.New"),
                    FontSize = 9.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ResolveThemeBrush("TextOnAccentFillColorPrimaryBrush")
                }
            };
            gridCell.Children.Add(newBadge);
        }

        var host = new ContentControl
        {
            Content = gridCell,
            Style = (Style)Resources["PreviewTileHostStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY
        };

        string label = item.Name + (item.SourceScope == DesktopOrganizationSourceScope.Public
            ? " · " + T("DesktopOrganization.Public.Badge")
            : string.Empty);
        AutomationProperties.SetName(
            host,
            string.IsNullOrWhiteSpace(description) ? label : label + " · " + description);
        string tooltip = settings.FileWidget.ShowFileItemPathTooltips ? item.SourcePath : label;
        if (!string.IsNullOrWhiteSpace(description))
        {
            tooltip += "\n" + description;
        }

        ToolTipService.SetToolTip(host, tooltip);

        var tile = new PreviewTile(item, description, host, image, fallback, iconSize);
        host.PointerEntered += (_, _) =>
        {
            tile.IsHovered = true;
            UpdateSingleTileVisual(tile);
        };
        host.PointerExited += (_, _) =>
        {
            tile.IsHovered = false;
            UpdateSingleTileVisual(tile);
        };

        if (_readOnlyMode)
        {
            host.Tapped += (_, _) => CreateItemDetails(item, description).ShowAt(host);
        }
        else
        {
            host.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler((_, e) => OnTilePointerPressed(tile, e)),
                handledEventsToo: true);

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
            detailsItem.Click += (_, _) => CreateItemDetails(item, description).ShowAt(host);
            contextMenu.Items.Add(detailsItem);
            host.ContextFlyout = contextMenu;
        }

        _ = LoadIconAsync(
            tile,
            _iconGeneration,
            settings.FileWidget.HideShortcutArrowOverlay,
            settings.FileWidget.ShowImageFilesAsIcons);
        return tile;
    }

    private void OnTilePointerPressed(PreviewTile tile, PointerRoutedEventArgs e)
    {
        if (_disposed || _readOnlyMode)
        {
            return;
        }

        _dragController.TryBeginCandidate(
            tile.Host,
            e,
            DesktopOrganizationDragOrigin.PreviewCard,
            BucketKey,
            () => ResolveTileDragPayload(tile),
            () => HandleTileClickSelection(tile));
    }

    private IReadOnlyList<string> ResolveTileDragPayload(PreviewTile tile)
    {
        if (!_session.SelectedSourcePaths.Contains(tile.Item.SourcePath))
        {
            _session.SelectAllInBucket([tile.Item.SourcePath], BucketKey);
            TileSelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        return _session.GetDragPayloadForTargetItem(tile.Item.SourcePath);
    }

    private void HandleTileClickSelection(PreviewTile tile)
    {
        if (_disposed || _readOnlyMode)
        {
            return;
        }

        IReadOnlyList<string> orderedPaths = _tiles.Select(t => t.Item.SourcePath).ToList();
        _session.HandleTileClick(
            tile.Item.SourcePath,
            BucketKey,
            orderedPaths,
            isCtrlDown: IsKeyDown(VirtualKey.Control),
            isShiftDown: IsKeyDown(VirtualKey.Shift));
        tile.Host.Focus(FocusState.Pointer);
        TileSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CardRootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_disposed || _readOnlyMode)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source)
        {
            DependencyObject? current = source;
            while (current is not null && !ReferenceEquals(current, CardRootGrid))
            {
                if (current is ButtonBase or TextBox ||
                    _tiles.Any(tile => ReferenceEquals(tile.Host, current)))
                {
                    return;
                }

                current = VisualTreeHelper.GetParent(current);
            }
        }

        if (_session.ClearTileSelection())
        {
            TileSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ExcludeTileOrSelectedBatch(PreviewTile tile)
    {
        if (_disposed || _readOnlyMode)
        {
            return;
        }

        IReadOnlyList<string> paths =
            _session.SelectedSourcePaths.Contains(tile.Item.SourcePath) && _session.SelectedSourcePaths.Count > 1
                ? _session.SelectedSourcePaths.ToList()
                : [tile.Item.SourcePath];

        ExcludeItemsRequested?.Invoke(this, paths);
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
                ImageSource? icon = await IconHelper.GetIconAsync(
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
        if (_disposed || TitleRenameBox.Visibility == Visibility.Visible)
        {
            return;
        }

        object? focused = XamlRoot is not null ? FocusManager.GetFocusedElement(XamlRoot) : null;
        PreviewTile? focusedTile = _tiles.FirstOrDefault(tile => ReferenceEquals(tile.Host, focused));

        if (!_readOnlyMode && e.Key == VirtualKey.A && IsKeyDown(VirtualKey.Control))
        {
            _session.SelectAllInBucket(_target.Items.Select(item => item.SourcePath), BucketKey);
            TileSelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (!_readOnlyMode && e.Key == VirtualKey.F2)
        {
            BeginInlineRename();
            e.Handled = true;
            return;
        }

        if (!_readOnlyMode && e.Key is VirtualKey.Delete or VirtualKey.Back)
        {
            if (_session.SelectedSourcePaths.Count > 0)
            {
                ExcludeItemsRequested?.Invoke(this, _session.SelectedSourcePaths.ToList());
                e.Handled = true;
                return;
            }

            if (focusedTile is not null)
            {
                ExcludeItemsRequested?.Invoke(this, [focusedTile.Item.SourcePath]);
                e.Handled = true;
                return;
            }
        }

        if (focusedTile is not null && e.Key == VirtualKey.Enter)
        {
            CreateItemDetails(focusedTile.Item, focusedTile.Description).ShowAt(focusedTile.Host);
            e.Handled = true;
            return;
        }

        if (focusedTile is not null && e.Key == VirtualKey.Space)
        {
            if (_readOnlyMode)
            {
                CreateItemDetails(focusedTile.Item, focusedTile.Description).ShowAt(focusedTile.Host);
            }
            else
            {
                _session.ToggleTileSelection(focusedTile.Item.SourcePath, BucketKey);
                TileSelectionChanged?.Invoke(this, EventArgs.Empty);
            }

            e.Handled = true;
            return;
        }

        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down &&
            focusedTile is not null &&
            _columns > 0 &&
            _tiles.Count > 0)
        {
            int current = _tiles.IndexOf(focusedTile);
            if (current < 0)
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
            _tiles[next].Host.Focus(FocusState.Keyboard);
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
        if (_readOnlyMode)
        {
            return;
        }

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

            if (cancel)
            {
                InlineRenameCanceled?.Invoke(this, EventArgs.Empty);
            }
            else if (!string.IsNullOrWhiteSpace(candidate) &&
                     !string.Equals(candidate, EffectiveBoxTitle, StringComparison.Ordinal))
            {
                BoxTitleRenamed?.Invoke(this, candidate);
            }
        }
        finally
        {
            _isCommittingRename = false;
        }
    }

    private void UpdateSelectionVisuals()
    {
        int itemCount = _target.Items.Count;
        CountText.Inlines.Clear();
        if (_readOnlyMode)
        {
            CountText.Text = Format("DesktopOrganization.Preview.ItemCount", itemCount);
        }
        else if (!_target.CreatesWidget)
        {
            CountText.Inlines.Add(new Run
            {
                Text = T("DesktopOrganization.Layout.RouteBadgeExisting") + " · "
            });
            CountText.Inlines.Add(new Run
            {
                Text = $"+{itemCount}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ResolveThemeBrush("AccentTextFillColorPrimaryBrush")
            });
        }
        else
        {
            CountText.Text = T("DesktopOrganization.Layout.RouteBadgeNew") + " · " +
                             Format("DesktopOrganization.Preview.ItemCount", itemCount);
        }

        foreach (PreviewTile tile in _tiles)
        {
            UpdateSingleTileVisual(tile);
        }
    }

    private void UpdateSingleTileVisual(PreviewTile tile)
    {
        bool isSelected = !_readOnlyMode && _session.SelectedSourcePaths.Contains(tile.Item.SourcePath);
        if (isSelected)
        {
            tile.Host.Background = _selectedTileSurfaceBrush ?? _transparentBrush;
            tile.Host.BorderBrush = _selectedTileBorderBrush ?? _transparentBrush;
        }
        else if (tile.IsHovered)
        {
            tile.Host.Background = _hoverTileSurfaceBrush ?? _transparentBrush;
            tile.Host.BorderBrush = _hoverTileBorderBrush ?? _transparentBrush;
        }
        else
        {
            tile.Host.Background = _transparentBrush;
            tile.Host.BorderBrush = _transparentBrush;
        }
    }

    private void ItemsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_disposed && _layout.CellWidth > 0 && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) >= 24)
        {
            AppSettings settings = App.Current.SettingsService.Settings;
            LayoutItems(settings);
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

    private static string T(string key) => App.Current.LocalizationService.T(key);

    private static string Format(string key, params object[] args) =>
        App.Current.LocalizationService.Format(key, args);

    private sealed class PreviewTile(
        DesktopOrganizationFileSnapshot item,
        string description,
        ContentControl host,
        Image image,
        FontIcon fallback,
        double iconSize)
    {
        public DesktopOrganizationFileSnapshot Item { get; } = item;
        public string Description { get; } = description;
        public ContentControl Host { get; } = host;
        public Image Image { get; } = image;
        public FontIcon Fallback { get; } = fallback;
        public double IconSize { get; } = iconSize;
        public bool IsHovered { get; set; }
    }

    private sealed record PreviewTileAppearance(
        FileWidgetIconLayout Layout,
        bool HideShortcutArrow,
        bool ImagesAsIcons,
        bool ShowFileExtensions,
        bool HideShortcutExtension,
        bool ShowPathTooltips,
        double RasterizationScale,
        ElementTheme Theme);
}
