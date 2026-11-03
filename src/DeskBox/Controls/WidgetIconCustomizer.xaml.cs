using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

/// <summary>Localized view item for the emoji category strip.</summary>
public sealed record WidgetEmojiCategoryOption(string Id, string Label, string[] Emojis);

/// <summary>
/// Per-widget title-icon picker: emoji palette (categories + recent) and a
/// custom image entry, with instant apply and a restore-default action.
/// Hosted inside a flyout; the system file picker cannot live inside a
/// light-dismiss flyout, so the image request is delegated to the owning
/// window through <see cref="PickImageRequested"/>.
/// </summary>
public sealed partial class WidgetIconCustomizer : UserControl
{
    private readonly Random _random = new();
    private WidgetConfig? _config;

    /// <summary>Raised after the config was mutated and persisted.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised when the user asked for the system image picker. The hosting
    /// flyout has already been hidden: the picker steals window focus and
    /// would light-dismiss it anyway.
    /// </summary>
    public event EventHandler? PickImageRequested;

    public FlyoutBase? HostFlyout { get; set; }

    public WidgetIconCustomizer()
    {
        InitializeComponent();

        IconTabSegmented.SelectionChanged += (_, _) =>
            SelectTab(isEmojiTab: IconTabSegmented.SelectedIndex == 0);
        CategoryList.SelectionChanged += CategoryList_SelectionChanged;
        EmojiGrid.ItemClick += EmojiGrid_ItemClick;
        RandomButton.Click += (_, _) => SelectRandomEmoji();
        ResetButton.Click += (_, _) => ResetToDefault();
        ChooseImageButton.Click += (_, _) =>
        {
            HostFlyout?.Hide();
            PickImageRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Initialize(WidgetConfig config)
    {
        _config = config;
        LocalizationService localization = App.Current.LocalizationService;
        TitleText.Text = localization.T("Widget.CustomIcon.Title");
        SubtitleText.Text = localization.T("Widget.CustomIcon.Subtitle");
        EmojiTabItem.Content = localization.T("Widget.CustomIcon.TabEmoji");
        ImageTabItem.Content = localization.T("Widget.CustomIcon.TabImage");
        RandomButton.Content = localization.T("Widget.CustomIcon.Random");
        ResetButton.Content = localization.T("Widget.CustomIcon.Reset");
        RecentLabel.Text = localization.T("Widget.CustomIcon.Recent");
        ChooseImageButton.Content = localization.T("Widget.CustomIcon.ChooseImage");
        ImageFormatsHint.Text = localization.T("Widget.CustomIcon.ImageHint");

        PreviewDefaultIcon.IconKind = ResolveDefaultIconKind(config);
        CategoryList.ItemsSource = BuildCategoryOptions();
        if (CategoryList.Items.Count > 0)
        {
            CategoryList.SelectedIndex = 0;
        }

        _ = LoadRecentAsync();
        RefreshFromConfig(initializeTabs: true);
    }

    private static string ResolveDefaultIconKind(WidgetConfig config)
    {
        return WidgetTitleIconKindNames.FromWidgetConfig(config);
    }

    private static List<WidgetEmojiCategoryOption> BuildCategoryOptions()
    {
        LocalizationService localization = App.Current.LocalizationService;
        return WidgetTitleIconEmojiCatalog.Categories
            .Select(category => new WidgetEmojiCategoryOption(
                category.Id,
                localization.T("Widget.CustomIcon.Category." + category.Id),
                category.Emojis))
            .ToList();
    }

    private void CategoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is WidgetEmojiCategoryOption category)
        {
            EmojiGrid.ItemsSource = category.Emojis;
        }
    }

    private void EmojiGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        EmojiGrid.SelectedItem = null;
        if (e.ClickedItem is string emoji)
        {
            SelectEmoji(emoji);
        }
    }

    private void SelectRandomEmoji()
    {
        string[] pool = CategoryList.SelectedItem is WidgetEmojiCategoryOption category
            ? category.Emojis
            : WidgetTitleIconEmojiCatalog.GetAllEmojis();
        if (pool.Length > 0)
        {
            SelectEmoji(pool[_random.Next(pool.Length)]);
        }
    }

    private void SelectEmoji(string emoji)
    {
        if (_config is null)
        {
            return;
        }

        WidgetTitleIconCustomization.SetEmojiOverride(_config, emoji);
        PersistAndNotify();
        RefreshFromConfig(initializeTabs: false);
        _ = UpdateRecentAsync(emoji);
    }

    private void ResetToDefault()
    {
        if (_config is null)
        {
            return;
        }

        string? imageFileName = WidgetTitleIconCustomization.GetImageFileNameOverride(_config);
        WidgetTitleIconCustomization.Clear(_config);
        if (imageFileName is not null)
        {
            WidgetTitleIconAssetStore.Current.DeleteImage(_config.Id, imageFileName);
        }

        PersistAndNotify();
        RefreshFromConfig(initializeTabs: false);
    }

    private void PersistAndNotify()
    {
        if (App.Current.SettingsService is { } settingsService)
        {
            // Per-widget chrome: the shell refresh rides the Changed event,
            // so skip the global settings notification (the dim slider fires
            // this per tick).
            settingsService.UpdateWidget(_config!, notifySubscribers: false);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshFromConfig(bool initializeTabs)
    {
        if (_config is null)
        {
            return;
        }

        string? emoji = WidgetTitleIconCustomization.GetEmojiOverride(_config);
        string? imageFileName = WidgetTitleIconCustomization.GetImageFileNameOverride(_config);
        string? imagePath = WidgetTitleIconAssetStore.Current.ResolveImagePath(
            _config.Id,
            imageFileName);

        if (initializeTabs)
        {
            SelectTab(isEmojiTab: emoji is not null || imagePath is null);
        }

        bool showEmoji = emoji is not null;
        bool showImage = !showEmoji && imagePath is not null;
        PreviewEmojiText.Text = emoji ?? string.Empty;
        PreviewEmojiText.Visibility = showEmoji ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Source = showImage
            ? WidgetTitleIconImageSourceFactory.TryCreate(imagePath)
            : null;
        PreviewImage.Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
        PreviewDefaultIcon.Visibility = showEmoji || showImage
            ? Visibility.Collapsed
            : Visibility.Visible;

        LocalizationService localization = App.Current.LocalizationService;
        ImagePanelHint.Text = localization.T(
            imagePath is null
                ? "Widget.CustomIcon.ImageNone"
                : "Widget.CustomIcon.ImageCurrent");
        ImagePreview.Source = showImage ? PreviewImage.Source : null;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void SelectTab(bool isEmojiTab)
    {
        IconTabSegmented.SelectedIndex = isEmojiTab ? 0 : 1;
        EmojiPanel.Visibility = isEmojiTab ? Visibility.Visible : Visibility.Collapsed;
        ImagePanel.Visibility = isEmojiTab ? Visibility.Collapsed : Visibility.Visible;
        RandomButton.Visibility = isEmojiTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadRecentAsync()
    {
        IReadOnlyList<string> recent = await WidgetTitleIconAssetStore.Current.LoadRecentEmojisAsync();
        DispatcherQueue.TryEnqueue(() => RebuildRecentRow(recent));
    }

    private async Task UpdateRecentAsync(string emoji)
    {
        IReadOnlyList<string> recent = await WidgetTitleIconAssetStore.Current.AddRecentEmojiAsync(emoji);
        DispatcherQueue.TryEnqueue(() => RebuildRecentRow(recent));
    }

    private void RebuildRecentRow(IReadOnlyList<string> recent)
    {
        RecentHost.Children.Clear();
        if (recent.Count == 0)
        {
            RecentPanel.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (string emoji in recent)
        {
            var emojiText = new TextBlock
            {
                Text = emoji,
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = 16
            };
            var button = new Button
            {
                Content = emojiText,
                MinWidth = 36,
                MinHeight = 32,
                Padding = new Thickness(4, 2, 4, 2)
            };
            string picked = emoji;
            button.Click += (_, _) => SelectEmoji(picked);
            RecentHost.Children.Add(button);
        }

        RecentPanel.Visibility = Visibility.Visible;
    }
}
