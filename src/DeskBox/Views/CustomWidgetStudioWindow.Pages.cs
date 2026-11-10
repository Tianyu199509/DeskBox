using System.Globalization;
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
    private const double StartPageMaxWidth = 1360;

    private InfoBar? _pageInfoBar;
    // P2：暂存区变化时只刷新提示条本身，避免重建首屏打断正在输入的需求。
    private Action? _refreshAgentStageBanner;
    private Action? _refreshTemplateGalleryFromRemote;
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
    private Button? _i18nHeaderButton;
    private FontIcon? _i18nHeaderIcon;
    private TextBlock? _i18nHeaderLabel;
    private Button? _previewCultureOverridePillButton;
    private Border? _localeCenterHostBorder;
    private TextBlock? _localeCenterSummaryText;
    private TextBlock? _localeCenterHintText;
    private ProgressBar? _localeCenterOverallProgressBar;
    private Button? _localeCenterSyncAllButton;
    private Button? _localeCenterSync2Button;
    private Button? _localeCenterRetryFailedButton;
    private Button? _localeCenterStopButton;
    private Button? _localeCenterCloseButton;
    private readonly Dictionary<string, LocaleCardUiRefs> _localeCenterCardRefs =
        new(StringComparer.OrdinalIgnoreCase);
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
    private ProgressRing? _stageGeneratingProgressRing;
    private Border? _stageGeneratingStatusPill;
    private TextBlock? _stageGeneratingStatusText;
    private TextBlock? _blueprintTitleText;
    private Border? _blueprintFrame;
    private Border? _blueprintDivider;
    private readonly List<(Border Block, bool UseSecondaryWash)> _blueprintSkeletonBlocks = [];
    private Border? _frameDivider;
    private FontIcon? _frameEditGlyph;
    private Button? _pickElementButton;
    private FontIcon? _pickElementIcon;
    private TextBlock? _pickElementLabel;
    private ToolkitControls.Segmented? _stageViewportModeSegmented;
    private ToolkitControls.Segmented? _stageBackdropSegmented;
    private Border? _capsulePreviewFrame;
    private TextBlock? _capsuleEmojiText;
    private FontIcon? _capsuleGlyphIcon;
    private TextBlock? _capsuleTitleText;
    private TextBlock? _capsuleSummaryText;
    private Border? _capsuleBadgeBorder;
    private TextBlock? _capsuleBadgeText;
    private Button? _capsulePrimaryActionButton;
    private FontIcon? _capsulePrimaryActionIcon;
    private ProgressBar? _capsuleProgressBar;
    private Border? _scriptErrorBannerBorder;
    private TextBlock? _scriptErrorBannerText;
    private Button? _scriptErrorFixButton;
    private Button? _sampleDataToggleButton;
    private Button? _settingsPreviewButton;
    private BitmapImage? _cachedWallpaperBitmap;
    private bool _wallpaperLoadAttempted;
    private StartGallerySourceMode _activeStartGalleryMode = StartGallerySourceMode.Templates;
    private TemplateGalleryCategory _activeGalleryCategory = TemplateGalleryCategory.All;
    private int _stagePulseToken;

    // Direct API live streaming elements
    private TextBlock? _directStageTitleText;
    private TextBlock? _directStatsBadgeText;
    private Border? _directThinkingContainer;
    private TextBlock? _directThinkingHeaderText;
    private TextBlock? _directThinkingCollapsedPreviewText;
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
    private bool _directThinkingCollapsedByUser = true;
    private bool _chatStickToBottom = true;
    private bool _streamStickToBottom = true;
    private bool _thinkingStickToBottom = true;
    private int _programmaticChatScrollDepth;
    private int _programmaticStreamScrollDepth;
    private int _programmaticThinkingScrollDepth;

    private enum StartGallerySourceMode
    {
        Templates,
        MyWidgets
    }

    internal enum TemplateGalleryCategory
    {
        All,
        Habits,
        Tools,
        Live,
        Ai
    }

    private sealed record LocaleCardUiRefs(
        string Code,
        string NativeName,
        Border CardBorder,
        Button SelectButton,
        TextBlock NameText,
        FontIcon PreviewEyeIcon,
        TextBlock StatusText,
        Button RetrySingleButton,
        ProgressBar ProgressBar);

    private sealed record ExistingWidgetCardItem(
        string Id,
        string Name,
        string Emoji,
        string Summary,
        string ManifestJson,
        bool IsVisibleOnDesktop,
        int RevisionCount,
        long LastUpdatedUtcTicks,
        string? LogicJs = null,
        CustomWidgetLocaleCatalog? Locales = null,
        string ManifestVersion = "1.0.0");

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

            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(inst.Id);
            string? logicJs = store.ReadLogicJsOrNull();
            CustomWidgetLocaleCatalog loadedCatalog = store.ReadLocaleCatalog();
            CustomWidgetLocaleCatalog? locales = loadedCatalog.HasAnyLocale ? loadedCatalog : null;

            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
            CustomWidgetManifest? manifest = parsed.Manifest;
            if (manifest is not null && locales is not null)
            {
                manifest = manifest.WithLocales(locales);
            }

            string emoji = ResolveFallbackEmojiForManifest(manifest);
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
                string manifestPath = store.ManifestPath;
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
                LastUpdatedUtcTicks: Math.Max(fileTicks, lastRevTicks),
                LogicJs: logicJs,
                Locales: locales,
                ManifestVersion: string.IsNullOrWhiteSpace(manifest?.Version) ? "1.0.0" : manifest!.Version.Trim()));
        }

        return items
            .OrderByDescending(static x => x.LastUpdatedUtcTicks)
            .ToList();
    }

    internal sealed record WorkshopCardSpec(
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
        string UpgradeRequirementLabel = "",
        CustomWidgetLocaleCatalog? Locales = null,
        string? LogicJs = null,
        string Version = "1.0.0",
        bool IsRemoteOnly = false,
        bool IsRemoteUpgraded = false);

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

    private IReadOnlyList<WorkshopCardSpec> GetWorkshopGallerySpecs() =>
        BuildWorkshopGallerySpecs(CustomWidgetRemoteCatalogService.Shared, _loc);

    internal static IReadOnlyList<WorkshopCardSpec> BuildWorkshopGallerySpecs(
        CustomWidgetRemoteCatalogService catalogService,
        LocalizationService loc)
    {
        IReadOnlyList<CustomWidgetCatalogEntry> mergedOfficial = catalogService.GetMergedOfficialTemplates();
        var mergedByNormId = new Dictionary<string, CustomWidgetCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetCatalogEntry entry in mergedOfficial)
        {
            string normId = CustomWidgetTemplates.TryNormalizeKnownTemplateId(entry.Id) ?? entry.Id.Trim();
            if (!string.IsNullOrWhiteSpace(normId))
            {
                mergedByNormId[normId] = entry;
            }
        }

        (string Id, string DefaultEmoji, string LabelKey, string DescriptionKey, string PromptKey, TemplateGalleryCategory Category)[] curatedBuiltIns =
        [
            (CustomWidgetTemplates.HydrationStarterId, "💧", "CustomWidget.AiWizard.Chip.Water.Label", "CustomWidget.Workshop.Card.Water.Desc", "CustomWidget.AiWizard.Chip.Water.Prompt", TemplateGalleryCategory.Habits),
            (CustomWidgetTemplates.AiInboxStarterId, "⚡", "CustomWidget.Studio.Starter.AiInbox.Label", "CustomWidget.Workshop.Card.AiInbox.Desc", "CustomWidget.Studio.Starter.AiInbox.Prompt", TemplateGalleryCategory.Ai),
            (CustomWidgetTemplates.SystemPulseTemplateId, "📊", "CustomWidget.Template.SystemPulse.Name", "CustomWidget.Template.SystemPulse.Description", "CustomWidget.AiWizard.Chip.Monitor.Prompt", TemplateGalleryCategory.Live),
            (CustomWidgetTemplates.NewsRssStarterId, "📰", "CustomWidget.Studio.Starter.NewsRss.Label", "CustomWidget.Workshop.Card.NewsRss.Desc", "CustomWidget.Studio.Starter.NewsRss.Prompt", TemplateGalleryCategory.Live),
            (CustomWidgetTemplates.PomodoroTemplateId, "🍅", "CustomWidget.Template.Pomodoro.Name", "CustomWidget.Template.Pomodoro.Description", "CustomWidget.Workshop.Card.Pomodoro.Prompt", TemplateGalleryCategory.Habits),
            (CustomWidgetTemplates.CountdownTemplateId, "⏳", "CustomWidget.Template.Countdown.Name", "CustomWidget.Template.Countdown.Description", string.Empty, TemplateGalleryCategory.Tools),
            (CustomWidgetTemplates.WorldClockTemplateId, "🌍", "CustomWidget.Template.WorldClock.Name", "CustomWidget.Template.WorldClock.Description", string.Empty, TemplateGalleryCategory.Tools),
            (CustomWidgetTemplates.DevToolboxTemplateId, "🧰", "CustomWidget.Template.DevToolbox.Name", "CustomWidget.Template.DevToolbox.Description", string.Empty, TemplateGalleryCategory.Tools),
            (CustomWidgetTemplates.AiPulseTemplateId, "🧠", "CustomWidget.Template.AiPulse.Name", "CustomWidget.Template.AiPulse.Description", string.Empty, TemplateGalleryCategory.Ai),
            (CustomWidgetTemplates.AgentHudTemplateId, "🤖", "CustomWidget.Studio.Starter.AgentHud.Label", "CustomWidget.Workshop.Card.AgentHud.Desc", "CustomWidget.Studio.Starter.AgentHud.Prompt", TemplateGalleryCategory.Ai)
        ];

        var specs = new List<WorkshopCardSpec>(mergedOfficial.Count);
        var consumedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string id, string defaultEmoji, string labelKey, string descriptionKey, string promptKey, TemplateGalleryCategory category) in curatedBuiltIns)
        {
            if (!mergedByNormId.TryGetValue(id, out CustomWidgetCatalogEntry? mergedEntry))
            {
                // Remotely deprecated built-in template is omitted fromGetMergedOfficialTemplates().
                continue;
            }

            consumedIds.Add(id);
            consumedIds.Add(mergedEntry.Id);

            string cleanJson = CustomWidgetAiGeneratorService.ExtractInlineManifestLocalesAndStrip(
                mergedEntry.ManifestJson ?? string.Empty,
                mergedEntry.Locales,
                out CustomWidgetLocaleCatalog? cleanLocales);

            string emoji = mergedEntry.IsRemoteUpgraded && CustomWidgetTemplates.IsEmojiIcon(mergedEntry.IconGlyph)
                ? mergedEntry.IconGlyph.Trim()
                : defaultEmoji;

            string? directLabel = null;
            string? directDesc = null;
            if (mergedEntry.IsRemoteUpgraded)
            {
                CustomWidgetTemplateBundle localBundle = CustomWidgetTemplates.GetTemplateBundle(id);
                CustomWidgetManifest? localManifest = CustomWidgetManifestParser.Parse(localBundle.ManifestJson).Manifest;
                if (localManifest is not null && localBundle.Locales is { IsEmpty: false })
                {
                    localManifest = localManifest.WithLocales(localBundle.Locales);
                }

                string localTitleZh = localManifest?.ResolveLocalizedName("zh-CN") ?? mergedEntry.DefaultTitle;
                string localDescZh = localManifest?.ResolveLocalizedDescription("zh-CN") ?? string.Empty;

                bool remoteRenamed = mergedEntry.Names is { Count: > 0 } ||
                                     (!string.IsNullOrWhiteSpace(mergedEntry.Name) &&
                                      !string.Equals(mergedEntry.Name.Trim(), localTitleZh.Trim(), StringComparison.Ordinal));
                bool remoteRedescribed = mergedEntry.Descriptions is { Count: > 0 } ||
                                         (!string.IsNullOrWhiteSpace(mergedEntry.Description) &&
                                          !string.Equals(mergedEntry.Description.Trim(), localDescZh.Trim(), StringComparison.Ordinal));

                if (remoteRenamed)
                {
                    directLabel = mergedEntry.ResolveDisplayName(loc);
                }

                if (remoteRedescribed)
                {
                    directDesc = mergedEntry.ResolveDescription(loc);
                }
            }

            specs.Add(new WorkshopCardSpec(
                id,
                emoji,
                labelKey,
                descriptionKey,
                promptKey,
                cleanJson,
                category,
                DirectLabel: directLabel,
                DirectDescription: directDesc,
                RequiresClientUpgrade: mergedEntry.RequiresClientUpgrade,
                UpgradeRequirementLabel: mergedEntry.UpgradeRequirementLabel,
                Locales: mergedEntry.Locales ?? cleanLocales,
                LogicJs: mergedEntry.LogicJs,
                Version: string.IsNullOrWhiteSpace(mergedEntry.Version) ? "1.0.0" : mergedEntry.Version,
                IsRemoteOnly: false,
                IsRemoteUpgraded: mergedEntry.IsRemoteUpgraded));
        }

        foreach (CustomWidgetCatalogEntry remote in mergedOfficial)
        {
            if (!remote.IsRemoteOnly || !consumedIds.Add(remote.Id))
            {
                continue;
            }

            string emoji = CustomWidgetTemplates.IsEmojiIcon(remote.IconGlyph)
                ? remote.IconGlyph.Trim()
                : "✨";
            string resolvedName = remote.ResolveDisplayName(loc);
            string cleanRemoteJson = CustomWidgetAiGeneratorService.ExtractInlineManifestLocalesAndStrip(
                remote.ManifestJson ?? string.Empty,
                remote.Locales,
                out CustomWidgetLocaleCatalog? remoteLocales);
            specs.Add(new WorkshopCardSpec(
                remote.Id,
                emoji,
                string.Empty,
                string.Empty,
                string.Empty,
                cleanRemoteJson,
                InferRemoteTemplateCategory(remote.Id, resolvedName),
                DirectLabel: resolvedName,
                DirectDescription: remote.ResolveDescription(loc),
                RequiresClientUpgrade: remote.RequiresClientUpgrade,
                UpgradeRequirementLabel: remote.UpgradeRequirementLabel,
                Locales: remote.Locales ?? remoteLocales,
                LogicJs: remote.LogicJs,
                Version: string.IsNullOrWhiteSpace(remote.Version) ? "1.0.0" : remote.Version,
                IsRemoteOnly: true,
                IsRemoteUpgraded: false));
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

        var startHeroActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top
        };

        var startImportPkgBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(11, 0, 11, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12.5,
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Content = CreateSmallIconLabel("\uE8E5", _loc.T("CustomWidget.Workshop.ImportPackageButton"))
        };
        ToolTipService.SetToolTip(startImportPkgBtn, _loc.T("CustomWidget.Workshop.ImportPackageButton"));
        AutomationProperties.SetName(startImportPkgBtn, _loc.T("CustomWidget.Workshop.ImportPackageButton"));
        startImportPkgBtn.Click += async (_, _) => await ImportPackageFromDiskInStudioAsync();
        startHeroActions.Children.Add(startImportPkgBtn);

        var startSwitchModeBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(11, 0, 11, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12.5,
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Content = CreateSmallIconLabel("\uE8AB", _loc.T("CustomWidget.Workshop.SwitchModeButton"))
        };
        ToolTipService.SetToolTip(startSwitchModeBtn, _loc.T("CustomWidget.Workshop.Onboarding.Title"));
        AutomationProperties.SetName(startSwitchModeBtn, _loc.T("CustomWidget.Workshop.SwitchModeButton"));
        startSwitchModeBtn.Click += (_, _) => ShowAgentMcpOnboardingTour(forceShow: true);
        startHeroActions.Children.Add(startSwitchModeBtn);

        var startGuideBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(11, 0, 11, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12.5,
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Content = CreateSmallIconLabel("\uE897", _loc.T("CustomWidget.Workshop.GuideButton"))
        };
        ToolTipService.SetToolTip(startGuideBtn, _loc.T("CustomWidget.Workshop.GuideButton"));
        AutomationProperties.SetName(startGuideBtn, _loc.T("CustomWidget.Workshop.GuideButton"));
        startGuideBtn.Click += (_, _) => StartChannelStepTour(ResolveEffectiveStudioChannel(out _));
        startHeroActions.Children.Add(startGuideBtn);

        Button startApiDocsBtn = CreateSubtleApiDocsToggleButton(
            "CustomWidget.ApiDocs.Button",
            fontSize: 12.5,
            horizontalPadding: 12);
        startHeroActions.Children.Add(startApiDocsBtn);

        Grid.SetColumn(startHeroActions, 1);
        startHeroRow.Children.Add(startHeroActions);

        column.Children.Add(startHeroRow);

        _refreshAgentStageBanner = null;
        _refreshTemplateGalleryFromRemote = null;
        if (CreateDraftRecoveryBannerIfNeeded() is { } startRecoveryBanner)
        {
            startRecoveryBanner.Margin = new Thickness(0, 14, 0, 0);
            column.Children.Add(startRecoveryBanner);
        }

        // P2：外部 Agent 的写入默认只进暂存区，首屏必须显式暴露「待确认、桌面未变」。
        if (CreateAgentStageBannerIfNeeded() is { } startAgentStageBanner)
        {
            startAgentStageBanner.Margin = new Thickness(0, 12, 0, 0);
            column.Children.Add(startAgentStageBanner);
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

        (string Emoji, string LabelKey, string PromptKey)[] starterChips =
        [
            ("💧", "CustomWidget.AiWizard.Chip.Water.Label", "CustomWidget.AiWizard.Chip.Water.Prompt"),
            ("⚡", "CustomWidget.Studio.Starter.AiInbox.Label", "CustomWidget.Studio.Starter.AiInbox.Prompt"),
            ("📊", "CustomWidget.Template.SystemPulse.Name", "CustomWidget.AiWizard.Chip.Monitor.Prompt"),
            ("📰", "CustomWidget.Studio.Starter.NewsRss.Label", "CustomWidget.Studio.Starter.NewsRss.Prompt"),
            ("🍅", "CustomWidget.Template.Pomodoro.Name", "CustomWidget.Workshop.Card.Pomodoro.Prompt")
        ];

        var chips = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        foreach ((string emoji, string labelKey, string promptKey) in starterChips)
        {
            string prompt = _loc.T(promptKey);
            string capturedEmoji = emoji;
            string chipLabel = _loc.T(labelKey);
            Button chip = CreateChipButton(chipLabel);
            chip.Click += (_, _) =>
            {
                _selectedEmoji = capturedEmoji;
                ideaBox.Text = prompt;
                ideaBox.SelectionStart = ideaBox.Text.Length;
                ideaBox.Focus(FocusState.Programmatic);
            };
            chips.Children.Add(chip);
        }

        Grid.SetColumn(chips, 1);
        tryRow.Children.Add(chips);
        column.Children.Add(tryRow);

        int initialMyWidgetCount = App.Current.WidgetManager?.GetCustomWidgetInstances().Count ?? 0;

        var templatesHeaderRow = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 6,
            Margin = new Thickness(0, 26, 0, 14)
        };
        templatesHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        templatesHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        templatesHeaderRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        templatesHeaderRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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

        string myWidgetsTabLabel = initialMyWidgetCount > 0
            ? _loc.Format("CustomWidget.Workshop.MyWidgets.TabCount", initialMyWidgetCount)
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
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        templatesHeaderRow.Children.Add(galleryModeSegmented);
        Grid.SetRow(gallerySubtitleText, 1);
        templatesHeaderRow.Children.Add(gallerySubtitleText);

        var syncStatusText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        FontIcon CreateSyncIdleIcon() => new()
        {
            Glyph = "\uE72C",
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush")
        };

        var syncCatalogButton = new Button
        {
            Width = 30,
            Height = 30,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Content = CreateSyncIdleIcon()
        };
        AutomationProperties.SetName(syncCatalogButton, _loc.T("CustomWidget.Workshop.Sync.AriaLabel"));

        var syncHeaderCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { syncStatusText, syncCatalogButton }
        };
        Grid.SetColumn(syncHeaderCluster, 1);
        templatesHeaderRow.Children.Add(syncHeaderCluster);

        var filterBar = new SelectorBar
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -4)
        };
        Grid.SetRow(filterBar, 1);
        Grid.SetColumn(filterBar, 1);
        templatesHeaderRow.Children.Add(filterBar);
        column.Children.Add(templatesHeaderRow);

        var cardsHostContainer = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
        bool suppressFilterEvent = false;
        bool suppressModeEvent = false;
        bool isManualSyncInProgress = false;
        int syncStatusClearToken = 0;
        IReadOnlyList<WorkshopCardSpec>? cachedSpecs = null;

        void RefreshSyncButtonTooltip()
        {
            int templateCount = cachedSpecs?.Count ?? CustomWidgetRemoteCatalogService.Shared.GetMergedOfficialTemplates().Count;
            DateTimeOffset? lastCheckedUtc = CustomWidgetRemoteCatalogService.Shared.GetLastCheckedUtc();
            string tip = lastCheckedUtc.HasValue
                ? _loc.Format(
                    "CustomWidget.Workshop.Sync.ButtonTooltipChecked",
                    FormatGalleryRelativeTimestamp(lastCheckedUtc.Value),
                    templateCount)
                : _loc.Format("CustomWidget.Workshop.Sync.ButtonTooltipNever", templateCount);
            ToolTipService.SetToolTip(syncCatalogButton, tip);
        }

        async Task ShowTransientSyncStatusAsync(string text, string brushKey, int durationMs)
        {
            int token = ++syncStatusClearToken;
            syncStatusText.Text = text;
            syncStatusText.Foreground = ThemeBrush(brushKey);
            syncStatusText.Visibility = Visibility.Visible;

            if (durationMs <= 0)
            {
                return;
            }

            await Task.Delay(durationMs);
            if (!_isClosed && _page == StudioPage.Start && token == syncStatusClearToken)
            {
                syncStatusText.Visibility = Visibility.Collapsed;
            }
        }

        syncCatalogButton.PointerEntered += (_, _) => RefreshSyncButtonTooltip();
        RefreshSyncButtonTooltip();

        void SwitchToTemplatesMode()
        {
            suppressModeEvent = true;
            galleryModeSegmented.SelectedItem = templatesSegItem;
            suppressModeEvent = false;
            _activeStartGalleryMode = StartGallerySourceMode.Templates;
            RenderGallerySection(animate: true);
        }

        async Task HydrateGallerySectionAsync(int runId, bool animate)
        {
            bool isMyWidgets = _activeStartGalleryMode == StartGallerySourceMode.MyWidgets;
            filterBar.Visibility = isMyWidgets ? Visibility.Collapsed : Visibility.Visible;
            syncHeaderCluster.Visibility = isMyWidgets ? Visibility.Collapsed : Visibility.Visible;
            gallerySubtitleText.Text = _loc.T(isMyWidgets
                ? "CustomWidget.Workshop.MyWidgets.Subtitle"
                : "CustomWidget.Workshop.Start.TemplatesSubtitle");

            cardsHostContainer.Child = CreateGallerySkeletonGrid(cardCount: 6);

            await Task.Delay(32);
            if (_isClosed || _page != StudioPage.Start || runId != _galleryHydrationRunId)
            {
                return;
            }

            var hydratePreviewActions = new List<Action>();

            if (isMyWidgets)
            {
                IReadOnlyList<ExistingWidgetCardItem> existingItems = await Task.Run(GetExistingWidgetGalleryItems);
                if (_isClosed || _page != StudioPage.Start || runId != _galleryHydrationRunId)
                {
                    return;
                }

                string updatedTabLabel = existingItems.Count > 0
                    ? _loc.Format("CustomWidget.Workshop.MyWidgets.TabCount", existingItems.Count)
                    : _loc.T("CustomWidget.Workshop.MyWidgets.Tab");
                myWidgetsSegItem.Content = updatedTabLabel;
                AutomationProperties.SetName(myWidgetsSegItem, updatedTabLabel);

                if (existingItems.Count == 0)
                {
                    cardsHostContainer.Child = CreateMyWidgetsEmptyState(SwitchToTemplatesMode);
                    if (animate)
                    {
                        FluentMotionHelper.PlayEntranceSlideAndFade(cardsHostContainer, offsetY: 6f, durationMs: 150);
                    }

                    return;
                }

                var myCardElements = new List<FrameworkElement>(existingItems.Count);
                foreach (ExistingWidgetCardItem item in existingItems)
                {
                    myCardElements.Add(CreateExistingWidgetCard(item, out Action hydrateAction));
                    hydratePreviewActions.Add(hydrateAction);
                }

                cardsHostContainer.Child = CreateResponsiveCardsGrid(myCardElements);
            }
            else
            {
                cachedSpecs ??= await Task.Run(GetWorkshopGallerySpecs);
                if (_isClosed || _page != StudioPage.Start || runId != _galleryHydrationRunId)
                {
                    return;
                }

                RefreshSyncButtonTooltip();

                List<WorkshopCardSpec> filtered = _activeGalleryCategory == TemplateGalleryCategory.All
                    ? cachedSpecs.ToList()
                    : cachedSpecs.Where(s => s.Category == _activeGalleryCategory).ToList();

                var cardElements = new List<FrameworkElement>(filtered.Count);
                foreach (WorkshopCardSpec spec in filtered)
                {
                    cardElements.Add(CreateTemplateCard(spec, out Action hydrateAction));
                    hydratePreviewActions.Add(hydrateAction);
                }

                cardsHostContainer.Child = CreateResponsiveCardsGrid(cardElements);
            }

            if (animate)
            {
                FluentMotionHelper.PlayEntranceSlideAndFade(cardsHostContainer, offsetY: 6f, durationMs: 150);
            }

            for (int i = 0; i < hydratePreviewActions.Count; i++)
            {
                if (_isClosed || _page != StudioPage.Start || runId != _galleryHydrationRunId)
                {
                    return;
                }

                hydratePreviewActions[i]();
                if (i % 2 == 1)
                {
                    await Task.Delay(18);
                }
            }
        }

        void RenderGallerySection(bool animate)
        {
            DisposeTemplatePreviews();
            int runId = ++_galleryHydrationRunId;
            _ = HydrateGallerySectionAsync(runId, animate);
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

        syncCatalogButton.Click += async (_, _) =>
        {
            if (isManualSyncInProgress)
            {
                return;
            }

            isManualSyncInProgress = true;
            syncCatalogButton.IsEnabled = false;
            syncCatalogButton.Content = new ProgressRing
            {
                Width = 13,
                Height = 13,
                IsActive = true
            };
            _ = ShowTransientSyncStatusAsync(
                _loc.T("CustomWidget.Workshop.Sync.StatusSyncing"),
                "TextFillColorSecondaryBrush",
                durationMs: 0);

            try
            {
                Task<CustomWidgetCatalogSyncResult> catalogTask = CustomWidgetRemoteCatalogService.Shared.SyncAsync(force: true);
                Task<bool> promptTask = CustomWidgetRemotePromptService.Shared.RefreshInBackgroundAsync(force: true);
                await Task.WhenAll(catalogTask, promptTask);

                if (_isClosed || _page != StudioPage.Start)
                {
                    return;
                }

                CustomWidgetCatalogSyncResult syncResult = await catalogTask;
                RefreshSyncButtonTooltip();

                if (syncResult.Status == CustomWidgetCatalogSyncStatus.Updated)
                {
                    cachedSpecs = null;
                    if (_activeStartGalleryMode == StartGallerySourceMode.Templates)
                    {
                        RenderGallerySection(animate: true);
                    }

                    _ = ShowTransientSyncStatusAsync(
                        _loc.Format("CustomWidget.Workshop.Sync.StatusUpdated", Math.Max(1, syncResult.ChangedCount)),
                        "AccentTextFillColorPrimaryBrush",
                        durationMs: 4500);
                }
                else if (syncResult.Status == CustomWidgetCatalogSyncStatus.OfflineOrFailed)
                {
                    _ = ShowTransientSyncStatusAsync(
                        _loc.T("CustomWidget.Workshop.Sync.StatusOffline"),
                        "TextFillColorTertiaryBrush",
                        durationMs: 4000);
                }
                else
                {
                    _ = ShowTransientSyncStatusAsync(
                        _loc.T("CustomWidget.Workshop.Sync.StatusUpToDate"),
                        "TextFillColorSecondaryBrush",
                        durationMs: 3200);
                }
            }
            finally
            {
                isManualSyncInProgress = false;
                if (!_isClosed)
                {
                    syncCatalogButton.Content = CreateSyncIdleIcon();
                    syncCatalogButton.IsEnabled = true;
                }
            }
        };

        _refreshTemplateGalleryFromRemote = () =>
        {
            cachedSpecs = null;
            RefreshSyncButtonTooltip();
            if (!isManualSyncInProgress && _activeStartGalleryMode == StartGallerySourceMode.Templates)
            {
                RenderGallerySection(animate: true);
            }
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

        // P0：「是否已选择创作方式」与「是否看过教学」解耦。
        // ChannelAuto（空串）表示用户尚未显式选择，此时必须打开选择弹窗，
        // 不能让 ResolveEffectiveStudioChannel 静默推断成网页 AI。
        if (string.IsNullOrWhiteSpace(_prefs.PreferredStudioChannel))
        {
            ShowAgentMcpOnboardingTour(forceShow: true);
        }
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

    private FrameworkElement CreateCardPreviewSkeleton()
    {
        var miniFrame = new Border
        {
            Width = 220,
            Height = 128,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new Border
        {
            Width = 88,
            Height = 10,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeBrush("SubtleFillColorSecondaryBrush")
        });
        stack.Children.Add(new Border
        {
            Height = 42,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = ThemeBrush("SubtleFillColorTertiaryBrush")
        });

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border
        {
            Height = 24,
            CornerRadius = new CornerRadius(5),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush")
        });
        var rightBar = new Border
        {
            Height = 24,
            CornerRadius = new CornerRadius(5),
            Background = ThemeBrush("SubtleFillColorTertiaryBrush")
        };
        Grid.SetColumn(rightBar, 1);
        row.Children.Add(rightBar);
        stack.Children.Add(row);

        miniFrame.Child = stack;
        return miniFrame;
    }

    private FrameworkElement CreateGallerySkeletonGrid(int cardCount)
    {
        var rootStack = new StackPanel { Spacing = 10 };
        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(2, 0, 0, 2)
        };
        statusRow.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center
        });
        statusRow.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Gallery.LoadingSkeleton"),
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        rootStack.Children.Add(statusRow);

        var cards = new List<FrameworkElement>(cardCount);
        for (int i = 0; i < cardCount; i++)
        {
            var cardGrid = new Grid();
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(164) });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var previewWell = new Border
            {
                Background = ThemeBrush("LayerOnMicaBaseAltFillColorSecondaryBrush"),
                BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Child = CreateCardPreviewSkeleton()
            };
            cardGrid.Children.Add(previewWell);

            var textStack = new StackPanel { Spacing = 8, Padding = new Thickness(14, 14, 14, 16) };
            textStack.Children.Add(new Border
            {
                Width = 116,
                Height = 14,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = ThemeBrush("SubtleFillColorSecondaryBrush")
            });
            textStack.Children.Add(new Border
            {
                Height = 11,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = ThemeBrush("SubtleFillColorTertiaryBrush")
            });
            textStack.Children.Add(new Border
            {
                Width = 180,
                Height = 11,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = ThemeBrush("SubtleFillColorTertiaryBrush")
            });
            Grid.SetRow(textStack, 1);
            cardGrid.Children.Add(textStack);

            cards.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                Child = cardGrid
            });
        }

        rootStack.Children.Add(CreateResponsiveCardsGrid(cards));
        return rootStack;
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
            // 800 会把用户从别处粘来的详细需求静默截断（截断处没有提示、没有计数）。
            // 需求描述本身就是长文本，这里放宽到 4000，Shift+Enter 仍可换行。
            MaxLength = 4000,
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
            if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
            {
                return;
            }

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
        leftBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Action? refreshPasteVisibility = null;
        DropDownButton channelSelector = CreateAiChannelSelectorButton(() => refreshPasteVisibility?.Invoke());
        channelSelector.MaxWidth = 260;
        _activeChannelSelectorButton = channelSelector;
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
        _activeCapabilityPickerButton = capabilityPickerBtn;
        Grid.SetColumn(capabilityPickerBtn, 4);
        leftBar.Children.Add(capabilityPickerBtn);

        void RefreshComposerCapabilityChrome()
        {
            string culture = _loc.CurrentCultureName;
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
                string shortCapTitle = StripTrailingCapabilitySuffix(spec.GetTitle(culture));
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
                ToolTipService.SetToolTip(inspectBtn, $"@{spec.Id} — {spec.GetSummary(culture)}");
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
        quickSetupApiButton.Click += async (_, _) =>
        {
            if (ResolveEffectiveStudioChannel(out _) == StudioAiChannelKind.AgentMcp)
            {
                await ShowAgentMcpQuickConnectDialogAsync(_refreshActiveChannelSelector);
            }
            else
            {
                await ShowAiConfigDialogInStudioAsync(null, _refreshActiveChannelSelector);
            }
        };
        _activeQuickSetupButton = quickSetupApiButton;
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
        Grid.SetColumn(clearButton, 5);
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
        _activeComposerSubmitButton = submitButton;
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

            bool isAgentMcp = ResolveEffectiveStudioChannel(out _) == StudioAiChannelKind.AgentMcp;
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

            string effectiveLabel = isAgentMcp
                ? _loc.T("CustomWidget.Workshop.Composer.SubmitAgentMcp")
                : submitLabel;
            string effectiveTip = string.IsNullOrWhiteSpace(shortcutHintText)
                ? effectiveLabel
                : $"{effectiveLabel} ({shortcutHintText})";
            submitButton.Content = new FontIcon { Glyph = isAgentMcp ? "\uE8C8" : "\uE74A", FontSize = 13 };
            ToolTipService.SetToolTip(submitButton, effectiveTip);
            AutomationProperties.SetName(submitButton, effectiveLabel);
        }

        double latestCardWidth = 760;
        refreshPasteVisibility = () =>
        {
            StudioAiChannelKind channel = ResolveEffectiveStudioChannel(out _);
            bool isDirectChannel = channel == StudioAiChannelKind.DirectApi;
            bool isAgentMcpChannel = channel == StudioAiChannelKind.AgentMcp;

            _activeAgentEntryTarget = channelSelector;

            if (isAgentMcpChannel)
            {
                string mcpSetupText = _loc.T("CustomWidget.Workshop.Composer.QuickSetupMcp");
                quickSetupApiButton.Content = compactToolbar && latestCardWidth < 460
                    ? new FontIcon { Glyph = "\uE943", FontSize = 12 }
                    : CreateSmallIconLabel("\uE943", mcpSetupText);
                quickSetupApiButton.Padding = compactToolbar && latestCardWidth < 460
                    ? new Thickness(8, 0, 8, 0)
                    : new Thickness(10, 0, 10, 0);
                quickSetupApiButton.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(quickSetupApiButton, _loc.T("CustomWidget.Workshop.Composer.QuickSetupMcpTooltip"));
                AutomationProperties.SetName(quickSetupApiButton, mcpSetupText);
            }
            else if (isDirectChannel)
            {
                bool hasDirectConfigured = DeskBoxAiService.Shared.HasAnyConfiguredProvider();
                string apiSetupText = _loc.T("CustomWidget.Workshop.Composer.QuickSetupApi");
                quickSetupApiButton.Content = CreateSmallIconLabel("\uE945", apiSetupText);
                quickSetupApiButton.Padding = new Thickness(10, 0, 10, 0);
                quickSetupApiButton.Visibility = hasDirectConfigured ? Visibility.Collapsed : Visibility.Visible;
                ToolTipService.SetToolTip(quickSetupApiButton, _loc.T("CustomWidget.Workshop.Composer.ConfigKey"));
                AutomationProperties.SetName(quickSetupApiButton, apiSetupText);
            }
            else
            {
                quickSetupApiButton.Visibility = Visibility.Collapsed;
            }

            if (!compactToolbar)
            {
                string currentSubmitLabel = isAgentMcpChannel
                    ? _loc.T("CustomWidget.Workshop.Composer.SubmitAgentMcp")
                    : submitLabel;
                string currentSubmitTip = string.IsNullOrWhiteSpace(shortcutHintText)
                    ? currentSubmitLabel
                    : $"{currentSubmitLabel} ({shortcutHintText})";
                submitButton.Content = CreateSmallIconLabel(isAgentMcpChannel ? "\uE8C8" : "\uE945", currentSubmitLabel);
                ToolTipService.SetToolTip(submitButton, currentSubmitTip);
                AutomationProperties.SetName(submitButton, currentSubmitLabel);
            }
            else
            {
                RefreshCompactSubmitChrome(box.Text ?? string.Empty);
            }

            if (isDirectChannel || (isAgentMcpChannel && !compactToolbar))
            {
                pasteButton.Visibility = Visibility.Collapsed;
                pasteIcon.Visibility = Visibility.Collapsed;
            }
            else if (compactToolbar || isAgentMcpChannel)
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
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };

        void RefreshLabel()
        {
            StudioAiChannelKind activeChannel = ResolveEffectiveStudioChannel(out string? directId);
            if (activeChannel == StudioAiChannelKind.AgentMcp)
            {
                string mcpText = _loc.T("CustomWidget.Workshop.Composer.MenuAgentMcp");
                button.Content = CreateSmallIconLabel("\uE756", mcpText);
                button.BorderBrush = ThemeBrush("AccentFillColorDefaultBrush");
                ToolTipService.SetToolTip(button, _loc.T("CustomWidget.Workshop.Composer.ChannelAgentMcpTooltip"));
                AutomationProperties.SetName(button, mcpText);
                onChannelChanged?.Invoke();
                return;
            }

            button.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (activeChannel == StudioAiChannelKind.DirectApi)
            {
                if (directId is not null)
                {
                    ResolvedAiInvocation resolved = CustomWidgetAiGeneratorService.Shared.ResolveEffectiveSettings(directId);
                    string shortModel = resolved.Model.Contains('/')
                        ? resolved.Model[(resolved.Model.LastIndexOf('/') + 1)..]
                        : resolved.Model;
                    shortModel = TruncateTextElements(shortModel, 24);

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
                }
                else
                {
                    string unconfigDirect = _loc.T("CustomWidget.Workshop.Composer.ChannelDirectUnconfigured");
                    button.Content = CreateSmallIconLabel("\uE945", unconfigDirect);
                    ToolTipService.SetToolTip(button, _loc.T("CustomWidget.Workshop.Composer.ConfigKey"));
                    AutomationProperties.SetName(button, unconfigDirect);
                }

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

        _refreshActiveChannelSelector = RefreshLabel;

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            StudioAiChannelKind activeChannel = ResolveEffectiveStudioChannel(out string? activeDirectId);

            if (activeChannel == StudioAiChannelKind.DirectApi)
            {
                IReadOnlyList<CustomWidgetAiProviderPreset> configured =
                    CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders();

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
                            _prefs.SetPreferredStudioChannel(CustomWidgetStudioPreferencesStore.ChannelDirect);
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
                }
                else
                {
                    var unconfigDirectItem = new ToggleMenuFlyoutItem
                    {
                        Text = _loc.T("CustomWidget.Workshop.Composer.ChannelDirectUnconfigured"),
                        IsChecked = true,
                        Icon = new FontIcon { Glyph = "\uE945" }
                    };
                    unconfigDirectItem.Click += async (_, _) => await ShowAiConfigDialogInStudioAsync(null, RefreshLabel);
                    flyout.Items.Add(unconfigDirectItem);
                }

                flyout.Items.Add(new MenuFlyoutSeparator());
                var keyItem = new MenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.Workshop.Composer.ConfigKey"),
                    Icon = new FontIcon { Glyph = "\uE8D7" }
                };
                keyItem.Click += async (_, _) => await ShowAiConfigDialogInStudioAsync(activeDirectId, RefreshLabel);
                flyout.Items.Add(keyItem);
            }
            else if (activeChannel == StudioAiChannelKind.AgentMcp)
            {
                var agentActiveItem = new ToggleMenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.Workshop.Composer.MenuAgentMcp"),
                    IsChecked = true,
                    Icon = new FontIcon { Glyph = "\uE756" }
                };
                flyout.Items.Add(agentActiveItem);
                flyout.Items.Add(new MenuFlyoutSeparator());

                var copySetupPromptItem = new MenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.AgentHub.Playbook.CopyCreate"),
                    Icon = new FontIcon { Glyph = "\uE8C8" }
                };
                copySetupPromptItem.Click += (_, _) =>
                {
                    SetClipboardText(CustomWidgetAgentConnectorService.BuildMcpSetupPrompt(_loc.CurrentCultureName));
                };
                flyout.Items.Add(copySetupPromptItem);

                var mcpConfigItem = new MenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.Workshop.Composer.ManageMcpClients"),
                    Icon = new FontIcon { Glyph = "\uE943" }
                };
                mcpConfigItem.Click += async (_, _) => await ShowAgentMcpQuickConnectDialogAsync(RefreshLabel);
                flyout.Items.Add(mcpConfigItem);
            }
            else
            {
                string effectiveWebId = _prefs.ResolveEffectiveWebAiTarget(_loc.CurrentCultureName);
                foreach (WebAiTargetPreset target in CustomWidgetStudioPreferencesStore.GetOrderedWebAiTargets(_loc.CurrentCultureName))
                {
                    WebAiTargetPreset captured = target;
                    bool isSelected = string.Equals(captured.Id, effectiveWebId, StringComparison.OrdinalIgnoreCase);
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
                        _preferWebAiInSession = false;
                        _prefs.SetPreferredStudioChannel(CustomWidgetStudioPreferencesStore.ChannelWeb);
                        _prefs.SetPreferredWebAiTarget(captured.Id);
                        RefreshLabel();
                    };
                    flyout.Items.Add(item);
                }
            }
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
            StudioAiChannelKind activeChannel = ResolveEffectiveStudioChannel(out _);

            AddMenuItem(flyout, "\uE8C8", "CustomWidget.Workshop.More.CopyPrompt",
                async () => await BeginGenerationAsync(readText(), copyOnly: true));
            if (activeChannel == StudioAiChannelKind.WebAi)
            {
                AddMenuItem(flyout, "\uE77F", "CustomWidget.Workshop.More.PasteReply",
                    async () => await PasteReplyManuallyAsync());
            }
            if (activeChannel == StudioAiChannelKind.AgentMcp)
            {
                AddMenuItem(flyout, "\uE756", "CustomWidget.Workshop.More.CopyAgentTask",
                    async () => await CopyAgentKickoffPromptAsync(readText()));
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            AddMenuItem(flyout, "\uE8A1", "CustomWidget.ApiDocs.Button",
                async () => await ShowApiDocsDialogInStudioAsync());
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
            string culture = _loc.CurrentCultureName;
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
                    Text = CustomWidgetCapabilityRegistry.GetCategoryDisplayName(cat, culture),
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
                        Text = StripTrailingCapabilitySuffix(spec.GetTitle(culture)),
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
                        Text = spec.GetSummary(culture),
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

        BindFlyoutToStudioTheme(flyout, root);
        searchBox.TextChanged += (_, _) => RenderItems();
        flyout.Opening += (_, _) =>
        {
            searchBox.Text = string.Empty;
            RenderItems();
        };

        flyout.Content = root;
        return flyout;
    }

    private FrameworkElement CreateTemplateCard(WorkshopCardSpec spec, out Action hydratePreviewAction)
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
            IsHitTestVisible = false,
            Child = CreateCardPreviewSkeleton()
        };

        Viewbox? previewViewbox = null;
        hydratePreviewAction = () =>
        {
            if (_isClosed || _page != StudioPage.Start)
            {
                return;
            }

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
                vm.ApplyPreviewBundle(spec.ManifestJson, spec.LogicJs, spec.Locales, preserveRuntimeData: false);
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
                    Opacity = 0,
                    OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) },
                    Child = thumbFrame
                };
                previewWell.Child = previewViewbox;
                previewViewbox.Opacity = 1;
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
        };

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

        var rightMetaCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (!spec.RequiresClientUpgrade && (spec.IsRemoteOnly || spec.IsRemoteUpgraded))
        {
            string badgeLabel = _loc.T(spec.IsRemoteOnly
                ? "CustomWidget.Workshop.Card.BadgeNew"
                : "CustomWidget.Workshop.Card.BadgeUpdated");
            rightMetaCluster.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                Child = new TextBlock
                {
                    Text = badgeLabel,
                    FontSize = 10.5,
                    Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush")
                }
            });
        }

        rightMetaCluster.Children.Add(new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 11,
            Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(rightMetaCluster, 1);
        titleRow.Children.Add(rightMetaCluster);
        textStack.Children.Add(titleRow);

        string cardBodyText = spec.RequiresClientUpgrade
            ? _loc.Format("CustomWidget.ClientUpgrade.CardHint", spec.UpgradeRequirementLabel)
            : description;
        textStack.Children.Add(new TextBlock
        {
            Text = cardBodyText,
            Style = TextStyle("CaptionTextBlockStyle"),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            MinHeight = 34
        });

        string cleanSpecVer = TrimLeadingVersionPrefix(spec.Version);
        string sourceLabel = _loc.T(spec.IsRemoteOnly
            ? "CustomWidget.Workshop.Card.SourceRemoteNew"
            : spec.IsRemoteUpgraded
                ? "CustomWidget.Workshop.Card.SourceRemoteUpdated"
                : "CustomWidget.Workshop.Card.SourceBuiltIn");
        string tooltipHeader = $"{label} · v{cleanSpecVer} · {sourceLabel}";
        ToolTipService.SetToolTip(
            card,
            string.IsNullOrWhiteSpace(cardBodyText) ? tooltipHeader : $"{tooltipHeader}\n{cardBodyText}");

        Grid.SetRow(textStack, 1);
        cardGrid.Children.Add(textStack);
        card.Content = cardGrid;

        card.Click += (_, _) =>
        {
            if (spec.RequiresClientUpgrade || string.IsNullOrWhiteSpace(spec.ManifestJson))
            {
                return;
            }

            _selectedEmoji = CustomWidgetTemplates.MatchPresetOrNull(spec.Id) is not null
                ? null
                : spec.Emoji;
            _pendingPrompt = string.Empty;
            AddRevision(
                spec.ManifestJson,
                _loc.Format("CustomWidget.Workshop.History.FromTemplate", label),
                isAiChange: false,
                source: "template",
                isCommitted: false,
                markDirty: false,
                logicJs: spec.LogicJs,
                hasExplicitLogicJs: true,
                locales: spec.Locales,
                hasExplicitLocales: true);
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
                await PlaceTemplateDirectlyAsync(spec.ManifestJson, spec.Emoji, quickPlaceBtn, spec.Locales, spec.LogicJs);
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

    private FrameworkElement CreateExistingWidgetCard(ExistingWidgetCardItem item, out Action hydratePreviewAction)
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
            IsHitTestVisible = false,
            Child = CreateCardPreviewSkeleton()
        };

        Viewbox? previewViewbox = null;
        hydratePreviewAction = () =>
        {
            if (_isClosed || _page != StudioPage.Start)
            {
                return;
            }

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
                vm.ApplyPreviewBundle(item.ManifestJson, item.LogicJs, item.Locales, preserveRuntimeData: false);
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
                    Opacity = 0,
                    OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) },
                    Child = thumbFrame
                };
                previewWell.Child = previewViewbox;
                previewViewbox.Opacity = 1;
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
        };

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

        string cleanManifestVer = TrimLeadingVersionPrefix(item.ManifestVersion);
        string myCardHeader = item.LastUpdatedUtcTicks > 0
            ? _loc.Format(
                "CustomWidget.Workshop.MyWidgets.CardTooltipMeta",
                item.Name,
                cleanManifestVer,
                FormatGalleryRelativeTimestamp(new DateTimeOffset(item.LastUpdatedUtcTicks, TimeSpan.Zero)))
            : $"{item.Name} · v{cleanManifestVer}";
        ToolTipService.SetToolTip(
            card,
            string.IsNullOrWhiteSpace(item.Summary) ? myCardHeader : $"{myCardHeader}\n{item.Summary}");

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

        string forkTip = _loc.T("CustomWidget.Action.ForkInStudio");
        var quickForkBtn = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 10, 48, 0),
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
                Glyph = "\uE8C8",
                FontSize = 12,
                Foreground = ThemeBrush("TextFillColorPrimaryBrush")
            }
        };
        Canvas.SetZIndex(quickForkBtn, 2);
        ToolTipService.SetToolTip(quickForkBtn, forkTip);
        AutomationProperties.SetName(quickForkBtn, $"{item.Name} — {forkTip}");
        quickForkBtn.Click += (_, _) =>
        {
            InitializeForkFromWidget(item.Id, initialIdea: null);
            DispatcherQueue.TryEnqueue(() => _refineTextBox?.Focus(FocusState.Programmatic));
        };
        container.Children.Add(quickForkBtn);

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
                              quickForkBtn.FocusState != FocusState.Unfocused ||
                              quickLocateBtn.FocusState != FocusState.Unfocused;
            quickForkBtn.Opacity = showAction ? 1 : 0;
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
        quickForkBtn.GotFocus += (_, _) => RefreshHoverVisuals();
        quickForkBtn.LostFocus += (_, _) => RefreshHoverVisuals();
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

            ElementTheme desktopWidgetTheme = StudioExplicitTheme;
            ApplyStageWidgetTheme(desktopWidgetTheme);
            RefreshStageToolbarChrome();
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
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x16, 0x18, 0x1F))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE6, 0xEA, 0xF1));
        ApplyStageWidgetTheme(targetTheme);
        RefreshStageToolbarChrome();
    }

    private void ApplyStageWidgetTheme(ElementTheme widgetTheme)
    {
        ElementTheme effective = widgetTheme == ElementTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark;

        if (_widgetFrame is not null)
        {
            ApplyWidgetFrameTheme(
                _widgetFrame,
                _previewContent,
                _frameTitleText,
                _frameEditGlyph,
                _frameDivider,
                effective);
        }

        if (_blueprintFrame is not null)
        {
            ApplyWidgetFrameTheme(
                _blueprintFrame,
                content: null,
                _blueprintTitleText,
                editGlyph: null,
                _blueprintDivider,
                effective);
            foreach ((Border block, bool useSecondaryWash) in _blueprintSkeletonBlocks)
            {
                block.Background = StageThemeBrush(
                    useSecondaryWash ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush",
                    effective);
                if (block.BorderThickness.Top > 0)
                {
                    block.BorderBrush = StageThemeBrush("DividerStrokeColorDefaultBrush", effective);
                }
            }
        }

        ApplyCapsulePreviewCardTheme(effective);
    }

    private void ApplyWidgetFrameTheme(
        Border frame,
        CustomWidgetContent? content,
        TextBlock? titleText,
        FontIcon? editGlyph,
        Border? divider,
        ElementTheme widgetTheme)
    {
        ElementTheme effective = widgetTheme == ElementTheme.Default
            ? StudioExplicitTheme
            : widgetTheme;
        bool isLight = effective == ElementTheme.Light;

        frame.RequestedTheme = effective;
        if (isLight)
        {
            frame.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xF7, 0xFB));
            frame.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1E, 0x00, 0x00, 0x00));
            frame.Resources["WidgetLayerFillSecondaryBrush"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0xDF, 0xFF, 0xFF, 0xFF));
            frame.Resources["WidgetLayerStrokeBrush"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0x16, 0x00, 0x00, 0x00));
        }
        else
        {
            frame.Background = StageThemeBrush("SolidBackgroundFillColorTertiaryBrush", ElementTheme.Dark);
            frame.BorderBrush = StageThemeBrush("CardStrokeColorDefaultBrush", ElementTheme.Dark);
            frame.Resources.Remove("WidgetLayerFillSecondaryBrush");
            frame.Resources.Remove("WidgetLayerStrokeBrush");
        }

        if (titleText is not null)
        {
            titleText.Foreground = StageThemeBrush("TextFillColorPrimaryBrush", effective);
        }

        if (editGlyph is not null)
        {
            editGlyph.Foreground = StageThemeBrush("TextFillColorTertiaryBrush", effective);
        }

        if (divider is not null)
        {
            divider.Background = StageThemeBrush("DividerStrokeColorDefaultBrush", effective);
        }

        if (content is not null)
        {
            bool themeChanged = content.RequestedTheme != effective;
            content.RequestedTheme = effective;
            if (themeChanged)
            {
                if (content.IsLoaded)
                {
                    content.RebuildNativeTree();
                }
                else
                {
                    content.RequestCoalescedRebuild();
                }
            }
        }
    }

    private void RefreshStageToolbarChrome()
    {
        ElementTheme stageTheme = _stageBorder?.RequestedTheme == ElementTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark;
        bool isLightStage = stageTheme == ElementTheme.Light;

        Brush pillBg = new SolidColorBrush(
            isLightStage
                ? Windows.UI.Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Brush pillBorder = new SolidColorBrush(
            isLightStage
                ? Windows.UI.Color.FromArgb(0x1C, 0x00, 0x00, 0x00)
                : Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        Brush textPrimary = StageThemeBrush("TextFillColorPrimaryBrush", stageTheme);
        Brush textSecondary = StageThemeBrush("TextFillColorSecondaryBrush", stageTheme);

        if (_pickElementButton is not null)
        {
            _pickElementButton.IsEnabled = !_isCapsulePreviewMode && !string.IsNullOrWhiteSpace(CurrentManifestJson);
            _pickElementButton.RequestedTheme = stageTheme;
            if (_isInspectorModeEnabled && _pickElementButton.IsEnabled)
            {
                Brush accentStroke = StageThemeBrush("AccentFillColorDefaultBrush", stageTheme);
                Brush accentText = StageThemeBrush("AccentTextFillColorPrimaryBrush", stageTheme);
                Windows.UI.Color accentColor = accentStroke is SolidColorBrush scb
                    ? scb.Color
                    : (isLightStage
                        ? Windows.UI.Color.FromArgb(255, 37, 99, 235)
                        : Windows.UI.Color.FromArgb(255, 96, 165, 250));
                _pickElementButton.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb((byte)(isLightStage ? 30 : 44), accentColor.R, accentColor.G, accentColor.B));
                _pickElementButton.BorderBrush = accentStroke;
                _pickElementButton.BorderThickness = new Thickness(1);
                if (_pickElementIcon is not null)
                {
                    _pickElementIcon.Foreground = accentText;
                }
                if (_pickElementLabel is not null)
                {
                    _pickElementLabel.Foreground = accentText;
                    _pickElementLabel.FontWeight = FontWeights.SemiBold;
                }
            }
            else
            {
                _pickElementButton.Background = pillBg;
                _pickElementButton.BorderBrush = pillBorder;
                _pickElementButton.BorderThickness = new Thickness(1);
                if (_pickElementIcon is not null)
                {
                    _pickElementIcon.Foreground = textSecondary;
                }
                if (_pickElementLabel is not null)
                {
                    _pickElementLabel.Foreground = textPrimary;
                    _pickElementLabel.FontWeight = FontWeights.Normal;
                }
            }
        }

        if (_sizeQuickButton is not null)
        {
            _sizeQuickButton.RequestedTheme = stageTheme;
            _sizeQuickButton.Background = pillBg;
            _sizeQuickButton.BorderBrush = pillBorder;
            _sizeQuickButton.BorderThickness = new Thickness(1);
            _sizeQuickButton.Foreground = textPrimary;
        }

        if (_sampleDataToggleButton is not null)
        {
            _sampleDataToggleButton.RequestedTheme = stageTheme;
            _sampleDataToggleButton.Background = pillBg;
            _sampleDataToggleButton.BorderBrush = pillBorder;
            _sampleDataToggleButton.BorderThickness = new Thickness(1);
            _sampleDataToggleButton.Foreground = textPrimary;
        }

        if (_settingsPreviewButton is not null)
        {
            _settingsPreviewButton.RequestedTheme = stageTheme;
            _settingsPreviewButton.Background = pillBg;
            _settingsPreviewButton.BorderBrush = pillBorder;
            _settingsPreviewButton.BorderThickness = new Thickness(1);
            _settingsPreviewButton.Foreground = textPrimary;
        }

        if (_previewCultureOverridePillButton is not null)
        {
            bool showOverride = !string.IsNullOrWhiteSpace(_activePreviewCultureOverride) &&
                                !string.IsNullOrWhiteSpace(CurrentManifestJson);
            if (!showOverride)
            {
                _previewCultureOverridePillButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                CustomWidgetSupportedLocaleInfo info =
                    CustomWidgetLocaleCatalog.GetLocaleInfo(_activePreviewCultureOverride!);
                string labelText = $"{info.NativeName} ({info.Code})  ✕";
                string resetTip = _loc.Format("CustomWidget.Workshop.I18n.ResetPreviewCultureTip", info.NativeName);
                Brush accentStroke = StageThemeBrush("AccentFillColorDefaultBrush", stageTheme);
                Brush accentText = StageThemeBrush("AccentTextFillColorPrimaryBrush", stageTheme);
                Windows.UI.Color accentColor = accentStroke is SolidColorBrush scb
                    ? scb.Color
                    : (isLightStage
                        ? Windows.UI.Color.FromArgb(255, 37, 99, 235)
                        : Windows.UI.Color.FromArgb(255, 96, 165, 250));

                _previewCultureOverridePillButton.RequestedTheme = stageTheme;
                _previewCultureOverridePillButton.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb((byte)(isLightStage ? 28 : 42), accentColor.R, accentColor.G, accentColor.B));
                _previewCultureOverridePillButton.BorderBrush = accentStroke;
                _previewCultureOverridePillButton.BorderThickness = new Thickness(1);
                _previewCultureOverridePillButton.Foreground = accentText;
                _previewCultureOverridePillButton.Content = CreateSmallIconLabel("\uF2B7", labelText);
                ToolTipService.SetToolTip(_previewCultureOverridePillButton, resetTip);
                AutomationProperties.SetName(_previewCultureOverridePillButton, resetTip);
                _previewCultureOverridePillButton.Visibility = Visibility.Visible;
            }
        }

        if (_stageViewportModeSegmented is not null)
        {
            _stageViewportModeSegmented.RequestedTheme = stageTheme;
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(_stageViewportModeSegmented);
        }

        if (_stageBackdropSegmented is not null)
        {
            _stageBackdropSegmented.RequestedTheme = stageTheme;
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(_stageBackdropSegmented);
        }

        if (_stageGeneratingStatusPill is not null && _stageGeneratingStatusText is not null)
        {
            _stageGeneratingStatusPill.RequestedTheme = stageTheme;
            _stageGeneratingStatusPill.Background = new SolidColorBrush(
                isLightStage
                    ? Windows.UI.Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF)
                    : Windows.UI.Color.FromArgb(0xE8, 0x1E, 0x22, 0x2B));
            _stageGeneratingStatusPill.BorderBrush = pillBorder;
            _stageGeneratingStatusText.Foreground = textSecondary;
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

        ElementTheme initialWidgetTheme = _stageBackdropMode switch
        {
            StageBackdropMode.Light => ElementTheme.Light,
            StageBackdropMode.Dark => ElementTheme.Dark,
            _ => StudioExplicitTheme
        };

        var frame = new Border
        {
            Width = 320,
            MinHeight = 228,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        _blueprintFrame = frame;
        _blueprintSkeletonBlocks.Clear();

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
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
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

        var divider = new Border();
        _blueprintDivider = divider;
        Grid.SetRow(divider, 1);
        grid.Children.Add(divider);

        var bodyStack = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(16, 18, 16, 18),
            VerticalAlignment = VerticalAlignment.Center
        };
        var skelTop = new Border
        {
            Height = 16,
            Width = 176,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(5)
        };
        _blueprintSkeletonBlocks.Add((skelTop, true));
        bodyStack.Children.Add(skelTop);

        var skelMid = new Border
        {
            Height = 46,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1)
        };
        _blueprintSkeletonBlocks.Add((skelMid, false));
        bodyStack.Children.Add(skelMid);

        var bottomRow = new Grid { ColumnSpacing = 8 };
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var leftBlock = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(6)
        };
        _blueprintSkeletonBlocks.Add((leftBlock, true));
        bottomRow.Children.Add(leftBlock);
        var rightBlock = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(6)
        };
        _blueprintSkeletonBlocks.Add((rightBlock, false));
        Grid.SetColumn(rightBlock, 1);
        bottomRow.Children.Add(rightBlock);
        bodyStack.Children.Add(bottomRow);

        Grid.SetRow(bodyStack, 2);
        grid.Children.Add(bodyStack);
        frame.Child = grid;

        ApplyWidgetFrameTheme(frame, null, _blueprintTitleText, null, divider, initialWidgetTheme);
        foreach ((Border block, bool useSecondaryWash) in _blueprintSkeletonBlocks)
        {
            block.Background = StageThemeBrush(
                useSecondaryWash ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush",
                initialWidgetTheme);
            if (block.BorderThickness.Top > 0)
            {
                block.BorderBrush = StageThemeBrush("DividerStrokeColorDefaultBrush", initialWidgetTheme);
            }
        }

        return frame;
    }

    private void RefreshInPlaceGenerationChrome(bool refreshStream = true)
    {
        if (_stageGeneratingProgressBar is not null)
        {
            _stageGeneratingProgressBar.IsIndeterminate = _isInPlaceGenerating;
            if (_stageGeneratingProgressRing is not null)
            {
                _stageGeneratingProgressRing.IsActive = _isInPlaceGenerating;
            }
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
                bool isAgentMcp = ResolveEffectiveStudioChannel(out _) == StudioAiChannelKind.AgentMcp;
                string sendLabel = isAgentMcp
                    ? _loc.T("CustomWidget.Workshop.Composer.SubmitAgentMcp")
                    : _loc.T("CustomWidget.Workshop.Refine.Button");
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

                _refineSubmitButton.Content = new FontIcon { Glyph = isAgentMcp ? "\uE8C8" : "\uE74A", FontSize = 13 };
                ToolTipService.SetToolTip(_refineSubmitButton, string.IsNullOrWhiteSpace(hint) ? sendLabel : $"{sendLabel} ({hint})");
                AutomationProperties.SetName(_refineSubmitButton, sendLabel);
            }
        }

        if (_refineTextBox is not null)
        {
            _refineTextBox.IsEnabled = !((_isInPlaceGenerating && _waitingKind == WaitingKind.Direct) || _isI18nQueueRunning);
        }

        if (_commitButton is not null)
        {
            _commitButton.IsEnabled = !IsAnyStudioJobRunning && !string.IsNullOrWhiteSpace(CurrentManifestJson);
        }

        if (_undoButton is not null)
        {
            _undoButton.IsEnabled = !IsAnyStudioJobRunning && _revisionIndex > 0;
        }

        if (_redoButton is not null)
        {
            _redoButton.IsEnabled = !IsAnyStudioJobRunning && _revisionIndex < _revisions.Count - 1;
        }

        RefreshTargetNodePillChrome();
        RefreshScriptErrorBannerChrome();
        RefreshLocaleCenterChrome();
        if (_suggestionChipsPanel is not null)
        {
            _suggestionChipsPanel.Visibility = _suggestionChipsPanel.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            _suggestionChipsPanel.IsHitTestVisible = !IsAnyStudioJobRunning;
            _suggestionChipsPanel.Opacity = IsAnyStudioJobRunning ? 0.45 : 1.0;
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

    private void RefreshSettingsPreviewBadgeChrome()
    {
        if (_settingsPreviewButton is null)
        {
            return;
        }

        int settingsCount = _previewVm?.Manifest.Settings.Count ?? 0;
        if (settingsCount <= 0 || string.IsNullOrWhiteSpace(CurrentManifestJson))
        {
            _settingsPreviewButton.Visibility = Visibility.Collapsed;
            return;
        }

        string label = _loc.Format("CustomWidget.Workshop.Stage.SettingsBadgeFormat", settingsCount);
        string tip = _loc.T("CustomWidget.Workshop.Stage.SettingsTooltip");
        _settingsPreviewButton.Content = CreateSmallIconLabel("\uE713", label);
        ToolTipService.SetToolTip(_settingsPreviewButton, tip);
        AutomationProperties.SetName(_settingsPreviewButton, label);
        _settingsPreviewButton.Visibility = Visibility.Visible;
    }

    private void ReapplySessionSettingOverridesToPreviewVm()
    {
        if (_previewVm is null || (_sessionSettingOverrides.Count == 0 && _sessionSecretOverrides.Count == 0))
        {
            return;
        }

        var declaredSettings = _previewVm.Manifest.Settings;
        if (declaredSettings.Count == 0)
        {
            return;
        }

        foreach (CustomWidgetSettingDefinition def in declaredSettings)
        {
            if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                if (_sessionSecretOverrides.TryGetValue(def.Key, out string? secretVal))
                {
                    _ = _previewVm.SetSecretValueAsync(def.Key, secretVal);
                }
            }
            else if (_sessionSettingOverrides.TryGetValue(def.Key, out string? settingVal))
            {
                _ = _previewVm.SetSettingValueAsync(def.Key, settingVal);
            }
        }
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
            _previewVm.ApplyPreviewBundle(curJson, CurrentLogicJs, CurrentLocales, preserveRuntimeData: false);
            _previewVm.SetPreviewCultureOverride(_activePreviewCultureOverride);
        }
        else
        {
            _previewVm.ApplyPreviewBundle(curJson, CurrentLogicJs, CurrentLocales, preserveRuntimeData: false);
            _previewVm.SetPreviewCultureOverride(_activePreviewCultureOverride);
            if (!string.IsNullOrWhiteSpace(_previewSampleDataJson))
            {
                _previewVm.ApplyPreviewSampleDataJson(_previewSampleDataJson);
            }
        }

        ReapplySessionSettingOverridesToPreviewVm();
        SyncRuntimeScriptErrorFromPreviewVm();
        RefreshCapsulePreviewCardContent();
        RefreshSampleDataBadgeChrome();
    }

    private CustomWidgetManifest? ResolvePreviewManifestWithSidecar(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        CustomWidgetManifest? manifest = CustomWidgetManifestParser.Parse(json!).Manifest;
        CustomWidgetLocaleCatalog? locales = _activeI18nWorkingCatalog ?? CurrentLocales;
        if (manifest is not null && locales is { HasAnyLocale: true })
        {
            manifest = manifest.WithLocales(locales);
        }

        return manifest;
    }

    private void ApplyLivePreviewUpdateInPlace()
    {
        string? json = CurrentManifestJson;
        if (string.IsNullOrWhiteSpace(json) || _previewVm is null || _previewContent is null)
        {
            ShowPreviewPage(justLoaded: true);
            return;
        }

        _previewVm.ApplyPreviewBundle(json, CurrentLogicJs, CurrentLocales, preserveRuntimeData: true);
        _previewVm.SetPreviewCultureOverride(_activePreviewCultureOverride);
        SyncRuntimeScriptErrorFromPreviewVm();
        ReapplySessionSettingOverridesToPreviewVm();
        if (!IsEditMode && !string.IsNullOrWhiteSpace(_previewSampleDataJson) && !_isSampleDataMutedInPreview)
        {
            _previewVm.ApplyPreviewSampleDataJson(_previewSampleDataJson);
        }
        else if (_isSampleDataMutedInPreview)
        {
            _previewVm.ClearPreviewSampleData();
        }

        CustomWidgetManifest? effectiveManifest = ResolvePreviewManifestWithSidecar(json);
        CustomWidgetSizeSpec nextSize = effectiveManifest?.DefaultSize ?? new CustomWidgetSizeSpec();
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

        string nextTitle = effectiveManifest?.ResolveLocalizedName(EffectivePreviewCultureName) ?? _loc.T("CustomWidget.Title");
        string nextEmoji = ResolveDisplayEmoji(effectiveManifest);
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
        RefreshCapsulePreviewCardContent();
        RefreshStageViewportModeVisuals();
        RefreshTitleBarContextBadge();
        RefreshSampleDataBadgeChrome();
        RefreshSettingsPreviewBadgeChrome();
        RefreshRevisionChrome();
        RefreshInPlaceGenerationChrome(refreshStream: false);
        RefreshTargetNodePillChrome();
        RefreshScriptErrorBannerChrome();
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

    /// <summary>
    /// P2：首屏「来自外部工具的待确认更改」提示条。
    /// 批次来自 App.GetPendingAgentStages()；「查看预览」只读取暂存内容，
    /// 「忽略」直接丢弃批次，两者都不会改动桌面。
    /// </summary>
    private InfoBar? CreateAgentStageBannerIfNeeded()
    {
        var bar = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            IsOpen = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var viewBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12.5,
            Content = _loc.T("CustomWidget.Workshop.AgentStage.ViewButton")
        };
        var dismissBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12.5,
            Content = _loc.T("CustomWidget.Workshop.AgentStage.DiscardButton")
        };

        string? currentStageId = null;

        void Refresh()
        {
            var pending = App.Current.GetPendingAgentStages();
            currentStageId = pending.Count > 0 ? pending[0].StageId : null;
            bar.Visibility = currentStageId is null ? Visibility.Collapsed : Visibility.Visible;
            if (currentStageId is not null)
            {
                bar.Message = _loc.Format("CustomWidget.Workshop.AgentStage.Notice", pending.Count);
            }

            viewBtn.IsEnabled = currentStageId is not null;
            dismissBtn.IsEnabled = currentStageId is not null;
        }

        viewBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(currentStageId))
            {
                OpenAgentStagePreview(currentStageId);
            }
        };

        dismissBtn.Click += async (_, _) =>
        {
            string? id = currentStageId;
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            dismissBtn.IsEnabled = false;
            try
            {
                await App.Current.DiscardAgentStageAsync(id);
            }
            finally
            {
                dismissBtn.IsEnabled = true;
                Refresh();
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 2)
        };
        actions.Children.Add(viewBtn);
        actions.Children.Add(dismissBtn);
        bar.Content = actions;

        _refreshAgentStageBanner = Refresh;
        Refresh();
        return bar;
    }

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

    private static string TrimLeadingVersionPrefix(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return "1.0.0";
        }

        string trimmed = rawVersion.Trim();
        if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V') && char.IsDigit(trimmed[1]))
        {
            return trimmed[1..];
        }

        return trimmed;
    }

    private string FormatGalleryRelativeTimestamp(DateTimeOffset timestampUtc)
    {
        DateTimeOffset local = timestampUtc.ToLocalTime();
        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan elapsed = now - local;

        if (elapsed >= TimeSpan.Zero)
        {
            if (elapsed.TotalSeconds < 60)
            {
                return _loc.T("QuickCapture.JustNow");
            }

            if (elapsed.TotalMinutes < 60)
            {
                int minutes = Math.Max(1, (int)Math.Floor(elapsed.TotalMinutes));
                return _loc.Format("QuickCapture.MinutesAgoFormat", minutes);
            }
        }

        if (local.Date == now.Date)
        {
            return local.ToString("HH:mm");
        }

        if (local.Year == now.Year)
        {
            return local.ToString("MM-dd HH:mm");
        }

        return local.ToString("yyyy-MM-dd HH:mm");
    }

    private void ShowPreviewPage(bool justLoaded)
    {
        _refreshAgentStageBanner = null;
        _refreshTemplateGalleryFromRemote = null;
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
        CustomWidgetManifest? effectiveManifest = hasLiveManifest ? ResolvePreviewManifestWithSidecar(json) : null;
        CustomWidgetSizeSpec size = effectiveManifest?.DefaultSize ?? new CustomWidgetSizeSpec();
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
                _previewVm.ScriptErrorChanged += OnPreviewVmScriptErrorChanged;
                _previewVm.MetricsRefreshed += OnPreviewVmMetricsRefreshed;
                _previewVm.PropertyChanged += OnPreviewVmPropertyChanged;
            }

            _previewVm.IsUntrustedStagePreview = _isExternalImportSession || !string.IsNullOrWhiteSpace(_pendingAgentStageId);
            _previewVm.ApplyPreviewBundle(json!, CurrentLogicJs, CurrentLocales, preserveRuntimeData: true);
            _previewVm.SetPreviewCultureOverride(_activePreviewCultureOverride);
            SyncRuntimeScriptErrorFromPreviewVm();
            ReapplySessionSettingOverridesToPreviewVm();
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
                IsNodeSelectionEnabled = !_isCapsulePreviewMode && _isInspectorModeEnabled,
                SelectedNodePath = _isCapsulePreviewMode ? null : _selectedTargetNode?.NodePath
            };
            _previewContent.NodePicked += OnPreviewNodePicked;
            _previewContent.Loaded += OnPreviewContentLoadedForHealth;
        }

        var root = new Grid
        {
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
            if (_isI18nQueueRunning)
            {
                CancelI18nQueue();
            }

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
        _frameDivider = null;
        _frameEditGlyph = null;
        _blueprintTitleText = null;
        _blueprintFrame = null;
        _blueprintDivider = null;
        _blueprintSkeletonBlocks.Clear();
        _pickElementButton = null;
        _pickElementIcon = null;
        _pickElementLabel = null;
        _settingsPreviewButton = null;
        _previewCultureOverridePillButton = null;
        _i18nHeaderButton = null;
        _i18nHeaderIcon = null;
        _i18nHeaderLabel = null;
        _localeCenterHostBorder = null;
        _localeCenterSummaryText = null;
        _localeCenterHintText = null;
        _localeCenterOverallProgressBar = null;
        _localeCenterSyncAllButton = null;
        _localeCenterSync2Button = null;
        _localeCenterRetryFailedButton = null;
        _localeCenterStopButton = null;
        _localeCenterCloseButton = null;
        _localeCenterCardRefs.Clear();
        _stageViewportModeSegmented = null;
        _stageBackdropSegmented = null;
        _capsulePreviewFrame = null;
        _capsuleEmojiText = null;
        _capsuleGlyphIcon = null;
        _capsuleTitleText = null;
        _capsuleSummaryText = null;
        _capsuleBadgeBorder = null;
        _capsuleBadgeText = null;
        _capsulePrimaryActionButton = null;
        _capsulePrimaryActionIcon = null;
        _capsuleProgressBar = null;
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
            Text = ResolveDisplayEmoji(effectiveManifest),
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
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

        _i18nHeaderIcon = new FontIcon
        {
            Glyph = "\uF2B7",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        _i18nHeaderLabel = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.I18n.HeaderButton"),
            FontSize = 12,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var i18nHeaderRow = new Grid { ColumnSpacing = 6 };
        i18nHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        i18nHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        i18nHeaderRow.Children.Add(_i18nHeaderIcon);
        Grid.SetColumn(_i18nHeaderLabel, 1);
        i18nHeaderRow.Children.Add(_i18nHeaderLabel);

        _i18nHeaderButton = new Button
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            IsEnabled = hasLiveManifest,
            Content = i18nHeaderRow
        };
        _i18nHeaderButton.Click += (_, _) => OpenLocaleCenter(toggleIfAlreadyOpen: true);
        rightActions.Children.Add(_i18nHeaderButton);

        var previewSwitchModeHeaderBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Content = CreateSmallIconLabel("\uE8AB", _loc.T("CustomWidget.Workshop.SwitchModeButton"))
        };
        ToolTipService.SetToolTip(previewSwitchModeHeaderBtn, _loc.T("CustomWidget.Workshop.Onboarding.Title"));
        AutomationProperties.SetName(previewSwitchModeHeaderBtn, _loc.T("CustomWidget.Workshop.SwitchModeButton"));
        previewSwitchModeHeaderBtn.Click += (_, _) => ShowAgentMcpOnboardingTour(forceShow: true);
        rightActions.Children.Add(previewSwitchModeHeaderBtn);

        var previewGuideHeaderBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Content = CreateSmallIconLabel("\uE897", _loc.T("CustomWidget.Workshop.GuideButton"))
        };
        ToolTipService.SetToolTip(previewGuideHeaderBtn, _loc.T("CustomWidget.Workshop.GuideButton"));
        AutomationProperties.SetName(previewGuideHeaderBtn, _loc.T("CustomWidget.Workshop.GuideButton"));
        previewGuideHeaderBtn.Click += (_, _) => StartChannelStepTour(ResolveEffectiveStudioChannel(out _));
        rightActions.Children.Add(previewGuideHeaderBtn);

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
            bool hasCurrentCode = !string.IsNullOrWhiteSpace(CurrentManifestJson);
            if (hasCurrentCode)
            {
                AddMenuItem(
                    sessionMoreFlyout,
                    "\uE943",
                    "CustomWidget.Workshop.More.ViewCode",
                    ShowWorkspaceCodeViewerDialogAsync);

                var i18nSub = new MenuFlyoutSubItem
                {
                    Text = _loc.T("CustomWidget.Workshop.More.I18nMenu"),
                    Icon = new FontIcon { Glyph = "\uF2B7" }
                };
                var openCenterItem = new MenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.Workshop.I18n.OpenCenterMenu"),
                    Icon = new FontIcon { Glyph = "\uE8EC" }
                };
                openCenterItem.Click += (_, _) => OpenLocaleCenter(toggleIfAlreadyOpen: false);
                i18nSub.Items.Add(openCenterItem);
                i18nSub.Items.Add(new MenuFlyoutSeparator());

                var sync13Item = new MenuFlyoutItem
                {
                    Text = _loc.T("CustomWidget.Workshop.More.I18nSync13"),
                    Icon = new FontIcon { Glyph = "\uE774" },
                    IsEnabled = !IsAnyStudioJobRunning
                };
                sync13Item.Click += async (_, _) => await RunIncrementalI18nBatchAsync(null);
                i18nSub.Items.Add(sync13Item);

                var sync2Item = new MenuFlyoutItem
                {
                    Text = FormatBilingualSyncLabel(shortLabel: false),
                    Icon = new FontIcon { Glyph = "\uE8C1" },
                    IsEnabled = !IsAnyStudioJobRunning
                };
                sync2Item.Click += async (_, _) =>
                    await RunIncrementalI18nBatchAsync(ResolveActiveBilingualCultureCodes());
                i18nSub.Items.Add(sync2Item);

                sessionMoreFlyout.Items.Add(i18nSub);
                sessionMoreFlyout.Items.Add(new MenuFlyoutSeparator());
            }

            AddMenuItem(
                sessionMoreFlyout,
                "\uE8A5",
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
            IsEnabled = hasLiveManifest && !IsAnyStudioJobRunning,
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
            MaxWidth = 840,
            Width = new GridLength(initialStageWidth)
        };
        body.ColumnDefinitions.Add(_stageColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
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
                double maxStage = Math.Clamp(Math.Min(w - 400, w * 0.48), 300, 840);
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
        stageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _stageWallpaperLayer = new Border
        {
            CornerRadius = new CornerRadius(7),
            Opacity = 0.88,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(_stageWallpaperLayer, 4);
        stageGrid.Children.Add(_stageWallpaperLayer);

        _stageWallpaperScrim = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x76, 0x0D, 0x10, 0x18)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRowSpan(_stageWallpaperScrim, 4);
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
            ? (effectiveManifest?.ResolveLocalizedName(EffectivePreviewCultureName) ?? _loc.T("CustomWidget.Title"))
            : (_isInPlaceGenerating
                ? _loc.T("CustomWidget.Workshop.Stage.BlueprintPreparing")
                : _loc.T("CustomWidget.Title"));

        UIElement stageCenterChild;
        if (hasLiveManifest && _previewContent is not null)
        {
            _widgetFrame = CreateWidgetFrame(
                ResolveDisplayEmoji(effectiveManifest),
                resolvedManifestTitle,
                _previewContent,
                size,
                clipViewport: false,
                out _frameEmojiText,
                out _frameTitleText);
            FluentMotionHelper.AttachSmoothBoundsImplicitAnimations(_widgetFrame);
            _capsulePreviewFrame = BuildInteractiveCapsulePreviewCard();

            var viewportHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            viewportHost.Children.Add(_widgetFrame);
            viewportHost.Children.Add(_capsulePreviewFrame);
            stageCenterChild = viewportHost;
        }
        else
        {
            _widgetFrame = null;
            _capsulePreviewFrame = null;
            stageCenterChild = BuildBlueprintStagePlaceholder();
        }

        ToolkitControls.Segmented viewportModeSegmented = BuildStageViewportModeSegmented(hasLiveManifest);
        viewportModeSegmented.Margin = new Thickness(12, 4, 12, 4);
        Grid.SetRow(viewportModeSegmented, 1);
        stageGrid.Children.Add(viewportModeSegmented);

        var stageViewbox = new Viewbox
        {
            Margin = new Thickness(18, 6, 18, 12),
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stageCenterChild
        };
        Grid.SetRow(stageViewbox, 2);
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
        _stageGeneratingProgressRing = new ProgressRing
        {
            IsActive = _isInPlaceGenerating,
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        pillStack.Children.Add(_stageGeneratingProgressRing);
        pillStack.Children.Add(_stageGeneratingStatusText);
        _stageGeneratingStatusPill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(12, 0, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = ThemeBrush("SolidBackgroundFillColorBaseBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
            Child = pillStack
        };
        Grid.SetRow(_stageGeneratingStatusPill, 2);
        stageGrid.Children.Add(_stageGeneratingStatusPill);

        ToolkitControls.Segmented backdropSegmented = BuildStageBackdropSegmented();
        backdropSegmented.Margin = new Thickness(12, 2, 12, 12);
        Grid.SetRow(backdropSegmented, 3);
        stageGrid.Children.Add(backdropSegmented);

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
        RefreshCapsulePreviewCardContent();
        RefreshStageViewportModeVisuals();
        ApplyStageBackdropVisuals();
        RefreshTitleBarContextBadge();
        RefreshSampleDataBadgeChrome();
        RefreshSettingsPreviewBadgeChrome();
        RefreshRevisionChrome();
        RefreshInPlaceGenerationChrome(refreshStream: false);
        RefreshTargetNodePillChrome();
        RefreshScriptErrorBannerChrome();
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

        if (!_prefs.HasSeenAgentMcpGuide)
        {
            ShowAgentMcpOnboardingTour(forceShow: false);
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

        var leftTools = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 6,
            VerticalSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        _pickElementIcon = new FontIcon
        {
            Glyph = "\uE81D",
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        _pickElementLabel = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.CoCreate.PickElement"),
            FontSize = 12,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var pickContentRow = new Grid { ColumnSpacing = 5 };
        pickContentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickContentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pickContentRow.Children.Add(_pickElementIcon);
        Grid.SetColumn(_pickElementLabel, 1);
        pickContentRow.Children.Add(_pickElementLabel);

        var pickToggle = new Button
        {
            Height = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            IsEnabled = hasLiveManifest,
            Content = pickContentRow
        };
        _pickElementButton = pickToggle;
        if (!hasLiveManifest)
        {
            _isInspectorModeEnabled = false;
        }
        ToolTipService.SetToolTip(pickToggle, _loc.T("CustomWidget.Workshop.CoCreate.PickElementTip"));
        AutomationProperties.SetName(pickToggle, _loc.T("CustomWidget.Workshop.CoCreate.PickElement"));
        pickToggle.Click += (_, _) =>
        {
            _isInspectorModeEnabled = !_isInspectorModeEnabled;
            if (!_isInspectorModeEnabled)
            {
                _selectedTargetNode = null;
            }

            if (_previewContent is not null)
            {
                _previewContent.SelectedNodePath = _selectedTargetNode?.NodePath;
                _previewContent.IsNodeSelectionEnabled = _isInspectorModeEnabled;
            }

            RefreshStageToolbarChrome();
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

        _settingsPreviewButton = new Button
        {
            Height = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            IsEnabled = hasLiveManifest,
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            Flyout = BuildSettingsPreviewFlyout()
        };
        leftTools.Children.Add(_settingsPreviewButton);

        _previewCultureOverridePillButton = new Button
        {
            Height = 28,
            Padding = new Thickness(9, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };
        _previewCultureOverridePillButton.Click += (_, _) => SetActivePreviewCultureOverride(null);
        leftTools.Children.Add(_previewCultureOverridePillButton);

        bar.Children.Add(leftTools);

        if (hasLiveManifest && !_prefs.HasSeenPickElementTip && !_isInPlaceGenerating)
        {
            var pickTip = new TeachingTip
            {
                Target = pickToggle,
                RequestedTheme = StudioExplicitTheme,
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
                        pickTip.RequestedTheme = StudioExplicitTheme;
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

    private ToolkitControls.Segmented BuildStageBackdropSegmented()
    {
        StageBackdropMode effectiveBackdrop = _stageBackdropMode == StageBackdropMode.Wallpaper && !_hasSystemWallpaperAvailable
            ? (Win32Helper.IsSystemDarkMode() ? StageBackdropMode.Dark : StageBackdropMode.Light)
            : _stageBackdropMode;

        var backdropSegmented = new ToolkitControls.Segmented
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _stageBackdropSegmented = backdropSegmented;

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

        return backdropSegmented;
    }

    private ToolkitControls.Segmented BuildStageViewportModeSegmented(bool hasLiveManifest)
    {
        var viewportSegmented = new ToolkitControls.Segmented
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 28,
            Height = 28,
            IsEnabled = hasLiveManifest,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _stageViewportModeSegmented = viewportSegmented;

        ToolkitControls.SegmentedItem CreateViewportItem(bool capsuleMode, IconElement icon, string labelKey)
        {
            string label = _loc.T(labelKey);
            var item = new ToolkitControls.SegmentedItem
            {
                Tag = capsuleMode,
                Icon = icon,
                Content = label,
                FontSize = 11.5
            };
            ToolTipService.SetToolTip(item, label);
            AutomationProperties.SetName(item, label);
            return item;
        }

        ToolkitControls.SegmentedItem expandedItem = CreateViewportItem(
            false,
            new FontIcon { Glyph = "\uE737", FontSize = 11.5 },
            "CustomWidget.Workshop.Stage.ViewExpanded");
        ToolkitControls.SegmentedItem capsuleItem = CreateViewportItem(
            true,
            CreateCapsuleViewportIcon(),
            "CustomWidget.Workshop.Stage.ViewCapsule");
        viewportSegmented.Items.Add(expandedItem);
        viewportSegmented.Items.Add(capsuleItem);
        viewportSegmented.SelectedItem = _isCapsulePreviewMode && hasLiveManifest ? capsuleItem : expandedItem;

        WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(viewportSegmented);
        viewportSegmented.ActualThemeChanged += (_, _) =>
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(viewportSegmented);

        viewportSegmented.SelectionChanged += (_, _) =>
        {
            if (viewportSegmented.SelectedItem is ToolkitControls.SegmentedItem selected &&
                selected.Tag is bool nextCapsuleMode &&
                nextCapsuleMode != _isCapsulePreviewMode)
            {
                _isCapsulePreviewMode = nextCapsuleMode;
                RefreshStageViewportModeVisuals();
            }
        };

        return viewportSegmented;
    }

    internal static PathIcon CreateCapsuleViewportIcon()
    {
        static PathFigure CreateStadiumFigure(double leftCenterX, double rightCenterX, double centerY, double radius)
        {
            var fig = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(leftCenterX, centerY - radius),
                IsClosed = true,
                IsFilled = true
            };
            fig.Segments.Add(new LineSegment
            {
                Point = new Windows.Foundation.Point(rightCenterX, centerY - radius)
            });
            fig.Segments.Add(new ArcSegment
            {
                Point = new Windows.Foundation.Point(rightCenterX, centerY + radius),
                Size = new Windows.Foundation.Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false
            });
            fig.Segments.Add(new LineSegment
            {
                Point = new Windows.Foundation.Point(leftCenterX, centerY + radius)
            });
            fig.Segments.Add(new ArcSegment
            {
                Point = new Windows.Foundation.Point(leftCenterX, centerY - radius),
                Size = new Windows.Foundation.Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false
            });
            return fig;
        }

        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        // 13.6 x 7.6 horizontal capsule stadium anchored at (0, 0) so PathIcon centers cleanly
        geometry.Figures.Add(CreateStadiumFigure(3.8, 9.8, 3.8, 3.8));
        geometry.Figures.Add(CreateStadiumFigure(3.8, 9.8, 3.8, 2.55));
        // Left status indicator dot + short summary bar mirroring DeskBox's desktop capsule
        geometry.Figures.Add(CreateStadiumFigure(3.9, 3.9, 3.8, 1.05));
        geometry.Figures.Add(CreateStadiumFigure(6.55, 10.15, 3.8, 0.6));

        return new PathIcon
        {
            Data = geometry,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Border BuildInteractiveCapsulePreviewCard()
    {
        var frame = new Border
        {
            Width = 348,
            MinHeight = 52,
            CornerRadius = new CornerRadius(26),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 7, 10, 7),
            Visibility = Visibility.Collapsed
        };
        FluentMotionHelper.AttachSmoothBoundsImplicitAnimations(frame);

        var outerGrid = new Grid { RowSpacing = 4 };
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var mainRow = new Grid { ColumnSpacing = 9, VerticalAlignment = VerticalAlignment.Center };
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconHost = new Grid
        {
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsuleEmojiText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _capsuleGlyphIcon = new FontIcon
        {
            Glyph = "\uE71D",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconHost.Children.Add(_capsuleEmojiText);
        iconHost.Children.Add(_capsuleGlyphIcon);
        mainRow.Children.Add(iconHost);

        var textCol = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsuleTitleText = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _capsuleSummaryText = new TextBlock
        {
            FontSize = 11.5,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textCol.Children.Add(_capsuleTitleText);
        textCol.Children.Add(_capsuleSummaryText);
        Grid.SetColumn(textCol, 1);
        mainRow.Children.Add(textCol);

        _capsuleBadgeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsuleBadgeBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _capsuleBadgeText
        };
        Grid.SetColumn(_capsuleBadgeBorder, 2);
        mainRow.Children.Add(_capsuleBadgeBorder);

        _capsulePrimaryActionIcon = new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 12
        };
        _capsulePrimaryActionButton = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Content = _capsulePrimaryActionIcon
        };
        _capsulePrimaryActionButton.Click += async (_, _) =>
        {
            if (_previewVm is not null)
            {
                await _previewVm.ExecuteCapsulePrimaryActionAsync();
                RefreshCapsulePreviewCardContent();
            }
        };
        Grid.SetColumn(_capsulePrimaryActionButton, 3);
        mainRow.Children.Add(_capsulePrimaryActionButton);

        outerGrid.Children.Add(mainRow);

        _capsuleProgressBar = new ProgressBar
        {
            Height = 3,
            Minimum = 0,
            Maximum = 1,
            Margin = new Thickness(4, 0, 6, 0),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_capsuleProgressBar, 1);
        outerGrid.Children.Add(_capsuleProgressBar);

        frame.Child = outerGrid;
        ApplyCapsulePreviewCardTheme(_stageBorder?.RequestedTheme ?? RootGrid.ActualTheme);
        return frame;
    }

    private void ApplyCapsulePreviewCardTheme(ElementTheme widgetTheme)
    {
        if (_capsulePreviewFrame is null)
        {
            return;
        }

        ElementTheme effective = widgetTheme == ElementTheme.Default
            ? StudioExplicitTheme
            : widgetTheme;
        bool isLight = effective == ElementTheme.Light;

        _capsulePreviewFrame.RequestedTheme = effective;
        _capsulePreviewFrame.Background = isLight
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xF6, 0xFA, 0xFB, 0xFE))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x20, 0x24, 0x2E));
        _capsulePreviewFrame.BorderBrush = isLight
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x00, 0x00, 0x00))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));

        if (_capsuleTitleText is not null)
        {
            _capsuleTitleText.Foreground = StageThemeBrush("TextFillColorPrimaryBrush", effective);
        }

        if (_capsuleSummaryText is not null)
        {
            _capsuleSummaryText.Foreground = StageThemeBrush("TextFillColorSecondaryBrush", effective);
        }

        if (_capsuleGlyphIcon is not null)
        {
            _capsuleGlyphIcon.Foreground = StageThemeBrush("AccentTextFillColorPrimaryBrush", effective);
        }

        RefreshCapsulePreviewCardContent();
    }

    private void RefreshCapsulePreviewCardContent()
    {
        if (_capsulePreviewFrame is null || _previewVm is null)
        {
            return;
        }

        ElementTheme effective = _capsulePreviewFrame.RequestedTheme == ElementTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark;
        bool isLight = effective == ElementTheme.Light;

        string glyph = _previewVm.CapsuleGlyph;
        bool isFluentGlyph = CustomWidgetTemplates.IsSegoeFluentGlyph(glyph);
        if (_capsuleGlyphIcon is not null && _capsuleEmojiText is not null)
        {
            if (isFluentGlyph)
            {
                _capsuleGlyphIcon.Glyph = glyph;
                _capsuleGlyphIcon.Visibility = Visibility.Visible;
                _capsuleEmojiText.Visibility = Visibility.Collapsed;
            }
            else
            {
                _capsuleEmojiText.Text = !string.IsNullOrWhiteSpace(glyph)
                    ? glyph
                    : ResolveDisplayEmoji(_previewVm.Manifest);
                _capsuleEmojiText.Visibility = Visibility.Visible;
                _capsuleGlyphIcon.Visibility = Visibility.Collapsed;
            }
        }

        if (_capsuleTitleText is not null)
        {
            string title = _previewVm.CapsuleTitle;
            _capsuleTitleText.Text = string.IsNullOrWhiteSpace(title)
                ? _previewVm.Manifest.ResolveLocalizedName(EffectivePreviewCultureName)
                : title;
        }

        if (_capsuleSummaryText is not null)
        {
            string summary = _previewVm.CapsuleSummary;
            _capsuleSummaryText.Text = summary;
            _capsuleSummaryText.Visibility = string.IsNullOrWhiteSpace(summary)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        if (_capsuleBadgeBorder is not null && _capsuleBadgeText is not null)
        {
            string badge = _previewVm.CapsuleBadge;
            if (string.IsNullOrWhiteSpace(badge))
            {
                _capsuleBadgeBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                _capsuleBadgeText.Text = badge;
                if (_previewVm.CapsuleBadgeIsWarning)
                {
                    _capsuleBadgeBorder.Background = new SolidColorBrush(
                        isLight
                            ? Windows.UI.Color.FromArgb(0x28, 0xD9, 0x77, 0x06)
                            : Windows.UI.Color.FromArgb(0x38, 0xF5, 0x9E, 0x0B));
                    _capsuleBadgeText.Foreground = new SolidColorBrush(
                        isLight
                            ? Windows.UI.Color.FromArgb(0xFF, 0xB4, 0x53, 0x09)
                            : Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24));
                }
                else
                {
                    _capsuleBadgeBorder.Background = StageThemeBrush("SubtleFillColorSecondaryBrush", effective);
                    _capsuleBadgeText.Foreground = StageThemeBrush("TextFillColorSecondaryBrush", effective);
                }

                _capsuleBadgeBorder.Visibility = Visibility.Visible;
            }
        }

        if (_capsulePrimaryActionButton is not null && _capsulePrimaryActionIcon is not null)
        {
            if (_previewVm.CapsuleHasPrimaryAction)
            {
                _capsulePrimaryActionIcon.Glyph = _previewVm.CapsulePrimaryActionGlyph;
                string tip = _previewVm.CapsulePrimaryActionTooltip;
                ToolTipService.SetToolTip(_capsulePrimaryActionButton, string.IsNullOrWhiteSpace(tip) ? null : tip);
                AutomationProperties.SetName(
                    _capsulePrimaryActionButton,
                    string.IsNullOrWhiteSpace(tip) ? _loc.T("CustomWidget.Workshop.Stage.ViewCapsule") : tip);
                _capsulePrimaryActionButton.Visibility = Visibility.Visible;
            }
            else
            {
                _capsulePrimaryActionButton.Visibility = Visibility.Collapsed;
            }
        }

        if (_capsuleProgressBar is not null)
        {
            if (_previewVm.CapsuleProgress is { } prog)
            {
                _capsuleProgressBar.Value = Math.Clamp(prog, 0.0, 1.0);
                _capsuleProgressBar.Visibility = Visibility.Visible;
            }
            else
            {
                _capsuleProgressBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void RefreshStageViewportModeVisuals()
    {
        bool hasLiveManifest = !string.IsNullOrWhiteSpace(CurrentManifestJson);
        bool showCapsule = hasLiveManifest && _isCapsulePreviewMode && _capsulePreviewFrame is not null;

        if (_widgetFrame is not null)
        {
            _widgetFrame.Visibility = showCapsule ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_capsulePreviewFrame is not null)
        {
            _capsulePreviewFrame.Visibility = showCapsule ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_sizeQuickButton is not null)
        {
            _sizeQuickButton.Visibility = showCapsule ? Visibility.Collapsed : Visibility.Visible;
        }

        if (showCapsule)
        {
            _isInspectorModeEnabled = false;
            _selectedTargetNode = null;
            if (_previewContent is not null)
            {
                _previewContent.SelectedNodePath = null;
                _previewContent.IsNodeSelectionEnabled = false;
                _previewContent.OnCompactStateChanged(true);
            }
        }
        else if (_previewContent is not null)
        {
            _previewContent.OnCompactStateChanged(false);
            _previewContent.IsNodeSelectionEnabled = _isInspectorModeEnabled;
            _previewContent.SelectedNodePath = _selectedTargetNode?.NodePath;
        }

        if (_refineTextBox is not null)
        {
            _refineTextBox.PlaceholderText = _loc.T(showCapsule
                ? "CustomWidget.Workshop.Refine.CapsulePlaceholder"
                : "CustomWidget.Workshop.Refine.Placeholder");
        }

        RefreshCapsulePreviewCardContent();
        RefreshStageToolbarChrome();
        RefreshTargetNodePillChrome();
        RefreshSuggestionChips();
    }

    private void SyncRuntimeScriptErrorFromPreviewVm()
    {
        CustomWidgetScriptRuntimeError? err = _previewVm?.LastScriptError;
        _liveRuntimeScriptError = err is null
            ? null
            : (string.IsNullOrWhiteSpace(err.FunctionName)
                ? err.ErrorMessage
                : $"[{err.FunctionName}] {err.ErrorMessage}");
        _liveRuntimeScriptErrorLine = err is { ErrorLine: > 0 } ? err.ErrorLine : null;
    }

    private void OnPreviewVmScriptErrorChanged(CustomWidgetScriptRuntimeError? error)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed)
            {
                return;
            }

            _liveRuntimeScriptError = error is null
                ? null
                : (string.IsNullOrWhiteSpace(error.FunctionName)
                    ? error.ErrorMessage
                    : $"[{error.FunctionName}] {error.ErrorMessage}");
            _liveRuntimeScriptErrorLine = error is { ErrorLine: > 0 } ? error.ErrorLine : null;
            RefreshScriptErrorBannerChrome();
        });
    }

    private void OnPreviewVmMetricsRefreshed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed && _page == StudioPage.Preview)
            {
                RefreshCapsulePreviewCardContent();
            }
        });
    }

    private void OnPreviewVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed && _page == StudioPage.Preview)
            {
                RefreshCapsulePreviewCardContent();
            }
        });
    }

    private void RefreshScriptErrorBannerChrome()
    {
        if (_scriptErrorBannerBorder is null || _scriptErrorBannerText is null)
        {
            return;
        }

        if (_isInPlaceGenerating || string.IsNullOrWhiteSpace(_liveRuntimeScriptError))
        {
            _scriptErrorBannerBorder.Visibility = Visibility.Collapsed;
            return;
        }

        string linePrefix = _liveRuntimeScriptErrorLine is { } ln and > 0
            ? $"logic.js:{ln}: "
            : "logic.js: ";
        _scriptErrorBannerText.Text = linePrefix + _liveRuntimeScriptError;
        if (_scriptErrorFixButton is not null)
        {
            _scriptErrorFixButton.IsEnabled = !_isInPlaceGenerating;
        }

        _scriptErrorBannerBorder.Visibility = Visibility.Visible;
    }

    private async Task TriggerRuntimeScriptAutoFixAsync()
    {
        if (_isInPlaceGenerating || string.IsNullOrWhiteSpace(_liveRuntimeScriptError))
        {
            return;
        }

        string locRef = _liveRuntimeScriptErrorLine is { } ln and > 0
            ? $"logic.js:{ln}"
            : "logic.js";
        string prompt = string.Format(
            CultureInfo.InvariantCulture,
            "Fix the runtime error in {0}: {1}. Keep existing UI layout and state keys intact.",
            locRef,
            _liveRuntimeScriptError);
        await BeginGenerationAsync(prompt);
    }

    private async Task ShowWorkspaceCodeViewerDialogAsync()
    {
        if (_isDialogOpen || _isClosed || RootGrid.XamlRoot is null || string.IsNullOrWhiteSpace(CurrentManifestJson))
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            string manifestCode = CurrentManifestJson ?? string.Empty;
            string? logicCode = CurrentLogicJs;
            CustomWidgetLocaleCatalog? localeCatalog = _activeI18nWorkingCatalog ?? CurrentLocales;

            var tabs = new List<(string FileName, string Content)>
            {
                ("manifest.json", manifestCode)
            };
            if (!string.IsNullOrWhiteSpace(logicCode))
            {
                tabs.Add(("logic.js", logicCode!));
            }

            var localeFiles = new List<(string Label, string Content)>();
            if (localeCatalog is { HasAnyLocale: true })
            {
                if (localeCatalog.Meta is { Hashes.Count: > 0 } meta)
                {
                    localeFiles.Add((
                        $"locales/{CustomWidgetLocaleCatalog.MetaFileName}",
                        CustomWidgetLocaleCatalog.FormatMetaJson(meta)));
                }

                foreach (CustomWidgetSupportedLocaleInfo info in CustomWidgetLocaleCatalog.SupportedLocales)
                {
                    if (localeCatalog.Locales.TryGetValue(info.Code, out IReadOnlyDictionary<string, string>? dict) &&
                        dict.Count > 0)
                    {
                        localeFiles.Add((
                            $"locales/{info.Code}.json  ({info.NativeName})",
                            CustomWidgetLocaleCatalog.FormatLocaleDictionaryJson(dict)));
                    }
                }

                foreach ((string code, IReadOnlyDictionary<string, string> dict) in localeCatalog.Locales)
                {
                    if (dict.Count == 0 ||
                        CustomWidgetLocaleCatalog.SupportedLocales.Any(s =>
                            string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    localeFiles.Add((
                        $"locales/{code}.json",
                        CustomWidgetLocaleCatalog.FormatLocaleDictionaryJson(dict)));
                }

                if (localeFiles.Count > 0)
                {
                    tabs.Add(("locales/", localeFiles[0].Content));
                }
            }

            var rootStack = new StackPanel { Spacing = 10, MinWidth = 560 };
            var topBar = new Grid { ColumnSpacing = 8 };
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tabSegmented = new ToolkitControls.Segmented
            {
                SelectionMode = ListViewSelectionMode.Single,
                MinHeight = 30,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            foreach ((string fileName, _) in tabs)
            {
                tabSegmented.Items.Add(new ToolkitControls.SegmentedItem
                {
                    Tag = fileName,
                    Content = fileName,
                    FontSize = 12
                });
            }
            tabSegmented.SelectedIndex = 0;
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(tabSegmented);
            topBar.Children.Add(tabSegmented);

            var localeFileCombo = new ComboBox
            {
                MinWidth = 196,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(6),
                FontSize = 12,
                Visibility = Visibility.Collapsed
            };
            int defaultLocaleIdx = 0;
            string effectiveCulture = EffectivePreviewCultureName;
            for (int i = 0; i < localeFiles.Count; i++)
            {
                localeFileCombo.Items.Add(new ComboBoxItem
                {
                    Content = localeFiles[i].Label,
                    Tag = localeFiles[i].Content
                });
                if (localeFiles[i].Label.StartsWith($"locales/{effectiveCulture}.json", StringComparison.OrdinalIgnoreCase))
                {
                    defaultLocaleIdx = i;
                }
            }
            if (localeFileCombo.Items.Count > 0)
            {
                localeFileCombo.SelectedIndex = defaultLocaleIdx;
            }
            Grid.SetColumn(localeFileCombo, 1);
            topBar.Children.Add(localeFileCombo);

            var codeBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
                FontSize = 12,
                MinHeight = 360,
                MaxHeight = 460,
                Text = tabs[0].Content
            };
            ScrollViewer.SetVerticalScrollBarVisibility(codeBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(codeBox, ScrollBarVisibility.Auto);

            var copyBtn = new Button
            {
                Height = 30,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                Content = CreateSmallIconLabel("\uE8C8", _loc.T("CustomWidget.Workshop.CodeViewer.CopyCurrent"))
            };
            copyBtn.Click += (_, _) => SetClipboardText(codeBox.Text ?? string.Empty);
            Grid.SetColumn(copyBtn, 2);
            topBar.Children.Add(copyBtn);

            void RefreshSelectedCodeText()
            {
                int idx = tabSegmented.SelectedIndex;
                if (idx < 0 || idx >= tabs.Count)
                {
                    return;
                }

                bool isLocalesTab = string.Equals(tabs[idx].FileName, "locales/", StringComparison.Ordinal);
                localeFileCombo.Visibility = isLocalesTab && localeFiles.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (isLocalesTab && localeFileCombo.SelectedItem is ComboBoxItem { Tag: string locJson })
                {
                    codeBox.Text = locJson;
                }
                else
                {
                    codeBox.Text = tabs[idx].Content;
                }
            }

            tabSegmented.SelectionChanged += (_, _) => RefreshSelectedCodeText();
            localeFileCombo.SelectionChanged += (_, _) => RefreshSelectedCodeText();

            rootStack.Children.Add(topBar);
            rootStack.Children.Add(codeBox);

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = StudioExplicitTheme,
                Title = _loc.T("CustomWidget.Workshop.CodeViewer.Title"),
                Content = rootStack,
                CloseButtonText = _loc.T("Common.Close"),
                DefaultButton = ContentDialogButton.Close
            };
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) &&
                style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }

            await dialog.ShowAsync();
        }
        finally
        {
            _isDialogOpen = false;
        }
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

        BindFlyoutToStudioTheme(flyout, stack);
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

    private Flyout BuildSettingsPreviewFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var hostScroll = new ScrollViewer
        {
            MaxHeight = 440,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var rootStack = new StackPanel
        {
            Spacing = 12,
            Width = 308,
            Padding = new Thickness(2, 0, 4, 2)
        };
        hostScroll.Content = rootStack;

        void RebuildControls()
        {
            rootStack.Children.Clear();
            CustomWidgetViewModel? vm = _previewVm;
            if (vm is null)
            {
                return;
            }

            IReadOnlyList<CustomWidgetSettingDefinition> localizedSettings =
                vm.Manifest.ResolveLocalizedSettings(EffectivePreviewCultureName);
            if (localizedSettings.Count == 0)
            {
                return;
            }

            var headerGrid = new Grid { ColumnSpacing = 8 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Spacing = 2 };
            titleStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.Stage.SettingsFlyoutTitle"),
                Style = TextStyle("BodyStrongTextBlockStyle")
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.Stage.SettingsFlyoutSubtitle"),
                Style = TextStyle("CaptionTextBlockStyle"),
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            headerGrid.Children.Add(titleStack);

            var resetBtn = new Button
            {
                Height = 26,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(5),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Top,
                Content = _loc.T("CustomWidget.Workshop.Stage.SettingsResetDefaults")
            };
            resetBtn.Click += async (_, _) =>
            {
                _sessionSettingOverrides.Clear();
                _sessionSecretOverrides.Clear();
                foreach (CustomWidgetSettingDefinition def in localizedSettings)
                {
                    if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
                    {
                        await vm.SetSecretValueAsync(def.Key, string.Empty);
                    }
                    else
                    {
                        await vm.SetSettingValueAsync(def.Key, def.DefaultValue);
                    }
                }

                RebuildControls();
            };
            Grid.SetColumn(resetBtn, 1);
            headerGrid.Children.Add(resetBtn);
            rootStack.Children.Add(headerGrid);

            rootStack.Children.Add(new Border
            {
                Height = 1,
                Background = ThemeBrush("DividerStrokeColorDefaultBrush")
            });

            foreach (CustomWidgetSettingDefinition def in localizedSettings)
            {
                string settingKey = def.Key;
                string currentVal = vm.Settings.TryGetValue(settingKey, out string? liveVal)
                    ? liveVal
                    : def.DefaultValue;

                if (string.Equals(def.Type, "moduleList", StringComparison.OrdinalIgnoreCase) && def.Options.Count > 0)
                {
                    var modSection = new StackPanel { Spacing = 6 };
                    modSection.Children.Add(new TextBlock
                    {
                        Text = def.Label,
                        FontSize = 12.5,
                        FontWeight = FontWeights.SemiBold
                    });

                    var modRowsHost = new StackPanel { Spacing = 4 };
                    string liveRaw = currentVal;

                    void RenderModuleRows()
                    {
                        modRowsHost.Children.Clear();
                        IReadOnlyList<CustomWidgetModuleEntry> entries =
                            CustomWidgetModuleListHelper.ParseEntries(liveRaw, def.Options);
                        int enabledCount = entries.Count(static e => e.Enabled);

                        for (int idx = 0; idx < entries.Count; idx++)
                        {
                            int capturedIdx = idx;
                            CustomWidgetModuleEntry entry = entries[idx];

                            var rowBorder = new Border
                            {
                                CornerRadius = new CornerRadius(6),
                                Padding = new Thickness(8, 4, 6, 4),
                                Background = ThemeBrush("SubtleFillColorTertiaryBrush"),
                                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                                BorderThickness = new Thickness(1)
                            };

                            var rowGrid = new Grid { ColumnSpacing = 6 };
                            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                            var cb = new CheckBox
                            {
                                Content = entry.Label,
                                IsChecked = entry.Enabled,
                                IsEnabled = !entry.Enabled || enabledCount > 1,
                                MinWidth = 0,
                                FontSize = 12,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            cb.Click += async (_, _) =>
                            {
                                bool nextEnabled = cb.IsChecked == true;
                                liveRaw = CustomWidgetModuleListHelper.SetModuleEnabled(
                                    liveRaw,
                                    def.Options,
                                    entry.Id,
                                    nextEnabled);
                                _sessionSettingOverrides[settingKey] = liveRaw;
                                await vm.SetSettingValueAsync(settingKey, liveRaw);
                                RenderModuleRows();
                            };
                            rowGrid.Children.Add(cb);

                            async Task MoveModuleAsync(int delta)
                            {
                                int targetIdx = capturedIdx + delta;
                                if (targetIdx < 0 || targetIdx >= entries.Count)
                                {
                                    return;
                                }

                                var tokens = entries
                                    .Select(static e => e.Enabled ? e.Id : "-" + e.Id)
                                    .ToList();
                                (tokens[capturedIdx], tokens[targetIdx]) = (tokens[targetIdx], tokens[capturedIdx]);
                                liveRaw = CustomWidgetModuleListHelper.SerializeEntries(
                                    CustomWidgetModuleListHelper.ParseEntries(
                                        string.Join(",", tokens),
                                        def.Options));
                                _sessionSettingOverrides[settingKey] = liveRaw;
                                await vm.SetSettingValueAsync(settingKey, liveRaw);
                                RenderModuleRows();
                            }

                            var upBtn = new Button
                            {
                                Width = 24,
                                Height = 24,
                                Padding = new Thickness(0),
                                CornerRadius = new CornerRadius(4),
                                IsEnabled = capturedIdx > 0,
                                Content = new FontIcon { Glyph = "\uE70E", FontSize = 10 }
                            };
                            ToolTipService.SetToolTip(upBtn, _loc.T("CustomWidget.Workshop.Stage.SettingsMoveUp"));
                            upBtn.Click += async (_, _) => await MoveModuleAsync(-1);
                            Grid.SetColumn(upBtn, 1);
                            rowGrid.Children.Add(upBtn);

                            var downBtn = new Button
                            {
                                Width = 24,
                                Height = 24,
                                Padding = new Thickness(0),
                                CornerRadius = new CornerRadius(4),
                                IsEnabled = capturedIdx < entries.Count - 1,
                                Content = new FontIcon { Glyph = "\uE70D", FontSize = 10 }
                            };
                            ToolTipService.SetToolTip(downBtn, _loc.T("CustomWidget.Workshop.Stage.SettingsMoveDown"));
                            downBtn.Click += async (_, _) => await MoveModuleAsync(1);
                            Grid.SetColumn(downBtn, 2);
                            rowGrid.Children.Add(downBtn);

                            rowBorder.Child = rowGrid;
                            modRowsHost.Children.Add(rowBorder);
                        }
                    }

                    RenderModuleRows();
                    modSection.Children.Add(modRowsHost);
                    rootStack.Children.Add(modSection);
                    continue;
                }

                if (string.Equals(def.Type, "toggle", StringComparison.OrdinalIgnoreCase))
                {
                    var row = new Grid { ColumnSpacing = 10 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock
                    {
                        Text = def.Label,
                        FontSize = 12.5,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    var toggle = new ToggleSwitch
                    {
                        MinWidth = 0,
                        OnContent = string.Empty,
                        OffContent = string.Empty,
                        IsOn = CustomWidgetExpressionEvaluator.IsTruthy(currentVal),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    AutomationProperties.SetName(toggle, def.Label);
                    toggle.Toggled += async (_, _) =>
                    {
                        string nextVal = toggle.IsOn ? "true" : "false";
                        _sessionSettingOverrides[settingKey] = nextVal;
                        await vm.SetSettingValueAsync(settingKey, nextVal);
                    };
                    Grid.SetColumn(toggle, 1);
                    row.Children.Add(toggle);
                    rootStack.Children.Add(row);
                    continue;
                }

                if (def.Options.Count > 0)
                {
                    IReadOnlyList<CustomWidgetOptionSpec> effectiveOpts =
                        CustomWidgetContextMenuBuilder.ResolveEffectiveSettingOptions(def, EffectivePreviewCultureName);
                    var combo = new ComboBox
                    {
                        Header = def.Label,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        CornerRadius = new CornerRadius(6)
                    };
                    int selectedIndex = 0;
                    for (int i = 0; i < effectiveOpts.Count; i++)
                    {
                        CustomWidgetOptionSpec opt = effectiveOpts[i];
                        combo.Items.Add(new ComboBoxItem
                        {
                            Content = string.IsNullOrWhiteSpace(opt.Label) ? opt.Value : opt.Label,
                            Tag = opt.Value
                        });
                        if (string.Equals(opt.Value, currentVal, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                        }
                    }

                    if (combo.Items.Count > 0)
                    {
                        combo.SelectedIndex = selectedIndex;
                    }

                    combo.SelectionChanged += async (_, _) =>
                    {
                        if (combo.SelectedItem is ComboBoxItem { Tag: string selectedVal })
                        {
                            _sessionSettingOverrides[settingKey] = selectedVal;
                            await vm.SetSettingValueAsync(settingKey, selectedVal);
                        }
                    };
                    rootStack.Children.Add(combo);
                    continue;
                }

                if (string.Equals(def.Type, "number", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(def.Type, "int", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(def.Type, "integer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(def.Type, "double", StringComparison.OrdinalIgnoreCase))
                {
                    double fallbackNum = double.TryParse(
                        def.DefaultValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double parsedDef)
                        ? parsedDef
                        : 0;
                    double initialNum = double.TryParse(
                        currentVal,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double parsedCur)
                        ? parsedCur
                        : fallbackNum;
                    bool isInt = !string.Equals(def.Type, "double", StringComparison.OrdinalIgnoreCase) &&
                                 Math.Abs(fallbackNum - Math.Truncate(fallbackNum)) < 1e-6;

                    var numBox = new NumberBox
                    {
                        Header = def.Label,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        CornerRadius = new CornerRadius(6),
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                        SmallChange = isInt ? 1 : 0.5,
                        LargeChange = isInt ? 10 : 2,
                        Minimum = fallbackNum >= 0 ? 0 : -1_000_000,
                        Maximum = 1_000_000,
                        Value = initialNum
                    };
                    numBox.ValueChanged += async (_, args) =>
                    {
                        if (double.IsNaN(args.NewValue))
                        {
                            return;
                        }

                        string formatted = isInt
                            ? ((long)Math.Round(args.NewValue)).ToString(CultureInfo.InvariantCulture)
                            : args.NewValue.ToString("0.##", CultureInfo.InvariantCulture);
                        _sessionSettingOverrides[settingKey] = formatted;
                        await vm.SetSettingValueAsync(settingKey, formatted);
                    };
                    rootStack.Children.Add(numBox);
                    continue;
                }

                if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
                {
                    string existingSecret = vm.Secrets.TryGetValue(settingKey, out string? sVal) ? sVal : string.Empty;
                    var secStack = new StackPanel { Spacing = 4 };
                    secStack.Children.Add(new TextBlock
                    {
                        Text = def.Label,
                        FontSize = 12.5
                    });

                    var pwdRow = new Grid { ColumnSpacing = 6 };
                    pwdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    pwdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var pwdBox = new PasswordBox
                    {
                        CornerRadius = new CornerRadius(6),
                        PlaceholderText = !string.IsNullOrWhiteSpace(existingSecret)
                            ? CustomWidgetSecretStore.MaskSecret(existingSecret)
                            : _loc.T("CustomWidget.Secret.SettingsFieldPlaceholder")
                    };
                    AutomationProperties.SetName(pwdBox, def.Label);

                    var saveSecBtn = new Button
                    {
                        CornerRadius = new CornerRadius(6),
                        Content = _loc.T("Common.Save")
                    };

                    async Task CommitSecretPreviewAsync()
                    {
                        string typed = pwdBox.Password.Trim();
                        _sessionSecretOverrides[settingKey] = typed;
                        await vm.SetSecretValueAsync(settingKey, typed);
                        pwdBox.Password = string.Empty;
                        pwdBox.PlaceholderText = !string.IsNullOrWhiteSpace(typed)
                            ? CustomWidgetSecretStore.MaskSecret(typed)
                            : _loc.T("CustomWidget.Secret.SettingsFieldPlaceholder");
                    }

                    saveSecBtn.Click += async (_, _) => await CommitSecretPreviewAsync();
                    pwdBox.KeyDown += async (_, e) =>
                    {
                        if (e.Key == VirtualKey.Enter)
                        {
                            e.Handled = true;
                            await CommitSecretPreviewAsync();
                        }
                    };
                    pwdRow.Children.Add(pwdBox);
                    Grid.SetColumn(saveSecBtn, 1);
                    pwdRow.Children.Add(saveSecBtn);
                    secStack.Children.Add(pwdRow);

                    secStack.Children.Add(new TextBlock
                    {
                        Text = _loc.T("CustomWidget.Secret.SettingsDpapiNote"),
                        FontSize = 11,
                        Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                        TextWrapping = TextWrapping.Wrap
                    });
                    rootStack.Children.Add(secStack);
                    continue;
                }

                var textBox = new TextBox
                {
                    Header = def.Label,
                    Text = currentVal,
                    CornerRadius = new CornerRadius(6),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                async Task CommitTextSettingAsync()
                {
                    string nextText = textBox.Text.Trim();
                    _sessionSettingOverrides[settingKey] = nextText;
                    await vm.SetSettingValueAsync(settingKey, nextText);
                }

                textBox.LostFocus += async (_, _) => await CommitTextSettingAsync();
                textBox.KeyDown += async (_, e) =>
                {
                    if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
                    {
                        return;
                    }

                    if (e.Key == VirtualKey.Enter)
                    {
                        e.Handled = true;
                        await CommitTextSettingAsync();
                    }
                };
                rootStack.Children.Add(textBox);
            }
        }

        BindFlyoutToStudioTheme(flyout, hostScroll);
        flyout.Opening += (_, _) => RebuildControls();
        flyout.Content = hostScroll;
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
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0 — Docked Multi-Locale Progress & Live Preview Center
        _localeCenterHostBorder = BuildLocaleCenterPanel();
        layout.Children.Add(_localeCenterHostBorder);

        // Row 1 — Persistent Multi-Turn Co-Creation Stream (sits directly on Mica workspace layer)
        _chatStreamPanel = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(0, 4, 8, 14)
        };
        _chatStreamScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _chatStreamPanel
        };
        _chatStreamScroller.ViewChanged += (s, _) =>
        {
            if (_programmaticChatScrollDepth > 0 || s is not ScrollViewer sv)
            {
                return;
            }

            _chatStickToBottom = (sv.ScrollableHeight - sv.VerticalOffset) <= 24.0;
        };
        _chatStreamScroller.PointerWheelChanged += (s, e) =>
        {
            if (s is not ScrollViewer sv)
            {
                return;
            }

            int delta = e.GetCurrentPoint(sv).Properties.MouseWheelDelta;
            if (delta > 0 && sv.ScrollableHeight > 0.5)
            {
                _chatStickToBottom = false;
            }
        };
        Grid.SetRow(_chatStreamScroller, 1);
        layout.Children.Add(_chatStreamScroller);

        // Row 1 — Bottom Dock: InfoBar + Picked Node Pill + Curated Chips + Compact-Submit Multi-Line Composer
        var dockStack = new StackPanel { Spacing = 8 };

        _pageInfoBar = CreatePageInfoBar();
        dockStack.Children.Add(_pageInfoBar);

        _scriptErrorBannerText = new TextBlock
        {
            FontSize = 12,
            Foreground = ThemeBrush("SystemFillColorCriticalBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap
        };
        _scriptErrorFixButton = new Button
        {
            Height = 26,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(5),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Content = CreateSmallIconLabel("\uE945", _loc.T("CustomWidget.Workshop.ScriptError.FixWithAi"))
        };
        _scriptErrorFixButton.Click += async (_, _) => await TriggerRuntimeScriptAutoFixAsync();

        var scriptErrorGrid = new Grid { ColumnSpacing = 8 };
        scriptErrorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scriptErrorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scriptErrorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scriptErrorGrid.Children.Add(new FontIcon
        {
            Glyph = "\uE7BA",
            FontSize = 13,
            Foreground = ThemeBrush("SystemFillColorCriticalBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_scriptErrorBannerText, 1);
        scriptErrorGrid.Children.Add(_scriptErrorBannerText);
        Grid.SetColumn(_scriptErrorFixButton, 2);
        scriptErrorGrid.Children.Add(_scriptErrorFixButton);

        _scriptErrorBannerBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 8, 6),
            Background = ThemeBrush("SystemFillColorCriticalBackgroundBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
            Child = scriptErrorGrid
        };
        dockStack.Children.Add(_scriptErrorBannerBorder);

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
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
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

        Grid.SetRow(dockStack, 2);
        layout.Children.Add(dockStack);

        return layout;
    }

    private Border BuildLocaleCenterPanel()
    {
        _localeCenterCardRefs.Clear();
        var rootStack = new StackPanel { Spacing = 8 };

        var topRow = new Grid { ColumnSpacing = 8 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleCol = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleLine.Children.Add(new FontIcon
        {
            Glyph = "\uF2B7",
            FontSize = 13,
            Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        titleLine.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.I18n.CenterTitle"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleCol.Children.Add(titleLine);

        _localeCenterSummaryText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        titleCol.Children.Add(_localeCenterSummaryText);
        topRow.Children.Add(titleCol);

        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        _localeCenterSync2Button = new Button
        {
            Height = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Content = FormatBilingualSyncLabel(shortLabel: true)
        };
        _localeCenterSync2Button.Click += async (_, _) =>
            await RunIncrementalI18nBatchAsync(ResolveActiveBilingualCultureCodes());
        actionsRow.Children.Add(_localeCenterSync2Button);

        _localeCenterRetryFailedButton = new Button
        {
            Height = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Visibility = Visibility.Collapsed
        };
        _localeCenterRetryFailedButton.Click += async (_, _) =>
        {
            if (_lastFailedLocaleErrors.Count > 0)
            {
                await RunIncrementalI18nBatchAsync(_lastFailedLocaleErrors.Keys.ToArray());
            }
        };
        actionsRow.Children.Add(_localeCenterRetryFailedButton);

        _localeCenterSyncAllButton = new Button
        {
            Height = 28,
            Padding = new Thickness(11, 0, 11, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Style = AccentButtonStyle(),
            Content = CreateSmallIconLabel("\uE774", _loc.T("CustomWidget.Workshop.I18n.SyncAllShort"))
        };
        _localeCenterSyncAllButton.Click += async (_, _) => await RunIncrementalI18nBatchAsync(null);
        actionsRow.Children.Add(_localeCenterSyncAllButton);

        _localeCenterStopButton = new Button
        {
            Height = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11.5,
            Visibility = Visibility.Collapsed,
            Content = CreateSmallIconLabel("\uE71A", _loc.T("CustomWidget.Workshop.Stream.StopButton"))
        };
        _localeCenterStopButton.Click += (_, _) => CancelI18nQueue();
        actionsRow.Children.Add(_localeCenterStopButton);

        _localeCenterCloseButton = CreateIconButton("\uE711", _loc.T("Common.Close"));
        _localeCenterCloseButton.Width = 28;
        _localeCenterCloseButton.Height = 28;
        _localeCenterCloseButton.CornerRadius = new CornerRadius(6);
        _localeCenterCloseButton.Click += (_, _) =>
        {
            if (!_isI18nQueueRunning)
            {
                _isLocaleCenterOpen = false;
                RefreshLocaleCenterChrome();
            }
        };
        actionsRow.Children.Add(_localeCenterCloseButton);

        Grid.SetColumn(actionsRow, 1);
        topRow.Children.Add(actionsRow);
        rootStack.Children.Add(topRow);

        _localeCenterOverallProgressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 4,
            IsIndeterminate = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        rootStack.Children.Add(_localeCenterOverallProgressBar);

        var matrixGrid = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 6,
            Padding = new Thickness(0, 2, 4, 2)
        };
        matrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        matrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        IReadOnlyList<CustomWidgetSupportedLocaleInfo> supported = CustomWidgetLocaleCatalog.SupportedLocales;
        int rowCount = (supported.Count + 1) / 2;
        for (int r = 0; r < rowCount; r++)
        {
            matrixGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int idx = 0; idx < supported.Count; idx++)
        {
            CustomWidgetSupportedLocaleInfo info = supported[idx];
            string capturedCode = info.Code;

            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
                Background = ThemeBrush("SubtleFillColorTertiaryBrush")
            };

            var innerGrid = new Grid { RowSpacing = 4, Padding = new Thickness(9, 6, 9, 6) };
            innerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            innerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var headerLine = new Grid { ColumnSpacing = 6 };
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var previewEye = new FontIcon
            {
                Glyph = "\uE890",
                FontSize = 10.5,
                Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            headerLine.Children.Add(previewEye);

            var nameText = new TextBlock
            {
                Text = $"{info.NativeName}  {info.Code}",
                FontSize = 11.5,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameText, 1);
            headerLine.Children.Add(nameText);

            var statusText = new TextBlock
            {
                FontSize = 11,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush")
            };
            Grid.SetColumn(statusText, 2);
            headerLine.Children.Add(statusText);

            var retrySingleBtn = new Button
            {
                Width = 20,
                Height = 20,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Visibility = Visibility.Collapsed,
                Content = new FontIcon { Glyph = "\uE72C", FontSize = 9.5 }
            };
            ToolTipService.SetToolTip(retrySingleBtn, _loc.T("CustomWidget.Workshop.I18n.RetrySingleTip"));
            retrySingleBtn.Click += async (_, _) =>
            {
                if (!IsAnyStudioJobRunning)
                {
                    await RunIncrementalI18nBatchAsync([capturedCode]);
                }
            };
            Grid.SetColumn(retrySingleBtn, 3);
            headerLine.Children.Add(retrySingleBtn);

            innerGrid.Children.Add(headerLine);

            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                Height = 3,
                IsIndeterminate = false,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetRow(bar, 1);
            innerGrid.Children.Add(bar);

            var selectBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = innerGrid
            };
            AutomationProperties.SetName(selectBtn, $"{info.NativeName} ({info.Code})");
            selectBtn.Click += (_, _) => SetActivePreviewCultureOverride(capturedCode);

            cardBorder.Child = selectBtn;
            Grid.SetRow(cardBorder, idx / 2);
            Grid.SetColumn(cardBorder, idx % 2);
            matrixGrid.Children.Add(cardBorder);

            _localeCenterCardRefs[info.Code] = new LocaleCardUiRefs(
                info.Code,
                info.NativeName,
                cardBorder,
                selectBtn,
                nameText,
                previewEye,
                statusText,
                retrySingleBtn,
                bar);
        }

        var matrixScroll = new ScrollViewer
        {
            MaxHeight = 224,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = matrixGrid
        };
        rootStack.Children.Add(matrixScroll);

        _localeCenterHintText = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.I18n.ClickToPreviewHint"),
            FontSize = 11,
            Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        rootStack.Children.Add(_localeCenterHintText);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Background = ThemeBrush("LayerFillColorAltBrush"),
            Visibility = Visibility.Collapsed,
            Child = rootStack
        };
    }

    private void RefreshLocaleCenterChrome()
    {
        bool hasManifest = !string.IsNullOrWhiteSpace(_activeI18nWorkingManifestJson ?? CurrentManifestJson);
        CustomWidgetI18nSyncPlan? plan = hasManifest ? GetOrBuildCurrentI18nSyncPlan() : null;
        int totalLocales = CustomWidgetLocaleCatalog.SupportedLocales.Count;
        int requiredKeys = plan?.RequiredKeys.Count ?? 0;

        int readyLocaleCount = 0;
        int pendingLocaleCount = 0;
        int overallDoneUnits = 0;
        int overallTotalUnits = Math.Max(1, totalLocales * Math.Max(1, requiredKeys));

        Dictionary<string, CustomWidgetLocaleTaskPlan> planByCulture =
            plan?.LocalePlans.ToDictionary(static p => p.Culture, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, CustomWidgetLocaleTaskPlan>(StringComparer.OrdinalIgnoreCase);

        string activePreviewCulture = EffectivePreviewCultureName;

        foreach (CustomWidgetSupportedLocaleInfo info in CustomWidgetLocaleCatalog.SupportedLocales)
        {
            planByCulture.TryGetValue(info.Code, out CustomWidgetLocaleTaskPlan? lp);
            _liveLocaleJobUpdates.TryGetValue(info.Code, out LocaleTranslationProgressUpdate? liveUpdate);
            _lastFailedLocaleErrors.TryGetValue(info.Code, out string? lastErr);

            bool isLocaleReady = false;
            int doneKeysForLocale = 0;
            int maxKeysForLocale = Math.Max(1, requiredKeys);
            string statusLabel;
            Brush statusBrush = ThemeBrush("TextFillColorSecondaryBrush");
            bool showRetrySingle = false;
            string? errorTip = null;

            if (requiredKeys == 0)
            {
                isLocaleReady = true;
                doneKeysForLocale = 1;
                statusLabel = "—";
            }
            else if (liveUpdate is not null)
            {
                maxKeysForLocale = Math.Max(1, liveUpdate.RequiredTotalKeys);
                switch (liveUpdate.State)
                {
                    case LocaleTranslationJobState.Completed:
                    case LocaleTranslationJobState.SkippedUpToDate:
                        isLocaleReady = true;
                        doneKeysForLocale = maxKeysForLocale;
                        statusLabel = lp?.Status == CustomWidgetLocaleSyncStatus.BaseReady
                            ? _loc.T("CustomWidget.Workshop.I18n.StatusBase")
                            : _loc.T("CustomWidget.Workshop.I18n.StatusReady");
                        statusBrush = ThemeBrush("AccentTextFillColorPrimaryBrush");
                        break;
                    case LocaleTranslationJobState.Running:
                        doneKeysForLocale = Math.Clamp(
                            liveUpdate.ReadyTotalKeys + liveUpdate.TranslatedCount,
                            0,
                            maxKeysForLocale);
                        statusLabel = _loc.Format(
                            "CustomWidget.Workshop.I18n.StatusRunning",
                            Math.Min(liveUpdate.TranslatedCount, liveUpdate.TotalDeltaCount),
                            liveUpdate.TotalDeltaCount);
                        statusBrush = ThemeBrush("AccentTextFillColorPrimaryBrush");
                        pendingLocaleCount++;
                        break;
                    case LocaleTranslationJobState.Retrying:
                        doneKeysForLocale = Math.Clamp(
                            liveUpdate.ReadyTotalKeys + liveUpdate.TranslatedCount,
                            0,
                            maxKeysForLocale);
                        statusLabel = _loc.T("CustomWidget.Workshop.I18n.StatusRetrying");
                        statusBrush = ThemeBrush("SystemFillColorCautionBrush");
                        pendingLocaleCount++;
                        break;
                    case LocaleTranslationJobState.Queued:
                        doneKeysForLocale = Math.Clamp(liveUpdate.ReadyTotalKeys, 0, maxKeysForLocale);
                        statusLabel = _loc.T("CustomWidget.Workshop.I18n.StatusQueued");
                        pendingLocaleCount++;
                        break;
                    case LocaleTranslationJobState.Failed:
                        doneKeysForLocale = Math.Clamp(liveUpdate.ReadyTotalKeys, 0, maxKeysForLocale);
                        statusLabel = _loc.T("CustomWidget.Workshop.I18n.StatusFailed");
                        statusBrush = ThemeBrush("SystemFillColorCriticalBrush");
                        errorTip = liveUpdate.ErrorMessage ?? lastErr;
                        showRetrySingle = !IsAnyStudioJobRunning;
                        pendingLocaleCount++;
                        break;
                    default:
                        doneKeysForLocale = Math.Clamp(liveUpdate.ReadyTotalKeys, 0, maxKeysForLocale);
                        statusLabel = _loc.Format("CustomWidget.Workshop.I18n.StatusMissing", liveUpdate.TotalDeltaCount);
                        pendingLocaleCount++;
                        break;
                }
            }
            else if (lp is not null)
            {
                maxKeysForLocale = Math.Max(1, lp.TotalRequiredKeys);
                bool requiresCompileForNonBase = plan?.ManifestChanged == true &&
                                                 lp.Status != CustomWidgetLocaleSyncStatus.BaseReady;
                if (!lp.NeedsTranslation && !requiresCompileForNonBase)
                {
                    isLocaleReady = true;
                    doneKeysForLocale = maxKeysForLocale;
                    statusLabel = lp.Status == CustomWidgetLocaleSyncStatus.BaseReady
                        ? _loc.T("CustomWidget.Workshop.I18n.StatusBase")
                        : _loc.T("CustomWidget.Workshop.I18n.StatusReady");
                    statusBrush = lp.Status == CustomWidgetLocaleSyncStatus.BaseReady
                        ? ThemeBrush("TextFillColorSecondaryBrush")
                        : ThemeBrush("AccentTextFillColorPrimaryBrush");
                }
                else if (requiresCompileForNonBase && !lp.NeedsTranslation)
                {
                    doneKeysForLocale = Math.Max(0, maxKeysForLocale - 1);
                    statusLabel = _loc.Format("CustomWidget.Workshop.I18n.StatusMissing", 1);
                    pendingLocaleCount++;
                }
                else if (!string.IsNullOrWhiteSpace(lastErr))
                {
                    doneKeysForLocale = Math.Clamp(lp.ReadyKeyCount, 0, maxKeysForLocale);
                    statusLabel = _loc.T("CustomWidget.Workshop.I18n.StatusFailed");
                    statusBrush = ThemeBrush("SystemFillColorCriticalBrush");
                    errorTip = lastErr;
                    showRetrySingle = !IsAnyStudioJobRunning;
                    pendingLocaleCount++;
                }
                else
                {
                    doneKeysForLocale = Math.Clamp(lp.ReadyKeyCount, 0, maxKeysForLocale);
                    statusLabel = lp.StaleKeys.Count > 0 && lp.MissingKeys.Count == 0
                        ? _loc.Format("CustomWidget.Workshop.I18n.StatusStale", lp.DeltaKeys.Count)
                        : _loc.Format("CustomWidget.Workshop.I18n.StatusMissing", lp.DeltaKeys.Count);
                    pendingLocaleCount++;
                }
            }
            else
            {
                statusLabel = "—";
            }

            if (isLocaleReady)
            {
                readyLocaleCount++;
            }

            overallDoneUnits += doneKeysForLocale;

            if (_localeCenterCardRefs.TryGetValue(info.Code, out LocaleCardUiRefs? ui))
            {
                bool isActivePreview = string.Equals(activePreviewCulture, info.Code, StringComparison.OrdinalIgnoreCase);
                ui.PreviewEyeIcon.Visibility = isActivePreview ? Visibility.Visible : Visibility.Collapsed;
                ui.NameText.FontWeight = isActivePreview ? FontWeights.SemiBold : FontWeights.Normal;
                ui.NameText.Foreground = isActivePreview
                    ? ThemeBrush("AccentTextFillColorPrimaryBrush")
                    : ThemeBrush("TextFillColorPrimaryBrush");
                ui.CardBorder.BorderBrush = isActivePreview
                    ? ThemeBrush("AccentFillColorDefaultBrush")
                    : ThemeBrush("DividerStrokeColorDefaultBrush");
                ui.CardBorder.Background = isActivePreview
                    ? ThemeBrush("SubtleFillColorSecondaryBrush")
                    : ThemeBrush("SubtleFillColorTertiaryBrush");

                ui.StatusText.Text = statusLabel;
                ui.StatusText.Foreground = statusBrush;
                ui.RetrySingleButton.Visibility = showRetrySingle ? Visibility.Visible : Visibility.Collapsed;
                ui.ProgressBar.Maximum = maxKeysForLocale;
                ui.ProgressBar.Value = doneKeysForLocale;
                ui.SelectButton.IsEnabled = hasManifest;

                string baseTip = _loc.Format("CustomWidget.Workshop.I18n.PreviewLocaleCardTip", info.NativeName, info.Code);
                ToolTipService.SetToolTip(
                    ui.SelectButton,
                    string.IsNullOrWhiteSpace(errorTip) ? baseTip : $"{errorTip}\n{baseTip}");
            }
        }

        if (_i18nHeaderButton is not null && _i18nHeaderLabel is not null && _i18nHeaderIcon is not null)
        {
            _i18nHeaderButton.IsEnabled = hasManifest;
            bool openOrRunning = _isLocaleCenterOpen || _isI18nQueueRunning;
            string pillText = !hasManifest || requiredKeys == 0
                ? _loc.T("CustomWidget.Workshop.I18n.HeaderButton")
                : (readyLocaleCount >= totalLocales
                    ? $"{readyLocaleCount}/{totalLocales} ✓"
                    : $"{readyLocaleCount}/{totalLocales}");
            _i18nHeaderLabel.Text = pillText;
            _i18nHeaderButton.Background = ThemeBrush(openOrRunning ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush");
            _i18nHeaderButton.BorderBrush = ThemeBrush(openOrRunning ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
            _i18nHeaderIcon.Foreground = ThemeBrush(openOrRunning ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");

            string headerTip = requiredKeys > 0
                ? _loc.Format("CustomWidget.Workshop.I18n.HeaderTooltipFormat", readyLocaleCount, totalLocales, requiredKeys)
                : _loc.T("CustomWidget.Workshop.I18n.CenterTitle");
            ToolTipService.SetToolTip(_i18nHeaderButton, headerTip);
            AutomationProperties.SetName(_i18nHeaderButton, headerTip);
        }

        if (_localeCenterHostBorder is null)
        {
            return;
        }

        bool showPanel = hasManifest && (_isLocaleCenterOpen || _isI18nQueueRunning);
        _localeCenterHostBorder.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;
        if (!showPanel)
        {
            return;
        }

        if (_localeCenterSummaryText is not null)
        {
            _localeCenterSummaryText.Text = requiredKeys == 0
                ? _loc.T("CustomWidget.Workshop.I18n.NoLocalizableText")
                : (_isI18nQueueRunning
                    ? _loc.Format("CustomWidget.Workshop.I18n.SummaryRunningFormat", readyLocaleCount, totalLocales, requiredKeys)
                    : _loc.Format("CustomWidget.Workshop.I18n.SummaryIdleFormat", readyLocaleCount, totalLocales, requiredKeys));
        }

        if (_localeCenterOverallProgressBar is not null)
        {
            double pct = requiredKeys == 0
                ? 100.0
                : Math.Clamp(overallDoneUnits * 100.0 / overallTotalUnits, 0.0, 100.0);
            _localeCenterOverallProgressBar.Value = pct;
        }

        if (_localeCenterHintText is not null)
        {
            _localeCenterHintText.Text = requiredKeys == 0
                ? _loc.T("CustomWidget.Workshop.I18n.NoLocalizableText")
                : _loc.T("CustomWidget.Workshop.I18n.ClickToPreviewHint");
        }

        if (_localeCenterSyncAllButton is not null)
        {
            _localeCenterSyncAllButton.Visibility = _isI18nQueueRunning ? Visibility.Collapsed : Visibility.Visible;
            _localeCenterSyncAllButton.IsEnabled = !IsAnyStudioJobRunning && requiredKeys > 0;
        }

        if (_localeCenterSync2Button is not null)
        {
            _localeCenterSync2Button.Content = FormatBilingualSyncLabel(shortLabel: true);
            _localeCenterSync2Button.Visibility = _isI18nQueueRunning ? Visibility.Collapsed : Visibility.Visible;
            _localeCenterSync2Button.IsEnabled = !IsAnyStudioJobRunning && requiredKeys > 0;
        }

        if (_localeCenterRetryFailedButton is not null)
        {
            int failedCount = _lastFailedLocaleErrors.Count;
            if (!_isI18nQueueRunning && failedCount > 0)
            {
                _localeCenterRetryFailedButton.Content = _loc.Format("CustomWidget.Workshop.I18n.RetryFailedButton", failedCount);
                _localeCenterRetryFailedButton.IsEnabled = !IsAnyStudioJobRunning;
                _localeCenterRetryFailedButton.Visibility = Visibility.Visible;
            }
            else
            {
                _localeCenterRetryFailedButton.Visibility = Visibility.Collapsed;
            }
        }

        if (_localeCenterStopButton is not null)
        {
            _localeCenterStopButton.Visibility = _isI18nQueueRunning ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_localeCenterCloseButton is not null)
        {
            _localeCenterCloseButton.IsEnabled = !_isI18nQueueRunning;
        }
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

        if (_isInPlaceGenerating || _isCapsulePreviewMode || _selectedTargetNode is not { } picked)
        {
            _targetNodePillBorder.Visibility = Visibility.Collapsed;
            return;
        }

        string baseText = string.IsNullOrWhiteSpace(picked.DisplayLabel)
            ? picked.NodeType
            : _loc.Format("CustomWidget.Workshop.CoCreate.PickedPill", picked.NodeType, picked.DisplayLabel);
        string? scriptSummary = InspectNodeScriptSummary(CurrentManifestJson, picked.NodePath);
        _targetNodePillText.Text = string.IsNullOrWhiteSpace(scriptSummary)
            ? baseText
            : $"{baseText} · {scriptSummary}";
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

        void AddPromptChip(string chipLabel, string promptToFill, string? glyph = null, IconElement? iconElement = null)
        {
            if (_suggestionChipsPanel.Children.Count >= 4 ||
                string.IsNullOrWhiteSpace(chipLabel) ||
                string.IsNullOrWhiteSpace(promptToFill) ||
                !addedLabels.Add(chipLabel))
            {
                return;
            }

            Button chip = CreateChipButton(chipLabel, glyph, iconElement);
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

        if (_isCapsulePreviewMode)
        {
            AddPromptChip(
                _loc.T("CustomWidget.Workshop.Chip.CapsuleAction.Label"),
                _loc.T("CustomWidget.Workshop.Chip.CapsuleAction.Prompt"),
                "\uE710");
            AddPromptChip(
                _loc.T("CustomWidget.Workshop.Chip.CapsuleProgress.Label"),
                _loc.T("CustomWidget.Workshop.Chip.CapsuleProgress.Prompt"),
                glyph: null, iconElement: CreateCapsuleViewportIcon());
        }

        if (_latestHealthReport is { HasIssues: true })
        {
            foreach (CustomWidgetHealthIssue issue in _latestHealthReport.Issues.Take(2))
            {
                if (!string.IsNullOrWhiteSpace(issue.SuggestedPrompt))
                {
                    string shortIssue = TruncateTextElements(issue.Message, 26);
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
                string shortText = TruncateTextElements(s, 24);
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

        _suggestionChipsPanel.Visibility = _suggestionChipsPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _suggestionChipsPanel.IsHitTestVisible = !IsAnyStudioJobRunning;
        _suggestionChipsPanel.Opacity = IsAnyStudioJobRunning ? 0.45 : 1.0;
    }

    private void RefreshCoCreationStream()
    {
        if (_chatStreamPanel is null)
        {
            return;
        }

        _chatStreamPanel.Children.Clear();
        _chatStreamPanel.Padding = new Thickness(0, 4, 8, 14);

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
                        IsEnabled = !IsAnyStudioJobRunning,
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
                    chip.IsEnabled = !IsAnyStudioJobRunning;
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

            Border activeWaitingCard = _waitingKind switch
            {
                WaitingKind.Direct => BuildInPlaceDirectStreamingCard(),
                WaitingKind.AgentMcp => BuildInPlaceAgentMcpWaitingCard(),
                _ => BuildInPlaceWebAiWaitingCard()
            };
            activeWaitingCard.SizeChanged += (_, e) =>
            {
                if (!_isInPlaceGenerating || !_chatStickToBottom)
                {
                    return;
                }

                if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5)
                {
                    QueueStickyScrollToBottomChat();
                }
            };
            _chatStreamPanel.Children.Add(activeWaitingCard);
        }

        if (_chatStickToBottom)
        {
            QueueStickyScrollToBottomChat();
        }
    }

    private Border BuildInPlaceDirectStreamingCard()
    {
        var column = new StackPanel { Spacing = 10 };

        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleLeft = new Grid { ColumnSpacing = 9, VerticalAlignment = VerticalAlignment.Center };
        titleLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleLeft.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 17,
            Height = 17,
            VerticalAlignment = VerticalAlignment.Center
        });
        _directStageTitleText = new TextBlock
        {
            Text = FormatDirectStageWithDetectedName(
                ResolveDirectStageTitle(_inFlightStage, _directActiveProviderName),
                _inFlightDetectedWidgetName),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_directStageTitleText, 1);
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

        // Friendly 3-step indicator
        var stepsRow = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        _directStep1Pill = CreateStreamStepPill("1 · " + _loc.T("CustomWidget.Workshop.Stream.StepUnderstand"));
        _directStep2Pill = CreateStreamStepPill("2 · " + _loc.T("CustomWidget.Workshop.Stream.StepAssemble"));
        _directStep3Pill = CreateStreamStepPill("3 · " + _loc.T("CustomWidget.Workshop.Stream.StepValidate"));
        stepsRow.Children.Add(_directStep1Pill);
        stepsRow.Children.Add(_directStep2Pill);
        stepsRow.Children.Add(_directStep3Pill);
        UpdateDirectStepPills(_inFlightStage);
        column.Children.Add(stepsRow);

        // Collapsible Reasoning / Thinking summary card (collapsed by default with 1-line live streaming ticker)
        var thinkingInner = new StackPanel { Spacing = 5 };
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

        _directThinkingCollapsedPreviewText = new TextBlock
        {
            FontSize = 11.5,
            Height = 18,
            MaxLines = 1,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
            IsTextSelectionEnabled = false,
            Visibility = _directThinkingCollapsedByUser ? Visibility.Visible : Visibility.Collapsed
        };
        thinkingInner.Children.Add(_directThinkingCollapsedPreviewText);

        _directThinkingText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false
        };
        _directThinkingScroller = new ScrollViewer
        {
            Height = 152,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = _directThinkingCollapsedByUser ? Visibility.Collapsed : Visibility.Visible,
            Content = _directThinkingText
        };
        _directThinkingScroller.ViewChanged += (s, _) =>
        {
            if (_programmaticThinkingScrollDepth > 0 || s is not ScrollViewer sv)
            {
                return;
            }

            _thinkingStickToBottom = (sv.ScrollableHeight - sv.VerticalOffset) <= 20.0;
        };
        AttachNestedScrollerWheelHandoff(_directThinkingScroller, v => _thinkingStickToBottom = v);
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
            if (_directThinkingCollapsedPreviewText is not null)
            {
                _directThinkingCollapsedPreviewText.Visibility = _directThinkingCollapsedByUser
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            toggleThinkBtn.Content = _loc.T(_directThinkingCollapsedByUser
                ? "CustomWidget.Workshop.Stream.Expand"
                : "CustomWidget.Workshop.Stream.Collapse");
            if (!_directThinkingCollapsedByUser)
            {
                _thinkingStickToBottom = true;
                QueueStickyScrollToBottomThinking();
            }
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

        // Live JSON Stream well (expanded by default; toggled via </> button)
        var streamInner = new StackPanel { Spacing = 5 };
        var streamHeaderRow = new Grid { ColumnSpacing = 8 };
        streamHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        streamHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        streamHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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

        var copyStreamBtn = new Button
        {
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 11 }
        };
        ToolTipService.SetToolTip(copyStreamBtn, _loc.T("CustomWidget.Workshop.CopyManifest"));
        copyStreamBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_directStreamText?.Text))
            {
                SetClipboardText(_directStreamText.Text);
            }
        };
        Grid.SetColumn(copyStreamBtn, 2);
        streamHeaderRow.Children.Add(copyStreamBtn);
        streamInner.Children.Add(streamHeaderRow);

        _directStreamText = new TextBlock
        {
            Text = _loc.Format("CustomWidget.Workshop.Direct.Caption", _directActiveProviderName),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 11.5,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false
        };
        _directStreamScroller = new ScrollViewer
        {
            Height = 188,
            Padding = new Thickness(2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _directStreamText
        };
        _directStreamScroller.ViewChanged += (s, _) =>
        {
            if (_programmaticStreamScrollDepth > 0 || s is not ScrollViewer sv)
            {
                return;
            }

            _streamStickToBottom = (sv.ScrollableHeight - sv.VerticalOffset) <= 20.0;
        };
        AttachNestedScrollerWheelHandoff(_directStreamScroller, v => _streamStickToBottom = v);
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
                if (_showRawStreamCodeInSession)
                {
                    _streamStickToBottom = true;
                    QueueStickyScrollToBottomStream();
                }
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
            CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(14, 11, 14, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
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
            AiGenerationStage.Connecting or AiGenerationStage.Thinking or AiGenerationStage.InspectingCapabilities or AiGenerationStage.ProbingApi => 1,
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
        pill.BorderThickness = new Thickness(isActive ? 0 : 1);
        pill.BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush");
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

    private IReadOnlyList<string> ResolveActiveBilingualCultureCodes() =>
        CustomWidgetLocaleCatalog.ResolveBilingualCultureCodes(
            _loc.CurrentCultureName,
            CurrentLocales?.Meta?.BaseLocale);

    private string FormatBilingualSyncLabel(bool shortLabel)
    {
        IReadOnlyList<string> pair = ResolveActiveBilingualCultureCodes();
        bool isZhEnPair = pair.Count == 2 &&
            ((pair[0].StartsWith("zh-", StringComparison.OrdinalIgnoreCase) &&
              string.Equals(pair[1], "en-US", StringComparison.OrdinalIgnoreCase)) ||
             (string.Equals(pair[0], "en-US", StringComparison.OrdinalIgnoreCase) &&
              pair[1].StartsWith("zh-", StringComparison.OrdinalIgnoreCase)));
        if (isZhEnPair)
        {
            return _loc.T(shortLabel
                ? "CustomWidget.Workshop.I18n.Sync2Short"
                : "CustomWidget.Workshop.More.I18nSync2");
        }

        string first = string.Equals(pair[0], "en-US", StringComparison.OrdinalIgnoreCase)
            ? "EN"
            : CustomWidgetLocaleCatalog.GetLocaleInfo(pair[0]).NativeName;
        string second = string.Equals(pair[1], "en-US", StringComparison.OrdinalIgnoreCase)
            ? "EN"
            : CustomWidgetLocaleCatalog.GetLocaleInfo(pair[1]).NativeName;
        return _loc.Format(
            shortLabel
                ? "CustomWidget.Workshop.I18n.SyncBilingualPairFormat"
                : "CustomWidget.Workshop.More.I18nSyncBilingualPairFormat",
            first,
            second);
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

        bool isSameTabFollowUp = _isWebAiSameTabFollowUp && !_isWebAiRepairFlow && !copyOnly;
        string title = _isWebAiRepairFlow
            ? _loc.T("CustomWidget.Workshop.Web.RepairTitle")
            : copyOnly
                ? _loc.T("CustomWidget.Workshop.Web.TitleCopyOnly")
                : isSameTabFollowUp
                    ? _loc.Format("CustomWidget.Workshop.Web.FollowUpTitle", preset.DisplayName)
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
        string step1Text = copyOnly || _isWebAiRepairFlow
            ? _loc.T("CustomWidget.Workshop.Web.Step1CopyOnly")
            : isSameTabFollowUp
                ? _loc.Format("CustomWidget.Workshop.Web.FollowUpStep1", preset.DisplayName)
                : _loc.T("CustomWidget.Workshop.Web.Step1");
        steps.Children.Add(CreateStepRow("1", step1Text));
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
                string? promptToCopy = _lastStandaloneWebAiPrompt ?? _lastSelfCopiedText;
                if (!string.IsNullOrEmpty(promptToCopy))
                {
                    SetClipboardText(promptToCopy);
                }

                _openedWebAiTargetIdInSession = preset.Id;
                _isWebAiSameTabFollowUp = false;
                OpenWebAi(preset);
                RefreshCoCreationStream();
            };
            actions.Children.Add(reopen);

            if (isSameTabFollowUp && !string.IsNullOrEmpty(_lastStandaloneWebAiPrompt))
            {
                var copyFullBtn = new Button
                {
                    Height = 30,
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(6),
                    Content = CreateSmallIconLabel("\uE8C8", _loc.T("CustomWidget.Workshop.Web.CopyFullPrompt"))
                };
                copyFullBtn.Click += (_, _) =>
                {
                    if (!string.IsNullOrEmpty(_lastStandaloneWebAiPrompt))
                    {
                        SetClipboardText(_lastStandaloneWebAiPrompt);
                    }
                };
                actions.Children.Add(copyFullBtn);
            }
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
                _openedWebAiTargetIdInSession = captured.Id;
                _isWebAiSameTabFollowUp = false;
                string? promptToCopy = _lastStandaloneWebAiPrompt ?? _lastSelfCopiedText;
                if (!string.IsNullOrEmpty(promptToCopy))
                {
                    SetClipboardText(promptToCopy);
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
            CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(14, 11, 14, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Background = ThemeBrush("LayerFillColorAltBrush"),
            Child = column
        };
    }

    private Border BuildInPlaceAgentMcpWaitingCard()
    {
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
                Glyph = "\uE756",
                FontSize = 14,
                Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush")
            }
        });
        titleHeader.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.AgentMcp.WaitingTitle"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        column.Children.Add(titleHeader);

        var steps = new StackPanel { Spacing = 8 };
        steps.Children.Add(CreateStepRow("1", _loc.T("CustomWidget.Workshop.AgentMcp.Step1")));
        steps.Children.Add(CreateStepRow("2", _loc.T("CustomWidget.Workshop.AgentMcp.Step2")));
        steps.Children.Add(CreateStepRow("\u2713", _loc.T("CustomWidget.Workshop.AgentMcp.Step3")));
        column.Children.Add(steps);

        var waitingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        waitingRow.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
        waitingRow.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.AgentMcp.WaitingStatus"),
            Style = TextStyle("CaptionTextBlockStyle"),
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        column.Children.Add(waitingRow);

        var actions = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        string recopyLabel = _loc.T("CustomWidget.Workshop.AgentMcp.RecopyPrompt");
        var recopyBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            Style = AccentButtonStyle(),
            Content = CreateSmallIconLabel("\uE8C8", recopyLabel)
        };
        recopyBtn.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(_lastSelfCopiedText))
            {
                SetClipboardText(_lastSelfCopiedText);
                recopyBtn.Content = "✓ " + _loc.T("CustomWidget.Native.Dialog.CopiedButton");
                await Task.Delay(1500);
                recopyBtn.Content = CreateSmallIconLabel("\uE8C8", recopyLabel);
            }
        };
        actions.Children.Add(recopyBtn);

        var connectBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Content = CreateSmallIconLabel("\uE943", _loc.T("CustomWidget.Workshop.Composer.QuickSetupMcp"))
        };
        connectBtn.Click += async (_, _) => await ShowAgentMcpQuickConnectDialogAsync(RefreshCoCreationStream);
        actions.Children.Add(connectBtn);

        var manualPasteBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Content = CreateSmallIconLabel("\uE77F", _loc.T("CustomWidget.Workshop.Web.ManualPaste"))
        };
        manualPasteBtn.Click += async (_, _) => await PasteReplyManuallyAsync();
        actions.Children.Add(manualPasteBtn);

        var guideBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Content = CreateSmallIconLabel("\uE897", _loc.T("CustomWidget.Workshop.GuideButton"))
        };
        guideBtn.Click += (_, _) => StartChannelStepTour(ResolveEffectiveStudioChannel(out _));
        actions.Children.Add(guideBtn);

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
            CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(14, 11, 14, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Background = ThemeBrush("LayerFillColorAltBrush"),
            Child = column
        };
    }

    private void StopOnboardingAnimationTimer()
    {
        if (_onboardingAnimTimer is not null)
        {
            _onboardingAnimTimer.Stop();
            _onboardingAnimTimer = null;
        }
    }

    private void CloseOnboardingOverlay()
    {
        StopOnboardingAnimationTimer();
        _activeOnboardingOverlayKind = OnboardingOverlayKind.None;
        StudioOnboardingTeachingTip.IsOpen = false;
        StudioOnboardingHost.Children.Clear();
        StudioOnboardingHost.Visibility = Visibility.Collapsed;
        // 用户未做选择就关闭弹窗：不要留一个待生成意图，避免后续切换通道时误触发。
        _pendingChannelSelectionIdea = null;
    }

    private void ApplySelectedStudioChannel(StudioAiChannelKind channel)
    {
        _preferWebAiInSession = false;
        string prefValue = channel switch
        {
            StudioAiChannelKind.DirectApi => CustomWidgetStudioPreferencesStore.ChannelDirect,
            StudioAiChannelKind.AgentMcp => CustomWidgetStudioPreferencesStore.ChannelAgentMcp,
            _ => CustomWidgetStudioPreferencesStore.ChannelWeb
        };
        _prefs.SetPreferredStudioChannel(prefValue);
        _refreshActiveChannelSelector?.Invoke();
        RefreshInPlaceGenerationChrome(refreshStream: false);

        // 选择弹窗是被「开始生成」唤起的：确认后继续原来的生成意图，不要求用户再点一次。
        if (!string.IsNullOrWhiteSpace(_pendingChannelSelectionIdea))
        {
            string idea = _pendingChannelSelectionIdea;
            _pendingChannelSelectionIdea = null;
            _ = BeginGenerationAsync(idea);
        }
    }

    private void ShowAgentMcpOnboardingTour(bool forceShow = false)
    {
        if (_isClosed || (!forceShow && _prefs.HasSeenAgentMcpGuide))
        {
            return;
        }

        int runId = ++_onboardingTourRunId;
        _ = OpenChannelSelectionModalAsync(runId);
    }

    private sealed class OnboardingChannelSceneRefs
    {
        public required Grid Root { get; init; }
        public required Border LeftCard { get; init; }
        public required Border CenterBadge { get; init; }
        public required Border ConduitTrack { get; init; }
        public required Border ConduitPulse { get; init; }
        public required Border RightCard { get; init; }
        public required Border Step0FocusChip { get; init; }
        public required Border Step1FocusChip { get; init; }
        public required Border Step2FocusChip { get; init; }
        public required UIElement RightInnerContent { get; init; }
    }

    private async Task OpenChannelSelectionModalAsync(int runId)
    {
        await Task.Delay(40);
        if (_isClosed || runId != _onboardingTourRunId)
        {
            return;
        }

        StopOnboardingAnimationTimer();
        _activeOnboardingOverlayKind = OnboardingOverlayKind.ChannelModal;
        StudioOnboardingTeachingTip.IsOpen = false;
        StudioOnboardingHost.Children.Clear();
        StudioOnboardingHost.Visibility = Visibility.Visible;

        StudioAiChannelKind selectedChannel = ResolveEffectiveStudioChannel(out _);
        StudioAiChannelKind? hoveredCard = null;
        int activeStep = 0;
        int? hoveredStep = null;

        static Windows.UI.Color WithAlpha(Windows.UI.Color c, byte a) =>
            Windows.UI.Color.FromArgb(a, c.R, c.G, c.B);

        static Windows.UI.Color BlendOverWhite(Windows.UI.Color c, double amount)
        {
            double clamped = Math.Clamp(amount, 0.0, 1.0);
            byte r = (byte)Math.Round(255 * (1.0 - clamped) + c.R * clamped);
            byte g = (byte)Math.Round(255 * (1.0 - clamped) + c.G * clamped);
            byte b = (byte)Math.Round(255 * (1.0 - clamped) + c.B * clamped);
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }

        bool IsLight() => StudioExplicitTheme == ElementTheme.Light;

        Windows.UI.Color GetChannelAccent(StudioAiChannelKind kind)
        {
            bool light = IsLight();
            return kind switch
            {
                StudioAiChannelKind.DirectApi => light
                    ? Windows.UI.Color.FromArgb(255, 2, 132, 199)
                    : Windows.UI.Color.FromArgb(255, 56, 189, 248),
                StudioAiChannelKind.AgentMcp => light
                    ? Windows.UI.Color.FromArgb(255, 79, 70, 229)
                    : Windows.UI.Color.FromArgb(255, 129, 140, 248),
                _ => light
                    ? Windows.UI.Color.FromArgb(255, 13, 148, 136)
                    : Windows.UI.Color.FromArgb(255, 45, 212, 191)
            };
        }

        string GetChannelShortTitle(StudioAiChannelKind kind) => kind switch
        {
            StudioAiChannelKind.DirectApi => _loc.T("CustomWidget.Workshop.Onboarding.ModeDirectTitle"),
            StudioAiChannelKind.AgentMcp => _loc.T("CustomWidget.Workshop.Onboarding.ModeAgentTitle"),
            _ => _loc.T("CustomWidget.Workshop.Onboarding.ModeWebTitle")
        };

        (string Badge, string Tagline, string Summary, string Glyph) GetChannelMeta(StudioAiChannelKind kind) => kind switch
        {
            StudioAiChannelKind.DirectApi => (
                _loc.T("CustomWidget.Workshop.Onboarding.ModeDirectBadge"),
                _loc.T("CustomWidget.Workshop.Onboarding.ModeDirectDesc"),
                _loc.T("CustomWidget.Workshop.Onboarding.ModeDirectHint"),
                "\uE945"),
            StudioAiChannelKind.AgentMcp => (
                _loc.T("CustomWidget.Workshop.Onboarding.ModeAgentBadge"),
                _loc.T("CustomWidget.Workshop.Onboarding.ModeAgentDesc"),
                _loc.T("CustomWidget.Workshop.Onboarding.ModeAgentHint"),
                "\uE943"),
            _ => (
                _loc.T("CustomWidget.Workshop.Onboarding.ModeWebBadge"),
                _loc.T("CustomWidget.Workshop.Onboarding.ModeWebDesc"),
                _loc.T("CustomWidget.Workshop.Onboarding.ModeWebHint"),
                "\uE774")
        };

        static string StripLeadingStepNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            ReadOnlySpan<char> span = text.AsSpan().TrimStart();
            if (span.Length >= 3 && char.IsDigit(span[0]) && span[1] == '.' && span[2] == ' ')
            {
                return span[3..].ToString();
            }

            return text;
        }

        (string Step1, string Step2, string Step3) GetChannelSteps(StudioAiChannelKind kind) => kind switch
        {
            StudioAiChannelKind.DirectApi => (
                StripLeadingStepNumber(_loc.T("CustomWidget.Workshop.Onboarding.ModeDirectAnim1")),
                StripLeadingStepNumber(_loc.T("CustomWidget.Workshop.Onboarding.ModeDirectAnim2")),
                StripLeadingStepNumber(_loc.T("CustomWidget.Workshop.Onboarding.ModeDirectAnim3"))),
            StudioAiChannelKind.AgentMcp => (
                StripLeadingStepNumber(_loc.T("CustomWidget.Workshop.Onboarding.ModeAgentAnim1")),
                StripLeadingStepNumber(_loc.T("CustomWidget.Workshop.Onboarding.ModeAgentAnim2")),
                StripLeadingStepNumber(_loc.T("CustomWidget.Workshop.Onboarding.ModeAgentAnim3"))),
            _ => (
                StripLeadingStepNumber(_loc.T("CustomWidget.Workshop.Onboarding.ModeWebAnim1")),
                StripLeadingStepNumber(_loc.T("CustomWidget.Workshop.Onboarding.ModeWebAnim2")),
                StripLeadingStepNumber(_loc.T("CustomWidget.Workshop.Onboarding.ModeWebAnim3")))
        };

        void ConfirmAndClose()
        {
            string channelWire = selectedChannel switch
            {
                StudioAiChannelKind.DirectApi => CustomWidgetStudioPreferencesStore.ChannelDirect,
                StudioAiChannelKind.AgentMcp => CustomWidgetStudioPreferencesStore.ChannelAgentMcp,
                _ => CustomWidgetStudioPreferencesStore.ChannelWeb
            };
            bool isFirstTimeForChannel = !_prefs.HasSeenChannelStepTour(channelWire);
            bool isUnconfiguredDirectApi = selectedChannel == StudioAiChannelKind.DirectApi &&
                !DeskBoxAiService.Shared.HasAnyConfiguredProvider();

            _prefs.MarkAgentMcpGuideSeen();

            if (isFirstTimeForChannel || isUnconfiguredDirectApi)
            {
                if (isUnconfiguredDirectApi)
                {
                    _pendingChannelSelectionIdea = null;
                }

                if (string.IsNullOrWhiteSpace(_pendingChannelSelectionIdea))
                {
                    StartChannelStepTour(selectedChannel);
                    return;
                }
            }

            ApplySelectedStudioChannel(selectedChannel);
            CloseOnboardingOverlay();
        }

        void CancelAndClose()
        {
            if (!string.IsNullOrWhiteSpace(_prefs.PreferredStudioChannel))
            {
                _prefs.MarkAgentMcpGuideSeen();
            }

            CloseOnboardingOverlay();
        }

        static RadialGradientBrush CreateRadialGlowBrush(Windows.UI.Color accent, byte alpha)
        {
            var brush = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.5),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            brush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = WithAlpha(accent, alpha) });
            brush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = WithAlpha(accent, 0) });
            return brush;
        }

        Border CreateWindowDot(byte alpha = 64) => new()
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(IsLight()
                ? Windows.UI.Color.FromArgb((byte)Math.Min(255, alpha + 28), 100, 116, 139)
                : Windows.UI.Color.FromArgb(alpha, 148, 163, 184))
        };

        Windows.UI.Color NeutralSkelColor(byte alpha) => IsLight()
            ? Windows.UI.Color.FromArgb(alpha, 100, 116, 139)
            : Windows.UI.Color.FromArgb(alpha, 148, 163, 184);

        static Border CreateSkeletonBar(double width, double height, Windows.UI.Color color, double cornerRadius = 3) => new()
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(cornerRadius),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(color)
        };

        OnboardingChannelSceneRefs BuildChannelDiorama(StudioAiChannelKind kind)
        {
            bool lightScene = IsLight();
            Windows.UI.Color channelAccent = GetChannelAccent(kind);
            var root = new Grid
            {
                Width = 576,
                Height = 136,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(222) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(222) });

            var leftCard = new Border
            {
                Height = 124,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(leftCard, 0);

            var conduitGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 48
            };
            var conduitTrack = new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var conduitPulse = new Border
            {
                Width = 28,
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0
            };
            string centerGlyph = kind switch
            {
                StudioAiChannelKind.DirectApi => "\uE945",
                StudioAiChannelKind.AgentMcp => "\uE943",
                _ => "\uE8C8"
            };
            var centerBadge = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon
                {
                    Glyph = centerGlyph,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            conduitGrid.Children.Add(conduitTrack);
            conduitGrid.Children.Add(conduitPulse);
            conduitGrid.Children.Add(centerBadge);
            Grid.SetColumn(conduitGrid, 1);

            var rightCard = new Border
            {
                Height = 124,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13, 10, 13, 10),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(rightCard, 2);

            Border step0Chip;
            Border step1Chip;
            Border step2Chip;
            UIElement rightBody;

            if (kind == StudioAiChannelKind.DirectApi)
            {
                var leftStack = new StackPanel { Spacing = 8 };
                var topChrome = new Grid();
                topChrome.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                topChrome.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                topChrome.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                dots.Children.Add(CreateWindowDot());
                dots.Children.Add(CreateWindowDot());
                topChrome.Children.Add(dots);

                step0Chip = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 2),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = "API Key ✓",
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                Grid.SetColumn(step0Chip, 2);
                topChrome.Children.Add(step0Chip);
                leftStack.Children.Add(topChrome);

                var promptRow = new Grid { ColumnSpacing = 8 };
                promptRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                promptRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var promptLines = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                promptLines.Children.Add(CreateSkeletonBar(116, 6, NeutralSkelColor(150)));
                promptLines.Children.Add(CreateSkeletonBar(76, 6, NeutralSkelColor(100)));
                promptRow.Children.Add(promptLines);

                var sendDot = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(WithAlpha(channelAccent, (byte)(lightScene ? 236 : 220))),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new FontIcon
                    {
                        Glyph = "\uE74A",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(lightScene
                            ? Microsoft.UI.Colors.White
                            : Windows.UI.Color.FromArgb(255, 10, 18, 28)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Grid.SetColumn(sendDot, 1);
                promptRow.Children.Add(sendDot);

                step1Chip = new Border
                {
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(10, 9, 8, 9),
                    BorderThickness = new Thickness(1),
                    Child = promptRow
                };
                leftStack.Children.Add(step1Chip);

                var providerPills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (string label in new[] { "DeepSeek", "OpenAI", "Claude" })
                {
                    providerPills.Children.Add(new TextBlock
                    {
                        Text = label,
                        FontSize = 9.5,
                        Foreground = ThemeBrush("TextFillColorTertiaryBrush")
                    });
                }
                leftStack.Children.Add(providerPills);
                leftCard.Child = leftStack;

                var rightStack = new StackPanel { Spacing = 9 };
                var widgetHeader = new Grid();
                widgetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                widgetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                widgetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var weatherRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
                weatherRow.Children.Add(new FontIcon
                {
                    Glyph = "\uE706",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(channelAccent)
                });
                weatherRow.Children.Add(new TextBlock
                {
                    Text = "24°",
                    FontSize = 19,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeBrush("TextFillColorPrimaryBrush")
                });
                widgetHeader.Children.Add(weatherRow);

                step2Chip = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 2),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "● LIVE",
                        FontSize = 9.5,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                Grid.SetColumn(step2Chip, 2);
                widgetHeader.Children.Add(step2Chip);
                rightStack.Children.Add(widgetHeader);

                var bentoGrid = new Grid { ColumnSpacing = 8 };
                bentoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                bentoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var miniTileA = new Border
                {
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8, 8, 8, 8),
                    Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            CreateSkeletonBar(52, 5, NeutralSkelColor(140)),
                            CreateSkeletonBar(74, 6, WithAlpha(channelAccent, (byte)(lightScene ? 205 : 220)))
                        }
                    }
                };
                var miniTileB = new Border
                {
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8, 8, 8, 8),
                    Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            CreateSkeletonBar(64, 5, WithAlpha(channelAccent, (byte)(lightScene ? 165 : 170))),
                            CreateSkeletonBar(48, 5, NeutralSkelColor(115))
                        }
                    }
                };
                Grid.SetColumn(miniTileB, 1);
                bentoGrid.Children.Add(miniTileA);
                bentoGrid.Children.Add(miniTileB);
                rightBody = bentoGrid;
                rightStack.Children.Add(bentoGrid);
                rightCard.Child = rightStack;
            }
            else if (kind == StudioAiChannelKind.AgentMcp)
            {
                var leftStack = new StackPanel { Spacing = 7 };
                var ideHeader = new Grid();
                ideHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                ideHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                ideHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                dots.Children.Add(CreateWindowDot());
                dots.Children.Add(CreateWindowDot());
                dots.Children.Add(CreateWindowDot());
                ideHeader.Children.Add(dots);

                step0Chip = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 2),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = "MCP ✓",
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                Grid.SetColumn(step0Chip, 2);
                ideHeader.Children.Add(step0Chip);
                leftStack.Children.Add(ideHeader);

                leftStack.Children.Add(new TextBlock
                {
                    Text = "> deskbox --mcp",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10.5,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush")
                });

                step1Chip = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 5, 8, 5),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = "⚡ upsert_widget",
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 10.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeBrush("TextFillColorPrimaryBrush")
                    }
                };
                leftStack.Children.Add(step1Chip);
                leftStack.Children.Add(CreateSkeletonBar(108, 5, NeutralSkelColor(100)));
                leftCard.Child = leftStack;

                var rightStack = new StackPanel { Spacing = 9 };
                var telemetryHeader = new Grid();
                telemetryHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                telemetryHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                telemetryHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                telemetryHeader.Children.Add(new TextBlock
                {
                    Text = "SYS · 28%",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeBrush("TextFillColorPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });

                step2Chip = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 2),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "✓ Staged",
                        FontSize = 9.5,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                Grid.SetColumn(step2Chip, 2);
                telemetryHeader.Children.Add(step2Chip);
                rightStack.Children.Add(telemetryHeader);

                var barsRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Height = 52,
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                double[] barHeights = [20, 34, 26, 44, 30, 38, 24];
                for (int b = 0; b < barHeights.Length; b++)
                {
                    byte barAlpha = (byte)(b % 2 == 0 ? 225 : 155);
                    barsRow.Children.Add(new Border
                    {
                        Width = 18,
                        Height = barHeights[b],
                        CornerRadius = new CornerRadius(4),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Background = new SolidColorBrush(WithAlpha(channelAccent, barAlpha))
                    });
                }
                rightBody = barsRow;
                rightStack.Children.Add(barsRow);
                rightCard.Child = rightStack;
            }
            else
            {
                var leftStack = new StackPanel { Spacing = 7 };
                step0Chip = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 3, 8, 3),
                    BorderThickness = new Thickness(1),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            new FontIcon { Glyph = "\uE774", FontSize = 10 },
                            new TextBlock
                            {
                                Text = "chat · deepseek",
                                FontFamily = new FontFamily("Consolas"),
                                FontSize = 10,
                                FontWeight = FontWeights.SemiBold
                            }
                        }
                    }
                };
                leftStack.Children.Add(step0Chip);

                leftStack.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    CornerRadius = new CornerRadius(7, 7, 2, 7),
                    Padding = new Thickness(9, 5, 9, 5),
                    Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                    Child = CreateSkeletonBar(78, 5, NeutralSkelColor(145))
                });

                var replyRow = new Grid { ColumnSpacing = 8 };
                replyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                replyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                replyRow.Children.Add(new TextBlock
                {
                    Text = "{ \"widget\": ... }",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeBrush("TextFillColorPrimaryBrush")
                });
                var copyMiniIcon = new FontIcon
                {
                    Glyph = "\uE8C8",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(channelAccent)
                };
                Grid.SetColumn(copyMiniIcon, 1);
                replyRow.Children.Add(copyMiniIcon);

                step1Chip = new Border
                {
                    CornerRadius = new CornerRadius(7, 7, 7, 2),
                    Padding = new Thickness(9, 6, 9, 6),
                    BorderThickness = new Thickness(1),
                    Child = replyRow
                };
                leftStack.Children.Add(step1Chip);
                leftCard.Child = leftStack;

                var rightStack = new StackPanel { Spacing = 8 };
                var timerHeader = new Grid();
                timerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                timerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                timerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                timerHeader.Children.Add(new FontIcon
                {
                    Glyph = "\uE916",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(channelAccent),
                    VerticalAlignment = VerticalAlignment.Center
                });

                step2Chip = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 2),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "✓ Auto",
                        FontSize = 9.5,
                        FontWeight = FontWeights.SemiBold
                    }
                };
                Grid.SetColumn(step2Chip, 2);
                timerHeader.Children.Add(step2Chip);
                rightStack.Children.Add(timerHeader);

                var focusBody = new StackPanel { Spacing = 8 };
                focusBody.Children.Add(new TextBlock
                {
                    Text = "25:00",
                    FontSize = 22,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeBrush("TextFillColorPrimaryBrush")
                });
                focusBody.Children.Add(new Border
                {
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                    Child = new Border
                    {
                        Width = 132,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        CornerRadius = new CornerRadius(3),
                        Background = new SolidColorBrush(WithAlpha(channelAccent, (byte)(lightScene ? 215 : 230)))
                    }
                });
                rightBody = focusBody;
                rightStack.Children.Add(focusBody);
                rightCard.Child = rightStack;
            }

            root.Children.Add(leftCard);
            root.Children.Add(conduitGrid);
            root.Children.Add(rightCard);

            return new OnboardingChannelSceneRefs
            {
                Root = root,
                LeftCard = leftCard,
                CenterBadge = centerBadge,
                ConduitTrack = conduitTrack,
                ConduitPulse = conduitPulse,
                RightCard = rightCard,
                Step0FocusChip = step0Chip,
                Step1FocusChip = step1Chip,
                Step2FocusChip = step2Chip,
                RightInnerContent = rightBody
            };
        }

        var rootOverlay = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        var modalScrim = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(IsLight()
                ? Windows.UI.Color.FromArgb(92, 15, 23, 42)
                : Windows.UI.Color.FromArgb(148, 4, 7, 13))
        };
        rootOverlay.Children.Add(modalScrim);

        var contentStack = new StackPanel
        {
            Spacing = 16,
            Padding = new Thickness(26, 22, 26, 20)
        };

        // 1. Clean Header
        var headerTitle = new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Onboarding.Title"),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("TextFillColorPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        contentStack.Children.Add(headerTitle);

        // 2. Interactive Hero Stage (Mini-UI Diorama + Concise Summary + Interactive Step Scrubber)
        var ambientTopLeftGlow = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 340,
            Height = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-60, -50, 0, 0),
            IsHitTestVisible = false
        };
        var stageFocalHalo = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 280,
            Height = 176,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        var sceneSwitchHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var scenes = new Dictionary<StudioAiChannelKind, OnboardingChannelSceneRefs>
        {
            [StudioAiChannelKind.DirectApi] = BuildChannelDiorama(StudioAiChannelKind.DirectApi),
            [StudioAiChannelKind.AgentMcp] = BuildChannelDiorama(StudioAiChannelKind.AgentMcp),
            [StudioAiChannelKind.WebAi] = BuildChannelDiorama(StudioAiChannelKind.WebAi)
        };
        foreach (OnboardingChannelSceneRefs scene in scenes.Values)
        {
            scene.Root.Visibility = Visibility.Collapsed;
            sceneSwitchHost.Children.Add(scene.Root);
        }

        var stageViewportGrid = new Grid
        {
            Height = 182,
            CornerRadius = new CornerRadius(11, 11, 0, 0),
            Padding = new Thickness(18, 12, 18, 10),
            Background = new SolidColorBrush(IsLight()
                ? Windows.UI.Color.FromArgb(255, 238, 243, 250)
                : Windows.UI.Color.FromArgb(84, 14, 18, 26))
        };
        stageViewportGrid.Children.Add(ambientTopLeftGlow);
        stageViewportGrid.Children.Add(stageFocalHalo);
        stageViewportGrid.Children.Add(new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = sceneSwitchHost
        });

        var stageSummaryText = new TextBlock
        {
            FontSize = 13,
            Foreground = ThemeBrush("TextFillColorPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 600
        };

        var stepPillBorders = new Border[3];
        var stepNumBadges = new Border[3];
        var stepNumTexts = new TextBlock[3];
        var stepLabelTexts = new TextBlock[3];
        var stepConnectors = new Border[2];

        var stepsScrubberRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };

        Action<int, bool>? applyStageStepRef = null;
        void RestartAutoStepBeat()
        {
            if (_onboardingAnimTimer is not null)
            {
                _onboardingAnimTimer.Stop();
                _onboardingAnimTimer.Start();
            }
        }

        for (int i = 0; i < 3; i++)
        {
            int capturedStep = i;
            var numText = new TextBlock
            {
                Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stepNumTexts[i] = numText;

            var numBadge = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                VerticalAlignment = VerticalAlignment.Center,
                Child = numText
            };
            stepNumBadges[i] = numBadge;

            var stepLabel = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1
            };
            stepLabelTexts[i] = stepLabel;

            var pillRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                VerticalAlignment = VerticalAlignment.Center
            };
            pillRow.Children.Add(numBadge);
            pillRow.Children.Add(stepLabel);

            var pillBorder = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(9, 5, 12, 5),
                BorderThickness = new Thickness(1),
                IsTapEnabled = true,
                Child = pillRow
            };
            pillBorder.PointerEntered += (_, _) =>
            {
                hoveredStep = capturedStep;
                if (activeStep != capturedStep)
                {
                    activeStep = capturedStep;
                    applyStageStepRef?.Invoke(activeStep, true);
                }
            };
            pillBorder.PointerExited += (_, _) =>
            {
                if (hoveredStep == capturedStep)
                {
                    hoveredStep = null;
                    RestartAutoStepBeat();
                }
            };
            pillBorder.Tapped += (_, e) =>
            {
                e.Handled = true;
                activeStep = capturedStep;
                applyStageStepRef?.Invoke(activeStep, true);
                RestartAutoStepBeat();
            };

            stepPillBorders[i] = pillBorder;
            stepsScrubberRow.Children.Add(pillBorder);

            if (i < 2)
            {
                var connector = new Border
                {
                    Width = 20,
                    Height = 1.5,
                    CornerRadius = new CornerRadius(1),
                    VerticalAlignment = VerticalAlignment.Center
                };
                stepConnectors[i] = connector;
                stepsScrubberRow.Children.Add(connector);
            }
        }

        var stageNarrativeBorder = new Border
        {
            Padding = new Thickness(20, 12, 20, 14),
            CornerRadius = new CornerRadius(0, 0, 11, 11),
            Background = IsLight()
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    stageSummaryText,
                    stepsScrubberRow
                }
            }
        };

        var stageStack = new StackPanel();
        stageStack.Children.Add(stageViewportGrid);
        stageStack.Children.Add(stageNarrativeBorder);

        var heroStageBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Background = ThemeBrush("LayerFillColorAltBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            Child = stageStack
        };
        contentStack.Children.Add(heroStageBorder);

        // 3. Three Compact Horizontal Fluent Selector Cards
        var cardsGrid = new Grid { ColumnSpacing = 12 };
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cardSurfaces = new Dictionary<StudioAiChannelKind, Border>();
        var cardIconPlates = new Dictionary<StudioAiChannelKind, Border>();
        var cardIconGlyphs = new Dictionary<StudioAiChannelKind, FontIcon>();
        var cardBadgePlates = new Dictionary<StudioAiChannelKind, Border>();
        var cardBadgeTexts = new Dictionary<StudioAiChannelKind, TextBlock>();
        var cardPillIndicators = new Dictionary<StudioAiChannelKind, Border>();

        StudioAiChannelKind[] channels =
        [
            StudioAiChannelKind.DirectApi,
            StudioAiChannelKind.AgentMcp,
            StudioAiChannelKind.WebAi
        ];

        void RefreshCardVisuals(bool animateIndicator)
        {
            bool light = IsLight();
            foreach (StudioAiChannelKind kind in channels)
            {
                bool isSelected = kind == selectedChannel;
                bool isHovered = hoveredCard == kind;
                Windows.UI.Color accent = GetChannelAccent(kind);

                Border card = cardSurfaces[kind];
                if (isSelected)
                {
                    var selectedGrad = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0),
                        EndPoint = new Windows.Foundation.Point(0, 1)
                    };
                    selectedGrad.GradientStops.Add(new GradientStop
                    {
                        Offset = 0,
                        Color = light ? BlendOverWhite(accent, 0.11) : WithAlpha(accent, 28)
                    });
                    selectedGrad.GradientStops.Add(new GradientStop
                    {
                        Offset = 1,
                        Color = light ? BlendOverWhite(accent, 0.03) : WithAlpha(accent, 10)
                    });
                    card.Background = selectedGrad;
                    card.BorderBrush = new SolidColorBrush(WithAlpha(accent, (byte)(light ? 164 : 148)));
                }
                else if (isHovered)
                {
                    card.Background = light
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 250, 251, 254))
                        : ThemeBrush("SubtleFillColorSecondaryBrush");
                    card.BorderBrush = light
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x38, 0x00, 0x00, 0x00))
                        : ThemeBrush("ControlStrongStrokeColorDefaultBrush");
                }
                else
                {
                    card.Background = light
                        ? new SolidColorBrush(Microsoft.UI.Colors.White)
                        : ThemeBrush("CardBackgroundFillColorDefaultBrush");
                    card.BorderBrush = light
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x1C, 0x00, 0x00, 0x00))
                        : ThemeBrush("CardStrokeColorDefaultBrush");
                }

                Border iconPlate = cardIconPlates[kind];
                FontIcon iconGlyph = cardIconGlyphs[kind];
                if (isSelected)
                {
                    iconPlate.Background = new SolidColorBrush(WithAlpha(accent, (byte)(light ? 26 : 38)));
                    iconPlate.BorderBrush = new SolidColorBrush(WithAlpha(accent, (byte)(light ? 72 : 92)));
                    iconGlyph.Foreground = new SolidColorBrush(accent);
                }
                else
                {
                    iconPlate.Background = ThemeBrush("SubtleFillColorSecondaryBrush");
                    iconPlate.BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush");
                    iconGlyph.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
                }

                Border badgePlate = cardBadgePlates[kind];
                TextBlock badgeLabel = cardBadgeTexts[kind];
                if (isSelected)
                {
                    badgePlate.Background = new SolidColorBrush(WithAlpha(accent, (byte)(light ? 22 : 34)));
                    badgeLabel.Foreground = new SolidColorBrush(accent);
                }
                else
                {
                    badgePlate.Background = ThemeBrush("SubtleFillColorTertiaryBrush");
                    badgeLabel.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
                }

                Border indicator = cardPillIndicators[kind];
                indicator.Background = new SolidColorBrush(accent);
                FluentMotionHelper.AnimateStageFocus(
                    indicator,
                    scale: isSelected ? 1f : 0.35f,
                    offsetX: 0f,
                    offsetY: 0f,
                    opacity: isSelected ? 1f : 0f,
                    durationMs: animateIndicator ? 220 : 1);
            }
        }

        void ApplyStageStep(int step, bool animate)
        {
            bool light = IsLight();
            Windows.UI.Color accent = GetChannelAccent(selectedChannel);
            OnboardingChannelSceneRefs scene = scenes[selectedChannel];

            ambientTopLeftGlow.Fill = CreateRadialGlowBrush(accent, (byte)(light ? 28 : 42));
            stageFocalHalo.Fill = CreateRadialGlowBrush(accent, (byte)(light ? 34 : 52));

            float haloOffsetX = step switch
            {
                0 => -168f,
                1 => 0f,
                _ => 168f
            };
            FluentMotionHelper.AnimateStageFocus(
                stageFocalHalo,
                scale: 1f,
                offsetX: haloOffsetX,
                offsetY: 0f,
                opacity: 1f,
                durationMs: animate ? 460 : 1);

            Brush neutralCardBg = light
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : ThemeBrush("CardBackgroundFillColorDefaultBrush");
            Brush neutralCardBorder = light
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0x00, 0x00, 0x00))
                : ThemeBrush("CardStrokeColorDefaultBrush");
            var activeCardBorder = new SolidColorBrush(WithAlpha(accent, (byte)(light ? 156 : 160)));
            var activeChipBg = new SolidColorBrush(WithAlpha(accent, (byte)(light ? 26 : 40)));
            var activeChipBorder = new SolidColorBrush(WithAlpha(accent, (byte)(light ? 96 : 124)));
            Brush mutedChipBg = ThemeBrush("SubtleFillColorSecondaryBrush");
            Brush mutedChipBorder = ThemeBrush("DividerStrokeColorDefaultBrush");

            scene.LeftCard.Background = neutralCardBg;
            scene.RightCard.Background = neutralCardBg;
            scene.LeftCard.BorderBrush = step <= 1 ? activeCardBorder : neutralCardBorder;
            scene.RightCard.BorderBrush = step == 2 ? activeCardBorder : neutralCardBorder;

            scene.ConduitTrack.Background = new SolidColorBrush(WithAlpha(accent, (byte)(step == 1 ? 150 : 62)));
            scene.ConduitPulse.Background = new SolidColorBrush(accent);
            scene.CenterBadge.Background = step == 1 ? activeChipBg : neutralCardBg;
            scene.CenterBadge.BorderBrush = step == 1 ? activeCardBorder : neutralCardBorder;
            if (scene.CenterBadge.Child is FontIcon badgeIcon)
            {
                badgeIcon.Foreground = step == 1
                    ? new SolidColorBrush(accent)
                    : ThemeBrush("TextFillColorSecondaryBrush");
            }

            void StyleFocusChip(Border chip, bool focused)
            {
                chip.Background = focused ? activeChipBg : mutedChipBg;
                chip.BorderBrush = focused ? activeChipBorder : mutedChipBorder;
                if (chip.Child is TextBlock tb)
                {
                    tb.Foreground = focused
                        ? new SolidColorBrush(accent)
                        : ThemeBrush("TextFillColorSecondaryBrush");
                }
                else if (chip.Child is StackPanel sp)
                {
                    foreach (UIElement item in sp.Children)
                    {
                        if (item is FontIcon fi)
                        {
                            fi.Foreground = focused
                                ? new SolidColorBrush(accent)
                                : ThemeBrush("TextFillColorSecondaryBrush");
                        }
                        else if (item is TextBlock spTb)
                        {
                            spTb.Foreground = focused
                                ? new SolidColorBrush(accent)
                                : ThemeBrush("TextFillColorSecondaryBrush");
                        }
                    }
                }
            }

            StyleFocusChip(scene.Step0FocusChip, step == 0);
            StyleFocusChip(scene.Step1FocusChip, step == 1);
            StyleFocusChip(scene.Step2FocusChip, step == 2);

            int dur = animate ? 380 : 1;
            if (step == 0)
            {
                FluentMotionHelper.AnimateStageFocus(scene.LeftCard, 1.02f, 0f, -2f, 1.0f, dur);
                FluentMotionHelper.AnimateStageFocus(scene.CenterBadge, 0.96f, 0f, 0f, 0.68f, dur);
                FluentMotionHelper.AnimateStageFocus(scene.RightCard, 0.975f, 0f, 2f, 0.62f, dur);
                FluentMotionHelper.AnimateStageFocus(scene.RightInnerContent, 0.97f, 0f, 2f, 0.58f, dur);
                scene.ConduitPulse.Opacity = 0;
            }
            else if (step == 1)
            {
                FluentMotionHelper.AnimateStageFocus(scene.LeftCard, 1.0f, 0f, 0f, 0.92f, dur);
                FluentMotionHelper.AnimateStageFocus(scene.CenterBadge, 1.10f, 0f, -1f, 1.0f, dur);
                FluentMotionHelper.AnimateStageFocus(scene.RightCard, 0.99f, 0f, 0f, 0.80f, dur);
                FluentMotionHelper.AnimateStageFocus(scene.RightInnerContent, 0.99f, 0f, 0f, 0.78f, dur);
                if (animate)
                {
                    FluentMotionHelper.PlayConduitPulse(scene.ConduitPulse, -44f, 44f, durationMs: 760);
                }
            }
            else
            {
                FluentMotionHelper.AnimateStageFocus(scene.LeftCard, 0.98f, 0f, 1f, 0.66f, dur);
                FluentMotionHelper.AnimateStageFocus(scene.CenterBadge, 0.98f, 0f, 0f, 0.72f, dur);
                FluentMotionHelper.AnimateStageFocus(scene.RightCard, 1.025f, 0f, -3f, 1.0f, dur);
                FluentMotionHelper.AnimateStageFocus(scene.RightInnerContent, 1.0f, 0f, 0f, 1.0f, dur);
                scene.ConduitPulse.Opacity = 0;
            }

            for (int i = 0; i < 3; i++)
            {
                bool isStepActive = i == step;
                Border pill = stepPillBorders[i];
                Border badge = stepNumBadges[i];
                TextBlock num = stepNumTexts[i];
                TextBlock label = stepLabelTexts[i];

                pill.Background = isStepActive
                    ? new SolidColorBrush(WithAlpha(accent, (byte)(light ? 24 : 36)))
                    : ThemeBrush("SubtleFillColorTertiaryBrush");
                pill.BorderBrush = isStepActive
                    ? new SolidColorBrush(WithAlpha(accent, (byte)(light ? 115 : 140)))
                    : ThemeBrush("DividerStrokeColorDefaultBrush");

                badge.Background = isStepActive
                    ? new SolidColorBrush(accent)
                    : ThemeBrush("SubtleFillColorSecondaryBrush");
                num.Foreground = isStepActive
                    ? new SolidColorBrush(light ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 10, 16, 26))
                    : ThemeBrush("TextFillColorSecondaryBrush");
                label.Foreground = isStepActive
                    ? ThemeBrush("TextFillColorPrimaryBrush")
                    : ThemeBrush("TextFillColorSecondaryBrush");

                FluentMotionHelper.AnimateStageFocus(
                    pill,
                    scale: isStepActive ? 1.02f : 1f,
                    offsetX: 0f,
                    offsetY: isStepActive ? -1f : 0f,
                    opacity: 1f,
                    durationMs: animate ? 220 : 1);
            }

            for (int c = 0; c < 2; c++)
            {
                stepConnectors[c].Background = step > c
                    ? new SolidColorBrush(WithAlpha(accent, 140))
                    : ThemeBrush("DividerStrokeColorDefaultBrush");
            }
        }
        applyStageStepRef = ApplyStageStep;

        void SwitchActiveChannel(StudioAiChannelKind nextChannel, bool animateTransition)
        {
            selectedChannel = nextChannel;
            activeStep = 0;

            foreach ((StudioAiChannelKind kind, OnboardingChannelSceneRefs scene) in scenes)
            {
                scene.Root.Visibility = kind == selectedChannel ? Visibility.Visible : Visibility.Collapsed;
            }

            (_, _, string summary, _) = GetChannelMeta(selectedChannel);
            stageSummaryText.Text = summary;

            (string s1, string s2, string s3) = GetChannelSteps(selectedChannel);
            stepLabelTexts[0].Text = s1;
            stepLabelTexts[1].Text = s2;
            stepLabelTexts[2].Text = s3;

            RefreshCardVisuals(animateIndicator: animateTransition);
            ApplyStageStep(activeStep, animate: animateTransition);

            if (animateTransition)
            {
                FluentMotionHelper.PlayEntranceSlideAndFade(scenes[selectedChannel].Root, offsetY: 8f, durationMs: 220);
                FluentMotionHelper.PlayEntranceSlideAndFade(stageSummaryText, offsetY: 4f, durationMs: 180);
                RestartAutoStepBeat();
            }
        }

        for (int idx = 0; idx < channels.Length; idx++)
        {
            StudioAiChannelKind kind = channels[idx];
            (string badgeText, string taglineText, _, string glyph) = GetChannelMeta(kind);

            var iconGlyph = new FontIcon
            {
                Glyph = glyph,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            cardIconGlyphs[kind] = iconGlyph;

            var iconPlate = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = iconGlyph
            };
            cardIconPlates[kind] = iconPlate;

            var badgeLabel = new TextBlock
            {
                Text = badgeText,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold
            };
            cardBadgeTexts[kind] = badgeLabel;

            var badgePlate = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Child = badgeLabel
            };
            cardBadgePlates[kind] = badgePlate;

            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.Children.Add(iconPlate);
            Grid.SetColumn(badgePlate, 2);
            topRow.Children.Add(badgePlate);

            var textStack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 10, 0, 8) };
            textStack.Children.Add(new TextBlock
            {
                Text = GetChannelShortTitle(kind),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("TextFillColorPrimaryBrush"),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new TextBlock
            {
                Text = taglineText,
                FontSize = 11.5,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var pillIndicator = new Border
            {
                Width = 22,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Opacity = 0
            };
            cardPillIndicators[kind] = pillIndicator;

            var cardLayout = new Grid();
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardLayout.Children.Add(topRow);
            Grid.SetRow(textStack, 1);
            cardLayout.Children.Add(textStack);
            Grid.SetRow(pillIndicator, 2);
            cardLayout.Children.Add(pillIndicator);

            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(15, 13, 15, 8),
                BorderThickness = new Thickness(1),
                MinHeight = 104,
                IsTapEnabled = true,
                IsDoubleTapEnabled = true,
                IsTabStop = true,
                UseSystemFocusVisuals = true,
                Child = cardLayout
            };
            AutomationProperties.SetName(cardBorder, GetChannelShortTitle(kind));

            cardBorder.PointerEntered += (_, _) =>
            {
                hoveredCard = kind;
                RefreshCardVisuals(animateIndicator: false);
                FluentMotionHelper.AnimateCardElevation(cardBorder, offsetY: -2f, scale: 1f, durationMs: 150);
            };
            cardBorder.PointerExited += (_, _) =>
            {
                if (hoveredCard == kind)
                {
                    hoveredCard = null;
                }
                RefreshCardVisuals(animateIndicator: false);
                FluentMotionHelper.AnimateCardElevation(cardBorder, offsetY: 0f, scale: 1f, durationMs: 160);
            };
            cardBorder.PointerPressed += (_, _) =>
            {
                FluentMotionHelper.AnimateCardElevation(cardBorder, offsetY: -1f, scale: 0.985f, durationMs: 90);
            };
            cardBorder.PointerReleased += (_, _) =>
            {
                float targetY = hoveredCard == kind ? -2f : 0f;
                FluentMotionHelper.AnimateCardElevation(cardBorder, offsetY: targetY, scale: 1f, durationMs: 140);
            };
            cardBorder.PointerCanceled += (_, _) =>
            {
                FluentMotionHelper.AnimateCardElevation(cardBorder, offsetY: 0f, scale: 1f, durationMs: 140);
            };
            cardBorder.Tapped += (_, e) =>
            {
                e.Handled = true;
                if (selectedChannel != kind)
                {
                    SwitchActiveChannel(kind, animateTransition: true);
                }
            };
            cardBorder.DoubleTapped += (_, e) =>
            {
                e.Handled = true;
                selectedChannel = kind;
                ConfirmAndClose();
            };
            cardBorder.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Space)
                {
                    e.Handled = true;
                    if (selectedChannel != kind)
                    {
                        SwitchActiveChannel(kind, animateTransition: true);
                    }
                }
            };

            cardSurfaces[kind] = cardBorder;
            Grid.SetColumn(cardBorder, idx);
            cardsGrid.Children.Add(cardBorder);
        }

        contentStack.Children.Add(cardsGrid);

        // 4. Clean Balanced Footer (Left subtle tip + Right short buttons)
        var footerGrid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 2, 0, 0) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var footerHintRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        footerHintRow.Children.Add(new FontIcon
        {
            Glyph = "\uE8AB",
            FontSize = 11.5,
            Foreground = ThemeBrush("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        footerHintRow.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Onboarding.Subtitle"),
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        footerGrid.Children.Add(footerHintRow);

        var footerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var cancelBtn = new Button
        {
            MinWidth = 84,
            Height = 34,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(6),
            Content = _loc.T("Common.Cancel")
        };
        cancelBtn.Click += (_, _) => CancelAndClose();
        footerButtons.Children.Add(cancelBtn);

        var confirmBtn = new Button
        {
            MinWidth = 98,
            Height = 34,
            Padding = new Thickness(18, 0, 18, 0),
            CornerRadius = new CornerRadius(6),
            Style = AccentButtonStyle(),
            Content = _loc.T("CustomWidget.Workshop.Onboarding.SkipTourButton")
        };
        confirmBtn.Click += (_, _) => ConfirmAndClose();
        footerButtons.Children.Add(confirmBtn);

        Grid.SetColumn(footerButtons, 1);
        footerGrid.Children.Add(footerButtons);
        contentStack.Children.Add(footerGrid);

        var modalShadow = new ThemeShadow();
        modalShadow.Receivers.Add(modalScrim);
        var modalScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = contentStack
        };

        var modalCard = new Border
        {
            RequestedTheme = StudioExplicitTheme,
            MaxWidth = 740,
            Width = 720,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            CornerRadius = new CornerRadius(14),
            Background = IsLight()
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 246, 248, 251))
                : ThemeBrush("SolidBackgroundFillColorBaseBrush"),
            BorderBrush = IsLight()
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x00, 0x00, 0x00))
                : ThemeBrush("SurfaceStrokeColorFlyoutBrush"),
            BorderThickness = new Thickness(1),
            Shadow = modalShadow,
            Translation = new System.Numerics.Vector3(0, 0, 32),
            Child = modalScroll
        };

        modalCard.ActualThemeChanged += (_, _) =>
        {
            RefreshCardVisuals(animateIndicator: false);
            ApplyStageStep(activeStep, animate: false);
        };

        rootOverlay.Children.Add(modalCard);
        StudioOnboardingHost.Children.Add(rootOverlay);

        rootOverlay.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CancelAndClose();
            }
            else if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                ConfirmAndClose();
            }
            else if (e.Key is VirtualKey.Left or VirtualKey.Right)
            {
                int currentIdx = Array.IndexOf(channels, selectedChannel);
                if (currentIdx >= 0)
                {
                    int delta = e.Key == VirtualKey.Right ? 1 : -1;
                    int nextIdx = (currentIdx + delta + channels.Length) % channels.Length;
                    e.Handled = true;
                    SwitchActiveChannel(channels[nextIdx], animateTransition: true);
                }
            }
        };
        rootOverlay.Tapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, rootOverlay))
            {
                CancelAndClose();
            }
        };

        modalCard.Loaded += (_, _) =>
        {
            if (_isClosed)
            {
                return;
            }

            StopOnboardingAnimationTimer();
            SwitchActiveChannel(selectedChannel, animateTransition: false);
            FluentMotionHelper.PlayEntranceSlideAndFade(modalCard, offsetY: 12f, durationMs: 210);

            _onboardingAnimTimer = DispatcherQueue.CreateTimer();
            _onboardingAnimTimer.Interval = TimeSpan.FromMilliseconds(2400);
            _onboardingAnimTimer.Tick += (_, _) =>
            {
                if (_isClosed || StudioOnboardingHost.Visibility != Visibility.Visible)
                {
                    StopOnboardingAnimationTimer();
                    return;
                }

                if (hoveredStep.HasValue)
                {
                    return;
                }

                activeStep = (activeStep + 1) % 3;
                ApplyStageStep(activeStep, animate: true);
            };
            _onboardingAnimTimer.Start();

            confirmBtn.Focus(FocusState.Programmatic);
        };
    }

    private void StartChannelStepTour(StudioAiChannelKind channel)
    {
        if (_isClosed)
        {
            return;
        }

        string channelWire = channel switch
        {
            StudioAiChannelKind.DirectApi => CustomWidgetStudioPreferencesStore.ChannelDirect,
            StudioAiChannelKind.AgentMcp => CustomWidgetStudioPreferencesStore.ChannelAgentMcp,
            _ => CustomWidgetStudioPreferencesStore.ChannelWeb
        };

        int runId = ++_onboardingTourRunId;
        _prefs.MarkAgentMcpGuideSeen();
        _prefs.MarkChannelStepTourSeen(channelWire);
        ApplySelectedStudioChannel(channel);
        _ = RunChannelStepTourAsync(runId, channel);
    }

    private async Task RunChannelStepTourAsync(int runId, StudioAiChannelKind channel, int initialStep = 1)
    {
        StopOnboardingAnimationTimer();
        StudioOnboardingTeachingTip.IsOpen = false;

        await Task.Delay(90);
        if (_isClosed || runId != _onboardingTourRunId)
        {
            return;
        }

        _activeOnboardingOverlayKind = OnboardingOverlayKind.StepTour;
        _activeStepTourChannel = channel;
        _activeStepTourStep = Math.Clamp(initialStep, 1, 3);

        bool lightTour = StudioExplicitTheme == ElementTheme.Light;
        Windows.UI.Color tourAccent = ThemeBrush("AccentFillColorDefaultBrush") is SolidColorBrush accentBrush
            ? accentBrush.Color
            : (lightTour
                ? Windows.UI.Color.FromArgb(255, 2, 132, 199)
                : Windows.UI.Color.FromArgb(255, 56, 189, 248));
        Windows.UI.Color onTourAccent = (0.2126 * tourAccent.R + 0.7152 * tourAccent.G + 0.0722 * tourAccent.B) > 155
            ? Windows.UI.Color.FromArgb(255, 10, 16, 26)
            : Microsoft.UI.Colors.White;

        StudioOnboardingHost.Children.Clear();
        StudioOnboardingHost.Visibility = Visibility.Visible;

        var overlayCanvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var tourScrim = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(lightTour
                ? Windows.UI.Color.FromArgb(68, 15, 23, 42)
                : Windows.UI.Color.FromArgb(108, 4, 7, 13))
        };

        var rootBackdrop = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        rootBackdrop.Children.Add(tourScrim);
        rootBackdrop.Children.Add(overlayCanvas);
        StudioOnboardingHost.Children.Add(rootBackdrop);

        var spotlightBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(tourAccent),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)(lightTour ? 18 : 26),
                tourAccent.R,
                tourAccent.G,
                tourAccent.B)),
            IsHitTestVisible = false
        };
        overlayCanvas.Children.Add(spotlightBorder);

        var stepBadgeTexts = new TextBlock[3];
        var stepBadgeBorders = new Border[3];
        var stepDotsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        for (int i = 0; i < 3; i++)
        {
            var txt = new TextBlock
            {
                Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stepBadgeTexts[i] = txt;
            var pill = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Child = txt
            };
            stepBadgeBorders[i] = pill;
            stepDotsRow.Children.Add(pill);
        }

        var switchModeBtn = new Button
        {
            Height = 26,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 11,
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            Foreground = ThemeBrush("TextFillColorPrimaryBrush"),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Content = CreateSmallIconLabel("\uE8AB", _loc.T("CustomWidget.Workshop.SwitchModeButton"))
        };
        switchModeBtn.Click += (_, _) => ShowAgentMcpOnboardingTour(forceShow: true);

        var topBar = new Grid { ColumnSpacing = 8 };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topBar.Children.Add(stepDotsRow);
        Grid.SetColumn(switchModeBtn, 1);
        topBar.Children.Add(switchModeBtn);

        var stepTitleBlock = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("TextFillColorPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };

        var stepSubtitleBlock = new TextBlock
        {
            FontSize = 12.5,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19.5
        };

        var skipBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourSkip")
        };
        skipBtn.Click += (_, _) => CloseOnboardingOverlay();

        var primaryBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,
            Style = AccentButtonStyle()
        };

        var bottomBar = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(skipBtn, 1);
        bottomBar.Children.Add(skipBtn);
        Grid.SetColumn(primaryBtn, 2);
        bottomBar.Children.Add(primaryBtn);

        var cardStack = new StackPanel { Spacing = 10 };
        cardStack.Children.Add(topBar);
        cardStack.Children.Add(stepTitleBlock);
        cardStack.Children.Add(stepSubtitleBlock);
        cardStack.Children.Add(bottomBar);

        var coachmarkShadow = new ThemeShadow();
        coachmarkShadow.Receivers.Add(tourScrim);
        var coachmarkCard = new Border
        {
            RequestedTheme = StudioExplicitTheme,
            Width = 392,
            Padding = new Thickness(18, 16, 18, 16),
            CornerRadius = new CornerRadius(12),
            Background = lightTour
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 42, 48)),
            BorderBrush = lightTour
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0x00, 0x00, 0x00))
                : ThemeBrush("SurfaceStrokeColorFlyoutBrush"),
            BorderThickness = new Thickness(1),
            Shadow = coachmarkShadow,
            Translation = new System.Numerics.Vector3(0, 0, 32),
            Child = cardStack
        };
        overlayCanvas.Children.Add(coachmarkCard);

        static FrameworkElement? ResolveLoadedTarget(params FrameworkElement?[] candidates)
        {
            foreach (FrameworkElement? candidate in candidates)
            {
                if (candidate is { IsLoaded: true, Visibility: Visibility.Visible }
                    && candidate.ActualWidth > 0
                    && candidate.ActualHeight > 0)
                {
                    return candidate;
                }
            }

            return null;
        }

        int currentStep = 1;
        FrameworkElement? latestTargetElement = null;

        void PositionSpotlightAndCard(FrameworkElement? target)
        {
            latestTargetElement = target;
            double hostW = StudioOnboardingHost.ActualWidth > 0 ? StudioOnboardingHost.ActualWidth : RootGrid.ActualWidth;
            double hostH = StudioOnboardingHost.ActualHeight > 0 ? StudioOnboardingHost.ActualHeight : RootGrid.ActualHeight;
            if (hostW <= 0) hostW = 960;
            if (hostH <= 0) hostH = 640;

            if (target is not null)
            {
                try
                {
                    GeneralTransform transform = target.TransformToVisual(StudioOnboardingHost);
                    Windows.Foundation.Point topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    double pad = 6;
                    double spotX = Math.Max(8, topLeft.X - pad);
                    double spotY = Math.Max(8, topLeft.Y - pad);
                    double spotW = Math.Min(hostW - spotX - 8, target.ActualWidth + pad * 2);
                    double spotH = Math.Min(220, target.ActualHeight + pad * 2);

                    spotlightBorder.Visibility = Visibility.Visible;
                    spotlightBorder.Width = Math.Max(32, spotW);
                    spotlightBorder.Height = Math.Max(28, spotH);
                    Canvas.SetLeft(spotlightBorder, spotX);
                    Canvas.SetTop(spotlightBorder, spotY);

                    double cardW = coachmarkCard.Width;
                    double cardLeft = Math.Clamp(spotX, 16, Math.Max(16, hostW - cardW - 16));
                    double belowTop = spotY + spotH + 12;
                    double cardTop = belowTop + 216 <= hostH
                        ? belowTop
                        : Math.Max(16, spotY - 216);

                    Canvas.SetLeft(coachmarkCard, cardLeft);
                    Canvas.SetTop(coachmarkCard, cardTop);
                    return;
                }
                catch
                {
                }
            }

            spotlightBorder.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(coachmarkCard, Math.Max(16, (hostW - coachmarkCard.Width) / 2));
            Canvas.SetTop(coachmarkCard, Math.Max(24, (hostH - 220) / 2));
        }

        void RenderStep(int step)
        {
            currentStep = step;
            _activeStepTourStep = step;
            for (int i = 0; i < 3; i++)
            {
                bool active = (i + 1) == step;
                stepBadgeBorders[i].Background = active
                    ? new SolidColorBrush(tourAccent)
                    : new SolidColorBrush(lightTour
                        ? Windows.UI.Color.FromArgb(0x12, 0x00, 0x00, 0x00)
                        : Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                stepBadgeTexts[i].Foreground = active
                    ? new SolidColorBrush(onTourAccent)
                    : ThemeBrush("TextFillColorSecondaryBrush");
            }

            FrameworkElement? composerTarget = _page == StudioPage.Preview ? _refineTextBox : _startIdeaTextBox;
            FrameworkElement? submitTarget = _page == StudioPage.Preview && _refineSubmitButton is not null
                ? _refineSubmitButton
                : _activeComposerSubmitButton;

            FrameworkElement? targetElement;
            if (channel == StudioAiChannelKind.DirectApi)
            {
                if (step == 1)
                {
                    targetElement = ResolveLoadedTarget(_activeQuickSetupButton, _activeChannelSelectorButton);
                    stepTitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Direct1Title");
                    stepSubtitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Direct1Subtitle");
                    primaryBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourNext1");
                    skipBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourSkip");
                    skipBtn.Visibility = Visibility.Visible;
                }
                else if (step == 2)
                {
                    targetElement = ResolveLoadedTarget(_activeCapabilityPickerButton, composerTarget, _activeChannelSelectorButton);
                    stepTitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Direct2Title");
                    stepSubtitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Direct2Subtitle");
                    primaryBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourNext2");
                    skipBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourSkip");
                    skipBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    targetElement = ResolveLoadedTarget(submitTarget, composerTarget);
                    stepTitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Direct3Title");
                    stepSubtitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Direct3Subtitle");
                    bool hasApi = CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders().Count > 0;
                    primaryBtn.Content = hasApi
                        ? _loc.T("CustomWidget.Workshop.AgentMcp.TourDone")
                        : _loc.T("CustomWidget.Workshop.Composer.QuickSetupApi");
                    skipBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourSkip");
                    skipBtn.Visibility = hasApi ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            else if (channel == StudioAiChannelKind.AgentMcp)
            {
                if (step == 1)
                {
                    targetElement = ResolveLoadedTarget(_activeQuickSetupButton, _activeChannelSelectorButton);
                    stepTitleBlock.Text = _loc.T("CustomWidget.Workshop.AgentMcp.Tour1Title");
                    stepSubtitleBlock.Text = _loc.T("CustomWidget.Workshop.AgentMcp.Tour1Subtitle");
                    primaryBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourNext1");
                    skipBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourSkip");
                    skipBtn.Visibility = Visibility.Visible;
                }
                else if (step == 2)
                {
                    targetElement = ResolveLoadedTarget(composerTarget, _activeChannelSelectorButton);
                    stepTitleBlock.Text = _loc.T("CustomWidget.Workshop.AgentMcp.Tour2Title");
                    stepSubtitleBlock.Text = _loc.T("CustomWidget.Workshop.AgentMcp.Tour2Subtitle");
                    primaryBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourNext2");
                    skipBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourSkip");
                    skipBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    targetElement = ResolveLoadedTarget(submitTarget, _activeQuickSetupButton, _activeChannelSelectorButton);
                    stepTitleBlock.Text = _loc.T("CustomWidget.Workshop.AgentMcp.Tour3Title");
                    stepSubtitleBlock.Text = _loc.T("CustomWidget.Workshop.AgentMcp.Tour3Subtitle");
                    primaryBtn.Content = _loc.T("CustomWidget.Workshop.Composer.QuickSetupMcp");
                    skipBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourDone");
                    skipBtn.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (step == 1)
                {
                    targetElement = ResolveLoadedTarget(_activeChannelSelectorButton);
                    stepTitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Web1Title");
                    stepSubtitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Web1Subtitle");
                    primaryBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourNext1");
                    skipBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourSkip");
                    skipBtn.Visibility = Visibility.Visible;
                }
                else if (step == 2)
                {
                    targetElement = ResolveLoadedTarget(composerTarget, _activeChannelSelectorButton);
                    stepTitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Web2Title");
                    stepSubtitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Web2Subtitle");
                    primaryBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourNext2");
                    skipBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourSkip");
                    skipBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    targetElement = ResolveLoadedTarget(submitTarget, composerTarget);
                    stepTitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Web3Title");
                    stepSubtitleBlock.Text = _loc.T("CustomWidget.Workshop.Onboarding.Web3Subtitle");
                    primaryBtn.Content = _loc.T("CustomWidget.Workshop.AgentMcp.TourDone");
                    skipBtn.Visibility = Visibility.Collapsed;
                }
            }

            PositionSpotlightAndCard(targetElement);
            FluentMotionHelper.PlayEntranceSlideAndFade(coachmarkCard, offsetY: 8f, durationMs: 180);
        }

        async Task AdvanceOrFinishAsync()
        {
            if (currentStep == 1)
            {
                RenderStep(2);
            }
            else if (currentStep == 2)
            {
                RenderStep(3);
            }
            else
            {
                CloseOnboardingOverlay();
                if (channel == StudioAiChannelKind.DirectApi &&
                    CustomWidgetAiGeneratorService.Shared.GetConfiguredProviders().Count == 0)
                {
                    await ShowAiConfigDialogInStudioAsync(null, _refreshActiveChannelSelector);
                }
                else if (channel == StudioAiChannelKind.AgentMcp)
                {
                    await ShowAgentMcpQuickConnectDialogAsync(_refreshActiveChannelSelector);
                }
            }
        }

        primaryBtn.Click += async (_, _) => await AdvanceOrFinishAsync();

        rootBackdrop.SizeChanged += (_, _) =>
        {
            if (runId == _onboardingTourRunId && StudioOnboardingHost.Visibility == Visibility.Visible)
            {
                PositionSpotlightAndCard(latestTargetElement);
            }
        };
        rootBackdrop.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CloseOnboardingOverlay();
            }
        };
        rootBackdrop.Tapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, rootBackdrop) || ReferenceEquals(e.OriginalSource, overlayCanvas))
            {
                CloseOnboardingOverlay();
            }
        };

        coachmarkCard.Loaded += (_, _) => primaryBtn.Focus(FocusState.Programmatic);
        RenderStep(Math.Clamp(initialStep, 1, 3));
    }

    private async Task ShowAgentMcpQuickConnectDialogAsync(Action? onAfterChange = null)
    {
        if (_isDialogOpen || _isClosed || RootGrid.XamlRoot is null)
        {
            return;
        }

        _isDialogOpen = true;
        using var bridgeDiagCts = new CancellationTokenSource();
        try
        {
            var rootStack = new StackPanel { Spacing = 12, Width = 476 };

            rootStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.AgentMcp.QuickConnectIntro"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush")
            });

            // ── Option 1 (Recommended): Send Auto-Setup Prompt to AI ─────────────
            var autoStack = new StackPanel { Spacing = 8 };
            autoStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.AgentMcp.AutoMethodTitle"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextFillColorPrimaryBrush")
            });
            autoStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.AgentMcp.AutoMethodDesc"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush")
            });

            var autoFeedbackText = new TextBlock
            {
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            string copyAutoLabel = _loc.T("CustomWidget.AgentHub.Playbook.CopyCreate");
            var copyPromptBtn = new Button
            {
                Height = 34,
                Padding = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(6),
                Style = AccentButtonStyle(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = CreateSmallIconLabel("\uE8C8", copyAutoLabel)
            };
            copyPromptBtn.Click += async (_, _) =>
            {
                SetClipboardText(CustomWidgetAgentConnectorService.BuildMcpSetupPrompt(_loc.CurrentCultureName));
                autoFeedbackText.Foreground = ThemeBrush("SystemFillColorSuccessBrush");
                autoFeedbackText.Text = _loc.T("CustomWidget.Workshop.AgentMcp.AutoPromptCopiedHint");
                autoFeedbackText.Visibility = Visibility.Visible;
                onAfterChange?.Invoke();

                copyPromptBtn.Content = "✓ " + _loc.T("CustomWidget.Native.Dialog.CopiedButton");
                try
                {
                    await Task.Delay(1400, bridgeDiagCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                copyPromptBtn.Content = CreateSmallIconLabel("\uE8C8", copyAutoLabel);
            };
            autoStack.Children.Add(copyPromptBtn);
            autoStack.Children.Add(autoFeedbackText);

            rootStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                Child = autoStack
            });

            // ── Option 2: Manual Client Config Snippet / CLI Command ─────────────
            var manualStack = new StackPanel { Spacing = 8 };
            manualStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.AgentMcp.ManualMethodTitle"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextFillColorPrimaryBrush")
            });
            manualStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.AgentMcp.ManualMethodDesc"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush")
            });

            (McpConfigFormat Format, string LabelKey)[] formats =
            [
                (McpConfigFormat.StandardJson, "CustomWidget.AgentHub.Format.StandardJson"),
                (McpConfigFormat.VsCodeJson, "CustomWidget.AgentHub.Format.VsCodeJson"),
                (McpConfigFormat.CodexToml, "CustomWidget.AgentHub.Format.CodexToml"),
                (McpConfigFormat.OpenCodeJson, "CustomWidget.AgentHub.Format.OpenCodeJson"),
                (McpConfigFormat.CliRegistration, "CustomWidget.AgentHub.Format.CliRegistration"),
                (McpConfigFormat.DeepSeekHarnessYaml, "CustomWidget.AgentHub.Format.DeepSeekHarnessYaml")
            ];

            var formatCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 12
            };
            foreach ((McpConfigFormat fmt, string labelKey) in formats)
            {
                formatCombo.Items.Add(new ComboBoxItem
                {
                    Content = _loc.T(labelKey),
                    Tag = fmt
                });
            }
            formatCombo.SelectedIndex = 0;

            string currentSnippetText = CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.StandardJson);

            // AcceptsReturn must be enabled BEFORE assigning multi-line Text on WinUI 3 TextBox;
            // otherwise RichEdit truncates the initial value at the first newline (leaving only "{").
            var snippetBox = new TextBox
            {
                AcceptsReturn = true,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FlowDirection = FlowDirection.LeftToRight,
                FontSize = 11.5,
                MinHeight = 132,
                MaxHeight = 176,
                CornerRadius = new CornerRadius(6)
            };
            ScrollViewer.SetVerticalScrollBarVisibility(snippetBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(snippetBox, ScrollBarVisibility.Auto);
            snippetBox.Text = currentSnippetText;

            formatCombo.SelectionChanged += (_, _) =>
            {
                if ((formatCombo.SelectedItem as ComboBoxItem)?.Tag is McpConfigFormat selectedFmt)
                {
                    currentSnippetText = CustomWidgetAgentConnectorService.BuildMcpSnippet(selectedFmt);
                    snippetBox.Text = currentSnippetText;
                }
            };

            var manualFeedbackText = new TextBlock
            {
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            var copySnippetBtn = new Button
            {
                Height = 32,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(6),
                VerticalAlignment = VerticalAlignment.Center,
                Content = _loc.T("CustomWidget.AgentHub.CopyMcpConfig")
            };
            copySnippetBtn.Click += async (_, _) =>
            {
                SetClipboardText(currentSnippetText);
                manualFeedbackText.Foreground = ThemeBrush("SystemFillColorSuccessBrush");
                manualFeedbackText.Text = _loc.T("CustomWidget.AgentHub.CopyMcpSuccessHint");
                manualFeedbackText.Visibility = Visibility.Visible;

                copySnippetBtn.Content = "✓ " + _loc.T("CustomWidget.Native.Dialog.CopiedButton");
                try
                {
                    await Task.Delay(1400, bridgeDiagCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                copySnippetBtn.Content = _loc.T("CustomWidget.AgentHub.CopyMcpConfig");
            };

            var snippetHeaderRow = new Grid { ColumnSpacing = 8 };
            snippetHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            snippetHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(formatCombo, 0);
            snippetHeaderRow.Children.Add(formatCombo);
            Grid.SetColumn(copySnippetBtn, 1);
            snippetHeaderRow.Children.Add(copySnippetBtn);

            manualStack.Children.Add(snippetHeaderRow);
            manualStack.Children.Add(snippetBox);
            manualStack.Children.Add(manualFeedbackText);

            rootStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                Child = manualStack
            });

            // ── Live Host Bridge Readiness + One-Click Self-Test ─────────────────
            var diagRow = new Grid { ColumnSpacing = 8 };
            diagRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            diagRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var diagStatusText = new TextBlock
            {
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush")
            };
            Grid.SetColumn(diagStatusText, 0);
            diagRow.Children.Add(diagStatusText);

            var selfTestBtn = new Button
            {
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Content = _loc.T("CustomWidget.AgentHub.SelfTestButton")
            };
            Grid.SetColumn(selfTestBtn, 1);
            diagRow.Children.Add(selfTestBtn);
            rootStack.Children.Add(diagRow);

            async Task RunQuickSelfTestAsync()
            {
                if (bridgeDiagCts.IsCancellationRequested || _isClosed)
                {
                    return;
                }

                selfTestBtn.IsEnabled = false;
                try
                {
                    AgentBridgeSelfTestResult diag = await CustomWidgetAgentConnectorService.RunBridgeSelfTestAsync(
                        cancellationToken: bridgeDiagCts.Token);
                    if (bridgeDiagCts.IsCancellationRequested || _isClosed)
                    {
                        return;
                    }

                    bool healthy = diag.McpProtocolOk && diag.PipeOnline;
                    diagStatusText.Foreground = ThemeBrush(
                        healthy ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
                    diagStatusText.Text = healthy
                        ? "✓ " + _loc.Format("CustomWidget.AgentHub.BridgeReady", diag.McpToolCount)
                        : (diag.ErrorMessage ?? _loc.T("CustomWidget.AgentHub.BridgeSubtitleOffline"));
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    if (!bridgeDiagCts.IsCancellationRequested && !_isClosed)
                    {
                        diagStatusText.Foreground = ThemeBrush("SystemFillColorCriticalBrush");
                        diagStatusText.Text = ex.Message;
                    }
                }
                finally
                {
                    if (!bridgeDiagCts.IsCancellationRequested && !_isClosed)
                    {
                        selfTestBtn.IsEnabled = true;
                    }
                }
            }

            selfTestBtn.Click += async (_, _) => await RunQuickSelfTestAsync();
            _ = RunQuickSelfTestAsync();

            var scrollHost = new ScrollViewer
            {
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 4, 0),
                Content = rootStack
            };

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = StudioExplicitTheme,
                FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(_loc.CurrentCultureName)
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight,
                Title = _loc.T("CustomWidget.Workshop.AgentMcp.QuickConnectTitle"),
                Content = scrollHost,
                SecondaryButtonText = _loc.T("CustomWidget.AgentHub.OpenSetupDocsButton"),
                CloseButtonText = _loc.T("Common.Close"),
                DefaultButton = ContentDialogButton.None
            };
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) &&
                style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }

            ContentDialogResult res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Secondary &&
                Uri.TryCreate(CustomWidgetAgentConnectorService.PublicGuideUrl, UriKind.Absolute, out Uri? guideUri))
            {
                _ = await Windows.System.Launcher.LaunchUriAsync(guideUri);
            }
        }
        finally
        {
            bridgeDiagCts.Cancel();
            _isDialogOpen = false;
        }
    }

    private static string TruncateTextElements(string? text, int maxTextElements)
    {
        if (string.IsNullOrEmpty(text) || maxTextElements <= 0)
        {
            return string.Empty;
        }

        var info = new System.Globalization.StringInfo(text);
        if (info.LengthInTextElements <= maxTextElements)
        {
            return text;
        }

        return info.SubstringByTextElements(0, maxTextElements) + "…";
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

        BindFlyoutToStudioTheme(flyout, stack);
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
                RequestedTheme = StudioExplicitTheme,
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
        string? curLogicJs = CurrentLogicJs;
        CustomWidgetLocaleCatalog? curLocales = CurrentLocales;
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
                markDirty: !IsEditMode,
                logicJs: curLogicJs,
                hasExplicitLogicJs: true,
                locales: curLocales,
                hasExplicitLocales: true);
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
        if (IsAnyStudioJobRunning || target < 0 || target >= _revisions.Count || _previewVm is null)
        {
            return;
        }

        _revisionIndex = target;
        _cachedI18nSyncPlan = null;
        StudioPersistedRevision selectedRev = _revisions[target];
        string json = selectedRev.ManifestJson;
        _previewVm.ApplyPreviewBundle(json, selectedRev.LogicJs, selectedRev.Locales, preserveRuntimeData: true);
        _previewVm.SetPreviewCultureOverride(_activePreviewCultureOverride);
        SyncRuntimeScriptErrorFromPreviewVm();
        ReapplySessionSettingOverridesToPreviewVm();
        if (!IsEditMode && !string.IsNullOrWhiteSpace(_previewSampleDataJson) && !_isSampleDataMutedInPreview)
        {
            _previewVm.ApplyPreviewSampleDataJson(_previewSampleDataJson);
        }
        else if (_isSampleDataMutedInPreview)
        {
            _previewVm.ClearPreviewSampleData();
        }

        CustomWidgetManifest? effectiveManifest = ResolvePreviewManifestWithSidecar(json);
        CustomWidgetSizeSpec nextSize = effectiveManifest?.DefaultSize ?? new CustomWidgetSizeSpec();
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

        string nextTitle = effectiveManifest?.ResolveLocalizedName(EffectivePreviewCultureName) ?? _loc.T("CustomWidget.Title");
        string nextEmoji = ResolveDisplayEmoji(effectiveManifest);
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
        RefreshCapsulePreviewCardContent();
        RefreshStageViewportModeVisuals();
        RefreshTitleBarContextBadge();
        RefreshSampleDataBadgeChrome();
        RefreshSettingsPreviewBadgeChrome();
        RefreshLocaleCenterChrome();
        RefreshRevisionChrome();
        RefreshScriptErrorBannerChrome();
        RefreshSuggestionChips();
    }

    private void RefreshRevisionChrome()
    {
        if (_undoButton is not null)
        {
            _undoButton.IsEnabled = !IsAnyStudioJobRunning && _revisionIndex > 0;
        }

        if (_redoButton is not null)
        {
            _redoButton.IsEnabled = !IsAnyStudioJobRunning && _revisionIndex < _revisions.Count - 1;
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
        UIElement? pulseTarget = _isCapsulePreviewMode && _capsulePreviewFrame is not null
            ? _capsulePreviewFrame
            : ((UIElement?)_widgetFrame ?? _stageBorder);
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

        return ResolveFallbackEmojiForManifest(manifest);
    }

    private static string ResolveFallbackEmojiForManifest(CustomWidgetManifest? manifest)
    {
        if (manifest is not null)
        {
            if (CustomWidgetTemplates.IsEmojiIcon(manifest.Icon))
            {
                return manifest.Icon.Trim();
            }

            if (CustomWidgetTemplates.TryNormalizeKnownTemplateId(manifest.Id) is { } templateId)
            {
                return templateId switch
                {
                    CustomWidgetTemplates.SystemPulseTemplateId => "📊",
                    CustomWidgetTemplates.PomodoroTemplateId => "🍅",
                    CustomWidgetTemplates.CountdownTemplateId => "⏳",
                    CustomWidgetTemplates.AiPulseTemplateId => "🧠",
                    CustomWidgetTemplates.WorldClockTemplateId => "🌍",
                    CustomWidgetTemplates.DevToolboxTemplateId => "🧰",
                    CustomWidgetTemplates.AgentHudTemplateId => "🤖",
                    CustomWidgetTemplates.HydrationStarterId => "💧",
                    CustomWidgetTemplates.AiInboxStarterId => "⚡",
                    CustomWidgetTemplates.NewsRssStarterId => "📰",
                    _ => "\u2728"
                };
            }
        }

        return "\u2728";
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
            BorderThickness = new Thickness(1)
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
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
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

        FontIcon? editGlyph = null;
        if (clipViewport)
        {
            grid.Children.Add(head);
        }
        else
        {
            editGlyph = new FontIcon
            {
                Glyph = "\uE70F",
                FontSize = 11,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center
            };
            _frameEditGlyph = editGlyph;
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

        var divider = new Border();
        if (!clipViewport)
        {
            _frameDivider = divider;
        }
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

        ApplyWidgetFrameTheme(frame, content, titleText, editGlyph, divider, RootGrid.ActualTheme);
        return frame;
    }

    private Flyout BuildHeaderQuickEditFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var stack = new StackPanel { Spacing = 10, Width = 256 };

        var emojiInput = new TextBox
        {
            Header = _loc.T("CustomWidget.Workshop.CoCreate.EmojiLabel"),
            MaxLength = 32
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

        BindFlyoutToStudioTheme(flyout, stack);
        flyout.Opening += (_, _) =>
        {
            CustomWidgetManifest? cur = ResolvePreviewManifestWithSidecar(CurrentManifestJson);
            emojiInput.Text = ResolveDisplayEmoji(cur);
            titleInput.Text = cur?.ResolveLocalizedName(EffectivePreviewCultureName) ?? string.Empty;
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
        AiGenerationStage.InspectingCapabilities => _loc.T("CustomWidget.AiApi.Stage.InspectingCapabilities"),
        AiGenerationStage.ProbingApi => _loc.T("CustomWidget.AiApi.Stage.ProbingApi"),
        AiGenerationStage.GeneratingJson => _loc.T("CustomWidget.Workshop.Stream.StageGenerating"),
        AiGenerationStage.Validating => _loc.T("CustomWidget.Workshop.Stream.StageValidating"),
        AiGenerationStage.AutoRepairing => _loc.T("CustomWidget.Workshop.Stream.StageRepairing"),
        _ => _loc.T("CustomWidget.Workshop.Direct.Title")
    };

    private bool _pendingChatStickyScrollQueued;
    private bool _pendingStreamStickyScrollQueued;
    private bool _pendingThinkingStickyScrollQueued;

    private static string FormatDirectStageWithDetectedName(string stageTitle, string? detectedWidgetName) =>
        string.IsNullOrWhiteSpace(detectedWidgetName)
            ? stageTitle
            : $"{stageTitle} · {detectedWidgetName.Trim()}";

    private static string ExtractLatestNonEmptySingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = text.AsSpan();
        int end = span.Length;
        while (end > 0)
        {
            while (end > 0 && (span[end - 1] == '\r' || span[end - 1] == '\n'))
            {
                end--;
            }

            if (end <= 0)
            {
                break;
            }

            int lineStart = end;
            while (lineStart > 0 && span[lineStart - 1] != '\n' && span[lineStart - 1] != '\r')
            {
                lineStart--;
            }

            ReadOnlySpan<char> line = span[lineStart..end].Trim();
            while (line.Length > 0 && (line[0] == '#' || line[0] == '*' || line[0] == '-' || line[0] == '>' || line[0] == ' '))
            {
                line = line[1..];
            }

            line = line.Trim();
            if (line.Length > 0)
            {
                if (line.Length > 160)
                {
                    return string.Concat(line[..160], "…");
                }

                return line.ToString();
            }

            end = lineStart;
        }

        return string.Empty;
    }

    private void QueueStickyScrollToBottomChat()
    {
        if (_pendingChatStickyScrollQueued || !_chatStickToBottom)
        {
            return;
        }

        _pendingChatStickyScrollQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _pendingChatStickyScrollQueued = false;
            if (_isClosed || !_chatStickToBottom || _chatStreamScroller is not { } scroller)
            {
                return;
            }

            ScrollToBottomExact(scroller, ref _programmaticChatScrollDepth);
        });
    }

    private void QueueStickyScrollToBottomStream()
    {
        if (_pendingStreamStickyScrollQueued || !_streamStickToBottom)
        {
            return;
        }

        _pendingStreamStickyScrollQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _pendingStreamStickyScrollQueued = false;
            if (_isClosed || !_streamStickToBottom || _directStreamScroller is not { Visibility: Visibility.Visible } scroller)
            {
                return;
            }

            ScrollToBottomExact(scroller, ref _programmaticStreamScrollDepth);
        });
    }

    private void QueueStickyScrollToBottomThinking()
    {
        if (_pendingThinkingStickyScrollQueued || !_thinkingStickToBottom)
        {
            return;
        }

        _pendingThinkingStickyScrollQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _pendingThinkingStickyScrollQueued = false;
            if (_isClosed || !_thinkingStickToBottom || _directThinkingScroller is not { Visibility: Visibility.Visible } scroller)
            {
                return;
            }

            ScrollToBottomExact(scroller, ref _programmaticThinkingScrollDepth);
        });
    }

    private static void ScrollToBottomExact(ScrollViewer scroller, ref int programmaticDepth)
    {
        scroller.UpdateLayout();
        double target = Math.Max(0.0, scroller.ScrollableHeight);
        if (Math.Abs(scroller.VerticalOffset - target) <= 0.5)
        {
            return;
        }

        programmaticDepth++;
        try
        {
            scroller.ChangeView(null, target, null, disableAnimation: true);
        }
        finally
        {
            programmaticDepth--;
        }
    }

    private void AttachNestedScrollerWheelHandoff(ScrollViewer innerScroller, Action<bool> setStickToBottom)
    {
        innerScroller.PointerWheelChanged += (_, e) =>
        {
            var props = e.GetCurrentPoint(innerScroller).Properties;
            if (props.IsHorizontalMouseWheel)
            {
                return;
            }

            int delta = props.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }

            if (delta > 0 && innerScroller.ScrollableHeight > 1.0)
            {
                setStickToBottom(false);
            }

            bool canScrollInnerUp = delta > 0 && innerScroller.VerticalOffset > 0.5;
            bool canScrollInnerDown = delta < 0 && (innerScroller.ScrollableHeight - innerScroller.VerticalOffset) > 0.5;
            if (innerScroller.ScrollableHeight > 1.0 && (canScrollInnerUp || canScrollInnerDown))
            {
                return;
            }

            if (_chatStreamScroller is { ScrollableHeight: > 0.5 } outer)
            {
                if (delta > 0)
                {
                    _chatStickToBottom = false;
                }

                double step = -(delta / 120.0) * 72.0;
                double nextOffset = Math.Clamp(outer.VerticalOffset + step, 0.0, outer.ScrollableHeight);
                _programmaticChatScrollDepth++;
                try
                {
                    outer.ChangeView(null, nextOffset, null, disableAnimation: true);
                }
                finally
                {
                    _programmaticChatScrollDepth--;
                }

                _chatStickToBottom = (outer.ScrollableHeight - nextOffset) <= 24.0;
                e.Handled = true;
            }
        };
    }

    private void UpdateDirectStreamProgress(AiGenerationStreamProgress progress)
    {
        _inFlightStage = progress.Stage;
        _directActiveModelName = progress.Model;
        _directLastCharTotal = progress.ReasoningChars + progress.ContentChars;

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
        }

        string stageTitle = ResolveDirectStageTitle(progress.Stage, progress.ProviderName);
        if (_directStageTitleText is not null)
        {
            _directStageTitleText.Text = FormatDirectStageWithDetectedName(stageTitle, _inFlightDetectedWidgetName);
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

                if (_directThinkingCollapsedPreviewText is not null)
                {
                    string latestLine = ExtractLatestNonEmptySingleLine(progress.ThinkingText);
                    if (!string.IsNullOrEmpty(latestLine))
                    {
                        _directThinkingCollapsedPreviewText.Text = latestLine;
                    }
                }

                string thinkTail = progress.ThinkingText.Length > 2800
                    ? "…\n" + progress.ThinkingText[^2800..]
                    : progress.ThinkingText;
                _directThinkingText.Text = thinkTail;

                if (_directThinkingScroller is { Visibility: Visibility.Visible } && _thinkingStickToBottom)
                {
                    QueueStickyScrollToBottomThinking();
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
                    _directStreamScroller is not null &&
                    _streamStickToBottom)
                {
                    QueueStickyScrollToBottomStream();
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

    /// <summary>
    /// 技术详情折叠块。异常原文只放这里，不再占据提示正文——
    /// 之前 6 处直接把 ex.Message 当正文，用户看到的是
    /// "HttpRequestException: 由于目标计算机积极拒绝" 这一类原文，既看不懂也不知道下一步做什么。
    /// </summary>
    private UIElement BuildFailureTechDetails(string detail)
    {
        return new Expander
        {
            Header = new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.Diag.TechDetailsHeader"),
                FontSize = 12
            },
            Content = new TextBlock
            {
                Text = detail,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5
            }
        };
    }

    /// <summary>
    /// 统一的失败提示：一句人能看懂的标题 + 一句「下一步该做什么」 + 折叠的技术详情。
    /// </summary>
    private void ShowFailureMessage(
        string titleKey,
        string bodyKey,
        Exception? ex,
        ButtonBase? action = null)
    {
        App.Log($"[CustomWidgetStudio] failure {titleKey}: {ex}");

        ShowPageMessage(
            InfoBarSeverity.Error,
            _loc.T(titleKey),
            _loc.T(bodyKey),
            action,
            ex is null ? null : BuildFailureTechDetails(ex.ToString()));
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

    private static Button CreateChipButton(string text, string? glyph = null, IconElement? iconElement = null)
    {
        var chip = new Button
        {
            MinHeight = 28,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            FontSize = 12,


            Content = iconElement is not null
                ? CreateSmallIconElementLabel(iconElement, text)
                : (string.IsNullOrWhiteSpace(glyph)
                    ? text
                    : CreateSmallIconLabel(glyph!, text))
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

    private static Grid CreateSmallIconElementLabel(IconElement icon, string text)
    {
        icon.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(icon);
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

    private Brush StageThemeBrush(string key, ElementTheme stageTheme)
    {
        ElementTheme resolved = stageTheme == ElementTheme.Default ? StudioExplicitTheme : stageTheme;
        if (NeutralInteractionBrush.ResolveThemedResource(key, resolved) is Brush themed)
        {
            return themed;
        }

        return ThemeBrush(key);
    }

    private ElementTheme StudioExplicitTheme =>
        NeutralInteractionBrush.IsDarkTheme(RootGrid) ? ElementTheme.Dark : ElementTheme.Light;

    private Style BuildStudioThemedFlyoutPresenterStyle(ElementTheme explicitTheme)
    {
        var style = new Style(typeof(FlyoutPresenter));
        if (Application.Current.Resources.TryGetValue(typeof(FlyoutPresenter), out object? implicitObj) &&
            implicitObj is Style implicitStyle)
        {
            style.BasedOn = implicitStyle;
        }
        else if (Application.Current.Resources.TryGetValue("DefaultFlyoutPresenterStyle", out object? defaultObj) &&
                 defaultObj is Style defaultStyle)
        {
            style.BasedOn = defaultStyle;
        }

        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, explicitTheme));
        return style;
    }

    private void BindFlyoutToStudioTheme(Flyout flyout, FrameworkElement? contentRoot = null)
    {
        void SyncTheme()
        {
            ElementTheme explicitTheme = StudioExplicitTheme;
            flyout.FlyoutPresenterStyle = BuildStudioThemedFlyoutPresenterStyle(explicitTheme);
            if (contentRoot is not null)
            {
                contentRoot.RequestedTheme = explicitTheme;
            }
            else if (flyout.Content is FrameworkElement fe)
            {
                fe.RequestedTheme = explicitTheme;
            }
        }

        SyncTheme();
        flyout.Opening += (_, _) => SyncTheme();
    }

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
                _previewVm.ScriptErrorChanged -= OnPreviewVmScriptErrorChanged;
                _previewVm.MetricsRefreshed -= OnPreviewVmMetricsRefreshed;
                _previewVm.PropertyChanged -= OnPreviewVmPropertyChanged;
                _previewVm.Dispose();
            }
        }
        catch
        {
        }

        _previewVm = null;
        _refreshTemplateGalleryFromRemote = null;
        _stageColumn = null;
        _stageWidthManuallyAdjusted = false;
        _stageBorder = null;
        _widgetFrame = null;
        _capsulePreviewFrame = null;
        _capsuleEmojiText = null;
        _capsuleGlyphIcon = null;
        _capsuleTitleText = null;
        _capsuleSummaryText = null;
        _capsuleBadgeBorder = null;
        _capsuleBadgeText = null;
        _capsulePrimaryActionButton = null;
        _capsulePrimaryActionIcon = null;
        _capsuleProgressBar = null;
        _scriptErrorBannerBorder = null;
        _scriptErrorBannerText = null;
        _scriptErrorFixButton = null;
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
        _i18nHeaderButton = null;
        _i18nHeaderIcon = null;
        _i18nHeaderLabel = null;
        _previewCultureOverridePillButton = null;
        _localeCenterHostBorder = null;
        _localeCenterSummaryText = null;
        _localeCenterHintText = null;
        _localeCenterOverallProgressBar = null;
        _localeCenterSyncAllButton = null;
        _localeCenterSync2Button = null;
        _localeCenterRetryFailedButton = null;
        _localeCenterStopButton = null;
        _localeCenterCloseButton = null;
        _localeCenterCardRefs.Clear();
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
        _stageGeneratingProgressRing = null;
        _stageGeneratingStatusPill = null;
        _stageGeneratingStatusText = null;
        _blueprintTitleText = null;
        _blueprintFrame = null;
        _blueprintDivider = null;
        _blueprintSkeletonBlocks.Clear();
        _frameDivider = null;
        _frameEditGlyph = null;
        _pickElementButton = null;
        _pickElementIcon = null;
        _pickElementLabel = null;
        _settingsPreviewButton = null;
        _stageViewportModeSegmented = null;
        _stageBackdropSegmented = null;
        _sampleDataToggleButton = null;
        _directStageTitleText = null;
        _directStatsBadgeText = null;
        _directThinkingContainer = null;
        _directThinkingHeaderText = null;
        _directThinkingCollapsedPreviewText = null;
        _directThinkingScroller = null;
        _directThinkingText = null;
        _pendingChatStickyScrollQueued = false;
        _pendingStreamStickyScrollQueued = false;
        _pendingThinkingStickyScrollQueued = false;
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
