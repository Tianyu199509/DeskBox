using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class WidgetViewModel
{
    private void UpdateDependentProperties()
    {
        string mappedFolderName = GetMappedFolderDisplayName();
        string managedAction = GetManagedActionText();
        bool isManagedStorage = FollowsDefaultStoragePath;

        IconGlyph = isManagedStorage ? "\uE8B7" : "\uE71B";
        TitleIconKind = WidgetTitleIconKindNames.FromFileWidget(isManagedStorage);
        TopAddButtonVisibility = Visibility.Visible;
        IconViewVisibility = ViewMode == ViewMode.Icon ? Visibility.Visible : Visibility.Collapsed;
        ListViewVisibility = ViewMode == ViewMode.List ? Visibility.Visible : Visibility.Collapsed;
        IsIconMode = ViewMode == ViewMode.Icon;
        IsListMode = ViewMode == ViewMode.List;
        LoadingVisibility = IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ModeLabel = isManagedStorage
            ? _localizationService.T("Widget.Mode.Managed")
            : _localizationService.T("Widget.Mode.Mapped");
        ModeDescription = isManagedStorage
            ? _localizationService.T("Widget.Mode.ManagedDescription")
            : _localizationService.T("Widget.Mode.MappedDescription");
        EmptyStateGlyph = IconGlyph;
        EmptyStateTitle = isManagedStorage
            ? _localizationService.T("Widget.Empty.ManagedTitle")
            : _localizationService.T("Widget.Empty.MappedTitle");
        EmptyStateText = isManagedStorage
            ? _localizationService.Format("Widget.Empty.ManagedText", managedAction, mappedFolderName)
            : _localizationService.Format("Widget.Empty.MappedText", mappedFolderName);
        OnPropertyChanged(nameof(SortModeLabel));
        UpdateFolderNavigationPresentation();
    }

    private string GetManagedActionText()
    {
        return _settingsService.Settings.ManagedDropAction switch
        {
            SettingsService.ManagedDropActionMove =>
                _localizationService.T("Common.Move"),
            SettingsService.ManagedDropActionFollowWindows =>
                _localizationService.T("Settings.DropAction.System"),
            _ => _localizationService.T("Common.Copy")
        };
    }

    private bool ShouldMoveManagedItems(
        IEnumerable<string>? sourcePaths = null,
        string? destinationFolderPath = null)
    {
        string action = _settingsService.Settings.ManagedDropAction;
        if (string.Equals(
                action,
                SettingsService.ManagedDropActionMove,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(
                action,
                SettingsService.ManagedDropActionFollowWindows,
                StringComparison.Ordinal) ||
            sourcePaths is null ||
            string.IsNullOrWhiteSpace(destinationFolderPath))
        {
            return false;
        }

        // Explorer moves within one volume and copies across volumes. If a
        // provider exposes an unknown root, AreAllOnSameVolume deliberately
        // returns false so the safer copy path is selected.
        return FileDropIntentPolicy.AreAllOnSameVolume(
            sourcePaths,
            destinationFolderPath);
    }

    private void ApplyLayoutSettings()
    {
        var settings = _settingsService.Settings;
        double iconSize = SettingsService.NormalizeIconSize(
            Config.IconSizeOverride ?? settings.IconSize);
        double textSize = Math.Clamp(settings.TextSize, SettingsService.MinTextSize, SettingsService.MaxTextSize);
        double densityScale = Math.Clamp(
            settings.LayoutDensityScale,
            SettingsService.MinLayoutDensityScale,
            SettingsService.MaxLayoutDensityScale);
        double horizontalScale = Math.Clamp(
            settings.HorizontalSpacingScale,
            SettingsService.MinSpacingScale,
            SettingsService.MaxSpacingScale);
        double verticalScale = Math.Clamp(
            settings.VerticalSpacingScale,
            SettingsService.MinSpacingScale,
            SettingsService.MaxSpacingScale);
        double fileNameWidthScale = Math.Clamp(
            settings.FileNameWidthScale,
            SettingsService.MinSpacingScale,
            SettingsService.MaxSpacingScale);
        int fileNameLineCount = SettingsService.NormalizeFileNameLineCount(settings.FileNameLineCount);

        double horizontalT = NormalizeScale(horizontalScale, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
        double verticalT = NormalizeScale(verticalScale, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
        double nameWidthT = NormalizeScale(fileNameWidthScale, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
        double densityT = NormalizeScale(
            densityScale,
            SettingsService.MinLayoutDensityScale,
            SettingsService.MaxLayoutDensityScale);

        double labelMaxWidth = Math.Max(iconSize, Lerp(iconSize, textSize * 10.5, nameWidthT));
        IconLabelMaxWidth = labelMaxWidth;
        IconTileWidth = Math.Max(iconSize + Lerp(6, 28, horizontalT), labelMaxWidth + Lerp(4, 16, horizontalT));
        double twoLineTileHeight = iconSize + Lerp(24, 70, verticalT);
        double oneLineTileHeight = Math.Max(
            iconSize + textSize + 8,
            twoLineTileHeight - textSize - 3);
        IconTileHeight = fileNameLineCount switch
        {
            SettingsService.HiddenFileNameLineCount => Math.Max(
                iconSize + 8,
                oneLineTileHeight - textSize - 3),
            SettingsService.MinFileNameLineCount => oneLineTileHeight,
            _ => twoLineTileHeight
        };
        IconTileMargin = new Thickness(
            Lerp(0, 2, horizontalT),
            Lerp(0, 2, verticalT),
            Lerp(0, 2, horizontalT),
            Lerp(0, 2, verticalT));
        IconTilePadding = new Thickness(
            Lerp(1, 5, horizontalT),
            Lerp(1, 6, verticalT),
            Lerp(1, 5, horizontalT),
            Lerp(1, 6, verticalT));
        IconContentSpacing = Lerp(1, 7, verticalT);
        IconImageSize = iconSize;
        IconLabelFontSize = textSize;
        IconLabelMaxLines = Math.Max(SettingsService.MinFileNameLineCount, fileNameLineCount);
        IconLabelVisibility = fileNameLineCount == SettingsService.HiddenFileNameLineCount
            ? Visibility.Collapsed
            : Visibility.Visible;
        _iconDecodePixelWidth = ResolveIconDecodePixelWidth(iconSize);

        double listScale = Lerp(0.68, 0.90, densityT);
        double listItemMarginY = Lerp(0, 2, verticalT);
        ListItemMargin = new Thickness(0, listItemMarginY * listScale, 0, listItemMarginY * listScale);
        ListItemPadding = new Thickness(
            Lerp(4, 12, horizontalT) * listScale,
            Lerp(2, 9, verticalT) * listScale,
            Lerp(4, 12, horizontalT) * listScale,
            Lerp(2, 9, verticalT) * listScale);
        ListIconSize = Math.Clamp(Math.Round(iconSize * 0.72 * listScale), 16, 32);
        ListLabelFontSize = textSize;
    }

    private static double Lerp(double min, double max, double t)
    {
        return min + ((max - min) * t);
    }

    private static double NormalizeScale(double value, double min, double max)
    {
        return Math.Abs(max - min) < 0.0001
            ? 0
            : (value - min) / (max - min);
    }

    private static int ResolveIconDecodePixelWidth(double iconSize)
    {
        return iconSize switch
        {
            <= 28 => 48,
            <= 34 => 64,
            <= 42 => 80,
            _ => 128
        };
    }

    public double EffectiveIconSize => SettingsService.NormalizeIconSize(
        Config.IconSizeOverride ?? _settingsService.Settings.IconSize);

    public bool SetIconSizeOverride(double? value)
    {
        double? normalized = value is null
            ? null
            : SettingsService.NormalizeIconSize(value.Value);
        if (Nullable.Equals(Config.IconSizeOverride, normalized))
        {
            return false;
        }

        int previousIconDecodePixelWidth = _iconDecodePixelWidth;
        Config.IconSizeOverride = normalized;
        ApplyLayoutSettings();
        RefreshStackLayoutMetrics();
        OnPropertyChanged(nameof(EffectiveIconSize));
        if (previousIconDecodePixelWidth != _iconDecodePixelWidth)
        {
            RefreshAllIcons();
        }

        _settingsService.SaveDebounced();
        return true;
    }

    private string GetMappedFolderDisplayName()
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return _localizationService.T("Common.CurrentLocation");
        }

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (MappedFolderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase) ||
            MappedFolderPath.Equals(publicDesktop, StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Common.Desktop");
        }

        string folderName = Path.GetFileName(MappedFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(folderName) ? MappedFolderPath : folderName;
    }

    private void OnSettingsChanged()
    {
        bool appearanceOnly =
            _settingsService.LastNotifiedChangeKind == SettingsChangeKind.Appearance;
        if (_dispatcherQueue.HasThreadAccess)
        {
            ApplySettingsChanges(appearanceOnly);
            return;
        }

        _dispatcherQueue.TryEnqueue(() => ApplySettingsChanges(appearanceOnly));
    }

    private void ApplySettingsChanges(bool appearanceOnly)
    {
        if (appearanceOnly)
        {
            // Window visuals are handled by the appearance preview channel;
            // keep only the VM-level opacity mirror in sync.
            WidgetOpacity = Math.Clamp(
                _settingsService.Settings.WidgetOpacity,
                SettingsService.MinWidgetOpacity,
                SettingsService.MaxWidgetOpacity);
            return;
        }

        _ = ApplySettingsChangesAsync();
    }

    private void OnLanguageChanged()
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            UpdateDependentProperties();
            RebuildStackDisplayItems();
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            UpdateDependentProperties();
            RebuildStackDisplayItems();
        });
    }

    private async Task ApplySettingsChangesAsync()
    {
        await RefreshFolderOpenBehaviorAsync();
        int previousIconDecodePixelWidth = _iconDecodePixelWidth;
        WidgetOpacity = Math.Clamp(
            _settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);
        ShowListItemDetails = _settingsService.Settings.ShowListItemDetails;
        ShowFileItemPathTooltips = _settingsService.Settings.ShowFileItemPathTooltips;
        ApplyLayoutSettings();
        UpdateDependentProperties();
        ApplyStackSettings();

        bool showFileExtensions = _settingsService.Settings.ShowFileExtensions;
        bool hideShortcutExtensionWhenShowingFileExtensions =
            _settingsService.Settings.HideShortcutExtensionWhenShowingFileExtensions;
        if (_showFileExtensions != showFileExtensions ||
            _hideShortcutExtensionWhenShowingFileExtensions != hideShortcutExtensionWhenShowingFileExtensions)
        {
            _showFileExtensions = showFileExtensions;
            _hideShortcutExtensionWhenShowingFileExtensions = hideShortcutExtensionWhenShowingFileExtensions;
            RefreshItemDisplayNames();
        }

        bool hideShortcutArrowOverlay = _settingsService.Settings.HideShortcutArrowOverlay;
        bool showImageFilesAsIcons = _settingsService.Settings.ShowImageFilesAsIcons;
        bool shouldRefreshAllIcons =
            _showImageFilesAsIcons != showImageFilesAsIcons ||
            previousIconDecodePixelWidth != _iconDecodePixelWidth;
        bool shouldRefreshShortcutIcons = _hideShortcutArrowOverlay != hideShortcutArrowOverlay;

        if (!shouldRefreshAllIcons && !shouldRefreshShortcutIcons)
        {
            return;
        }

        _hideShortcutArrowOverlay = hideShortcutArrowOverlay;
        _showImageFilesAsIcons = showImageFilesAsIcons;

        if (shouldRefreshAllIcons)
        {
            RefreshAllIcons();
            return;
        }

        await RefreshShortcutIconsAsync();
    }

    public void ApplyAppearancePreview()
    {
        int previousIconDecodePixelWidth = _iconDecodePixelWidth;
        WidgetOpacity = Math.Clamp(
            _settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);
        ApplyLayoutSettings();
        RefreshStackLayoutMetrics();
        if (previousIconDecodePixelWidth != _iconDecodePixelWidth)
        {
            RefreshAllIcons();
        }
    }

    /// <summary>
    /// Initialize the widget by loading its current content.
    /// </summary>
}
