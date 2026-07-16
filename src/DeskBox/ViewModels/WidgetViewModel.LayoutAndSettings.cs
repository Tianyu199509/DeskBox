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
    }

    private string GetManagedActionText()
    {
        return _localizationService.T("Common.Move");
    }

    private bool ShouldMoveManagedItems()
    {
        return true;
    }

    private void ApplyLayoutSettings()
    {
        var settings = _settingsService.Settings;
        double iconSize = Math.Clamp(settings.IconSize, SettingsService.MinIconSize, SettingsService.MaxIconSize);
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
        IconTileHeight = iconSize + Lerp(24, 70, verticalT);
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
        if (_dispatcherQueue.HasThreadAccess)
        {
            _ = ApplySettingsChangesAsync();
            return;
        }

        _dispatcherQueue.TryEnqueue(async () => await ApplySettingsChangesAsync());
    }

    private void OnLanguageChanged()
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            UpdateDependentProperties();
            return;
        }

        _dispatcherQueue.TryEnqueue(UpdateDependentProperties);
    }

    private async Task ApplySettingsChangesAsync()
    {
        WidgetOpacity = Math.Clamp(
            _settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);
        ShowListItemDetails = _settingsService.Settings.ShowListItemDetails;
        ShowFileItemPathTooltips = _settingsService.Settings.ShowFileItemPathTooltips;
        ApplyLayoutSettings();
        UpdateDependentProperties();

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
        bool shouldRefreshAllIcons = _showImageFilesAsIcons != showImageFilesAsIcons;
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
        WidgetOpacity = Math.Clamp(
            _settingsService.Settings.WidgetOpacity,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);
        ApplyLayoutSettings();
    }

    /// <summary>
    /// Initialize the widget by loading its current content.
    /// </summary>
}
