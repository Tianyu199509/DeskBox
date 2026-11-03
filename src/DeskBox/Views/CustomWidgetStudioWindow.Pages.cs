using System.IO;
using System.Text;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI.Core;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Views;

public sealed partial class CustomWidgetStudioWindow
{
    private const double StartPageMaxWidth = 1240;
    private const double PreviewPageMaxWidth = 1520;

    private InfoBar? _pageInfoBar;
    private readonly List<(CustomWidgetViewModel Vm, CustomWidgetContent Content)> _templatePreviews = [];

    // Preview page live elements
    private CustomWidgetViewModel? _previewVm;
    private CustomWidgetContent? _previewContent;
    private ColumnDefinition? _stageColumn;
    private bool _stageWidthManuallyAdjusted;
    private double _lastPreviewWidth = DesiredWidth;
    private double _currentPreviewWidgetWidth = 320;
    private Border? _stageBorder;
    private Border? _widgetFrame;
    private TextBlock? _headerEmojiText;
    private TextBlock? _nameText;
    private TextBlock? _frameEmojiText;
    private TextBlock? _frameTitleText;
    private TextBlock? _revisionBadgeText;
    private Border? _revisionBadge;
    private Button? _undoButton;
    private Button? _redoButton;
    private Button? _sizeQuickButton;
    private DropDownButton? _revisionsMenuButton;
    private Button? _briefHeaderButton;
    private ScrollViewer? _chatStreamScroller;
    private StackPanel? _chatStreamPanel;
    private CustomWidgetWrapPanel? _suggestionChipsPanel;
    private Border? _targetNodePillBorder;
    private TextBlock? _targetNodePillText;
    private TextBox? _refineTextBox;
    private Button? _refineSubmitButton;
    private Button? _commitButton;
    private Border? _stageWallpaperLayer;
    private Border? _stageWallpaperScrim;
    private ProgressBar? _stageGeneratingProgressBar;
    private Border? _stageGeneratingStatusPill;
    private TextBlock? _stageGeneratingStatusText;
    private TextBlock? _blueprintTitleText;
    private Button? _sampleDataToggleButton;
    private BitmapImage? _cachedWallpaperBitmap;
    private bool _wallpaperLoadAttempted;
    private StartGallerySourceMode _activeStartGalleryMode = StartGallerySourceMode.Templates;
    private TemplateGalleryCategory _activeGalleryCategory = TemplateGalleryCategory.All;
    private int _stagePulseToken;

    // Direct API live streaming elements
    private TextBlock? _directStageTitleText;
    private TextBlock? _directStatsBadgeText;
    private Border? _directDetectedWidgetBadge;
    private TextBlock? _directDetectedWidgetText;
    private Border? _directThinkingContainer;
    private TextBlock? _directThinkingHeaderText;
    private ScrollViewer? _directThinkingScroller;
    private TextBlock? _directThinkingText;
    private Border? _directRawStreamWell;
    private TextBlock? _directStreamHintText;
    private ScrollViewer? _directStreamScroller;
    private TextBlock? _directStreamText;
    private Border? _directStep1Pill;
    private Border? _directStep2Pill;
    private Border? _directStep3Pill;
    private string _directActiveProviderName = string.Empty;
    private string _directActiveModelName = string.Empty;
    private long _directStartTickMs;
    private int _directLastCharTotal;
    private bool _directThinkingCollapsedByUser;

    private enum StartGallerySourceMode
    {
        Templates,
        MyWidgets
    }

    private enum TemplateGalleryCategory
    {
        All,
        Habits,
        Tools,
        Live,
        Ai
    }

    private sealed record ExistingWidgetCardItem(
        string Id,
        string Name,
        string Emoji,
        string Summary,
        string ManifestJson,
        bool IsVisibleOnDesktop,
        int RevisionCount,
        long LastUpdatedUtcTicks);

    private IReadOnlyList<ExistingWidgetCardItem> GetExistingWidgetGalleryItems()
    {
        IReadOnlyList<GlanceWidgetInstanceInfo>? instances = App.Current.WidgetManager?.GetCustomWidgetInstances();
        if (instances is null || instances.Count == 0)
        {
            return [];
        }

        var items = new List<ExistingWidgetCardItem>(instances.Count);
        foreach (GlanceWidgetInstanceInfo inst in instances)
        {
            if (string.IsNullOrWhiteSpace(inst.Id))
            {
                continue;
            }

            string? manifestJson = TryReadWidgetManifest(inst.Id);
            if (string.IsNullOrWhiteSpace(manifestJson))
            {
                continue;
            }

            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
            CustomWidgetManifest? manifest = parsed.Manifest;
            string emoji = manifest is not null && CustomWidgetTemplates.IsEmojiIcon(manifest.Icon)
                ? manifest.Icon.Trim()
                : "✨";
            string name = !string.IsNullOrWhiteSpace(inst.Name)
                ? inst.Name.Trim()
                : (manifest?.ResolveLocalizedName(_loc.CurrentCultureName) ?? _loc.T("CustomWidget.Title"));

            int revisionCount = 1;
            long lastRevTicks = 0;
            string latestRevSummary = string.Empty;
            try
            {
                string revPath = Path.Combine(
                    _sessionStore.GetWidgetDirectory(inst.Id),
                    CustomWidgetStudioSessionStore.RevisionsFileName);
                if (File.Exists(revPath))
                {
                    List<StudioPersistedRevision> revs = CustomWidgetStudioSessionStore.ParseRevisionsJson(
                        File.ReadAllText(revPath, Encoding.UTF8));
                    if (revs.Count > 0)
                    {
                        revisionCount = revs.Count;
                        lastRevTicks = revs[^1].CreatedAtUtcTicks;
                        latestRevSummary = revs[^1].ChangeSummary?.Trim() ?? string.Empty;
                    }
                }
            }
            catch
            {
            }

            long fileTicks = 0;
            try
            {
                string manifestPath = CustomWidgetDataStore.ForWidget(inst.Id).ManifestPath;
                if (File.Exists(manifestPath))
                {
                    fileTicks = File.GetLastWriteTimeUtc(manifestPath).Ticks;
                }
            }
            catch
            {
            }

            string manifestDesc = manifest?.ResolveLocalizedDescription(_loc.CurrentCultureName)?.Trim() ?? string.Empty;
            string summary = !string.IsNullOrWhiteSpace(manifestDesc)
                ? manifestDesc
                : (!string.IsNullOrWhiteSpace(latestRevSummary)
                    ? latestRevSummary
                    : _loc.T("CustomWidget.Workshop.MyWidgets.DefaultDesc"));

            items.Add(new ExistingWidgetCardItem(
                Id: inst.Id,
                Name: name,
                Emoji: emoji,
                Summary: summary,
                ManifestJson: manifestJson,
                IsVisibleOnDesktop: inst.IsEnabled,
                RevisionCount: revisionCount,
                LastUpdatedUtcTicks: Math.Max(fileTicks, lastRevTicks)));
        }

        return items
            .OrderByDescending(static x => x.LastUpdatedUtcTicks)
            .ToList();
    }

    private sealed record WorkshopCardSpec(
        string Id,
        string Emoji,
        string LabelKey,
        string DescriptionKey,
        string PromptKey,
        string ManifestJson,
        TemplateGalleryCategory Category,
        string? DirectLabel = null,
        string? DirectDescription = null,
        bool RequiresClientUpgrade = false,
        string UpgradeRequirementLabel = "");

    private static TemplateGalleryCategory InferRemoteTemplateCategory(string id, string? label)
    {
        string haystack = $"{id} {label}".ToLowerInvariant();
        if (haystack.Contains("ai", StringComparison.Ordinal) ||
            haystack.Contains("agent", StringComparison.Ordinal) ||
            haystack.Contains("llm", StringComparison.Ordinal))
        {
            return TemplateGalleryCategory.Ai;
        }
        if (haystack.Contains("rss", StringComparison.Ordinal) ||
            haystack.Contains("news", StringComparison.Ordinal) ||
            haystack.Contains("monitor", StringComparison.Ordinal) ||
            haystack.Contains("weather", StringComparison.Ordinal) ||
            haystack.Contains("stock", StringComparison.Ordinal) ||
            haystack.Contains("hardware", StringComparison.Ordinal))
        {
            return TemplateGalleryCategory.Live;
        }
        if (haystack.Contains("habit", StringComparison.Ordinal) ||
            haystack.Contains("water", StringComparison.Ordinal) ||
            haystack.Contains("check", StringComparison.Ordinal) ||
            haystack.Contains("todo", StringComparison.Ordinal) ||
            haystack.Contains("pomodoro", StringComparison.Ordinal))
        {
            return TemplateGalleryCategory.Habits;
        }

        return TemplateGalleryCategory.Tools;
    }

    private IReadOnlyList<WorkshopCardSpec> GetWorkshopGallerySpecs()
    {
        var specs = new List<WorkshopCardSpec>
        {
            new(
                CustomWidgetTemplates.HydrationStarterId,
                "💧",
                "CustomWidget.AiWizard.Chip.Water.Label",
                "CustomWidget.Workshop.Card.Water.Desc",
                "CustomWidget.AiWizard.Chip.Water.Prompt",
                CustomWidgetTemplates.ResolveEffectiveTemplateJson(CustomWidgetTemplates.HydrationStarterId),
                TemplateGalleryCategory.Habits),
            new(
                CustomWidgetTemplates.AiInboxStarterId,
                "⚡",
                "CustomWidget.Studio.Starter.AiInbox.Label",
                "CustomWidget.Workshop.Card.AiInbox.Desc",
                "CustomWidget.Studio.Starter.AiInbox.Prompt",
                CustomWidgetTemplates.ResolveEffectiveTemplateJson(CustomWidgetTemplates.AiInboxStarterId),
                TemplateGalleryCategory.Ai),
            new(
                CustomWidgetTemplates.ReleaseChecklistStarterId,
                "✅",
                "CustomWidget.Studio.Starter.Checklist.Label",
                "CustomWidget.Workshop.Card.Checklist.Desc",
                "CustomWidget.Studio.Starter.Checklist.Prompt",
                CustomWidgetTemplates.ResolveEffectiveTemplateJson(CustomWidgetTemplates.ReleaseChecklistStarterId),
                TemplateGalleryCategory.Habits),
            new(
                CustomWidgetTemplates.NewsRssStarterId,
                "📰",
                "CustomWidget.Studio.Starter.NewsRss.Label",
                "CustomWidget.Workshop.Card.NewsRss.Desc",
                "CustomWidget.Studio.Starter.NewsRss.Prompt",
                CustomWidgetTemplates.ResolveEffectiveTemplateJson(CustomWidgetTemplates.NewsRssStarterId),
                TemplateGalleryCategory.Live),
            new(
                CustomWidgetTemplates.AgentHudTemplateId,
                "🤖",
                "CustomWidget.Studio.Starter.AgentHud.Label",
                "CustomWidget.Workshop.Card.AgentHud.Desc",
                "CustomWidget.Studio.Starter.AgentHud.Prompt",
                CustomWidgetTemplates.ResolveEffectiveTemplateJson(CustomWidgetTemplates.AgentHudTemplateId),
                TemplateGalleryCategory.Ai),
            new(
                CustomWidgetTemplates.MiniHardwareStarterId,
                "📊",
                "CustomWidget.AiWizard.Chip.Monitor.Label",
                "CustomWidget.Workshop.Card.Monitor.Desc",
                "CustomWidget.AiWizard.Chip.Monitor.Prompt",
                CustomWidgetTemplates.ResolveEffectiveTemplateJson(CustomWidgetTemplates.MiniHardwareStarterId),
                TemplateGalleryCategory.Live),
            new(
                CustomWidgetTemplates.FileDropStarterId,
                "🗂️",
                "CustomWidget.AiWizard.Chip.Links.Label",
                "CustomWidget.Workshop.Card.Links.Desc",
                "CustomWidget.AiWizard.Chip.Links.Prompt",
                CustomWidgetTemplates.ResolveEffectiveTemplateJson(CustomWidgetTemplates.FileDropStarterId),
                TemplateGalleryCategory.Tools),
            new(
                CustomWidgetTemplates.QuickSnippetsTemplateId,
                "📋",
                "CustomWidget.Template.QuickSnippets.Name",
                "CustomWidget.Template.QuickSnippets.Description",
                "CustomWidget.Workshop.Card.Snippets.Prompt",
                CustomWidgetTemplates.ResolveEffectiveTemplateJson(CustomWidgetTemplates.QuickSnippetsTemplateId),
                TemplateGalleryCategory.Tools)
        };

        var existingIds = new HashSet<string>(specs.Select(static s => s.Id), StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetCatalogEntry remote in CustomWidgetRemoteCatalogService.Shared.GetMergedOfficialTemplates())
        {
            if (!remote.IsRemoteOnly || existingIds.Contains(remote.Id))
            {
                continue;
            }

            string emoji = CustomWidgetTemplates.IsEmojiIcon(remote.IconGlyph)
                ? remote.IconGlyph.Trim()
                : "✨";
            string resolvedName = remote.ResolveDisplayName(_loc);
            specs.Add(new WorkshopCardSpec(
                remote.Id,
                emoji,
                string.Empty,
                string.Empty,
                string.Empty,
                remote.ManifestJson ?? string.Empty,
                InferRemoteTemplateCategory(remote.Id, resolvedName),
                DirectLabel: resolvedName,
                DirectDescription: remote.ResolveDescription(_loc),
                RequiresClientUpgrade: remote.RequiresClientUpgrade,
                UpgradeRequirementLabel: remote.UpgradeRequirementLabel));
            existingIds.Add(remote.Id);
        }

        return specs;
    }

    // ── Start page ──────────────────────────────────────────────────────

    private void ShowStartPage(string? idea)
    {
        DisposePreview();
        DisposeTemplatePreviews();
        _page = StudioPage.Start;
        RefreshTitleBarContextBadge();

        IReadOnlyList<WorkshopCardSpec> specs = GetWorkshopGallerySpecs();

        var column = new StackPanel
        {
            MaxWidth = StartPageMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(32, 24, 32, 32)
        };

        var startHeroRow = new Grid { ColumnSpacing = 12 };
        startHeroRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        startHeroRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var startHeroTextCol = new StackPanel { Spacing = 4 };
        startHeroTextCol.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Start.Title"),
            Style = TextStyle("TitleTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap
        });
        startHeroTextCol.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Start.Subtitle"),
            Style = TextStyle("BodyTextBlockStyle"),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        startHeroRow.Children.Add(startHeroTextCol);

        Button startApiDocsBtn = CreateSubtleApiDocsToggleButton(
            "CustomWidget.ApiDocs.Button",
            fontSize: 12.5,
            horizontalPadding: 12);
        startApiDocsBtn.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(startApiDocsBtn, 1);
        startHeroRow.Children.Add(startApiDocsBtn);

        column.Children.Add(startHeroRow);

        if (CreateDraftRecoveryBannerIfNeeded() is { } startRecoveryBanner)
        {
            startRecoveryBanner.Margin = new Thickness(0, 14, 0, 0);
            column.Children.Add(startRecoveryBanner);
        }

        Border composer = CreateComposerCard(
            initialText: idea ?? string.Empty,
            placeholderText: _loc.T("CustomWidget.Workshop.Start.Placeholder"),
            automationName: _loc.T("CustomWidget.Workshop.Start.Title"),
            submitLabel: _loc.T("CustomWidget.Workshop.Start.Button"),
            shortcutHintText: _loc.T("CustomWidget.Workshop.Composer.ShortcutHint"),
            minTextHeight: 92,
            maxTextHeight: 210,
            compactToolbar: false,
            includeCodeToolInMore: false,
            onTextEdited: text => _startDraftIdea = text.Trim(),
            out TextBox ideaBox);
        _startIdeaTextBox = ideaBox;
        composer.Margin = new Thickness(0, 18, 0, 0);
        column.Children.Add(composer);

        _pageInfoBar = CreatePageInfoBar();
        _pageInfoBar.Margin = new Thickness(0, 10, 0, 0);
        column.Children.Add(_pageInfoBar);

        // Curated starter prompts: concise Fluent 2 chips without rainbow emoji clutter.
        var tryRow = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        tryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tryRow.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Start.TryLabel"),
            Style = TextStyle("CaptionTextBlockStyle"),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var chips = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        foreach (WorkshopCardSpec spec in specs.Where(static s => !string.IsNullOrWhiteSpace(s.PromptKey)).Take(5))
        {
            string prompt = _loc.T(spec.PromptKey);
            string emoji = spec.Emoji;
            string chipLabel = !string.IsNullOrWhiteSpace(spec.DirectLabel) ? spec.DirectLabel! : _loc.T(spec.LabelKey);
            Button chip = CreateChipButton(chipLabel);
            chip.Click += (_, _) =>
            {
                _selectedEmoji = emoji;
                ideaBox.Text = prompt;
                ideaBox.SelectionStart = ideaBox.Text.Length;
                ideaBox.Focus(FocusState.Programmatic);
            };
            chips.Children.Add(chip);
        }

        Grid.SetColumn(chips, 1);
        tryRow.Children.Add(chips);
        column.Children.Add(tryRow);

        IReadOnlyList<ExistingWidgetCardItem> existingItems = GetExistingWidgetGalleryItems();

        var templatesHeaderRow = new Grid
        {
            ColumnSpacing = 12,
            Margin = new Thickness(0, 26, 0, 14)
        };
        templatesHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        templatesHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var templatesHeaderStack = new StackPanel { Spacing = 6 };

        var galleryModeSegmented = new ToolkitControls.Segmented
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinHeight = 32,
            Height = 32
        };

        string templatesTabLabel = _loc.T("CustomWidget.Workshop.Start.TemplatesHeader");
        var templatesSegItem = new ToolkitControls.SegmentedItem
        {
            Tag = StartGallerySourceMode.Templates,
            Icon = new FontIcon { Glyph = "\uE8A9", FontSize = 12.5 },
            Content = templatesTabLabel
        };
        AutomationProperties.SetName(templatesSegItem, templatesTabLabel);

        string myWidgetsTabLabel = existingItems.Count > 0
            ? _loc.Format("CustomWidget.Workshop.MyWidgets.TabCount", existingItems.Count)
            : _loc.T("CustomWidget.Workshop.MyWidgets.Tab");
        var myWidgetsSegItem = new ToolkitControls.SegmentedItem
        {
            Tag = StartGallerySourceMode.MyWidgets,
            Icon = new FontIcon { Glyph = "\uE74C", FontSize = 12.5 },
            Content = myWidgetsTabLabel
        };
        AutomationProperties.SetName(myWidgetsSegItem, myWidgetsTabLabel);

        galleryModeSegmented.Items.Add(templatesSegItem);
        galleryModeSegmented.Items.Add(myWidgetsSegItem);
        galleryModeSegmented.SelectedItem = _activeStartGalleryMode == StartGallerySourceMode.MyWidgets
            ? myWidgetsSegItem
            : templatesSegItem;

        WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(galleryModeSegmented);
        galleryModeSegmented.ActualThemeChanged += (_, _) =>
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(galleryModeSegmented);

        var gallerySubtitleText = new TextBlock
        {
            Style = TextStyle("CaptionTextBlockStyle"),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };

        templatesHeaderStack.Children.Add(galleryModeSegmented);
        templatesHeaderStack.Children.Add(gallerySubtitleText);
        templatesHeaderRow.Children.Add(templatesHeaderStack);

        var filterBar = new SelectorBar
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -4)
        };
        Grid.SetColumn(filterBar, 1);
        templatesHeaderRow.Children.Add(filterBar);
        column.Children.Add(templatesHeaderRow);

        var cardsHostContainer = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
        bool suppressFilterEvent = false;
        bool suppressModeEvent = false;

        void SwitchToTemplatesMode()
        {
            suppressModeEvent = true;
            galleryModeSegmented.SelectedItem = templatesSegItem;
            suppressModeEvent = false;
            _activeStartGalleryMode = StartGallerySourceMode.Templates;
            RenderGallerySection(animate: true);
        }

        void RenderGallerySection(bool animate)
        {
            DisposeTemplatePreviews();
            bool isMyWidgets = _activeStartGalleryMode == StartGallerySourceMode.MyWidgets;
            filterBar.Visibility = isMyWidgets ? Visibility.Collapsed : Visibility.Visible;
            gallerySubtitleText.Text = _loc.T(isMyWidgets
                ? "CustomWidget.Workshop.MyWidgets.Subtitle"
                : "CustomWidget.Workshop.Start.TemplatesSubtitle");

            if (isMyWidgets)
            {
                existingItems = GetExistingWidgetGalleryItems();
                string updatedTabLabel = existingItems.Count > 0
                    ? _loc.Format("CustomWidget.Workshop.MyWidgets.TabCount", existingItems.Count)
                    : _loc.T("CustomWidget.Workshop.MyWidgets.Tab");
                myWidgetsSegItem.Content = updatedTabLabel;
                AutomationProperties.SetName(myWidgetsSegItem, updatedTabLabel);

                if (existingItems.Count == 0)
                {
                    cardsHostContainer.Child = CreateMyWidgetsEmptyState(SwitchToTemplatesMode);
                }
                else
                {
                    var myCardElements = new List<FrameworkElement>(existingItems.Count);
                    foreach (ExistingWidgetCardItem item in existingItems)
                    {
                        myCardElements.Add(CreateExistingWidgetCard(item));
                    }
                    cardsHostContainer.Child = CreateResponsiveCardsGrid(myCardElements);
                }
            }
            else
            {
                List<WorkshopCardSpec> filtered = _activeGalleryCategory == TemplateGalleryCategory.All
                    ? specs.ToList()
                    : specs.Where(s => s.Category == _activeGalleryCategory).ToList();

                var cardElements = new List<FrameworkElement>(filtered.Count);
                foreach (WorkshopCardSpec spec in filtered)
                {
                    cardElements.Add(CreateTemplateCard(spec));
                }

                cardsHostContainer.Child = CreateResponsiveCardsGrid(cardElements);
            }

            if (animate)
            {
                FluentMotionHelper.PlayEntranceSlideAndFade(cardsHostContainer, offsetY: 6f, durationMs: 150);
            }
        }

        (TemplateGalleryCategory Cat, string Key)[] categories =
        [
            (TemplateGalleryCategory.All, "CustomWidget.Workshop.Category.All"),
            (TemplateGalleryCategory.Habits, "CustomWidget.Workshop.Category.Habits"),
            (TemplateGalleryCategory.Tools, "CustomWidget.Workshop.Category.Tools"),
            (TemplateGalleryCategory.Live, "CustomWidget.Workshop.Category.Live"),
            (TemplateGalleryCategory.Ai, "CustomWidget.Workshop.Category.Ai")
        ];
        suppressFilterEvent = true;
        foreach ((TemplateGalleryCategory cat, string key) in categories)
        {
            string catLabel = _loc.T(key);
            var item = new SelectorBarItem
            {
                Text = catLabel,
                Tag = cat,
                IsSelected = cat == _activeGalleryCategory
            };
            AutomationProperties.SetName(item, catLabel);
            filterBar.Items.Add(item);
        }
        suppressFilterEvent = false;

        galleryModeSegmented.SelectionChanged += (_, _) =>
        {
            if (suppressModeEvent ||
                galleryModeSegmented.SelectedItem is not ToolkitControls.SegmentedItem { Tag: StartGallerySourceMode nextMode } ||
                nextMode == _activeStartGalleryMode)
            {
                return;
            }

            _activeStartGalleryMode = nextMode;
            RenderGallerySection(animate: true);
        };

        filterBar.SelectionChanged += (_, _) =>
        {
            if (suppressFilterEvent || filterBar.SelectedItem?.Tag is not TemplateGalleryCategory selectedCat)
            {
                return;
            }

            _activeGalleryCategory = selectedCat;
            RenderGallerySection(animate: true);
        };

        RenderGallerySection(animate: false);
        column.Children.Add(cardsHostContainer);

        var scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Grid { Children = { column } }
        };
        scroller.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width;
            column.Padding = w switch
            {
                < 860 => new Thickness(24, 20, 24, 28),
                < 1220 => new Thickness(32, 24, 32, 32),
                _ => new Thickness(40, 28, 40, 36)
            };
        };

        SetPage(scroller);

        DispatcherQueue.TryEnqueue(() =>
        {
            ideaBox.Focus(FocusState.Programmatic);
            ideaBox.SelectionStart = ideaBox.Text.Length;
        });
    }

    private static Grid CreateResponsiveCardsGrid(IReadOnlyList<FrameworkElement> cards)
    {
        var grid = new Grid
        {
            ColumnSpacing = 14,
            RowSpacing = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (FrameworkElement card in cards)
        {
            grid.Children.Add(card);
        }

        int activeColumns = 0;
        void Relayout(double availableWidth)
        {
            int targetColumns = CustomWidgetWorkshopFlow.ResolveGalleryColumnCount(availableWidth);
            if (targetColumns == activeColumns || cards.Count == 0)
            {
                return;
            }

            activeColumns = targetColumns;
            grid.ColumnDefinitions.Clear();
            for (int c = 0; c < targetColumns; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            int rowCount = (cards.Count + targetColumns - 1) / targetColumns;
            grid.RowDefinitions.Clear();
            for (int r = 0; r < rowCount; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (int i = 0; i < cards.Count; i++)
            {
                Grid.SetRow(cards[i], i / targetColumns);
                Grid.SetColumn(cards[i], i % targetColumns);
            }
        }

        Relayout(960);
        grid.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0)
            {
                Relayout(e.NewSize.Width);
            }
        };

        return grid;
    }

    private Border CreateComposerCard(
        string initialText,
        string placeholderText,
        string automationName,
        string submitLabel,
        string shortcutHintText,
        double minTextHeight,
        double maxTextHeight,
        bool compactToolbar,
        bool includeCodeToolInMore,
        Action<string>? onTextEdited,
        out TextBox textBox)
        => CreateComposerCard(
            initialText,
            placeholderText,
            automationName,
            submitLabel,
            shortcutHintText,
            minTextHeight,
            maxTextHeight,
            compactToolbar,
            includeCodeToolInMore,
            onTextEdited,
            out textBox,
            out _);

    private Border CreateComposerCard(
        string initialText,
        string placeholderText,
        string automationName,
        string submitLabel,
        string shortcutHintText,
        double minTextHeight,
        double maxTextHeight,
        bool compactToolbar,
        bool includeCodeToolInMore,
        Action<string>? onTextEdited,
        out TextBox textBox,
        out Button submitButtonOut)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var pinnedChipsWrap = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 6,
            VerticalSpacing = 6
        };
        var pinnedChipsHost = new Border
        {
            Padding = new Thickness(12, 10, 12, 0),
            Visibility = Visibility.Collapsed,
            Child = pinnedChipsWrap
        };
        root.Children.Add(pinnedChipsHost);

        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = minTextHeight,
            MaxHeight = maxTextHeight,
            MaxLength = 800,
            FontSize = 14,
            Padding = new Thickness(14, 12, 14, 8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            PlaceholderText = placeholderText,
            Text = initialText
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ApplyBorderlessTextBoxResources(box);
        AutomationProperties.SetName(box, automationName);

        bool isBoxFocused = false;
        box.GotFocus += (_, _) =>
        {
            isBoxFocused = true;
            card.BorderBrush = ThemeBrush("AccentFillColorDefaultBrush");
        };
        box.LostFocus += (_, _) =>
        {
            isBoxFocused = false;
            card.BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush");
        };
        card.PointerEntered += (_, _) =>
        {
            if (!isBoxFocused)
            {
                card.BorderBrush = ThemeBrush("ControlElevationBorderBrush");
            }
        };
        card.PointerExited += (_, _) =>
        {
            if (!isBoxFocused)
            {
                card.BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush");
            }
        };
        box.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            CoreVirtualKeyStates shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if ((shiftState & CoreVirtualKeyStates.Down) != 0)
            {
                return;
            }

            e.Handled = true;
            if (_isInPlaceGenerating && ReferenceEquals(box, _refineTextBox))
            {
                return;
            }

            await BeginGenerationAsync(box.Text);
        };

        Grid.SetRow(box, 1);
        root.Children.Add(box);

        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(12, 0, 12, 0),
            Opacity = 0.6,
            Background = ThemeBrush("DividerStrokeColorDefaultBrush")
        };
        Grid.SetRow(divider, 2);
        root.Children.Add(divider);

        var footer = new Grid
        {
            Padding = new Thickness(10, 7, 10, 8),
            ColumnSpacing = 8
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftBar = new Grid
        {
            ColumnSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        leftBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Action? refreshPasteVisibility = null;
        DropDownButton channelSelector = CreateAiChannelSelectorButton(() => refreshPasteVisibility?.Invoke());
        channelSelector.MaxWidth = 260;
        leftBar.Children.Add(channelSelector);

        var capabilityPickerBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        ToolTipService.SetToolTip(capabilityPickerBtn, _loc.T("CustomWidget.CapabilityPicker.Tooltip"));
        AutomationProperties.SetName(capabilityPickerBtn, _loc.T("CustomWidget.CapabilityPicker.Button"));

        Flyout capabilityFlyout = BuildCapabilityPickerFlyout(box);
        capabilityPickerBtn.Flyout = capabilityFlyout;
        Grid.SetColumn(capabilityPickerBtn, 1);
        leftBar.Children.Add(capabilityPickerBtn);

        void RefreshComposerCapabilityChrome()
        {
            bool isZh = CustomWidgetRemotePromptService.IsChineseCulture(_loc.CurrentCultureName);
            int pinCount = _pinnedCapabilityIds.Count;
            string btnText = pinCount > 0
                ? $"{_loc.T("CustomWidget.CapabilityPicker.ButtonShort")} · {pinCount}"
                : _loc.T(compactToolbar ? "CustomWidget.CapabilityPicker.ButtonShort" : "CustomWidget.CapabilityPicker.Button");
            capabilityPickerBtn.Content = CreateSmallIconLabel("\uE74C", btnText);
            capabilityPickerBtn.BorderBrush = pinCount > 0
                ? ThemeBrush("AccentFillColorDefaultBrush")
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            pinnedChipsWrap.Children.Clear();
            if (pinCount == 0)
            {
                pinnedChipsHost.Visibility = Visibility.Collapsed;
                return;
            }

            pinnedChipsHost.Visibility = Visibility.Visible;
            foreach (string rawId in _pinnedCapabilityIds.ToList())
            {
                CustomWidgetCapabilitySpec? spec = CustomWidgetCapabilityRegistry.GetById(rawId);
                if (spec is null)
                {
                    continue;
                }

                string capturedId = spec.Id;
                string shortCapTitle = StripTrailingCapabilitySuffix(spec.GetTitle(isZh));
                var pillRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var inspectBtn = new Button
                {
                    Height = 24,
                    Padding = new Thickness(6, 0, 4, 0),
                    MinWidth = 0,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    FontSize = 11.5,
                    Content = CreateSmallIconLabel(spec.Glyph, shortCapTitle)
                };
                ToolTipService.SetToolTip(inspectBtn, $"@{spec.Id} — {spec.GetSummary(isZh)}");
                inspectBtn.Click += async (_, _) => await ShowApiDocsDialogInStudioAsync(capturedId);
                pillRow.Children.Add(inspectBtn);

                var unpinBtn = new Button
                {
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 9 }
                };
                unpinBtn.Click += (_, _) => TogglePinnedCapability(capturedId);
                pillRow.Children.Add(unpinBtn);

                pinnedChipsWrap.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(2, 0, 3, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                    Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                    Child = pillRow
                });
            }
        }

        _refreshActiveComposerCapabilityChrome = RefreshComposerCapabilityChrome;
        RefreshComposerCapabilityChrome();

        bool hasConfiguredDirectApi = DeskBoxAiService.Shared.HasAnyConfiguredProvider();
        var quickSetupApiButton = new Button
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Content = CreateSmallIconLabel("\uE945", _loc.T("CustomWidget.Workshop.Composer.QuickSetupApi")),
            Visibility = hasConfiguredDirectApi ? Visibility.Collapsed : Visibility.Visible
        };
        ToolTipService.SetToolTip(quickSetupApiButton, _loc.T("CustomWidget.Workshop.Composer.ConfigKey"));
        AutomationProperties.SetName(quickSetupApiButton, _loc.T("CustomWidget.Workshop.Composer.QuickSetupApi"));
        quickSetupApiButton.Click += async (_, _) => await ShowAiConfigDialogInStudioAsync(null, null);
        Grid.SetColumn(quickSetupApiButton, 2);
        leftBar.Children.Add(quickSetupApiButton);

        var clearButton = new Button
        {
            Height = 28,
            Padding = new Thickness(8, 2, 8, 3),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            Content = _loc.T("CustomWidget.Workshop.Composer.Clear"),
            Visibility = string.IsNullOrWhiteSpace(initialText) ? Visibility.Collapsed : Visibility.Visible
        };
        clearButton.Click += (_, _) =>
        {
            box.Text = string.Empty;
            onTextEdited?.Invoke(string.Empty);
            box.Focus(FocusState.Programmatic);
        };
        Grid.SetColumn(clearButton, 3);
        leftBar.Children.Add(clearButton);

        footer.Children.Add(leftBar);

        var rightBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        Button pasteIcon = CreateIconButton("\uE77F", _loc.T("CustomWidget.Workshop.More.PasteReply"));
        pasteIcon.Width = 32;
        pasteIcon.Height = 32;
        pasteIcon.CornerRadius = new CornerRadius(6);
        pasteIcon.Click += async (_, _) => await PasteReplyManuallyAsync();
        rightBar.Children.Add(pasteIcon);

        var pasteButton = new Button
        {
            Height = 32,
            Padding = new Thickness(11, 0, 11, 0),
            CornerRadius = new CornerRadius(6),
            Content = CreateSmallIconLabel("\uE77F", _loc.T("CustomWidget.Workshop.Composer.PasteReplyShort"))
        };
        ToolTipService.SetToolTip(pasteButton, _loc.T("CustomWidget.Workshop.More.PasteReply"));
        AutomationProperties.SetName(pasteButton, _loc.T("CustomWidget.Workshop.More.PasteReply"));
        pasteButton.Click += async (_, _) => await PasteReplyManuallyAsync();
        rightBar.Children.Add(pasteButton);

        Button moreButton = CreateIconButton("\uE712", _loc.T("CustomWidget.Workshop.More.Tooltip"));
        moreButton.Width = 32;
        moreButton.Height = 32;
        moreButton.CornerRadius = new CornerRadius(6);
        moreButton.Flyout = BuildComposerMoreFlyout(() => box.Text, includeCodeToolInMore);
        rightBar.Children.Add(moreButton);

        string submitTooltip = string.IsNullOrWhiteSpace(shortcutHintText)
            ? submitLabel
            : $"{submitLabel} ({shortcutHintText})";

        Button submitButton;
        if (compactToolbar)
        {
            submitButton = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6)
            };
        }
        else
        {
            submitButton = new Button
            {
                Height = 32,
                Padding = new Thickness(16, 0, 16, 0),
                CornerRadius = new CornerRadius(6),
                Style = AccentButtonStyle(),
                Content = CreateSmallIconLabel("\uE945", submitLabel)
            };
        }

        ToolTipService.SetToolTip(submitButton, submitTooltip);
        AutomationProperties.SetName(submitButton, submitLabel);
        submitButton.Click += async (_, _) =>
        {
            if (_isInPlaceGenerating && ReferenceEquals(submitButton, _refineSubmitButton))
            {
                CancelWaiting();
                return;
            }

            await BeginGenerationAsync(box.Text);
        };
        rightBar.Children.Add(submitButton);

        void RefreshCompactSubmitChrome(string currentText)
        {
            if (!compactToolbar)
            {
                return;
            }

            if (_isInPlaceGenerating && ReferenceEquals(submitButton, _refineSubmitButton))
            {
                string stopLabel = _loc.T("CustomWidget.Workshop.Stream.StopButton");
                submitButton.Style = null;
                submitButton.Background = ThemeBrush("SubtleFillColorSecondaryBrush");
                submitButton.Foreground = ThemeBrush("TextFillColorPrimaryBrush");
                submitButton.Content = new FontIcon { Glyph = "\uE71A", FontSize = 11.5 };
                ToolTipService.SetToolTip(submitButton, stopLabel);
                AutomationProperties.SetName(submitButton, stopLabel);
                return;
            }

            bool hasText = !string.IsNullOrWhiteSpace(currentText);
            if (hasText)
            {
                submitButton.Style = AccentButtonStyle();
                submitButton.ClearValue(Control.BackgroundProperty);
                submitButton.ClearValue(Control.ForegroundProperty);
            }
            else
            {
                submitButton.Style = null;
                submitButton.Background = ThemeBrush("SubtleFillColorSecondaryBrush");
                submitButton.Foreground = ThemeBrush("TextFillColorTertiaryBrush");
            }

            submitButton.Content = new FontIcon { Glyph = "\uE74A", FontSize = 13 };
            ToolTipService.SetToolTip(submitButton, submitTooltip);
            AutomationProperties.SetName(submitButton, submitLabel);
        }

        double latestCardWidth = 760;
        refreshPasteVisibility = () =>
        {
            IReadOnlyList<CustomWidgetAiProviderPreset> configured =
                CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders();
            string? activeDirect = _preferWebAiInSession
                ? null
                : CustomWidgetWorkshopFlow.ResolveDirectProvider(configured, _prefs.PreferredAiProviderId);
            bool isDirectChannel = activeDirect is not null;

            if (isDirectChannel)
            {
                pasteButton.Visibility = Visibility.Collapsed;
                pasteIcon.Visibility = Visibility.Collapsed;
            }
            else if (compactToolbar)
            {
                pasteButton.Visibility = Visibility.Collapsed;
                pasteIcon.Visibility = Visibility.Visible;
            }
            else
            {
                bool showFullPaste = latestCardWidth >= 560;
                pasteButton.Visibility = showFullPaste ? Visibility.Visible : Visibility.Collapsed;
                pasteIcon.Visibility = showFullPaste ? Visibility.Collapsed : Visibility.Visible;
            }
        };

        RefreshCompactSubmitChrome(initialText ?? string.Empty);
        refreshPasteVisibility();

        string prevBoxText = initialText ?? string.Empty;
        box.TextChanged += (_, _) =>
        {
            string current = box.Text ?? string.Empty;
            clearButton.Visibility = string.IsNullOrWhiteSpace(current)
                ? Visibility.Collapsed
                : Visibility.Visible;
            RefreshCompactSubmitChrome(current);
            onTextEdited?.Invoke(current);

            // Power-user '@' trigger: typing '@' at start or after whitespace opens Capability Picker
            if (current.Length == prevBoxText.Length + 1 && current.EndsWith('@'))
            {
                bool afterBoundary = current.Length == 1 || char.IsWhiteSpace(current[^2]);
                if (afterBoundary && !capabilityFlyout.IsOpen)
                {
                    FlyoutBase.ShowAttachedFlyout(capabilityPickerBtn);
                    capabilityFlyout.ShowAt(capabilityPickerBtn);
                }
            }
            prevBoxText = current;
        };

        Grid.SetColumn(rightBar, 1);
        footer.Children.Add(rightBar);

        card.SizeChanged += (_, e) =>
        {
            latestCardWidth = e.NewSize.Width;
            refreshPasteVisibility();
            if (!compactToolbar)
            {
                channelSelector.MaxWidth = Math.Clamp(latestCardWidth - 280, 140, 260);
            }
            else
            {
                channelSelector.MaxWidth = Math.Clamp(latestCardWidth - 200, 128, 228);
            }
        };

        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        card.Child = root;
        textBox = box;
        submitButtonOut = submitButton;
        return card;
    }

    private static string StripTrailingCapabilitySuffix(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return rawTitle;
        }

        int openIdx = rawTitle.LastIndexOf(" (", StringComparison.Ordinal);
        if (openIdx > 0 && rawTitle.EndsWith(')'))
        {
            return rawTitle[..openIdx].Trim();
        }

        return rawTitle;
    }

    private static string FormatCleanChannelLabel(string providerDisplayName, string shortModel)
    {
        if (string.IsNullOrWhiteSpace(providerDisplayName))
        {
            return shortModel;
        }

        string cleanProvider = providerDisplayName.Replace(" ", string.Empty, StringComparison.Ordinal);
        string cleanModel = shortModel.Replace("-", string.Empty, StringComparison.Ordinal)
                                      .Replace("_", string.Empty, StringComparison.Ordinal);
        if (cleanProvider.Length >= 3 &&
            cleanModel.StartsWith(cleanProvider, StringComparison.OrdinalIgnoreCase))
        {
            return shortModel;
        }

        return $"{providerDisplayName} · {shortModel}";
    }

    private static void ApplyBorderlessTextBoxResources(TextBox box)
    {
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        box.Resources["TextControlBackground"] = transparent;
        box.Resources["TextControlBackgroundPointerOver"] = transparent;
        box.Resources["TextControlBackgroundFocused"] = transparent;
        box.Resources["TextControlBackgroundDisabled"] = transparent;
        box.Resources["TextControlBorderBrush"] = transparent;
        box.Resources["TextControlBorderBrushPointerOver"] = transparent;
        box.Resources["TextControlBorderBrushFocused"] = transparent;
        box.Resources["TextControlBorderBrushDisabled"] = transparent;
        box.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
    }

    private DropDownButton CreateAiChannelSelectorButton(Action? onChannelChanged = null)
    {
        var button = new DropDownButton
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(0)
        };

        void RefreshLabel()
        {
            IReadOnlyList<CustomWidgetAiProviderPreset> configured =
                CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders();
            string? directId = _preferWebAiInSession
                ? null
                : CustomWidgetWorkshopFlow.ResolveDirectProvider(configured, _prefs.PreferredAiProviderId);

            if (directId is not null)
            {
                ResolvedAiInvocation resolved = CustomWidgetAiGeneratorService.Shared.ResolveEffectiveSettings(directId);
                string shortModel = resolved.Model.Contains('/')
                    ? resolved.Model[(resolved.Model.LastIndexOf('/') + 1)..]
                    : resolved.Model;
                if (shortModel.Length > 24)
                {
                    shortModel = shortModel[..24] + "…";
                }

                string suffix = resolved.ThinkingMode switch
                {
                    AiThinkingMode.Enabled => " · " + _loc.T("CustomWidget.AiConfig.ThinkingBadgeOn"),
                    AiThinkingMode.Disabled => " · " + _loc.T("CustomWidget.AiConfig.ThinkingBadgeOff"),
                    _ => string.Empty
                };
                string baseLabel = FormatCleanChannelLabel(resolved.Preset.DisplayName, shortModel);
                string text = $"{baseLabel}{suffix}";
                string fullTip = $"{resolved.Preset.DisplayName} · {resolved.Model}{suffix}";
                button.Content = CreateSmallIconLabel("\uE945", text);
                ToolTipService.SetToolTip(button, fullTip);
                AutomationProperties.SetName(button, fullTip);
                onChannelChanged?.Invoke();
                return;
            }

            WebAiTargetPreset web = CustomWidgetStudioPreferencesStore.ResolveWebAiPreset(
                _prefs.ResolveEffectiveWebAiTarget(_loc.CurrentCultureName));
            string webText = web.IsCopyOnly
                ? _loc.T("CustomWidget.Workshop.Composer.ChannelCopyOnly")
                : _loc.Format("CustomWidget.Workshop.Composer.ChannelWeb", web.DisplayName);
            button.Content = CreateSmallIconLabel(web.IsCopyOnly ? "\uE8C8" : "\uE774", webText);
            ToolTipService.SetToolTip(button, webText);
            AutomationProperties.SetName(button, webText);
            onChannelChanged?.Invoke();
        }

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            IReadOnlyList<CustomWidgetAiProviderPreset> configured =
                CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders();
            string? activeDirectId = _preferWebAiInSession
                ? null
                : CustomWidgetWorkshopFlow.ResolveDirectProvider(configured, _prefs.PreferredAiProviderId);

            if (configured.Count > 0)
            {
                foreach (CustomWidgetAiProviderPreset provider in configured)
                {
                    CustomWidgetAiProviderPreset captured = provider;
                    bool isActive = string.Equals(captured.Id, activeDirectId, StringComparison.OrdinalIgnoreCase);
                    ResolvedAiInvocation provResolved = CustomWidgetAiGeneratorService.Shared.ResolveEffectiveSettings(captured.Id);
                    var item = new ToggleMenuFlyoutItem
                    {
                        Text = $"{captured.DisplayName} ({provResolved.Model})",
                        IsChecked = isActive,
                        Icon = new FontIcon { Glyph = "\uE945" }
                    };
                    item.Click += (_, _) =>
                    {
                        _preferWebAiInSession = false;
                        _prefs.SetPreferredAiProviderId(captured.Id);
                        RefreshLabel();
                    };
                    flyout.Items.Add(item);
                }

                if (activeDirectId is not null)
                {
                    CustomWidgetAiProviderPreset activePreset = CustomWidgetAiGeneratorService.FindProviderPreset(activeDirectId);
                    AiProviderCredentialEntry? activeEntry = CustomWidgetAiCredentialStore.Shared.Get(activePreset.Id);
                    ResolvedAiInvocation activeResolved = CustomWidgetAiGeneratorService.Shared.ResolveEffectiveSettings(activePreset.Id);

                    // Quick Model Switcher submenu
                    var modelSub = new MenuFlyoutSubItem
                    {
                        Text = _loc.Format("CustomWidget.Workshop.Composer.ModelMenu", activeResolved.Model),
                        Icon = new FontIcon { Glyph = "\uE950" }
                    };
                    var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(activeEntry?.CustomModel) && seenModels.Add(activeEntry!.CustomModel.Trim()))
                    {
                        AddModelChoiceItem(modelSub, activePreset, activeEntry.CustomModel.Trim(), activeResolved.Model, RefreshLabel);
                    }
                    foreach (string m in activePreset.Models)
                    {
                        if (seenModels.Add(m))
                        {
                            AddModelChoiceItem(modelSub, activePreset, m, activeResolved.Model, RefreshLabel);
                        }
                    }
                    modelSub.Items.Add(new MenuFlyoutSeparator());
                    var customModelItem = new MenuFlyoutItem
                    {
                        Text = _loc.T("CustomWidget.Workshop.Composer.MoreModels"),
                        Icon = new FontIcon { Glyph = "\uE72C" }
                    };
                    customModelItem.Click += async (_, _) => await ShowAiConfigDialogInStudioAsync(activePreset.Id, RefreshLabel);
                    modelSub.Items.Add(customModelItem);
                    flyout.Items.Add(modelSub);

                    // Quick Thinking Mode Switcher submenu
                    AiThinkingMode curMode = CustomWidgetAiGeneratorService.ParseThinkingMode(activeEntry?.ThinkingMode);
                    var thinkSub = new MenuFlyoutSubItem
                    {
                        Text = _loc.T("CustomWidget.AiConfig.ThinkingHeader"),
                        Icon = new FontIcon { Glyph = "\uE945" }
                    };
                    (AiThinkingMode Mode, string LabelKey)[] modes =
                    [
                        (AiThinkingMode.Auto, "CustomWidget.AiConfig.ThinkingAutoShort"),
                        (AiThinkingMode.Enabled, "CustomWidget.AiConfig.ThinkingEnabledShort"),
                        (AiThinkingMode.Disabled, "CustomWidget.AiConfig.ThinkingDisabledShort")
                    ];
                    foreach ((AiThinkingMode modeOption, string labelKey) in modes)
                    {
                        AiThinkingMode capturedMode = modeOption;
                        var mItem = new ToggleMenuFlyoutItem
                        {
                            Text = _loc.T(labelKey),
                            IsChecked = curMode == capturedMode
                        };
                        mItem.Click += (_, _) =>
                        {
                            CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(
                                activePreset.Id,
                                thinkingMode: CustomWidgetAiGeneratorService.ToThinkingWireValue(capturedMode));
                            RefreshLabel();
                        };
                        thinkSub.Items.Add(mItem);
                    }
                    flyout.Items.Add(thinkSub);
                }

                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            string effectiveWebId = _prefs.ResolveEffectiveWebAiTarget(_loc.CurrentCultureName);
            foreach (WebAiTargetPreset target in CustomWidgetStudioPreferencesStore.GetOrderedWebAiTargets(_loc.CurrentCultureName))
            {
                WebAiTargetPreset captured = target;
                bool isSelected = activeDirectId is null &&
                    string.Equals(captured.Id, effectiveWebId, StringComparison.OrdinalIgnoreCase);
                string label = captured.IsCopyOnly
                    ? _loc.T("CustomWidget.Workshop.Composer.ChannelCopyOnly")
                    : _loc.Format("CustomWidget.Workshop.Composer.ChannelWeb", captured.DisplayName);
                var item = new ToggleMenuFlyoutItem
                {
                    Text = label,
                    IsChecked = isSelected,
                    Icon = new FontIcon { Glyph = captured.IsCopyOnly ? "\uE8C8" : "\uE774" }
                };
                item.Click += (_, _) =>
                {
                    _preferWebAiInSession = configured.Count > 0;
                    _prefs.SetPreferredWebAiTarget(captured.Id);
                    RefreshLabel();
                };
                flyout.Items.Add(item);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            var keyItem = new MenuFlyoutItem
            {
                Text = _loc.T("CustomWidget.Workshop.Composer.ConfigKey"),
                Icon = new FontIcon { Glyph = "\uE8D7" }
            };
            keyItem.Click += async (_, _) => await ShowAiConfigDialogInStudioAsync(activeDirectId, RefreshLabel);
            flyout.Items.Add(keyItem);
        };

        button.Flyout = flyout;
        RefreshLabel();
        return button;
    }

    private static void AddModelChoiceItem(
        MenuFlyoutSubItem parent,
        CustomWidgetAiProviderPreset preset,
        string modelId,
        string activeModelId,
        Action refreshLabel)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = modelId,
            IsChecked = string.Equals(modelId, activeModelId, StringComparison.OrdinalIgnoreCase)
        };
        item.Click += (_, _) =>
        {
            string customVal = string.Equals(modelId, preset.DefaultModel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : modelId;
            CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(preset.Id, customModel: customVal);
            refreshLabel();
        };
        parent.Items.Add(item);
    }

    private MenuFlyout BuildComposerMoreFlyout(Func<string> readText, bool includeCodeTool)
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            AddMenuItem(flyout, "\uE8C8", "CustomWidget.Workshop.More.CopyPrompt",
                async () => await BeginGenerationAsync(readText(), copyOnly: true));
            AddMenuItem(flyout, "\uE77F", "CustomWidget.Workshop.More.PasteReply",
                async () => await PasteReplyManuallyAsync());
            if (CustomWidgetAiGeneratorService.Shared.HasAnyConfiguredProvider() && !_preferWebAiInSession)
            {
                AddMenuItem(flyout, "\uE774", "CustomWidget.Workshop.More.UseWebAi",
                    async () => await BeginGenerationAsync(readText(), forceWebAi: true));
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            AddMenuItem(flyout, "\uE8A1", "CustomWidget.ApiDocs.Button",
                async () => await ShowApiDocsDialogInStudioAsync());
            AddMenuItem(flyout, "\uE756", "CustomWidget.Workshop.More.CopyAgentTask",
                async () => await CopyAgentKickoffPromptAsync(readText()));
            if (includeCodeTool)
            {
                AddMenuItem(flyout, "\uE943", "CustomWidget.Workshop.More.CodeTool",
                    async () => await OpenInCodeToolAsync(readText()));
            }
        };
        return flyout;
    }

    private Flyout BuildCapabilityPickerFlyout(TextBox composerBox)
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.TopEdgeAlignedLeft
        };

        var root = new Grid
        {
            Width = 368,
            RowSpacing = 8
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(310) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var searchBox = new AutoSuggestBox
        {
            PlaceholderText = _loc.T("CustomWidget.CapabilityPicker.SearchPlaceholder"),
            QueryIcon = new SymbolIcon(Symbol.Find),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        root.Children.Add(searchBox);

        var listStack = new StackPanel { Spacing = 3, Padding = new Thickness(0, 0, 4, 2) };
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listStack
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        var bottomBar = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(0, 6, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush")
        };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var clearPinsBtn = new Button
        {
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Content = _loc.T("CustomWidget.Workshop.Composer.Clear")
        };
        clearPinsBtn.Click += (_, _) =>
        {
            ClearPinnedCapabilities();
            RenderItems();
        };
        bottomBar.Children.Add(clearPinsBtn);

        var openDocsBtn = new Button
        {
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Content = CreateSmallIconLabel("\uE8A1", _loc.T("CustomWidget.CapabilityPicker.OpenApiDocs"))
        };
        openDocsBtn.Click += async (_, _) =>
        {
            flyout.Hide();
            await ShowApiDocsDialogInStudioAsync();
        };
        Grid.SetColumn(openDocsBtn, 2);
        bottomBar.Children.Add(openDocsBtn);

        Grid.SetRow(bottomBar, 2);
        root.Children.Add(bottomBar);

        void RenderItems()
        {
            listStack.Children.Clear();
            bool isZh = CustomWidgetRemotePromptService.IsChineseCulture(_loc.CurrentCultureName);
            string q = searchBox.Text ?? string.Empty;
            clearPinsBtn.IsEnabled = _pinnedCapabilityIds.Count > 0;

            IReadOnlyList<CustomWidgetCapabilitySpec> matches = CustomWidgetCapabilityRegistry.Search(q);
            if (matches.Count == 0)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = _loc.T("CustomWidget.ApiDocs.EmptySearch"),
                    FontSize = 12,
                    Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
                    Margin = new Thickness(6, 12, 6, 12)
                });
                return;
            }

            CustomWidgetCapabilityCategory[] catOrder =
            [
                CustomWidgetCapabilityCategory.DataSource,
                CustomWidgetCapabilityCategory.Component,
                CustomWidgetCapabilityCategory.Action,
                CustomWidgetCapabilityCategory.ConfigAndSecurity
            ];

            foreach (CustomWidgetCapabilityCategory cat in catOrder)
            {
                List<CustomWidgetCapabilitySpec> group = matches.Where(c => c.Category == cat).ToList();
                if (group.Count == 0)
                {
                    continue;
                }

                listStack.Children.Add(new TextBlock
                {
                    Text = CustomWidgetCapabilityRegistry.GetCategoryDisplayName(cat, isZh),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
                    Margin = new Thickness(4, 6, 4, 2)
                });

                foreach (CustomWidgetCapabilitySpec spec in group)
                {
                    string capturedId = spec.Id;
                    bool pinned = IsCapabilityPinned(spec.Id);

                    var rowGrid = new Grid { ColumnSpacing = 10 };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    rowGrid.Children.Add(new FontIcon
                    {
                        Glyph = spec.Glyph,
                        FontSize = 14,
                        Foreground = pinned
                            ? ThemeBrush("AccentTextFillColorPrimaryBrush")
                            : ThemeBrush("TextFillColorSecondaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    var textCol = new StackPanel { Spacing = 1 };
                    var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    titleLine.Children.Add(new TextBlock
                    {
                        Text = StripTrailingCapabilitySuffix(spec.GetTitle(isZh)),
                        FontSize = 12.5,
                        FontWeight = pinned ? FontWeights.SemiBold : FontWeights.Normal
                    });
                    titleLine.Children.Add(new TextBlock
                    {
                        Text = "@" + spec.Id,
                        FontSize = 10.5,
                        FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                        Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    textCol.Children.Add(titleLine);

                    textCol.Children.Add(new TextBlock
                    {
                        Text = spec.GetSummary(isZh),
                        FontSize = 11,
                        Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    Grid.SetColumn(textCol, 1);
                    rowGrid.Children.Add(textCol);

                    var checkIcon = new FontIcon
                    {
                        Glyph = pinned ? "\uE73E" : "\uE710",
                        FontSize = 12,
                        Foreground = pinned
                            ? ThemeBrush("AccentTextFillColorPrimaryBrush")
                            : ThemeBrush("TextFillColorTertiaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(checkIcon, 2);
                    rowGrid.Children.Add(checkIcon);

                    var itemBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(8, 6, 8, 6),
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(pinned ? 1 : 0),
                        BorderBrush = pinned
                            ? ThemeBrush("AccentFillColorDefaultBrush")
                            : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Background = pinned
                            ? ThemeBrush("SubtleFillColorSecondaryBrush")
                            : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        Content = rowGrid
                    };
                    itemBtn.Click += (_, _) =>
                    {
                        // Strip trailing '@' if user opened picker via '@' keystroke
                        if (!string.IsNullOrEmpty(composerBox.Text) && composerBox.Text.EndsWith('@'))
                        {
                            composerBox.Text = composerBox.Text[..^1];
                            composerBox.SelectionStart = composerBox.Text.Length;
                        }

                        TogglePinnedCapability(capturedId);
                        RenderItems();
                    };
                    listStack.Children.Add(itemBtn);
                }
            }
        }

        searchBox.TextChanged += (_, _) => RenderItems();
        flyout.Opening += (_, _) =>
        {
            searchBox.Text = string.Empty;
            RenderItems();
        };

        flyout.Content = root;
        return flyout;
    }

    private FrameworkElement CreateTemplateCard(WorkshopCardSpec spec)
    {
        string label = !string.IsNullOrWhiteSpace(spec.DirectLabel) ? spec.DirectLabel! : _loc.T(spec.LabelKey);
        string description = !string.IsNullOrWhiteSpace(spec.DirectDescription) ? spec.DirectDescription! : _loc.T(spec.DescriptionKey);

        var container = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var card = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            IsEnabled = !spec.RequiresClientUpgrade
        };
        AutomationProperties.SetName(card, label);

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(164) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewWell = new Border
        {
            Background = ThemeBrush("LayerOnMicaBaseAltFillColorSecondaryBrush"),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            IsHitTestVisible = false
        };

        Viewbox? previewViewbox = null;
        try
        {
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(spec.ManifestJson);
            CustomWidgetSizeSpec rawSize = parsed.Manifest?.DefaultSize ?? new CustomWidgetSizeSpec();
            var thumbSize = new CustomWidgetSizeSpec
            {
                Width = Math.Clamp(rawSize.Width, 300, 356),
                Height = Math.Clamp(rawSize.Height, 224, 268)
            };
            var vm = new CustomWidgetViewModel(
                new WidgetConfig
                {
                    Id = "__studio_template_" + spec.Id,
                    Name = label,
                    WidgetKind = WidgetKind.Custom,
                    Width = thumbSize.Width,
                    Height = thumbSize.Height
                },
                _loc,
                store: null,
                dispatcherQueue: DispatcherQueue,
                notificationServiceProvider: null,
                isPreviewOnly: true);
            vm.ApplyPreviewManifestJson(spec.ManifestJson, preserveRuntimeData: false);
            var content = new CustomWidgetContent(vm);
            _templatePreviews.Add((vm, content));

            Border thumbFrame = CreateWidgetFrame(
                spec.Emoji,
                label,
                content,
                thumbSize,
                clipViewport: true,
                out _,
                out _);
            previewViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(14, 12, 14, 12),
                Child = thumbFrame
            };
            previewWell.Child = previewViewbox;
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Template preview failed for {spec.Id}: {ex.Message}");
            previewWell.Child = new TextBlock
            {
                Text = spec.Emoji,
                FontSize = 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        cardGrid.Children.Add(previewWell);

        var textStack = new StackPanel { Spacing = 4, Padding = new Thickness(14, 12, 14, 14) };
        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = spec.Emoji + "  " + label,
            Style = TextStyle("BodyStrongTextBlockStyle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        });
        var arrowIcon = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 11,
            Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(arrowIcon, 1);
        titleRow.Children.Add(arrowIcon);
        textStack.Children.Add(titleRow);

        textStack.Children.Add(new TextBlock
        {
            Text = spec.RequiresClientUpgrade
                ? _loc.Format("CustomWidget.ClientUpgrade.CardHint", spec.UpgradeRequirementLabel)
                : description,
            Style = TextStyle("CaptionTextBlockStyle"),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            MinHeight = 34
        });

        Grid.SetRow(textStack, 1);
        cardGrid.Children.Add(textStack);
        card.Content = cardGrid;

        card.Click += (_, _) =>
        {
            if (spec.RequiresClientUpgrade || string.IsNullOrWhiteSpace(spec.ManifestJson))
            {
                return;
            }

            _selectedEmoji = spec.Emoji;
            _pendingPrompt = string.Empty;
            AddRevision(
                spec.ManifestJson,
                _loc.Format("CustomWidget.Workshop.History.FromTemplate", label),
                isAiChange: false,
                source: "template",
                isCommitted: false,
                markDirty: false);
            ShowPreviewPage(justLoaded: true);
            DispatcherQueue.TryEnqueue(() => _refineTextBox?.Focus(FocusState.Programmatic));
        };

        container.Children.Add(card);

        if (!spec.RequiresClientUpgrade && !string.IsNullOrWhiteSpace(spec.ManifestJson))
        {
            string quickPlaceTip = _loc.T("CustomWidget.Workshop.Card.QuickPlaceTip");
            var quickPlaceBtn = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 10, 10, 0),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = ThemeBrush("SolidBackgroundFillColorBaseBrush"),
                BorderBrush = ThemeBrush("ControlElevationBorderBrush"),
                BorderThickness = new Thickness(1),
                Opacity = 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
                Content = new FontIcon
                {
                    Glyph = "\uE710",
                    FontSize = 13,
                    Foreground = ThemeBrush("TextFillColorPrimaryBrush")
                }
            };
            Canvas.SetZIndex(quickPlaceBtn, 2);
            ToolTipService.SetToolTip(quickPlaceBtn, quickPlaceTip);
            AutomationProperties.SetName(quickPlaceBtn, $"{label} — {quickPlaceTip}");
            quickPlaceBtn.Click += async (_, _) =>
                await PlaceTemplateDirectlyAsync(spec.ManifestJson, spec.Emoji, quickPlaceBtn);
            container.Children.Add(quickPlaceBtn);

            bool isPointerOverContainer = false;
            void RefreshHoverVisuals()
            {
                bool showAction = isPointerOverContainer ||
                                  card.FocusState != FocusState.Unfocused ||
                                  quickPlaceBtn.FocusState != FocusState.Unfocused;
                quickPlaceBtn.Opacity = showAction ? 1 : 0;
                if (previewViewbox is not null)
                {
                    FluentMotionHelper.AnimateHoverScale(previewViewbox, isPointerOverContainer ? 1.018f : 1.0f, 150);
                }
            }

            container.PointerEntered += (_, _) =>
            {
                isPointerOverContainer = true;
                RefreshHoverVisuals();
            };
            container.PointerExited += (_, _) =>
            {
                isPointerOverContainer = false;
                RefreshHoverVisuals();
            };
            card.GotFocus += (_, _) => RefreshHoverVisuals();
            card.LostFocus += (_, _) => RefreshHoverVisuals();
            quickPlaceBtn.GotFocus += (_, _) => RefreshHoverVisuals();
            quickPlaceBtn.LostFocus += (_, _) => RefreshHoverVisuals();
        }

        return container;
    }

    private Button CreateSubtleApiDocsToggleButton(
        string labelKey,
        double fontSize,
        double horizontalPadding)
    {
        var icon = new FontIcon
        {
            Glyph = "\uE8A1",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = _loc.T(labelKey),
            FontSize = fontSize,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var contentRow = new Grid { ColumnSpacing = 6 };
        contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentRow.Children.Add(icon);
        Grid.SetColumn(label, 1);
        contentRow.Children.Add(label);

        var button = new Button
        {
            Height = 32,
            Padding = new Thickness(horizontalPadding, 0, horizontalPadding, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Content = contentRow
        };
        ToolTipService.SetToolTip(button, _loc.T("CustomWidget.ApiDocs.Tooltip"));
        AutomationProperties.SetName(button, _loc.T("CustomWidget.ApiDocs.Button"));

        void ApplyVisual()
        {
            bool open = _isApiDocsDrawerOpen;
            button.Background = ThemeBrush(open ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush");
            button.BorderBrush = ThemeBrush(open ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
            icon.Foreground = ThemeBrush(open ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            label.Foreground = ThemeBrush("TextFillColorPrimaryBrush");
        }

        _refreshApiDocsToggleButtons = ApplyVisual;
        ApplyVisual();
        button.ActualThemeChanged += (_, _) => ApplyVisual();
        button.Click += async (_, _) =>
        {
            await ShowApiDocsDialogInStudioAsync();
            ApplyVisual();
        };

        return button;
    }

    private FrameworkElement CreateExistingWidgetCard(ExistingWidgetCardItem item)
    {
        var container = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var card = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(8)
        };
        AutomationProperties.SetName(card, item.Name);

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(164) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewWell = new Border
        {
            Background = ThemeBrush("LayerOnMicaBaseAltFillColorSecondaryBrush"),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            IsHitTestVisible = false
        };

        Viewbox? previewViewbox = null;
        try
        {
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(item.ManifestJson);
            CustomWidgetSizeSpec rawSize = parsed.Manifest?.DefaultSize ?? new CustomWidgetSizeSpec();
            var thumbSize = new CustomWidgetSizeSpec
            {
                Width = Math.Clamp(rawSize.Width, 300, 356),
                Height = Math.Clamp(rawSize.Height, 224, 268)
            };
            var vm = new CustomWidgetViewModel(
                new WidgetConfig
                {
                    Id = "__studio_mywidget_" + item.Id,
                    Name = item.Name,
                    WidgetKind = WidgetKind.Custom,
                    Width = thumbSize.Width,
                    Height = thumbSize.Height
                },
                _loc,
                store: null,
                dispatcherQueue: DispatcherQueue,
                notificationServiceProvider: null,
                isPreviewOnly: true);
            vm.ApplyPreviewManifestJson(item.ManifestJson, preserveRuntimeData: false);
            var content = new CustomWidgetContent(vm);
            _templatePreviews.Add((vm, content));

            Border thumbFrame = CreateWidgetFrame(
                item.Emoji,
                item.Name,
                content,
                thumbSize,
                clipViewport: true,
                out _,
                out _);
            previewViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(14, 12, 14, 12),
                Child = thumbFrame
            };
            previewWell.Child = previewViewbox;
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Existing widget preview failed for {item.Id}: {ex.Message}");
            previewWell.Child = new TextBlock
            {
                Text = item.Emoji,
                FontSize = 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        cardGrid.Children.Add(previewWell);

        var textStack = new StackPanel { Spacing = 4, Padding = new Thickness(14, 12, 14, 14) };
        var titleRow = new Grid { ColumnSpacing = 6 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = item.Emoji + "  " + item.Name,
            Style = TextStyle("BodyStrongTextBlockStyle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        });

        var rightMetaRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        bool isVisibleLive = item.IsVisibleOnDesktop;
        var statusText = new TextBlock
        {
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = statusText
        };
        void RefreshStatusVisual()
        {
            statusText.Text = _loc.T(isVisibleLive
                ? "CustomWidget.Studio.Card.StatusVisible"
                : "CustomWidget.Studio.Card.StatusHidden");
            statusText.Foreground = ThemeBrush(isVisibleLive
                ? "AccentTextFillColorPrimaryBrush"
                : "TextFillColorTertiaryBrush");
            statusBadge.Background = ThemeBrush(isVisibleLive
                ? "SubtleFillColorSecondaryBrush"
                : "SubtleFillColorTertiaryBrush");
        }
        RefreshStatusVisual();
        rightMetaRow.Children.Add(statusBadge);

        if (item.RevisionCount > 1)
        {
            rightMetaRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2, 5, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                Child = new TextBlock
                {
                    Text = $"v{item.RevisionCount}",
                    FontSize = 10.5,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush")
                }
            });
        }

        rightMetaRow.Children.Add(new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 11,
            Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(rightMetaRow, 1);
        titleRow.Children.Add(rightMetaRow);
        textStack.Children.Add(titleRow);

        textStack.Children.Add(new TextBlock
        {
            Text = item.Summary,
            Style = TextStyle("CaptionTextBlockStyle"),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            MinHeight = 34
        });

        Grid.SetRow(textStack, 1);
        cardGrid.Children.Add(textStack);
        card.Content = cardGrid;

        card.Click += (_, _) =>
        {
            _activeStartGalleryMode = StartGallerySourceMode.MyWidgets;
            Initialize(item.Id, initialIdea: null);
            DispatcherQueue.TryEnqueue(() => _refineTextBox?.Focus(FocusState.Programmatic));
        };

        container.Children.Add(card);

        string locateTip = _loc.T("CustomWidget.Workshop.MyWidgets.LocateTip");
        var quickLocateBtn = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 10, 10, 0),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = ThemeBrush("SolidBackgroundFillColorBaseBrush"),
            BorderBrush = ThemeBrush("ControlElevationBorderBrush"),
            BorderThickness = new Thickness(1),
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
            Content = new FontIcon
            {
                Glyph = "\uE8A7",
                FontSize = 12.5,
                Foreground = ThemeBrush("TextFillColorPrimaryBrush")
            }
        };
        Canvas.SetZIndex(quickLocateBtn, 2);
        ToolTipService.SetToolTip(quickLocateBtn, locateTip);
        AutomationProperties.SetName(quickLocateBtn, $"{item.Name} — {locateTip}");
        quickLocateBtn.Click += async (_, _) =>
        {
            WidgetManager? manager = App.Current.WidgetManager;
            if (manager is null)
            {
                return;
            }

            quickLocateBtn.IsEnabled = false;
            try
            {
                if (!isVisibleLive)
                {
                    await manager.SetCustomWidgetInstanceEnabledAsync(item.Id, true);
                    isVisibleLive = true;
                    RefreshStatusVisual();
                }

                await manager.LocateCustomWidgetAsync(item.Id);
            }
            finally
            {
                quickLocateBtn.IsEnabled = true;
            }
        };
        container.Children.Add(quickLocateBtn);

        bool isPointerOverContainer = false;
        void RefreshHoverVisuals()
        {
            bool showAction = isPointerOverContainer ||
                              card.FocusState != FocusState.Unfocused ||
                              quickLocateBtn.FocusState != FocusState.Unfocused;
            quickLocateBtn.Opacity = showAction ? 1 : 0;
            if (previewViewbox is not null)
            {
                FluentMotionHelper.AnimateHoverScale(previewViewbox, isPointerOverContainer ? 1.018f : 1.0f, 150);
            }
        }

        container.PointerEntered += (_, _) =>
        {
            isPointerOverContainer = true;
            RefreshHoverVisuals();
        };
        container.PointerExited += (_, _) =>
        {
            isPointerOverContainer = false;
            RefreshHoverVisuals();
        };
        card.GotFocus += (_, _) => RefreshHoverVisuals();
        card.LostFocus += (_, _) => RefreshHoverVisuals();
        quickLocateBtn.GotFocus += (_, _) => RefreshHoverVisuals();
        quickLocateBtn.LostFocus += (_, _) => RefreshHoverVisuals();

        return container;
    }

    private FrameworkElement CreateMyWidgetsEmptyState(Action onBrowseTemplates)
    {
        var browseBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            Content = CreateSmallIconLabel("\uE8A9", _loc.T("CustomWidget.Workshop.MyWidgets.BrowseTemplates"))
        };
        browseBtn.Click += (_, _) => onBrowseTemplates();

        var centerStack = new StackPanel
        {
            Spacing = 6,
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        centerStack.Children.Add(new FontIcon
        {
            Glyph = "\uE74C",
            FontSize = 26,
            Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        });
        centerStack.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.MyWidgets.EmptyTitle"),
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        centerStack.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.MyWidgets.EmptySubtitle"),
            Style = TextStyle("CaptionTextBlockStyle"),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        centerStack.Children.Add(browseBtn);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
            Padding = new Thickness(24, 42, 24, 42),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = centerStack
        };
    }

    // ── Stage Backdrop, Blueprint Placeholder & Zero-Bounce Updates ─────

    private async Task EnsureWallpaperLoadedAsync()
    {
        if (_wallpaperLoadAttempted || !_hasSystemWallpaperAvailable)
        {
            return;
        }

        _wallpaperLoadAttempted = true;
        try
        {
            string? path = DesktopWallpaperReadOps.TryResolveCurrentWallpaperImagePath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            byte[]? bytes = await Task.Run(() =>
            {
                using FileStream? fs = DesktopWallpaperReadOps.TryOpenSharedReadStream(path);
                if (fs is null || fs.Length <= 0 || fs.Length > 28 * 1024 * 1024)
                {
                    return null;
                }

                using var ms = new MemoryStream((int)fs.Length);
                fs.CopyTo(ms);
                return ms.ToArray();
            });

            if (bytes is null || bytes.Length == 0 || _isClosed)
            {
                return;
            }

            var bmp = new BitmapImage { DecodePixelWidth = 680 };
            using var memStream = new MemoryStream(bytes, writable: false);
            await bmp.SetSourceAsync(memStream.AsRandomAccessStream());
            if (_isClosed)
            {
                return;
            }

            _cachedWallpaperBitmap = bmp;
            ApplyStageBackdropVisuals();
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetStudio] Load wallpaper backdrop failed: {ex.Message}");
        }
    }

    private void ApplyStageBackdropVisuals()
    {
        if (_stageBorder is null)
        {
            return;
        }

        StageBackdropMode mode = _stageBackdropMode == StageBackdropMode.Wallpaper && !_hasSystemWallpaperAvailable
            ? (Win32Helper.IsSystemDarkMode() ? StageBackdropMode.Dark : StageBackdropMode.Light)
            : _stageBackdropMode;

        if (mode == StageBackdropMode.Wallpaper)
        {
            _stageBorder.RequestedTheme = ElementTheme.Dark;
            _stageBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x16, 0x1A, 0x22));
            if (_stageWallpaperLayer is not null)
            {
                if (_cachedWallpaperBitmap is not null)
                {
                    _stageWallpaperLayer.Background = new ImageBrush
                    {
                        ImageSource = _cachedWallpaperBitmap,
                        Stretch = Stretch.UniformToFill
                    };
                    _stageWallpaperLayer.Visibility = Visibility.Visible;
                }
                else
                {
                    _stageWallpaperLayer.Visibility = Visibility.Collapsed;
                }
            }

            if (_stageWallpaperScrim is not null)
            {
                _stageWallpaperScrim.Visibility = Visibility.Visible;
            }

            if (_widgetFrame is not null)
            {
                _widgetFrame.RequestedTheme = RootGrid.ActualTheme;
            }
            return;
        }

        if (_stageWallpaperLayer is not null)
        {
            _stageWallpaperLayer.Visibility = Visibility.Collapsed;
        }
        if (_stageWallpaperScrim is not null)
        {
            _stageWallpaperScrim.Visibility = Visibility.Collapsed;
        }

        bool isDark = mode == StageBackdropMode.Dark;
        ElementTheme targetTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        _stageBorder.RequestedTheme = targetTheme;
        _stageBorder.Background = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x18, 0x1A, 0x20))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF4, 0xF8));
        if (_widgetFrame is not null)
        {
            _widgetFrame.RequestedTheme = targetTheme;
        }
    }

    private Border BuildBlueprintStagePlaceholder()
    {
        string initialTitle = !string.IsNullOrWhiteSpace(_inFlightDetectedWidgetName)
            ? _inFlightDetectedWidgetName
            : (_isInPlaceGenerating
                ? _loc.T("CustomWidget.Workshop.Stage.BlueprintPreparing")
                : (!string.IsNullOrWhiteSpace(_pendingPrompt)
                    ? CustomWidgetWorkshopFlow.SummarizePrompt(_pendingPrompt, 32)
                    : _loc.T("CustomWidget.Title")));

        var frame = new Border
        {
            Width = 320,
            MinHeight = 228,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Background = ThemeBrush("SolidBackgroundFillColorTertiaryBrush"),
            RequestedTheme = _stageBackdropMode switch
            {
                StageBackdropMode.Light => ElementTheme.Light,
                StageBackdropMode.Dark => ElementTheme.Dark,
                _ => RootGrid.ActualTheme
            }
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        head.Children.Add(new TextBlock
        {
            Text = ResolveDisplayEmoji(null),
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        _blueprintTitleText = new TextBlock
        {
            Text = initialTitle,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        head.Children.Add(_blueprintTitleText);
        grid.Children.Add(head);

        var divider = new Border { Background = ThemeBrush("DividerStrokeColorDefaultBrush") };
        Grid.SetRow(divider, 1);
        grid.Children.Add(divider);

        var bodyStack = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(16, 18, 16, 18),
            VerticalAlignment = VerticalAlignment.Center
        };
        bodyStack.Children.Add(new Border
        {
            Height = 16,
            Width = 176,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(5),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush")
        });
        bodyStack.Children.Add(new Border
        {
            Height = 46,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(7),
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush")
        });
        var bottomRow = new Grid { ColumnSpacing = 8 };
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomRow.Children.Add(new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush")
        });
        var rightBlock = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Background = ThemeBrush("SubtleFillColorTertiaryBrush")
        };
        Grid.SetColumn(rightBlock, 1);
        bottomRow.Children.Add(rightBlock);
        bodyStack.Children.Add(bottomRow);

        Grid.SetRow(bodyStack, 2);
        grid.Children.Add(bodyStack);
        frame.Child = grid;
        return frame;
    }

    private void RefreshInPlaceGenerationChrome(bool refreshStream = true)
    {
        if (_stageGeneratingProgressBar is not null)
        {
            _stageGeneratingProgressBar.IsIndeterminate = _isInPlaceGenerating;
            _stageGeneratingProgressBar.Visibility = _isInPlaceGenerating ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_stageGeneratingStatusPill is not null && _stageGeneratingStatusText is not null)
        {
            if (_isInPlaceGenerating)
            {
                _stageGeneratingStatusText.Text = _waitingKind == WaitingKind.Web
                    ? _loc.T("CustomWidget.Workshop.Stage.StatusWaitingWeb")
                    : _loc.T("CustomWidget.Workshop.Stage.StatusUpdating");
                _stageGeneratingStatusPill.Visibility = Visibility.Visible;
            }
            else
            {
                _stageGeneratingStatusPill.Visibility = Visibility.Collapsed;
            }
        }

        if (_refineSubmitButton is not null)
        {
            if (_isInPlaceGenerating)
            {
                string stopLabel = _loc.T("CustomWidget.Workshop.Stream.StopButton");
                _refineSubmitButton.Style = null;
                _refineSubmitButton.Background = ThemeBrush("SubtleFillColorSecondaryBrush");
                _refineSubmitButton.Foreground = ThemeBrush("TextFillColorPrimaryBrush");
                _refineSubmitButton.Content = new FontIcon { Glyph = "\uE71A", FontSize = 11.5 };
                ToolTipService.SetToolTip(_refineSubmitButton, stopLabel);
                AutomationProperties.SetName(_refineSubmitButton, stopLabel);
            }
            else
            {
                string sendLabel = _loc.T("CustomWidget.Workshop.Refine.Button");
                string hint = _loc.T("CustomWidget.Workshop.Composer.RefineHint");
                bool hasText = !string.IsNullOrWhiteSpace(_refineTextBox?.Text);
                if (hasText)
                {
                    _refineSubmitButton.Style = AccentButtonStyle();
                    _refineSubmitButton.ClearValue(Control.BackgroundProperty);
                    _refineSubmitButton.ClearValue(Control.ForegroundProperty);
                }
                else
                {
                    _refineSubmitButton.Style = null;
                    _refineSubmitButton.Background = ThemeBrush("SubtleFillColorSecondaryBrush");
                    _refineSubmitButton.Foreground = ThemeBrush("TextFillColorTertiaryBrush");
                }

                _refineSubmitButton.Content = new FontIcon { Glyph = "\uE74A", FontSize = 13 };
                ToolTipService.SetToolTip(_refineSubmitButton, string.IsNullOrWhiteSpace(hint) ? sendLabel : $"{sendLabel} ({hint})");
                AutomationProperties.SetName(_refineSubmitButton, sendLabel);
            }
        }

        if (_refineTextBox is not null)
        {
            _refineTextBox.IsEnabled = !(_isInPlaceGenerating && _waitingKind == WaitingKind.Direct);
        }

        if (_commitButton is not null)
        {
            _commitButton.IsEnabled = !_isInPlaceGenerating && !string.IsNullOrWhiteSpace(CurrentManifestJson);
        }

        if (_undoButton is not null)
        {
            _undoButton.IsEnabled = !_isInPlaceGenerating && _revisionIndex > 0;
        }

        if (_redoButton is not null)
        {
            _redoButton.IsEnabled = !_isInPlaceGenerating && _revisionIndex < _revisions.Count - 1;
        }

        if (refreshStream)
        {
            RefreshCoCreationStream();
        }
    }

    private void RefreshSampleDataBadgeChrome()
    {
        if (_sampleDataToggleButton is null)
        {
            return;
        }

        bool hasSample = !IsEditMode &&
                         !string.IsNullOrWhiteSpace(CurrentManifestJson) &&
                         (!string.IsNullOrWhiteSpace(_previewSampleDataJson) || (_previewVm?.HasPreviewSampleData ?? false));
        if (!hasSample)
        {
            _sampleDataToggleButton.Visibility = Visibility.Collapsed;
            return;
        }

        string shortLabel = _isSampleDataMutedInPreview
            ? _loc.T("CustomWidget.Workshop.CoCreate.EmptyStateShort")
            : _loc.T("CustomWidget.Workshop.CoCreate.SampleDataShort");
        string fullStatus = _isSampleDataMutedInPreview
            ? _loc.T("CustomWidget.Workshop.CoCreate.EmptyStateBadge")
            : _loc.T("CustomWidget.Workshop.CoCreate.SampleDataBadge");
        string glyph = _isSampleDataMutedInPreview ? "\uE7B8" : "\uE9D9";

        _sampleDataToggleButton.Content = CreateSmallIconLabel(glyph, shortLabel);
        ToolTipService.SetToolTip(
            _sampleDataToggleButton,
            $"{fullStatus}\n{_loc.T("CustomWidget.Workshop.CoCreate.SampleDataToggleTip")}");
        AutomationProperties.SetName(_sampleDataToggleButton, fullStatus);
        _sampleDataToggleButton.Visibility = Visibility.Visible;
    }

    private void ToggleSampleDataPreviewMode()
    {
        string? curJson = CurrentManifestJson;
        if (_previewVm is null || string.IsNullOrWhiteSpace(curJson))
        {
            return;
        }

        _isSampleDataMutedInPreview = !_isSampleDataMutedInPreview;
        if (_isSampleDataMutedInPreview)
        {
            _previewVm.ClearPreviewSampleData();
            _previewVm.ApplyPreviewManifestJson(curJson, preserveRuntimeData: false);
        }
        else
        {
            _previewVm.ApplyPreviewManifestJson(curJson, preserveRuntimeData: false);
            if (!string.IsNullOrWhiteSpace(_previewSampleDataJson))
            {
                _previewVm.ApplyPreviewSampleDataJson(_previewSampleDataJson);
            }
        }

        RefreshSampleDataBadgeChrome();
    }

    private void ApplyLivePreviewUpdateInPlace()
    {
        string? json = CurrentManifestJson;
        if (string.IsNullOrWhiteSpace(json) || _previewVm is null || _previewContent is null)
        {
            ShowPreviewPage(justLoaded: true);
            return;
        }

        _previewVm.ApplyPreviewManifestJson(json, preserveRuntimeData: true);
        if (!IsEditMode && !string.IsNullOrWhiteSpace(_previewSampleDataJson) && !_isSampleDataMutedInPreview)
        {
            _previewVm.ApplyPreviewSampleDataJson(_previewSampleDataJson);
        }
        else if (_isSampleDataMutedInPreview)
        {
            _previewVm.ClearPreviewSampleData();
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(json);
        CustomWidgetSizeSpec nextSize = parsed.Manifest?.DefaultSize ?? new CustomWidgetSizeSpec();
        _currentPreviewWidgetWidth = nextSize.Width;

        if (_widgetFrame is not null)
        {
            _widgetFrame.Width = Math.Clamp(nextSize.Width, 240, 440);
            _widgetFrame.Height = Math.Clamp(nextSize.Height, 180, 560);
        }

        if (_sizeQuickButton is not null)
        {
            _sizeQuickButton.IsEnabled = true;
            _sizeQuickButton.Content = CreateSmallIconLabel("\uE740", $"{nextSize.Width}×{nextSize.Height}");
        }

        if (_stageColumn is not null && !_stageWidthManuallyAdjusted)
        {
            _stageColumn.Width = new GridLength(
                CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(_lastPreviewWidth, _currentPreviewWidgetWidth));
        }

        string nextTitle = parsed.Manifest?.ResolveLocalizedName(_loc.CurrentCultureName) ?? _loc.T("CustomWidget.Title");
        string nextEmoji = ResolveDisplayEmoji(parsed.Manifest);
        if (_headerEmojiText is not null)
        {
            _headerEmojiText.Text = nextEmoji;
        }

        if (_nameText is not null)
        {
            _nameText.Text = nextTitle;
        }

        if (_frameEmojiText is not null)
        {
            _frameEmojiText.Text = nextEmoji;
        }

        if (_frameTitleText is not null)
        {
            _frameTitleText.Text = nextTitle;
        }

        _latestHealthReport = BuildCurrentHealthReport(json);
        RefreshTitleBarContextBadge();
        RefreshSampleDataBadgeChrome();
        RefreshRevisionChrome();
        RefreshInPlaceGenerationChrome(refreshStream: false);
        RefreshTargetNodePillChrome();
        RefreshSuggestionChips();
        _ = PulseStageAsync();
    }

    private UIElement CreateStepRow(string badge, string text)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrush("AccentFillColorDefaultBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = badge,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("TextOnAccentFillColorPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        var label = new TextBlock
        {
            Text = text,
            Style = TextStyle("BodyTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    // ── Preview page & Co-Creation Stage ────────────────────────────────

    private InfoBar? CreateDraftRecoveryBannerIfNeeded()
    {
        if (_pendingRecoveryDraft is not { } draft)
        {
            return null;
        }

        var continueBtn = new Button
        {
            Content = _loc.T("CustomWidget.Workshop.CoCreate.DraftContinue"),
            CornerRadius = new CornerRadius(6)
        };
        continueBtn.Click += (_, _) => RestoreFromDraftSnapshot(draft);

        var bar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            IsClosable = true,
            IsOpen = true,
            Message = _loc.T("CustomWidget.Workshop.CoCreate.DraftRecoveryMessage"),
            ActionButton = continueBtn
        };
        bar.CloseButtonClick += (_, _) =>
        {
            DiscardActiveDraftCleanly();
            bar.Visibility = Visibility.Collapsed;
        };
        return bar;
    }

    private void ShowPreviewPage(bool justLoaded)
    {
        string? json = CurrentManifestJson;
        if (json is null && !_isInPlaceGenerating && _chatMessages.Count == 0)
        {
            ShowStartPage(_startDraftIdea);
            return;
        }

        DisposeTemplatePreviews();
        DisposePreviewContentOnly();
        _page = StudioPage.Preview;

        bool hasLiveManifest = !string.IsNullOrWhiteSpace(json);
        CustomWidgetParseResult? parsed = hasLiveManifest ? CustomWidgetManifestParser.Parse(json!) : null;
        CustomWidgetSizeSpec size = parsed?.Manifest?.DefaultSize ?? new CustomWidgetSizeSpec();
        _currentPreviewWidgetWidth = size.Width;

        if (hasLiveManifest)
        {
            if (_previewVm is null)
            {
                _previewVm = new CustomWidgetViewModel(
                    new WidgetConfig
                    {
                        Id = IsEditMode ? _targetWidgetId! : "__studio_preview__",
                        Name = _targetWidgetName ?? _loc.T("CustomWidget.Title"),
                        WidgetKind = WidgetKind.Custom,
                        Width = size.Width,
                        Height = size.Height
                    },
                    _loc,
                    store: null,
                    dispatcherQueue: DispatcherQueue,
                    notificationServiceProvider: null,
                    isPreviewOnly: true);
                _previewVm.FeedbackRequested += OnPreviewVmFeedbackRequested;
            }

            _previewVm.ApplyPreviewManifestJson(json!, preserveRuntimeData: true);
            if (!IsEditMode && !string.IsNullOrWhiteSpace(_previewSampleDataJson) && !_isSampleDataMutedInPreview)
            {
                _previewVm.ApplyPreviewSampleDataJson(_previewSampleDataJson);
            }
            else if (_isSampleDataMutedInPreview)
            {
                _previewVm.ClearPreviewSampleData();
            }

            _previewContent = new CustomWidgetContent(_previewVm)
            {
                IsNodeSelectionEnabled = _isInspectorModeEnabled,
                SelectedNodePath = _selectedTargetNode?.NodePath
            };
            _previewContent.NodePicked += OnPreviewNodePicked;
            _previewContent.Loaded += OnPreviewContentLoadedForHealth;
        }

        var root = new Grid
        {
            MaxWidth = PreviewPageMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(24, 12, 24, 18),
            RowSpacing = 12
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Row 0 — Unified Workspace Command Bar:
        // Left: [←] · [Emoji + Title ✎] · [v1 ▾] · [↶][↷]
        // Right: [需求备忘] · [API 参考] · [⋯] · [放到桌面 / 保存修改 (Primary Accent CTA)]
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Button back = CreateIconButton("\uE72B", _loc.T("CustomWidget.Workshop.Preview.Back"));
        back.Width = 32;
        back.Height = 32;
        back.CornerRadius = new CornerRadius(6);
        back.Click += async (_, _) =>
        {
            if (_isInPlaceGenerating)
            {
                CancelWaiting();
                if (_page == StudioPage.Start)
                {
                    return;
                }
            }

            if (HasUnsavedWork && !await ConfirmDiscardCleanlyAsync())
            {
                return;
            }

            string preservedIdea = _startDraftIdea;
            ResetSession();
            _targetWidgetId = null;
            _targetWidgetName = null;
            UpdateWindowTitle();
            _startDraftIdea = preservedIdea;
            ShowStartPage(preservedIdea);
        };
        header.Children.Add(back);

        _frameEmojiText = null;
        _frameTitleText = null;
        _blueprintTitleText = null;
        _revisionBadge = null;
        _revisionBadgeText = null;

        var leftStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        _headerEmojiText = new TextBlock
        {
            Text = ResolveDisplayEmoji(parsed?.Manifest),
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        };
        _nameText = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 280,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        var titleInnerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleInnerRow.Children.Add(_headerEmojiText);
        titleInnerRow.Children.Add(_nameText);
        if (hasLiveManifest)
        {
            titleInnerRow.Children.Add(new FontIcon
            {
                Glyph = "\uE70F",
                FontSize = 11,
                Opacity = 0.5,
                Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var titleEditBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(8, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = hasLiveManifest,
            Content = titleInnerRow
        };
        if (hasLiveManifest)
        {
            ToolTipService.SetToolTip(titleEditBtn, _loc.T("CustomWidget.Workshop.CoCreate.QuickHeaderTip"));
            AutomationProperties.SetName(titleEditBtn, _loc.T("CustomWidget.Workshop.CoCreate.QuickHeaderTip"));
            titleEditBtn.Flyout = BuildHeaderQuickEditFlyout();
        }
        leftStrip.Children.Add(titleEditBtn);

        _revisionsMenuButton = new DropDownButton
        {
            Height = 30,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var revisionsFlyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        revisionsFlyout.Opening += (_, _) =>
        {
            revisionsFlyout.Items.Clear();
            for (int i = _revisions.Count - 1; i >= 0; i--)
            {
                int capturedIndex = i;
                StudioPersistedRevision rev = _revisions[i];
                var item = new ToggleMenuFlyoutItem
                {
                    Text = _loc.Format("CustomWidget.Workshop.History.Item", i + 1, rev.ChangeSummary),
                    IsChecked = i == _revisionIndex
                };
                item.Click += (_, _) => SelectRevision(capturedIndex);
                revisionsFlyout.Items.Add(item);
            }
        };
        _revisionsMenuButton.Flyout = revisionsFlyout;
        leftStrip.Children.Add(_revisionsMenuButton);

        var undoRedo = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        _undoButton = CreateIconButton("\uE7A7", _loc.T("CustomWidget.Workshop.Preview.Undo"));
        _undoButton.Width = 30;
        _undoButton.Height = 30;
        _undoButton.CornerRadius = new CornerRadius(6);
        _redoButton = CreateIconButton("\uE7A6", _loc.T("CustomWidget.Workshop.Preview.Redo"));
        _redoButton.Width = 30;
        _redoButton.Height = 30;
        _redoButton.CornerRadius = new CornerRadius(6);
        _undoButton.Click += (_, _) => StepRevision(-1);
        _redoButton.Click += (_, _) => StepRevision(1);
        undoRedo.Children.Add(_undoButton);
        undoRedo.Children.Add(_redoButton);
        leftStrip.Children.Add(undoRedo);

        Grid.SetColumn(leftStrip, 1);
        header.Children.Add(leftStrip);

        var rightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        _briefHeaderButton = new Button
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Flyout = BuildDesignBriefFlyout()
        };
        RefreshBriefButtonChrome();
        rightActions.Children.Add(_briefHeaderButton);

        Button apiDocsHeaderBtn = CreateSubtleApiDocsToggleButton(
            "CustomWidget.ApiDocs.ButtonShort",
            fontSize: 12,
            horizontalPadding: 10);
        rightActions.Children.Add(apiDocsHeaderBtn);

        Button sessionMoreBtn = CreateIconButton("\uE712", _loc.T("CustomWidget.Workshop.More.Tooltip"));
        sessionMoreBtn.Width = 32;
        sessionMoreBtn.Height = 32;
        sessionMoreBtn.CornerRadius = new CornerRadius(6);
        var sessionMoreFlyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        sessionMoreFlyout.Opening += (_, _) =>
        {
            sessionMoreFlyout.Items.Clear();
            AddMenuItem(
                sessionMoreFlyout,
                "\uE943",
                "CustomWidget.Workshop.More.CodeTool",
                async () => await OpenInCodeToolAsync(_refineTextBox?.Text ?? string.Empty));
            AddMenuItem(
                sessionMoreFlyout,
                "\uE756",
                "CustomWidget.Workshop.More.CopyAgentTask",
                async () => await CopyAgentKickoffPromptAsync(_refineTextBox?.Text ?? string.Empty));
            sessionMoreFlyout.Items.Add(new MenuFlyoutSeparator());
            AddMenuItem(
                sessionMoreFlyout,
                "\uE74D",
                "CustomWidget.Workshop.CoCreate.ClearHistory",
                ConfirmAndClearCoCreationHistoryAsync);
        };
        sessionMoreBtn.Flyout = sessionMoreFlyout;
        rightActions.Children.Add(sessionMoreBtn);

        var commit = new Button
        {
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(6),
            Style = AccentButtonStyle(),
            IsEnabled = hasLiveManifest && !_isInPlaceGenerating,
            Content = CreateSmallIconLabel(
                IsEditMode ? "\uE74E" : "\uE710",
                _loc.T(IsEditMode ? "CustomWidget.Workshop.Footer.Save" : "CustomWidget.Workshop.Footer.Place"))
        };
        commit.Click += async (_, _) => await CommitAsync(commit);
        _commitButton = commit;
        rightActions.Children.Add(commit);

        Grid.SetColumn(rightActions, 2);
        header.Children.Add(rightActions);
        root.Children.Add(header);

        // Row 1 — Optional Draft Recovery Banner
        if (CreateDraftRecoveryBannerIfNeeded() is { } previewRecoveryBanner)
        {
            Grid.SetRow(previewRecoveryBanner, 1);
            root.Children.Add(previewRecoveryBanner);
        }

        // Row 2 — Resizable Split Workspace: Live Preview Stage (Left) | GridSplitter | Co-Creation Workspace (Right)
        var body = new Grid();
        double initialStageWidth = CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(_lastPreviewWidth, _currentPreviewWidgetWidth);
        _stageColumn = new ColumnDefinition
        {
            MinWidth = 300,
            MaxWidth = 640,
            Width = new GridLength(initialStageWidth)
        };
        body.ColumnDefinitions.Add(_stageColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
        Grid.SetRow(body, 2);

        root.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width;
            _lastPreviewWidth = w;
            root.Padding = w < 880
                ? new Thickness(16, 10, 16, 14)
                : new Thickness(24, 12, 24, 18);
            if (_stageColumn is not null)
            {
                double maxStage = Math.Clamp(w - 350, 300, 660);
                _stageColumn.MaxWidth = maxStage;
                if (!_stageWidthManuallyAdjusted)
                {
                    _stageColumn.Width = new GridLength(
                        Math.Min(maxStage, CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(w, _currentPreviewWidgetWidth)));
                }
                else if (_stageColumn.Width.Value > maxStage)
                {
                    _stageColumn.Width = new GridLength(maxStage);
                }
            }
        };

        _stageBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Background = ThemeBrush("LayerOnMicaBaseAltFillColorSecondaryBrush"),
            Margin = new Thickness(0, 0, 6, 0)
        };

        var stageGrid = new Grid();
        stageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _stageWallpaperLayer = new Border
        {
            CornerRadius = new CornerRadius(7),
            Opacity = 0.88,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(_stageWallpaperLayer, 2);
        stageGrid.Children.Add(_stageWallpaperLayer);

        _stageWallpaperScrim = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x76, 0x0D, 0x10, 0x18)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(_stageWallpaperScrim, 2);
        stageGrid.Children.Add(_stageWallpaperScrim);

        _stageGeneratingProgressBar = new ProgressBar
        {
            Height = 2,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 10, 0),
            IsIndeterminate = false,
            Visibility = Visibility.Collapsed
        };
        stageGrid.Children.Add(_stageGeneratingProgressBar);

        Grid stageTopBar = BuildStageTopToolbar(size, hasLiveManifest);
        stageGrid.Children.Add(stageTopBar);

        string resolvedManifestTitle = hasLiveManifest
            ? (parsed?.Manifest?.ResolveLocalizedName(_loc.CurrentCultureName) ?? _loc.T("CustomWidget.Title"))
            : (_isInPlaceGenerating
                ? _loc.T("CustomWidget.Workshop.Stage.BlueprintPreparing")
                : _loc.T("CustomWidget.Title"));

        UIElement stageCenterChild;
        if (hasLiveManifest && _previewContent is not null)
        {
            _widgetFrame = CreateWidgetFrame(
                ResolveDisplayEmoji(parsed?.Manifest),
                resolvedManifestTitle,
                _previewContent,
                size,
                clipViewport: false,
                out _frameEmojiText,
                out _frameTitleText);
            FluentMotionHelper.AttachSmoothBoundsImplicitAnimations(_widgetFrame);
            stageCenterChild = _widgetFrame;
        }
        else
        {
            _widgetFrame = null;
            stageCenterChild = BuildBlueprintStagePlaceholder();
        }

        var stageViewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Margin = new Thickness(18, 10, 18, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stageCenterChild
        };
        Grid.SetRow(stageViewbox, 1);
        stageGrid.Children.Add(stageViewbox);

        _stageGeneratingStatusText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var pillStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7
        };
        pillStack.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        pillStack.Children.Add(_stageGeneratingStatusText);
        _stageGeneratingStatusPill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(12, 0, 12, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = ThemeBrush("SolidBackgroundFillColorBaseBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
            Child = pillStack
        };
        Grid.SetRow(_stageGeneratingStatusPill, 1);
        stageGrid.Children.Add(_stageGeneratingStatusPill);

        _stageBorder.Child = stageGrid;
        body.Children.Add(_stageBorder);

        var stageSplitter = new ToolkitControls.GridSplitter
        {
            ResizeBehavior = ToolkitControls.GridSplitter.GridResizeBehavior.BasedOnAlignment,
            ResizeDirection = ToolkitControls.GridSplitter.GridResizeDirection.Columns
        };
        if (Application.Current.Resources.TryGetValue("WidgetMasterDetailSplitterStyle", out object? splitterRes) &&
            splitterRes is Style splitterStyle)
        {
            stageSplitter.Style = splitterStyle;
        }
        stageSplitter.PointerPressed += (_, _) => _stageWidthManuallyAdjusted = true;
        Grid.SetColumn(stageSplitter, 1);
        body.Children.Add(stageSplitter);

        FrameworkElement refinePanel = BuildRefinePanel();
        Grid.SetColumn(refinePanel, 2);
        body.Children.Add(refinePanel);
        root.Children.Add(body);

        SetPage(new Grid { Children = { root } });
        _nameText.Text = resolvedManifestTitle;
        ApplyStageBackdropVisuals();
        RefreshTitleBarContextBadge();
        RefreshSampleDataBadgeChrome();
        RefreshRevisionChrome();
        RefreshInPlaceGenerationChrome(refreshStream: false);
        RefreshTargetNodePillChrome();
        RefreshSuggestionChips();

        if (_pendingRecoveryDraft is null && _wasReconciledExternalOnOpen)
        {
            _wasReconciledExternalOnOpen = false;
            ShowPageMessage(
                InfoBarSeverity.Informational,
                string.Empty,
                _loc.T("CustomWidget.Workshop.CoCreate.ExternalSyncNotice"),
                null);
        }

        if (justLoaded && hasLiveManifest)
        {
            _ = PulseStageAsync();
        }
    }

    private Grid BuildStageTopToolbar(CustomWidgetSizeSpec currentSize, bool hasLiveManifest)
    {
        var bar = new Grid
        {
            Padding = new Thickness(10, 10, 10, 4),
            ColumnSpacing = 6
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        var pickToggle = new ToggleButton
        {
            Height = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            IsEnabled = hasLiveManifest,
            IsChecked = hasLiveManifest && _isInspectorModeEnabled,
            Content = CreateSmallIconLabel("\uE81D", _loc.T("CustomWidget.Workshop.CoCreate.PickElement"))
        };
        ToolTipService.SetToolTip(pickToggle, _loc.T("CustomWidget.Workshop.CoCreate.PickElementTip"));
        AutomationProperties.SetName(pickToggle, _loc.T("CustomWidget.Workshop.CoCreate.PickElement"));
        pickToggle.Click += (_, _) =>
        {
            _isInspectorModeEnabled = pickToggle.IsChecked == true;
            if (!_isInspectorModeEnabled)
            {
                _selectedTargetNode = null;
            }

            if (_previewContent is not null)
            {
                _previewContent.SelectedNodePath = _selectedTargetNode?.NodePath;
                _previewContent.IsNodeSelectionEnabled = _isInspectorModeEnabled;
            }

            RefreshTargetNodePillChrome();
        };
        leftTools.Children.Add(pickToggle);

        _sampleDataToggleButton = new Button
        {
            Height = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed
        };
        _sampleDataToggleButton.Click += (_, _) => ToggleSampleDataPreviewMode();
        leftTools.Children.Add(_sampleDataToggleButton);

        bar.Children.Add(leftTools);

        if (hasLiveManifest && !_prefs.HasSeenPickElementTip && !_isInPlaceGenerating)
        {
            var pickTip = new TeachingTip
            {
                Target = pickToggle,
                Title = _loc.T("CustomWidget.Workshop.CoCreate.PickTeachingTitle"),
                Subtitle = _loc.T("CustomWidget.Workshop.CoCreate.PickTeachingSubtitle"),
                PreferredPlacement = TeachingTipPlacementMode.Bottom,
                IsLightDismissEnabled = true
            };
            bar.Children.Add(pickTip);
            pickToggle.Loaded += (_, _) =>
            {
                if (_isClosed || _prefs.HasSeenPickElementTip)
                {
                    return;
                }

                _prefs.MarkPickElementTipSeen();
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isClosed &&
                        _page == StudioPage.Preview &&
                        pickTip.IsLoaded &&
                        pickTip.XamlRoot is not null)
                    {
                        pickTip.IsOpen = true;
                    }
                });
            };
        }

        var rightTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        StageBackdropMode effectiveBackdrop = _stageBackdropMode == StageBackdropMode.Wallpaper && !_hasSystemWallpaperAvailable
            ? (Win32Helper.IsSystemDarkMode() ? StageBackdropMode.Dark : StageBackdropMode.Light)
            : _stageBackdropMode;

        var backdropSegmented = new ToolkitControls.Segmented
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center
        };

        void AddBackdropItem(StageBackdropMode mode, string glyph, string labelKey)
        {
            string label = _loc.T(labelKey);
            var segItem = new ToolkitControls.SegmentedItem
            {
                Tag = mode,
                Icon = new FontIcon { Glyph = glyph, FontSize = 12 }
            };
            ToolTipService.SetToolTip(segItem, label);
            AutomationProperties.SetName(segItem, label);
            backdropSegmented.Items.Add(segItem);
            if (mode == effectiveBackdrop)
            {
                backdropSegmented.SelectedItem = segItem;
            }
        }

        if (_hasSystemWallpaperAvailable)
        {
            AddBackdropItem(StageBackdropMode.Wallpaper, "\uEB9F", "CustomWidget.Workshop.Stage.BackdropWallpaper");
        }
        AddBackdropItem(StageBackdropMode.Light, "\uE706", "CustomWidget.Workshop.Stage.BackdropLight");
        AddBackdropItem(StageBackdropMode.Dark, "\uE708", "CustomWidget.Workshop.Stage.BackdropDark");

        WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(backdropSegmented);
        WidgetSegmentedLayoutHelper.ApplyEqualItemWidths(backdropSegmented);
        backdropSegmented.MinWidth = _hasSystemWallpaperAvailable ? 92 : 64;
        backdropSegmented.ActualThemeChanged += (_, _) =>
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(backdropSegmented);

        backdropSegmented.SelectionChanged += (_, _) =>
        {
            if (backdropSegmented.SelectedItem is ToolkitControls.SegmentedItem selected &&
                selected.Tag is StageBackdropMode nextMode &&
                nextMode != _stageBackdropMode)
            {
                _stageBackdropMode = nextMode;
                ApplyStageBackdropVisuals();
            }
        };
        rightTools.Children.Add(backdropSegmented);

        _sizeQuickButton = new Button
        {
            Height = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            IsEnabled = hasLiveManifest,
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(0),
            Content = CreateSmallIconLabel("\uE740", $"{currentSize.Width}×{currentSize.Height}")
        };
        ToolTipService.SetToolTip(_sizeQuickButton, _loc.T("CustomWidget.Workshop.CoCreate.SizeButtonTip"));
        AutomationProperties.SetName(_sizeQuickButton, _loc.T("CustomWidget.Workshop.CoCreate.SizeButtonTip"));
        _sizeQuickButton.Flyout = BuildSizeQuickEditFlyout();
        rightTools.Children.Add(_sizeQuickButton);

        Grid.SetColumn(rightTools, 1);
        bar.Children.Add(rightTools);
        return bar;
    }

    private Flyout BuildSizeQuickEditFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var stack = new StackPanel { Spacing = 10, Width = 248 };

        var presetsRow = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        (string Key, int W, int H)[] presets =
        [
            ("CustomWidget.Workshop.CoCreate.SizeCompact", 280, 220),
            ("CustomWidget.Workshop.CoCreate.SizeStandard", 340, 340),
            ("CustomWidget.Workshop.CoCreate.SizeTall", 380, 440)
        ];

        var widthBox = new NumberBox
        {
            Header = _loc.T("CustomWidget.Workshop.CoCreate.SizeWidth"),
            Minimum = 200,
            Maximum = 640,
            SmallChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var heightBox = new NumberBox
        {
            Header = _loc.T("CustomWidget.Workshop.CoCreate.SizeHeight"),
            Minimum = 140,
            Maximum = 720,
            SmallChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };

        foreach ((string key, int w, int h) in presets)
        {
            int cw = w;
            int ch = h;
            Button pBtn = CreateChipButton($"{_loc.T(key)} {cw}×{ch}");
            pBtn.Click += (_, _) =>
            {
                flyout.Hide();
                ApplyZeroTokenMetadataEdit(newSize: new CustomWidgetSizeSpec { Width = cw, Height = ch });
            };
            presetsRow.Children.Add(pBtn);
        }
        stack.Children.Add(presetsRow);

        var dimsGrid = new Grid { ColumnSpacing = 8 };
        dimsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dimsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dimsGrid.Children.Add(widthBox);
        Grid.SetColumn(heightBox, 1);
        dimsGrid.Children.Add(heightBox);
        stack.Children.Add(dimsGrid);

        var applyBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = AccentButtonStyle(),
            Content = _loc.T("CustomWidget.Workshop.CoCreate.ApplyQuickEdit")
        };
        applyBtn.Click += (_, _) =>
        {
            int w = double.IsNaN(widthBox.Value) ? 320 : Math.Clamp((int)Math.Round(widthBox.Value), 200, 640);
            int h = double.IsNaN(heightBox.Value) ? 260 : Math.Clamp((int)Math.Round(heightBox.Value), 140, 720);
            flyout.Hide();
            ApplyZeroTokenMetadataEdit(newSize: new CustomWidgetSizeSpec { Width = w, Height = h });
        };
        stack.Children.Add(applyBtn);

        flyout.Opening += (_, _) =>
        {
            CustomWidgetSizeSpec cur = CustomWidgetManifestParser.Parse(CurrentManifestJson ?? string.Empty).Manifest?.DefaultSize
                ?? new CustomWidgetSizeSpec();
            widthBox.Value = cur.Width;
            heightBox.Value = cur.Height;
        };

        flyout.Content = stack;
        return flyout;
    }

    private void OnPreviewNodePicked(CustomWidgetPickedNode picked)
    {
        _selectedTargetNode = picked;
        if (_previewContent is not null)
        {
            _previewContent.SelectedNodePath = picked.NodePath;
        }

        RefreshTargetNodePillChrome();
        _refineTextBox?.Focus(FocusState.Programmatic);
    }

    private void OnPreviewContentLoadedForHealth(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed || _page != StudioPage.Preview)
            {
                return;
            }

            _latestHealthReport = BuildCurrentHealthReport(CurrentManifestJson);
            RefreshSuggestionChips();
        });
    }

    private FrameworkElement BuildRefinePanel()
    {
        var layout = new Grid { Margin = new Thickness(8, 0, 0, 0), RowSpacing = 10 };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0 — Persistent Multi-Turn Co-Creation Stream (sits directly on Mica workspace layer)
        _chatStreamPanel = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(0, 2, 4, 4),
            Transitions = FluentMotionHelper.CreateListTransitions()
        };
        _chatStreamScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _chatStreamPanel
        };
        layout.Children.Add(_chatStreamScroller);

        // Row 1 — Bottom Dock: InfoBar + Picked Node Pill + Curated Chips + Compact-Submit Multi-Line Composer
        var dockStack = new StackPanel { Spacing = 8 };

        _pageInfoBar = CreatePageInfoBar();
        dockStack.Children.Add(_pageInfoBar);

        _targetNodePillText = new TextBlock
        {
            FontSize = 12,
            Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        var clearPickBtn = new Button
        {
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }
        };
        ToolTipService.SetToolTip(clearPickBtn, _loc.T("CustomWidget.Workshop.CoCreate.ClearPick"));
        AutomationProperties.SetName(clearPickBtn, _loc.T("CustomWidget.Workshop.CoCreate.ClearPick"));
        clearPickBtn.Click += (_, _) =>
        {
            _selectedTargetNode = null;
            if (_previewContent is not null)
            {
                _previewContent.SelectedNodePath = null;
            }
            RefreshTargetNodePillChrome();
        };

        var targetPillGrid = new Grid { ColumnSpacing = 6 };
        targetPillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetPillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetPillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetPillGrid.Children.Add(new FontIcon
        {
            Glyph = "\uE81D",
            FontSize = 12,
            Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_targetNodePillText, 1);
        targetPillGrid.Children.Add(_targetNodePillText);
        Grid.SetColumn(clearPickBtn, 2);
        targetPillGrid.Children.Add(clearPickBtn);

        _targetNodePillBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("AccentFillColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
            Child = targetPillGrid
        };
        dockStack.Children.Add(_targetNodePillBorder);

        _suggestionChipsPanel = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        dockStack.Children.Add(_suggestionChipsPanel);

        Border refineComposer = CreateComposerCard(
            initialText: _refineDraftIdea,
            placeholderText: _loc.T("CustomWidget.Workshop.Refine.Placeholder"),
            automationName: _loc.T("CustomWidget.Workshop.Refine.Header"),
            submitLabel: _loc.T("CustomWidget.Workshop.Refine.Button"),
            shortcutHintText: _loc.T("CustomWidget.Workshop.Composer.RefineHint"),
            minTextHeight: 72,
            maxTextHeight: 164,
            compactToolbar: true,
            includeCodeToolInMore: true,
            onTextEdited: text => _refineDraftIdea = text.Trim(),
            out TextBox refineBox,
            out Button refineSubmitBtn);
        _refineTextBox = refineBox;
        _refineSubmitButton = refineSubmitBtn;
        dockStack.Children.Add(refineComposer);

        Grid.SetRow(dockStack, 1);
        layout.Children.Add(dockStack);

        return layout;
    }

    private void RefreshBriefButtonChrome()
    {
        if (_briefHeaderButton is null)
        {
            return;
        }

        string label = _loc.T("CustomWidget.Workshop.CoCreate.BriefButton");
        bool hasBrief = !string.IsNullOrWhiteSpace(_briefMarkdown);
        _briefHeaderButton.Content = CreateSmallIconLabel(hasBrief ? "\uE90A" : "\uE7C3", hasBrief ? label + " ✓" : label);
        ToolTipService.SetToolTip(_briefHeaderButton, _loc.T("CustomWidget.Workshop.CoCreate.BriefDialogDesc"));
        AutomationProperties.SetName(_briefHeaderButton, label);
    }

    private void RefreshTargetNodePillChrome()
    {
        if (_targetNodePillBorder is null || _targetNodePillText is null)
        {
            return;
        }

        if (_selectedTargetNode is not { } picked)
        {
            _targetNodePillBorder.Visibility = Visibility.Collapsed;
            return;
        }

        _targetNodePillText.Text = string.IsNullOrWhiteSpace(picked.DisplayLabel)
            ? picked.NodeType
            : _loc.Format("CustomWidget.Workshop.CoCreate.PickedPill", picked.NodeType, picked.DisplayLabel);
        _targetNodePillBorder.Visibility = Visibility.Visible;
    }

    private void RefreshSuggestionChips()
    {
        if (_suggestionChipsPanel is null)
        {
            return;
        }

        _suggestionChipsPanel.Children.Clear();
        var addedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPromptChip(string chipLabel, string promptToFill, string? glyph = null)
        {
            if (_suggestionChipsPanel.Children.Count >= 4 ||
                string.IsNullOrWhiteSpace(chipLabel) ||
                string.IsNullOrWhiteSpace(promptToFill) ||
                !addedLabels.Add(chipLabel))
            {
                return;
            }

            Button chip = CreateChipButton(chipLabel, glyph);
            chip.Click += (_, _) =>
            {
                if (_refineTextBox is null)
                {
                    return;
                }

                _refineTextBox.Text = promptToFill;
                _refineTextBox.SelectionStart = _refineTextBox.Text.Length;
                _refineTextBox.Focus(FocusState.Programmatic);
            };
            _suggestionChipsPanel.Children.Add(chip);
        }

        if (_latestHealthReport is { HasIssues: true })
        {
            foreach (CustomWidgetHealthIssue issue in _latestHealthReport.Issues.Take(2))
            {
                if (!string.IsNullOrWhiteSpace(issue.SuggestedPrompt))
                {
                    string shortIssue = issue.Message.Length > 26 ? issue.Message[..26] + "…" : issue.Message;
                    AddPromptChip(shortIssue, issue.SuggestedPrompt!, "\uE7BA");
                }
            }
        }

        StudioChatMessage? lastAssistant = _chatMessages.LastOrDefault(static m =>
            string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        if (lastAssistant is { Suggestions.Count: > 0 })
        {
            foreach (string s in lastAssistant.Suggestions.Take(3))
            {
                string shortText = s.Length > 24 ? s[..24] + "…" : s;
                AddPromptChip(shortText, s, "\uE945");
            }
        }

        (string Label, string Prompt)[] craftChips =
        [
            ("CustomWidget.Workshop.Chip.Softer.Label", "CustomWidget.Workshop.Chip.Softer.Prompt"),
            ("CustomWidget.Workshop.Chip.Bigger.Label", "CustomWidget.Workshop.Chip.Bigger.Prompt"),
            ("CustomWidget.Workshop.Chip.Progress.Label", "CustomWidget.Workshop.Chip.Progress.Prompt"),
            ("CustomWidget.Workshop.Chip.Compact.Label", "CustomWidget.Workshop.Chip.Compact.Prompt")
        ];
        foreach ((string labelKey, string promptKey) in craftChips)
        {
            AddPromptChip(_loc.T(labelKey), _loc.T(promptKey));
        }
    }

    private void RefreshCoCreationStream()
    {
        if (_chatStreamPanel is null)
        {
            return;
        }

        _chatStreamPanel.Children.Clear();

        if (_chatMessages.Count == 0 && !_isInPlaceGenerating)
        {
            var emptyCard = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush")
            };
            var emptyStack = new StackPanel { Spacing = 6 };
            if (_revisionIndex >= 0 && _revisionIndex < _revisions.Count)
            {
                emptyStack.Children.Add(new TextBlock
                {
                    Text = _loc.Format("CustomWidget.Workshop.History.Item", _revisionIndex + 1, _revisions[_revisionIndex].ChangeSummary),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            emptyStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.CoCreate.EmptyStreamHint"),
                FontSize = 12.5,
                LineHeight = 19,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            emptyCard.Child = emptyStack;
            _chatStreamPanel.Children.Add(emptyCard);
            return;
        }

        foreach (StudioChatMessage msg in _chatMessages)
        {
            if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                var userStack = new StackPanel { Spacing = 4 };
                string? targetLabel = !string.IsNullOrWhiteSpace(msg.TargetNodeLabel)
                    ? msg.TargetNodeLabel
                    : msg.TargetNodePath;
                if (!string.IsNullOrWhiteSpace(targetLabel))
                {
                    var targetRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5
                    };
                    targetRow.Children.Add(new FontIcon
                    {
                        Glyph = "\uE81D",
                        FontSize = 10.5,
                        Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    targetRow.Children.Add(new TextBlock
                    {
                        Text = targetLabel,
                        FontSize = 11,
                        Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    userStack.Children.Add(targetRow);
                }

                userStack.Children.Add(new TextBlock
                {
                    Text = msg.Text,
                    FontSize = 13,
                    LineHeight = 19,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });

                _chatStreamPanel.Children.Add(new Border
                {
                    MaxWidth = 480,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    CornerRadius = new CornerRadius(8, 8, 2, 8),
                    Padding = new Thickness(12, 8, 12, 9),
                    Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                    Child = userStack
                });
                continue;
            }

            int linkedRevIndex = -1;
            if (!string.IsNullOrWhiteSpace(msg.LinkedRevisionId))
            {
                linkedRevIndex = _revisions.FindIndex(r =>
                    string.Equals(r.RevisionId, msg.LinkedRevisionId, StringComparison.Ordinal));
            }

            string badgeText = msg.Kind switch
            {
                "answer" => _loc.T("CustomWidget.Workshop.CoCreate.KindAnswer"),
                "clarify" => _loc.T("CustomWidget.Workshop.CoCreate.KindClarify"),
                _ => linkedRevIndex >= 0
                    ? _loc.Format("CustomWidget.Workshop.CoCreate.KindEdit", linkedRevIndex + 1)
                    : _loc.T("CustomWidget.Workshop.History.AiChange")
            };

            var cardStack = new StackPanel { Spacing = 8 };
            var metaRow = new Grid { ColumnSpacing = 8 };
            metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            metaRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badgeText,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush")
                }
            });

            if (linkedRevIndex >= 0)
            {
                if (linkedRevIndex == _revisionIndex)
                {
                    var curTag = new TextBlock
                    {
                        Text = _loc.T("CustomWidget.Workshop.CoCreate.CurrentRevTag"),
                        FontSize = 11.5,
                        Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(curTag, 2);
                    metaRow.Children.Add(curTag);
                }
                else
                {
                    int targetRev = linkedRevIndex;
                    var restoreBtn = new Button
                    {
                        Height = 24,
                        Padding = new Thickness(8, 0, 8, 0),
                        CornerRadius = new CornerRadius(6),
                        FontSize = 11.5,
                        IsEnabled = !_isInPlaceGenerating,
                        Content = _loc.T("CustomWidget.Workshop.CoCreate.RestoreRevBtn")
                    };
                    restoreBtn.Click += (_, _) => SelectRevision(targetRev);
                    Grid.SetColumn(restoreBtn, 2);
                    metaRow.Children.Add(restoreBtn);
                }
            }

            cardStack.Children.Add(metaRow);

            cardStack.Children.Add(new TextBlock
            {
                Text = msg.Text,
                FontSize = 13,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });

            if (msg.DiffPills.Count > 0)
            {
                var diffWrap = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 4 };
                foreach (string pill in msg.DiffPills)
                {
                    diffWrap.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(7, 2, 7, 2),
                        Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                        Child = new TextBlock
                        {
                            Text = pill,
                            FontSize = 11,
                            Foreground = ThemeBrush("TextFillColorSecondaryBrush")
                        }
                    });
                }
                cardStack.Children.Add(diffWrap);
            }

            if (msg.Suggestions.Count > 0 &&
                (string.Equals(msg.Kind, "clarify", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(msg.Kind, "answer", StringComparison.OrdinalIgnoreCase)))
            {
                bool isClarifyTurn = string.Equals(msg.Kind, "clarify", StringComparison.OrdinalIgnoreCase);
                var sugWrap = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
                foreach (string sug in msg.Suggestions)
                {
                    string captured = sug;
                    Button chip = CreateChipButton(captured, isClarifyTurn ? "\uE8BD" : null);
                    chip.IsEnabled = !_isInPlaceGenerating;
                    chip.Click += async (_, _) =>
                    {
                        CoreVirtualKeyStates shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
                        bool editFirst = !isClarifyTurn || (shiftState & CoreVirtualKeyStates.Down) != 0;
                        if (editFirst)
                        {
                            if (_refineTextBox is not null)
                            {
                                _refineTextBox.Text = captured;
                                _refineTextBox.SelectionStart = _refineTextBox.Text.Length;
                                _refineTextBox.Focus(FocusState.Programmatic);
                            }
                            return;
                        }

                        await BeginGenerationAsync(captured);
                    };
                    sugWrap.Children.Add(chip);
                }
                cardStack.Children.Add(sugWrap);
            }

            _chatStreamPanel.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(8, 8, 8, 2),
                Padding = new Thickness(13, 10, 13, 11),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                Background = ThemeBrush("LayerFillColorAltBrush"),
                Child = cardStack
            });
        }

        if (_isInPlaceGenerating)
        {
            if (!_isWebAiRepairFlow && !string.IsNullOrWhiteSpace(_pendingPrompt))
            {
                var pendingUserStack = new StackPanel { Spacing = 4 };
                if (!string.IsNullOrWhiteSpace(_inFlightTargetNodeLabel))
                {
                    var pendingTargetRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5
                    };
                    pendingTargetRow.Children.Add(new FontIcon
                    {
                        Glyph = "\uE81D",
                        FontSize = 10.5,
                        Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    pendingTargetRow.Children.Add(new TextBlock
                    {
                        Text = _inFlightTargetNodeLabel,
                        FontSize = 11,
                        Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    pendingUserStack.Children.Add(pendingTargetRow);
                }

                pendingUserStack.Children.Add(new TextBlock
                {
                    Text = _pendingPrompt,
                    FontSize = 13,
                    LineHeight = 19,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });

                _chatStreamPanel.Children.Add(new Border
                {
                    MaxWidth = 480,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    CornerRadius = new CornerRadius(8, 8, 2, 8),
                    Padding = new Thickness(12, 8, 12, 9),
                    Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                    Child = pendingUserStack
                });
            }

            _chatStreamPanel.Children.Add(_waitingKind == WaitingKind.Direct
                ? BuildInPlaceDirectStreamingCard()
                : BuildInPlaceWebAiWaitingCard());
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_chatStreamScroller is { } scroller)
            {
                scroller.ChangeView(null, scroller.ScrollableHeight + 800, null, disableAnimation: true);
            }
        });
    }

    private Border BuildInPlaceDirectStreamingCard()
    {
        var column = new StackPanel { Spacing = 10 };

        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleLeft.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 17,
            Height = 17,
            VerticalAlignment = VerticalAlignment.Center
        });
        _directStageTitleText = new TextBlock
        {
            Text = ResolveDirectStageTitle(_inFlightStage, _directActiveProviderName),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        titleLeft.Children.Add(_directStageTitleText);
        headerGrid.Children.Add(titleLeft);

        var rightHeaderTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        TimeSpan curElapsed = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _directStartTickMs));
        _directStatsBadgeText = new TextBlock
        {
            Text = FormatDirectStatsText(_directActiveModelName, curElapsed, _directLastCharTotal),
            FontSize = 11.5,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        rightHeaderTools.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _directStatsBadgeText
        });

        var codeToggle = new ToggleButton
        {
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            IsChecked = _showRawStreamCodeInSession,
            Content = new FontIcon { Glyph = "\uE943", FontSize = 11.5 }
        };
        ToolTipService.SetToolTip(codeToggle, _loc.T("CustomWidget.Workshop.Stream.ToggleCodeTooltip"));
        AutomationProperties.SetName(codeToggle, _loc.T("CustomWidget.Workshop.Stream.ToggleCodeTooltip"));
        rightHeaderTools.Children.Add(codeToggle);

        Grid.SetColumn(rightHeaderTools, 1);
        headerGrid.Children.Add(rightHeaderTools);
        column.Children.Add(headerGrid);

        // Friendly 3-step indicator (novice-first: hides raw JSON unless developer clicks </>)
        var stepsRow = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        _directStep1Pill = CreateStreamStepPill("1 · " + _loc.T("CustomWidget.Workshop.Stream.StepUnderstand"));
        _directStep2Pill = CreateStreamStepPill("2 · " + _loc.T("CustomWidget.Workshop.Stream.StepAssemble"));
        _directStep3Pill = CreateStreamStepPill("3 · " + _loc.T("CustomWidget.Workshop.Stream.StepValidate"));
        stepsRow.Children.Add(_directStep1Pill);
        stepsRow.Children.Add(_directStep2Pill);
        stepsRow.Children.Add(_directStep3Pill);
        UpdateDirectStepPills(_inFlightStage);
        column.Children.Add(stepsRow);

        _directDetectedWidgetText = new TextBlock
        {
            Text = !string.IsNullOrWhiteSpace(_inFlightDetectedWidgetName)
                ? _loc.Format("CustomWidget.Workshop.Stream.DetectedWidget", _inFlightDetectedWidgetName)
                : string.Empty,
            FontSize = 11.5,
            Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
            FontWeight = FontWeights.SemiBold
        };
        _directDetectedWidgetBadge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 4, 9, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            Visibility = !string.IsNullOrWhiteSpace(_inFlightDetectedWidgetName) ? Visibility.Visible : Visibility.Collapsed,
            Child = _directDetectedWidgetText
        };
        column.Children.Add(_directDetectedWidgetBadge);

        // Collapsible Reasoning / Thinking summary card
        var thinkingInner = new StackPanel { Spacing = 6 };
        var thinkingHeaderRow = new Grid { ColumnSpacing = 8 };
        thinkingHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        thinkingHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _directThinkingHeaderText = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Stream.ThinkingTitle"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        thinkingHeaderRow.Children.Add(_directThinkingHeaderText);

        var toggleThinkBtn = new Button
        {
            Height = 22,
            Padding = new Thickness(7, 0, 7, 0),
            FontSize = 11,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = _loc.T(_directThinkingCollapsedByUser
                ? "CustomWidget.Workshop.Stream.Expand"
                : "CustomWidget.Workshop.Stream.Collapse")
        };
        Grid.SetColumn(toggleThinkBtn, 1);
        thinkingHeaderRow.Children.Add(toggleThinkBtn);
        thinkingInner.Children.Add(thinkingHeaderRow);

        _directThinkingText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        _directThinkingScroller = new ScrollViewer
        {
            MaxHeight = 96,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = _directThinkingCollapsedByUser ? Visibility.Collapsed : Visibility.Visible,
            Content = _directThinkingText
        };
        thinkingInner.Children.Add(_directThinkingScroller);

        toggleThinkBtn.Click += (_, _) =>
        {
            if (_directThinkingScroller is null)
            {
                return;
            }

            _directThinkingCollapsedByUser = _directThinkingScroller.Visibility == Visibility.Visible;
            _directThinkingScroller.Visibility = _directThinkingCollapsedByUser
                ? Visibility.Collapsed
                : Visibility.Visible;
            toggleThinkBtn.Content = _loc.T(_directThinkingCollapsedByUser
                ? "CustomWidget.Workshop.Stream.Expand"
                : "CustomWidget.Workshop.Stream.Collapse");
        };

        _directThinkingContainer = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            Visibility = Visibility.Collapsed,
            Child = thinkingInner
        };
        column.Children.Add(_directThinkingContainer);

        // Optional Raw JSON Stream well (hidden by default; toggled via </> button)
        var streamInner = new StackPanel { Spacing = 5 };
        var streamHeaderRow = new Grid { ColumnSpacing = 8 };
        streamHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        streamHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        streamHeaderRow.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Stream.OutputTitle"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        _directStreamHintText = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Stream.WaitingFirstChunk"),
            FontSize = 11,
            Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_directStreamHintText, 1);
        streamHeaderRow.Children.Add(_directStreamHintText);
        streamInner.Children.Add(streamHeaderRow);

        _directStreamText = new TextBlock
        {
            Text = _loc.Format("CustomWidget.Workshop.Direct.Caption", _directActiveProviderName),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 11.5,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        _directStreamScroller = new ScrollViewer
        {
            MinHeight = 84,
            MaxHeight = 176,
            Padding = new Thickness(2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _directStreamText
        };
        streamInner.Children.Add(_directStreamScroller);

        _directRawStreamWell = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            Visibility = _showRawStreamCodeInSession ? Visibility.Visible : Visibility.Collapsed,
            Child = streamInner
        };
        codeToggle.Click += (_, _) =>
        {
            _showRawStreamCodeInSession = codeToggle.IsChecked == true;
            if (_directRawStreamWell is not null)
            {
                _directRawStreamWell.Visibility = _showRawStreamCodeInSession
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        };
        column.Children.Add(_directRawStreamWell);

        var footerRow = new CustomWidgetWrapPanel { HorizontalSpacing = 8, VerticalSpacing = 6 };
        var switchWebBtn = new Button
        {
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Content = CreateSmallIconLabel("\uE774", _loc.T("CustomWidget.Workshop.Direct.SwitchToWebAi"))
        };
        switchWebBtn.Click += (_, _) => SwitchWaitingToWebAi();
        footerRow.Children.Add(switchWebBtn);

        var stopBtn = new Button
        {
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Content = CreateSmallIconLabel("\uE71A", _loc.T("CustomWidget.Workshop.Stream.StopButton"))
        };
        stopBtn.Click += (_, _) => CancelWaiting();
        footerRow.Children.Add(stopBtn);
        column.Children.Add(footerRow);

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(10, 10, 10, 3),
            Padding = new Thickness(14, 11, 14, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("AccentFillColorDefaultBrush"),
            Background = ThemeBrush("LayerFillColorAltBrush"),
            Child = column
        };
    }

    private Border CreateStreamStepPill(string label)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = ThemeBrush("TextFillColorTertiaryBrush")
            }
        };
    }

    private void UpdateDirectStepPills(AiGenerationStage stage)
    {
        int activeStep = stage switch
        {
            AiGenerationStage.Connecting or AiGenerationStage.Thinking => 1,
            AiGenerationStage.GeneratingJson => 2,
            _ => 3
        };

        ApplyStepPillState(_directStep1Pill, stepNumber: 1, activeStep);
        ApplyStepPillState(_directStep2Pill, stepNumber: 2, activeStep);
        ApplyStepPillState(_directStep3Pill, stepNumber: 3, activeStep);
    }

    private void ApplyStepPillState(Border? pill, int stepNumber, int activeStep)
    {
        if (pill is null)
        {
            return;
        }

        bool isActive = stepNumber == activeStep;
        bool isCompleted = stepNumber < activeStep;
        pill.BorderBrush = isActive
            ? ThemeBrush("AccentFillColorDefaultBrush")
            : ThemeBrush("DividerStrokeColorDefaultBrush");
        pill.Background = isActive || isCompleted
            ? ThemeBrush("SubtleFillColorSecondaryBrush")
            : ThemeBrush("SubtleFillColorTertiaryBrush");
        if (pill.Child is TextBlock tb)
        {
            tb.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            tb.Foreground = isActive
                ? ThemeBrush("AccentTextFillColorPrimaryBrush")
                : (isCompleted ? ThemeBrush("TextFillColorPrimaryBrush") : ThemeBrush("TextFillColorTertiaryBrush"));
        }
    }

    private Border BuildInPlaceWebAiWaitingCard()
    {
        WebAiTargetPreset preset = _activeWebPreset ??
            CustomWidgetStudioPreferencesStore.ResolveWebAiPreset(CustomWidgetStudioPreferencesStore.TargetCopyOnly);
        bool copyOnly = preset.IsCopyOnly;

        var column = new StackPanel { Spacing = 10 };

        var titleHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleHeader.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = _isWebAiRepairFlow ? "\uE90F" : "\uE774",
                FontSize = 14,
                Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush")
            }
        });

        string title = _isWebAiRepairFlow
            ? _loc.T("CustomWidget.Workshop.Web.RepairTitle")
            : copyOnly
                ? _loc.T("CustomWidget.Workshop.Web.TitleCopyOnly")
                : _loc.Format("CustomWidget.Workshop.Web.Title", preset.DisplayName);
        titleHeader.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        column.Children.Add(titleHeader);

        var steps = new StackPanel { Spacing = 8 };
        steps.Children.Add(CreateStepRow("1", _loc.T(copyOnly || _isWebAiRepairFlow
            ? "CustomWidget.Workshop.Web.Step1CopyOnly"
            : "CustomWidget.Workshop.Web.Step1")));
        steps.Children.Add(CreateStepRow("2", _loc.T("CustomWidget.Workshop.Web.Step2")));
        steps.Children.Add(CreateStepRow("\u2713", _loc.T("CustomWidget.Workshop.Web.Step3")));
        column.Children.Add(steps);

        var waitingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        waitingRow.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
        waitingRow.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Web.Waiting"),
            Style = TextStyle("CaptionTextBlockStyle"),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        column.Children.Add(waitingRow);

        var actions = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        var manualPaste = new Button
        {
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            Style = AccentButtonStyle(),
            Content = CreateSmallIconLabel("\uE77F", _loc.T("CustomWidget.Workshop.Web.ManualPaste"))
        };
        manualPaste.Click += async (_, _) => await PasteReplyManuallyAsync();
        actions.Children.Add(manualPaste);

        if (!copyOnly)
        {
            var reopen = new Button
            {
                Height = 30,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                Content = CreateSmallIconLabel("\uE8A7", _loc.T("CustomWidget.Workshop.Web.Reopen"))
            };
            reopen.Click += (_, _) =>
            {
                if (!string.IsNullOrEmpty(_lastSelfCopiedText))
                {
                    SetClipboardText(_lastSelfCopiedText);
                }

                OpenWebAi(preset);
            };
            actions.Children.Add(reopen);
        }

        var switchAi = new DropDownButton
        {
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Content = _loc.T("CustomWidget.Workshop.Web.SwitchAi")
        };
        var switchMenu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (WebAiTargetPreset target in CustomWidgetStudioPreferencesStore.GetOrderedWebAiTargets(_loc.CurrentCultureName, includeCopyOnly: false))
        {
            WebAiTargetPreset captured = target;
            var item = new MenuFlyoutItem { Text = captured.DisplayName };
            item.Click += (_, _) =>
            {
                _prefs.SetPreferredWebAiTarget(captured.Id);
                _activeWebPreset = captured;
                if (!string.IsNullOrEmpty(_lastSelfCopiedText))
                {
                    SetClipboardText(_lastSelfCopiedText);
                }

                OpenWebAi(captured);
                RefreshCoCreationStream();
            };
            switchMenu.Items.Add(item);
        }
        switchAi.Flyout = switchMenu;
        actions.Children.Add(switchAi);

        var cancelBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Content = _loc.T("Common.Cancel")
        };
        cancelBtn.Click += (_, _) => CancelWaiting();
        actions.Children.Add(cancelBtn);
        column.Children.Add(actions);

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(10, 10, 10, 3),
            Padding = new Thickness(14, 11, 14, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("AccentFillColorDefaultBrush"),
            Background = ThemeBrush("LayerFillColorAltBrush"),
            Child = column
        };
    }

    private Flyout BuildDesignBriefFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var stack = new StackPanel { Spacing = 10, Width = 356 };

        stack.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.CoCreate.BriefDialogTitle"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.CoCreate.BriefDialogDesc"),
            Style = TextStyle("CaptionTextBlockStyle"),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 156,
            MaxHeight = 280,
            MaxLength = CustomWidgetStudioSessionStore.MaxBriefBytes,
            PlaceholderText = _loc.T("CustomWidget.Workshop.CoCreate.BriefPlaceholder")
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        stack.Children.Add(editor);

        void CommitBriefText()
        {
            string trimmed = editor.Text?.Trim() ?? string.Empty;
            if (string.Equals(trimmed, _briefMarkdown, StringComparison.Ordinal))
            {
                return;
            }

            _briefMarkdown = trimmed;
            if (_uncommittedEditCount == 0 && !string.IsNullOrWhiteSpace(_briefMarkdown))
            {
                _uncommittedEditCount = 1;
            }
            PersistActiveDraftIfDirty();
            RefreshBriefButtonChrome();
        }

        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var clearBtn = new Button
        {
            Content = _loc.T("CustomWidget.Workshop.CoCreate.BriefClear")
        };
        clearBtn.Click += (_, _) => editor.Text = string.Empty;
        footer.Children.Add(clearBtn);

        var saveBtn = new Button
        {
            Style = AccentButtonStyle(),
            MinWidth = 88,
            Content = _loc.T("CustomWidget.Workshop.Footer.Save")
        };
        saveBtn.Click += (_, _) =>
        {
            CommitBriefText();
            flyout.Hide();
        };
        Grid.SetColumn(saveBtn, 2);
        footer.Children.Add(saveBtn);
        stack.Children.Add(footer);

        flyout.Opening += (_, _) => editor.Text = _briefMarkdown;
        flyout.Closed += (_, _) => CommitBriefText();

        flyout.Content = stack;
        return flyout;
    }

    private async Task ConfirmAndClearCoCreationHistoryAsync()
    {
        if (_isDialogOpen || _isClosed || RootGrid.XamlRoot is null)
        {
            return;
        }

        _isDialogOpen = true;
        ContentDialogResult res;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = _loc.T("CustomWidget.Workshop.CoCreate.ClearHistoryConfirmTitle"),
                Content = new TextBlock
                {
                    Text = _loc.T("CustomWidget.Workshop.CoCreate.ClearHistoryConfirmBody"),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = _loc.T("CustomWidget.Workshop.CoCreate.ClearHistory"),
                CloseButtonText = _loc.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) &&
                style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }

            res = await dialog.ShowAsync();
        }
        finally
        {
            _isDialogOpen = false;
        }

        if (res != ContentDialogResult.Primary)
        {
            return;
        }

        if (IsEditMode && !string.IsNullOrWhiteSpace(_targetWidgetId))
        {
            _sessionStore.ClearWidgetCoCreationHistory(_targetWidgetId!);
        }
        else
        {
            DiscardActiveDraftCleanly();
        }

        string? curManifest = CurrentManifestJson;
        _briefMarkdown = string.Empty;
        _chatMessages.Clear();
        _revisions.Clear();
        _revisionIndex = -1;
        if (!string.IsNullOrWhiteSpace(curManifest))
        {
            AddRevision(
                curManifest!,
                _loc.T("CustomWidget.Workshop.History.Current"),
                isAiChange: false,
                source: "manual",
                isCommitted: IsEditMode,
                markDirty: !IsEditMode);
        }

        ShowPreviewPage(justLoaded: false);
        ShowPageMessage(
            InfoBarSeverity.Success,
            string.Empty,
            _loc.T("CustomWidget.Workshop.CoCreate.HistoryCleared"),
            null);
    }

    private void StepRevision(int delta) => SelectRevision(_revisionIndex + delta);

    private void SelectRevision(int target)
    {
        if (target < 0 || target >= _revisions.Count || _previewVm is null)
        {
            return;
        }

        _revisionIndex = target;
        string json = _revisions[target].ManifestJson;
        _previewVm.ApplyPreviewManifestJson(json, preserveRuntimeData: true);
        if (!IsEditMode && !string.IsNullOrWhiteSpace(_previewSampleDataJson) && !_isSampleDataMutedInPreview)
        {
            _previewVm.ApplyPreviewSampleDataJson(_previewSampleDataJson);
        }
        else if (_isSampleDataMutedInPreview)
        {
            _previewVm.ClearPreviewSampleData();
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(json);
        CustomWidgetSizeSpec nextSize = parsed.Manifest?.DefaultSize ?? new CustomWidgetSizeSpec();
        _currentPreviewWidgetWidth = nextSize.Width;

        if (_widgetFrame is not null)
        {
            _widgetFrame.Width = Math.Clamp(nextSize.Width, 240, 440);
            _widgetFrame.Height = Math.Clamp(nextSize.Height, 180, 560);
        }

        if (_sizeQuickButton is not null)
        {
            _sizeQuickButton.Content = CreateSmallIconLabel("\uE740", $"{nextSize.Width}×{nextSize.Height}");
        }

        if (_stageColumn is not null && !_stageWidthManuallyAdjusted)
        {
            _stageColumn.Width = new GridLength(
                CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(_lastPreviewWidth, _currentPreviewWidgetWidth));
        }

        string nextTitle = parsed.Manifest?.ResolveLocalizedName(_loc.CurrentCultureName) ?? _loc.T("CustomWidget.Title");
        string nextEmoji = ResolveDisplayEmoji(parsed.Manifest);
        if (_headerEmojiText is not null)
        {
            _headerEmojiText.Text = nextEmoji;
        }

        if (_nameText is not null)
        {
            _nameText.Text = nextTitle;
        }

        if (_frameEmojiText is not null)
        {
            _frameEmojiText.Text = nextEmoji;
        }

        if (_frameTitleText is not null)
        {
            _frameTitleText.Text = nextTitle;
        }

        _latestHealthReport = BuildCurrentHealthReport(json);
        RefreshTitleBarContextBadge();
        RefreshSampleDataBadgeChrome();
        RefreshRevisionChrome();
        RefreshSuggestionChips();
    }

    private void RefreshRevisionChrome()
    {
        if (_undoButton is not null)
        {
            _undoButton.IsEnabled = !_isInPlaceGenerating && _revisionIndex > 0;
        }

        if (_redoButton is not null)
        {
            _redoButton.IsEnabled = !_isInPlaceGenerating && _revisionIndex < _revisions.Count - 1;
        }

        if (_revisionBadge is not null && _revisionBadgeText is not null)
        {
            _revisionBadgeText.Text = _loc.Format("CustomWidget.Workshop.Preview.Revision", Math.Max(1, _revisionIndex + 1));
            _revisionBadge.Visibility = _revisions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_revisionsMenuButton is not null)
        {
            string revMenuText = _loc.Format("CustomWidget.Workshop.CoCreate.RevisionsMenu", Math.Max(1, _revisionIndex + 1));
            _revisionsMenuButton.Content = CreateSmallIconLabel("\uE81C", revMenuText);
            AutomationProperties.SetName(_revisionsMenuButton, revMenuText);
        }

        RefreshBriefButtonChrome();
        RefreshCoCreationStream();
    }

    private Task PulseStageAsync()
    {
        ++_stagePulseToken;
        UIElement? pulseTarget = (UIElement?)_widgetFrame ?? _stageBorder;
        if (pulseTarget is not null && !_isClosed)
        {
            FluentMotionHelper.PlayDesktopArrivalPulse(pulseTarget);
        }

        return Task.CompletedTask;
    }

    private string ResolveDisplayEmoji(CustomWidgetManifest? manifest)
    {
        if (CustomWidgetTemplates.IsEmojiIcon(_selectedEmoji))
        {
            return _selectedEmoji!.Trim();
        }

        return manifest is not null && CustomWidgetTemplates.IsEmojiIcon(manifest.Icon)
            ? manifest.Icon.Trim()
            : "\u2728";
    }

    // ── Shared building blocks ──────────────────────────────────────────

    private Border CreateWidgetFrame(
        string emoji,
        string title,
        CustomWidgetContent content,
        CustomWidgetSizeSpec size,
        bool clipViewport,
        out TextBlock emojiText,
        out TextBlock titleText)
    {
        var frame = new Border
        {
            Width = Math.Clamp(size.Width, 240, 440),
            Height = Math.Clamp(size.Height, 180, 560),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Background = ThemeBrush("SolidBackgroundFillColorTertiaryBrush")
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        emojiText = new TextBlock
        {
            Text = emoji,
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        head.Children.Add(emojiText);
        Grid.SetColumn(titleText, 1);
        head.Children.Add(titleText);

        if (clipViewport)
        {
            grid.Children.Add(head);
        }
        else
        {
            var editGlyph = new FontIcon
            {
                Glyph = "\uE70F",
                FontSize = 11,
                Opacity = 0.55,
                Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(editGlyph, 2);
            head.Children.Add(editGlyph);

            var headerBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Content = head
            };
            ToolTipService.SetToolTip(headerBtn, _loc.T("CustomWidget.Workshop.CoCreate.QuickHeaderTip"));
            AutomationProperties.SetName(headerBtn, _loc.T("CustomWidget.Workshop.CoCreate.QuickHeaderTip"));
            headerBtn.Flyout = BuildHeaderQuickEditFlyout();
            grid.Children.Add(headerBtn);
        }

        var divider = new Border { Background = ThemeBrush("DividerStrokeColorDefaultBrush") };
        Grid.SetRow(divider, 1);
        grid.Children.Add(divider);

        UIElement bodyElement = clipViewport
            ? new ScrollViewer
            {
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            }
            : content;
        Grid.SetRow((FrameworkElement)bodyElement, 2);
        grid.Children.Add(bodyElement);
        frame.Child = grid;
        return frame;
    }

    private Flyout BuildHeaderQuickEditFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var stack = new StackPanel { Spacing = 10, Width = 256 };

        var emojiInput = new TextBox
        {
            Header = _loc.T("CustomWidget.Workshop.CoCreate.EmojiLabel"),
            MaxLength = 8
        };
        var titleInput = new TextBox
        {
            Header = _loc.T("CustomWidget.Workshop.CoCreate.TitleLabel"),
            MaxLength = 60
        };
        var applyBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = AccentButtonStyle(),
            Content = _loc.T("CustomWidget.Workshop.CoCreate.ApplyQuickEdit")
        };
        applyBtn.Click += (_, _) =>
        {
            flyout.Hide();
            ApplyZeroTokenMetadataEdit(
                newName: titleInput.Text,
                newIcon: emojiInput.Text);
        };

        stack.Children.Add(emojiInput);
        stack.Children.Add(titleInput);
        stack.Children.Add(applyBtn);

        flyout.Opening += (_, _) =>
        {
            CustomWidgetManifest? cur = CustomWidgetManifestParser.Parse(CurrentManifestJson ?? string.Empty).Manifest;
            emojiInput.Text = ResolveDisplayEmoji(cur);
            titleInput.Text = cur?.ResolveLocalizedName(_loc.CurrentCultureName) ?? string.Empty;
        };

        flyout.Content = stack;
        return flyout;
    }

    private void SetPage(UIElement element)
    {
        PageHost.Children.Clear();
        PageHost.Children.Add(element);
        FluentMotionHelper.PlayEntranceSlideAndFade(element, offsetY: 10f, durationMs: 180);
    }

    private InfoBar CreatePageInfoBar()
    {
        var bar = new InfoBar
        {
            IsClosable = true,
            IsOpen = false,
            Visibility = Visibility.Collapsed
        };
        bar.Closed += (_, _) => bar.Visibility = Visibility.Collapsed;
        return bar;
    }

    private string ResolveDirectStageTitle(AiGenerationStage stage, string providerName) => stage switch
    {
        AiGenerationStage.Connecting => _loc.Format("CustomWidget.Workshop.Stream.StageConnecting", providerName),
        AiGenerationStage.Thinking => _loc.T("CustomWidget.Workshop.Stream.StageThinking"),
        AiGenerationStage.GeneratingJson => _loc.T("CustomWidget.Workshop.Stream.StageGenerating"),
        AiGenerationStage.Validating => _loc.T("CustomWidget.Workshop.Stream.StageValidating"),
        AiGenerationStage.AutoRepairing => _loc.T("CustomWidget.Workshop.Stream.StageRepairing"),
        _ => _loc.T("CustomWidget.Workshop.Direct.Title")
    };

    private void UpdateDirectStreamProgress(AiGenerationStreamProgress progress)
    {
        _inFlightStage = progress.Stage;
        _directActiveModelName = progress.Model;
        _directLastCharTotal = progress.ReasoningChars + progress.ContentChars;

        string stageTitle = ResolveDirectStageTitle(progress.Stage, progress.ProviderName);
        if (_directStageTitleText is not null)
        {
            _directStageTitleText.Text = stageTitle;
        }

        if (_stageGeneratingStatusText is not null)
        {
            _stageGeneratingStatusText.Text = stageTitle;
        }

        UpdateDirectStepPills(progress.Stage);

        if (_directStatsBadgeText is not null)
        {
            _directStatsBadgeText.Text = FormatDirectStatsText(
                progress.Model,
                progress.Elapsed,
                _directLastCharTotal);
        }

        if (!string.IsNullOrWhiteSpace(progress.DetectedWidgetName))
        {
            _inFlightDetectedWidgetName = progress.DetectedWidgetName;
            if (_blueprintTitleText is not null)
            {
                _blueprintTitleText.Text = progress.DetectedWidgetName;
            }
            if (_nameText is not null && string.IsNullOrWhiteSpace(CurrentManifestJson))
            {
                _nameText.Text = progress.DetectedWidgetName;
            }
            if (_directDetectedWidgetBadge is not null && _directDetectedWidgetText is not null)
            {
                _directDetectedWidgetText.Text = _loc.Format(
                    "CustomWidget.Workshop.Stream.DetectedWidget",
                    progress.DetectedWidgetName);
                _directDetectedWidgetBadge.Visibility = Visibility.Visible;
            }
        }

        if (_directThinkingContainer is not null &&
            _directThinkingText is not null &&
            _directThinkingHeaderText is not null)
        {
            if (progress.ReasoningChars > 0)
            {
                _directThinkingContainer.Visibility = Visibility.Visible;
                _directThinkingHeaderText.Text = progress.Stage == AiGenerationStage.Thinking
                    ? _loc.Format("CustomWidget.Workshop.Stream.ThinkingLive", progress.ReasoningChars)
                    : _loc.Format("CustomWidget.Workshop.Stream.ThinkingDone", progress.ReasoningChars);

                string thinkTail = progress.ThinkingText.Length > 2800
                    ? "…\n" + progress.ThinkingText[^2800..]
                    : progress.ThinkingText;
                _directThinkingText.Text = thinkTail;

                if (_directThinkingScroller is { Visibility: Visibility.Visible } ts)
                {
                    ts.ChangeView(null, ts.ScrollableHeight + 400, null, disableAnimation: true);
                }
            }
        }

        if (_directStreamText is not null && _directStreamHintText is not null)
        {
            if (progress.ContentChars > 0)
            {
                _directStreamHintText.Text = _loc.Format("CustomWidget.Workshop.Stream.CharsReceived", progress.ContentChars);
                _directStreamText.Foreground = ThemeBrush("TextFillColorPrimaryBrush");
                string streamTail = progress.ContentText.Length > 4200
                    ? "…\n" + progress.ContentText[^4200..]
                    : progress.ContentText;
                _directStreamText.Text = streamTail;

                if (_directRawStreamWell is { Visibility: Visibility.Visible } &&
                    _directStreamScroller is { } scroller)
                {
                    scroller.ChangeView(null, scroller.ScrollableHeight + 600, null, disableAnimation: true);
                }
            }
            else if (progress.Stage == AiGenerationStage.Thinking)
            {
                _directStreamHintText.Text = _loc.T("CustomWidget.Workshop.Stream.ThinkingWaitOutput");
            }
        }
    }

    private string FormatDirectStatsText(string model, TimeSpan elapsed, int totalChars)
    {
        string timeText = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
        string shortModel = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;
        if (shortModel.Length > 20)
        {
            shortModel = shortModel[..20] + "…";
        }

        if (totalChars > 0)
        {
            return _loc.Format("CustomWidget.Workshop.Stream.StatsWithChars", shortModel, timeText, totalChars);
        }

        return string.IsNullOrWhiteSpace(shortModel)
            ? timeText
            : $"{shortModel} · {timeText}";
    }

    private void ShowPageMessage(
        InfoBarSeverity severity,
        string title,
        string message,
        ButtonBase? action,
        UIElement? extraContent = null)
    {
        if (_pageInfoBar is null)
        {
            return;
        }

        _pageInfoBar.Severity = severity;
        _pageInfoBar.Title = title;
        _pageInfoBar.Message = message;
        _pageInfoBar.ActionButton = action;
        _pageInfoBar.Content = extraContent;
        _pageInfoBar.Visibility = Visibility.Visible;
        _pageInfoBar.IsOpen = true;
    }

    private void AddMenuItem(MenuFlyout flyout, string glyph, string textKey, Func<Task> action)
    {
        var item = new MenuFlyoutItem
        {
            Text = _loc.T(textKey),
            Icon = new FontIcon { Glyph = glyph }
        };
        item.Click += async (_, _) => await action();
        flyout.Items.Add(item);
    }

    private static Button CreateIconButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 14 }
        };
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static Button CreateChipButton(string text, string? glyph = null)
    {
        var chip = new Button
        {
            MinHeight = 28,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Content = string.IsNullOrWhiteSpace(glyph)
                ? text
                : CreateSmallIconLabel(glyph!, text)
        };
        AutomationProperties.SetName(chip, text);
        return chip;
    }

    private static StackPanel CreateIconLabel(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static Grid CreateSmallIconLabel(string glyph, string text)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    private static Style? TextStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) ? value as Style : null;

    private static Style? AccentButtonStyle() =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? value) ? value as Style : null;

    private Brush ThemeBrush(string key)
    {
        if (NeutralInteractionBrush.ResolveThemedResource(key, RootGrid) is Brush themed)
        {
            return themed;
        }

        return Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush fallback
            ? fallback
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private void DisposePreviewContentOnly()
    {
        if (_previewContent is null)
        {
            return;
        }

        try
        {
            _previewContent.NodePicked -= OnPreviewNodePicked;
            _previewContent.Loaded -= OnPreviewContentLoadedForHealth;
            if (_previewContent.Parent is Panel parent)
            {
                parent.Children.Remove(_previewContent);
            }
            else if (_previewContent.Parent is ContentControl cc)
            {
                cc.Content = null;
            }

            _previewContent.Dispose();
        }
        catch
        {
        }

        _previewContent = null;
    }

    private void OnPreviewVmFeedbackRequested(object? sender, WidgetFeedbackRequestedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed || _page != StudioPage.Preview)
            {
                return;
            }

            InfoBarSeverity severity = e.Request.Severity switch
            {
                WidgetFeedbackSeverity.Error => InfoBarSeverity.Error,
                WidgetFeedbackSeverity.Warning => InfoBarSeverity.Warning,
                WidgetFeedbackSeverity.Success => InfoBarSeverity.Success,
                _ => InfoBarSeverity.Informational
            };

            ButtonBase? actionBtn = null;
            if (severity is InfoBarSeverity.Warning or InfoBarSeverity.Error &&
                !DeskBoxAiService.Shared.HasAnyConfiguredProvider())
            {
                var setupBtn = new Button { Content = _loc.T("CustomWidget.Workshop.Composer.QuickSetupApi") };
                setupBtn.Click += async (_, _) => await ShowAiConfigDialogInStudioAsync(null, null);
                actionBtn = setupBtn;
            }

            ShowPageMessage(severity, string.Empty, e.Request.Message, actionBtn);
        });
    }

    private void DisposePreview()
    {
        DisposePreviewContentOnly();
        try
        {
            if (_previewVm is not null)
            {
                _previewVm.FeedbackRequested -= OnPreviewVmFeedbackRequested;
                _previewVm.Dispose();
            }
        }
        catch
        {
        }

        _previewVm = null;
        _stageColumn = null;
        _stageWidthManuallyAdjusted = false;
        _stageBorder = null;
        _widgetFrame = null;
        _headerEmojiText = null;
        _nameText = null;
        _frameEmojiText = null;
        _frameTitleText = null;
        _revisionBadge = null;
        _revisionBadgeText = null;
        _undoButton = null;
        _redoButton = null;
        _sizeQuickButton = null;
        _revisionsMenuButton = null;
        _briefHeaderButton = null;
        _chatStreamScroller = null;
        _chatStreamPanel = null;
        _suggestionChipsPanel = null;
        _targetNodePillBorder = null;
        _targetNodePillText = null;
        _refineTextBox = null;
        _refineSubmitButton = null;
        _commitButton = null;
        _stageWallpaperLayer = null;
        _stageWallpaperScrim = null;
        _stageGeneratingProgressBar = null;
        _stageGeneratingStatusPill = null;
        _stageGeneratingStatusText = null;
        _blueprintTitleText = null;
        _sampleDataToggleButton = null;
        _directStageTitleText = null;
        _directStatsBadgeText = null;
        _directDetectedWidgetBadge = null;
        _directDetectedWidgetText = null;
        _directThinkingContainer = null;
        _directThinkingHeaderText = null;
        _directThinkingScroller = null;
        _directThinkingText = null;
        _directRawStreamWell = null;
        _directStreamHintText = null;
        _directStreamScroller = null;
        _directStreamText = null;
        _directStep1Pill = null;
        _directStep2Pill = null;
        _directStep3Pill = null;
    }

    private void DisposeTemplatePreviews()
    {
        foreach ((CustomWidgetViewModel vm, CustomWidgetContent content) in _templatePreviews)
        {
            try
            {
                content.Dispose();
                vm.Dispose();
            }
            catch
            {
            }
        }

        _templatePreviews.Clear();
    }

    /// <summary>Tiny 8px dot used as the change-list bullet.</summary>
    private readonly struct Ellipse8(Brush fill)
    {
        public FrameworkElement Element { get; } = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = fill,
            VerticalAlignment = VerticalAlignment.Center
        };
    }
}
