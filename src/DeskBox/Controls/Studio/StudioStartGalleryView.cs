using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.Studio;

internal sealed class StudioStartGalleryView : IDisposable
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private CustomWidgetStudioSessionStore _sessionStore => _shell.SessionStore;
    private StudioSessionController _session => _shell.Session;

    internal StudioStartGalleryView(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }
    private int _galleryHydrationRunId;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchDebounceTimer;
    private Action<bool>? _pendingSearchRenderAction;
    private Action? _refreshAgentStageBanner;
    private Action? _refreshTemplateGalleryFromRemote;
    private readonly Queue<PendingThumbnailCaptureJob> _thumbnailCaptureQueue = new();
    private bool _isThumbnailCapturePumpRunning;
    private bool _cachePruneScheduled;
    private (CustomWidgetViewModel Vm, CustomWidgetContent Content, GallerySlotState Slot)? _activeLiveCapture;
    private StartGallerySourceMode _activeStartGalleryMode = StartGallerySourceMode.Templates;
    private TemplateGalleryCategory _activeGalleryCategory = TemplateGalleryCategory.All;

    private sealed class GallerySlotState
    {
        public int RunId { get; set; }
        public int Generation { get; set; }
        public Image? AttachedImage { get; set; }
        public Border? PreviewWell { get; set; }

        public bool IsAlive(int expectedRunId, int expectedGeneration) =>
            RunId == expectedRunId && Generation == expectedGeneration;
    }

    private sealed class GalleryRowSlotState
    {
        public List<GallerySlotState> CardSlots { get; } = new(3);
    }

    private sealed record PendingThumbnailCaptureJob(
        int RunId,
        int SlotGeneration,
        GallerySlotState Slot,
        string CachePngPath,
        string? UserWidgetIdForStalePrune,
        string Label,
        string Emoji,
        string ManifestJson,
        string? LogicJs,
        CustomWidgetLocaleCatalog? Locales,
        string? ExistingWidgetId,
        Border PreviewWell,
        Action<FrameworkElement> SetScaleTarget);

    internal Action? RefreshAgentStageBanner { get => _refreshAgentStageBanner; set => _refreshAgentStageBanner = value; }
    internal Action? RefreshTemplateGalleryFromRemote { get => _refreshTemplateGalleryFromRemote; set => _refreshTemplateGalleryFromRemote = value; }
    internal StartGallerySourceMode ActiveStartGalleryMode { get => _activeStartGalleryMode; set => _activeStartGalleryMode = value; }

    private const double StartPageMaxWidth = 1360;

    private IReadOnlyList<ExistingWidgetCardItem> GetExistingWidgetGalleryItems()
    {
        IReadOnlyList<GlanceWidgetInstanceInfo> instances = _hostBridge.GetCustomWidgetInstances();
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

            string? manifestJson = StudioManifestInspector.TryReadWidgetManifest(inst.Id, _hostBridge.Log);
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

            string emoji = StudioManifestInspector.ResolveFallbackEmojiForManifest(manifest);
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
            (CustomWidgetTemplates.TodoAgendaTemplateId, "📆", "CustomWidget.Template.TodoAgenda.Name", "CustomWidget.Template.TodoAgenda.Description", string.Empty, TemplateGalleryCategory.Habits),
            (CustomWidgetTemplates.SnippetGridTemplateId, "⚡", "CustomWidget.Template.SnippetGrid.Name", "CustomWidget.Template.SnippetGrid.Description", string.Empty, TemplateGalleryCategory.Tools),
            (CustomWidgetTemplates.CountdownTemplateId, "📅", "CustomWidget.Template.Countdown.Name", "CustomWidget.Template.Countdown.Description", string.Empty, TemplateGalleryCategory.Tools),
            (CustomWidgetTemplates.HydrationStarterId, "💧", "CustomWidget.AiWizard.Chip.Water.Label", "CustomWidget.Workshop.Card.Water.Desc", "CustomWidget.AiWizard.Chip.Water.Prompt", TemplateGalleryCategory.Habits),
            (CustomWidgetTemplates.PomodoroTemplateId, "🍅", "CustomWidget.Template.Pomodoro.Name", "CustomWidget.Template.Pomodoro.Description", "CustomWidget.Workshop.Card.Pomodoro.Prompt", TemplateGalleryCategory.Habits),
            (CustomWidgetTemplates.FocusDialTemplateId, "🎧", "CustomWidget.Template.FocusDial.Name", "CustomWidget.Template.FocusDial.Description", string.Empty, TemplateGalleryCategory.Habits),
            (CustomWidgetTemplates.AiInboxStarterId, "✨", "CustomWidget.Studio.Starter.AiInbox.Label", "CustomWidget.Workshop.Card.AiInbox.Desc", "CustomWidget.Studio.Starter.AiInbox.Prompt", TemplateGalleryCategory.Ai),
            (CustomWidgetTemplates.WeatherDeskTemplateId, "🌤️", "CustomWidget.Template.WeatherDesk.Name", "CustomWidget.Template.WeatherDesk.Description", string.Empty, TemplateGalleryCategory.Live),
            (CustomWidgetTemplates.NewsRssStarterId, "📰", "CustomWidget.Studio.Starter.NewsRss.Label", "CustomWidget.Workshop.Card.NewsRss.Desc", "CustomWidget.Studio.Starter.NewsRss.Prompt", TemplateGalleryCategory.Live),
            (CustomWidgetTemplates.WorldClockTemplateId, "🌍", "CustomWidget.Template.WorldClock.Name", "CustomWidget.Template.WorldClock.Description", string.Empty, TemplateGalleryCategory.Tools),
            (CustomWidgetTemplates.DevToolboxTemplateId, "🛠️", "CustomWidget.Template.DevToolbox.Name", "CustomWidget.Template.DevToolbox.Description", string.Empty, TemplateGalleryCategory.Tools),
            (CustomWidgetTemplates.SystemPulseTemplateId, "📊", "CustomWidget.Template.SystemPulse.Name", "CustomWidget.Template.SystemPulse.Description", "CustomWidget.AiWizard.Chip.Monitor.Prompt", TemplateGalleryCategory.Live),
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
                StudioManifestInspector.InferRemoteTemplateCategory(remote.Id, resolvedName),
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

    internal void ShowStartPage(string? idea)
    {
        _shell.ComposerBuilder.ClearPageLifecycleCallbacks();
        _shell.ChannelOnboarding.ResetUiDelegates();
        _shell.StageViewport.DisposePreview();
        DisposeTemplatePreviews();
        _session.Page = StudioPage.Start;

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
            Style = StudioUiPrimitives.TextStyle("TitleTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap
        });
        startHeroTextCol.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Start.Subtitle"),
            Style = StudioUiPrimitives.TextStyle("BodyTextBlockStyle"),
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
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
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE8E5", _loc.T("CustomWidget.Workshop.ImportPackageButton"))
        };
        ToolTipService.SetToolTip(startImportPkgBtn, _loc.T("CustomWidget.Workshop.ImportPackageButton"));
        AutomationProperties.SetName(startImportPkgBtn, _loc.T("CustomWidget.Workshop.ImportPackageButton"));
        startImportPkgBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "StartImportPackage",
            _shell.CommitAndWorkspace.ImportPackageFromDiskInStudioAsync);
        startHeroActions.Children.Add(startImportPkgBtn);

        Button startApiDocsBtn = _shell.ComposerBuilder.CreateSubtleApiDocsToggleButton(
            "CustomWidget.ApiDocs.Button",
            fontSize: 12.5,
            horizontalPadding: 12);
        startHeroActions.Children.Add(startApiDocsBtn);

        Button startMoreBtn = StudioUiPrimitives.CreateIconButton("\uE712", _loc.T("CustomWidget.Workshop.More.Tooltip"));
        startMoreBtn.Width = 32;
        startMoreBtn.Height = 32;
        startMoreBtn.CornerRadius = new CornerRadius(6);
        startMoreBtn.Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush");
        startMoreBtn.BorderThickness = new Thickness(1);
        startMoreBtn.BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush");
        var startMoreFlyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var startSwitchModeItem = new MenuFlyoutItem
        {
            Text = _loc.T("CustomWidget.Workshop.SwitchModeButton"),
            Icon = new FontIcon { Glyph = "\uE8AB" }
        };
        startSwitchModeItem.Click += (_, _) => _shell.ChannelOnboarding.ShowAgentMcpOnboardingTour(forceShow: true);
        startMoreFlyout.Items.Add(startSwitchModeItem);

        var startGuideItem = new MenuFlyoutItem
        {
            Text = _loc.T("CustomWidget.Workshop.GuideButton"),
            Icon = new FontIcon { Glyph = "\uE897" }
        };
        startGuideItem.Click += (_, _) => _shell.ChannelOnboarding.StartChannelStepTour(_session.ResolveEffectiveStudioChannel(out _));
        startMoreFlyout.Items.Add(startGuideItem);
        startMoreBtn.Flyout = startMoreFlyout;
        startHeroActions.Children.Add(startMoreBtn);

        Grid.SetColumn(startHeroActions, 1);
        startHeroRow.Children.Add(startHeroActions);

        column.Children.Add(startHeroRow);

        _refreshAgentStageBanner = null;
        _refreshTemplateGalleryFromRemote = null;
        if (_shell.ComposerBuilder.CreateDraftRecoveryBannerIfNeeded() is { } startRecoveryBanner)
        {
            startRecoveryBanner.Margin = new Thickness(0, 14, 0, 0);
            column.Children.Add(startRecoveryBanner);
        }

        // P2：外部 Agent 的写入默认只进暂存区，首屏必须显式暴露「待确认、桌面未变」。
        if (_shell.ComposerBuilder.CreateAgentStageBannerIfNeeded() is { } startAgentStageBanner)
        {
            startAgentStageBanner.Margin = new Thickness(0, 12, 0, 0);
            column.Children.Add(startAgentStageBanner);
        }

        Border composer = _shell.ComposerBuilder.CreateComposerCard(
            initialText: idea ?? string.Empty,
            placeholderText: _loc.T("CustomWidget.Workshop.Start.Placeholder"),
            automationName: _loc.T("CustomWidget.Workshop.Start.Title"),
            submitLabel: _loc.T("CustomWidget.Workshop.Start.Button"),
            shortcutHintText: _loc.T("CustomWidget.Workshop.Composer.ShortcutHint"),
            minTextHeight: 92,
            maxTextHeight: 210,
            compactToolbar: false,
            includeCodeToolInMore: true,
            onTextEdited: text => _session.StartDraftIdea = text.Trim(),
            out TextBox ideaBox);
        _shell.ComposerBuilder.StartIdeaTextBox = ideaBox;
        composer.Margin = new Thickness(0, 18, 0, 0);
        column.Children.Add(composer);

        _shell.PageInfoBar = _shell.CreatePageInfoBar();
        _shell.PageInfoBar.Margin = new Thickness(0, 10, 0, 0);
        column.Children.Add(_shell.PageInfoBar);

        // Curated starter prompts: concise Fluent 2 chips without rainbow emoji clutter.
        var tryRow = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        tryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tryRow.Children.Add(new TextBlock
        {
            Text = _loc.T("CustomWidget.Workshop.Start.TryLabel"),
            Style = StudioUiPrimitives.TextStyle("CaptionTextBlockStyle"),
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        (string Emoji, string LabelKey, string PromptKey)[] starterChips =
        [
            ("📚", "CustomWidget.Studio.Starter.Schedule.Label", "CustomWidget.Studio.Starter.Schedule.Prompt"),
            ("💰", "CustomWidget.Studio.Starter.Expense.Label", "CustomWidget.Studio.Starter.Expense.Prompt"),
            ("🎛️", "CustomWidget.Studio.Starter.ControlHub.Label", "CustomWidget.Studio.Starter.ControlHub.Prompt"),
            ("🌱", "CustomWidget.Studio.Starter.HabitStickers.Label", "CustomWidget.Studio.Starter.HabitStickers.Prompt"),
            ("🖼️", "CustomWidget.Studio.Starter.ImageConverter.Label", "CustomWidget.Studio.Starter.ImageConverter.Prompt"),
            ("📖", "CustomWidget.Studio.Starter.DailyVocab.Label", "CustomWidget.Studio.Starter.DailyVocab.Prompt"),
            ("💱", "CustomWidget.Studio.Starter.MarketFx.Label", "CustomWidget.Studio.Starter.MarketFx.Prompt")
        ];

        var chips = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        foreach ((string emoji, string labelKey, string promptKey) in starterChips)
        {
            string prompt = _loc.T(promptKey);
            string capturedEmoji = emoji;
            string chipLabel = _loc.T(labelKey);
            Button chip = StudioUiPrimitives.CreateChipButton(chipLabel);
            chip.Click += (_, _) =>
            {
                _session.SelectedEmoji = capturedEmoji;
                _shell.ComposerBuilder.RefreshStartEmojiBadge();
                string existing = (ideaBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(existing) || starterChips.Any(sc => string.Equals(existing, _loc.T(sc.PromptKey).Trim(), StringComparison.Ordinal)))
                {
                    ideaBox.Text = prompt;
                }
                else if (!existing.Contains(prompt, StringComparison.Ordinal))
                {
                    ideaBox.Text = $"{existing}\n{prompt}";
                }
                ideaBox.SelectionStart = (ideaBox.Text ?? string.Empty).Length;
                ideaBox.Focus(FocusState.Programmatic);
            };
            chips.Children.Add(chip);
        }

        Grid.SetColumn(chips, 1);
        tryRow.Children.Add(chips);
        column.Children.Add(tryRow);

        int initialMyWidgetCount = _hostBridge.GetCustomWidgetInstances().Count;

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
            Style = StudioUiPrimitives.TextStyle("CaptionTextBlockStyle"),
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        templatesHeaderRow.Children.Add(galleryModeSegmented);
        Grid.SetRow(gallerySubtitleText, 2);
        Grid.SetColumnSpan(gallerySubtitleText, 2);
        templatesHeaderRow.Children.Add(gallerySubtitleText);

        var syncStatusText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        FontIcon CreateSyncIdleIcon() => new()
        {
            Glyph = "\uE72C",
            FontSize = 12,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush")
        };

        var syncCatalogButton = new Button
        {
            Width = 30,
            Height = 30,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
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
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(filterBar, 1);
        Grid.SetColumn(filterBar, 0);
        templatesHeaderRow.Children.Add(filterBar);

        var myWidgetsSearchBox = new AutoSuggestBox
        {
            Width = 228,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = _loc.T("CustomWidget.MyWidgets.SearchPlaceholder"),
            QueryIcon = new SymbolIcon(Symbol.Find),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(myWidgetsSearchBox, 1);
        Grid.SetColumn(myWidgetsSearchBox, 1);
        templatesHeaderRow.Children.Add(myWidgetsSearchBox);
        column.Children.Add(templatesHeaderRow);

        var cardsHostContainer = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
        bool suppressFilterEvent = false;
        bool suppressModeEvent = false;
        bool isManualSyncInProgress = false;
        int syncStatusClearToken = 0;
        IReadOnlyList<WorkshopCardSpec>? cachedSpecs = null;
        List<ExistingWidgetCardItem>? cachedExistingItems = null;
        Action<bool>? requestRender = null;

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
            syncStatusText.Foreground = StudioUiPrimitives.ThemeBrush(_shell, brushKey);
            syncStatusText.Visibility = Visibility.Visible;

            if (durationMs <= 0)
            {
                return;
            }

            await Task.Delay(durationMs);
            if (!_shell.IsClosed && _session.Page == StudioPage.Start && token == syncStatusClearToken)
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
            requestRender?.Invoke(true);
        }

        async Task HydrateGallerySectionAsync(int runId, bool animate)
        {
            bool isMyWidgets = _activeStartGalleryMode == StartGallerySourceMode.MyWidgets;
            filterBar.Visibility = isMyWidgets ? Visibility.Collapsed : Visibility.Visible;
            syncHeaderCluster.Visibility = isMyWidgets ? Visibility.Collapsed : Visibility.Visible;
            myWidgetsSearchBox.Visibility = !isMyWidgets || cachedExistingItems is null || cachedExistingItems.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            Grid.SetRow(gallerySubtitleText, isMyWidgets ? 1 : 2);
            Grid.SetColumnSpan(gallerySubtitleText, isMyWidgets ? 1 : 2);
            gallerySubtitleText.Text = _loc.T(isMyWidgets
                ? "CustomWidget.Workshop.MyWidgets.Subtitle"
                : "CustomWidget.Workshop.Start.TemplatesSubtitle");

            if (!_cachePruneScheduled)
            {
                _cachePruneScheduled = true;
                _ = Task.Run(() => StudioGalleryThumbnailCache.PruneCacheDirectory());
            }

            if ((isMyWidgets && cachedExistingItems is null) || (!isMyWidgets && cachedSpecs is null))
            {
                cardsHostContainer.Child = CreateGallerySkeletonGrid(cardCount: 6);
                await Task.Delay(32);
                if (_shell.IsClosed || _session.Page != StudioPage.Start || runId != _galleryHydrationRunId)
                {
                    return;
                }
            }

            if (isMyWidgets)
            {
                cachedExistingItems ??= (await Task.Run(GetExistingWidgetGalleryItems)).ToList();
                if (_shell.IsClosed || _session.Page != StudioPage.Start || runId != _galleryHydrationRunId)
                {
                    return;
                }

                string updatedTabLabel = cachedExistingItems.Count > 0
                    ? _loc.Format("CustomWidget.Workshop.MyWidgets.TabCount", cachedExistingItems.Count)
                    : _loc.T("CustomWidget.Workshop.MyWidgets.Tab");
                myWidgetsSegItem.Content = updatedTabLabel;
                AutomationProperties.SetName(myWidgetsSegItem, updatedTabLabel);

                myWidgetsSearchBox.Visibility = cachedExistingItems.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (cachedExistingItems.Count == 0)
                {
                    cardsHostContainer.Child = CreateMyWidgetsEmptyState(SwitchToTemplatesMode);
                    if (animate)
                    {
                        FluentMotionHelper.PlayEntranceSlideAndFade(cardsHostContainer, offsetY: 6f, durationMs: 150);
                    }

                    return;
                }

                string searchQuery = (myWidgetsSearchBox.Text ?? string.Empty).Trim();
                List<ExistingWidgetCardItem> filteredExisting = string.IsNullOrEmpty(searchQuery)
                    ? cachedExistingItems
                    : cachedExistingItems.Where(item =>
                            item.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
                            item.Id.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
                            item.Summary.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                if (filteredExisting.Count == 0)
                {
                    var emptyFilterPanel = new StackPanel
                    {
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 32, 0, 32)
                    };
                    emptyFilterPanel.Children.Add(new TextBlock
                    {
                        Text = _loc.T("CustomWidget.MyWidgets.SearchEmpty"),
                        Style = StudioUiPrimitives.TextStyle("BodyTextBlockStyle"),
                        Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                    var clearSearchBtn = new Button
                    {
                        Content = _loc.T("CustomWidget.Workshop.Composer.Clear"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        CornerRadius = new CornerRadius(6)
                    };
                    clearSearchBtn.Click += (_, _) => myWidgetsSearchBox.Text = string.Empty;
                    emptyFilterPanel.Children.Add(clearSearchBtn);
                    cardsHostContainer.Child = emptyFilterPanel;
                    return;
                }

                cardsHostContainer.Child = CreateVirtualizedCardsRepeater(
                    filteredExisting,
                    runId,
                    cardsHostContainer.ActualWidth,
                    (item, slotState) => CreateExistingWidgetCard(
                        item,
                        slotState,
                        runId,
                        onWidgetRemoved: () =>
                        {
                            StudioGalleryThumbnailCache.DeleteAllUserWidgetThumbs(item.Id);
                            cachedExistingItems = null;
                            requestRender?.Invoke(true);
                        }));
            }
            else
            {
                cachedSpecs ??= await Task.Run(GetWorkshopGallerySpecs);
                if (_shell.IsClosed || _session.Page != StudioPage.Start || runId != _galleryHydrationRunId)
                {
                    return;
                }

                RefreshSyncButtonTooltip();

                List<WorkshopCardSpec> filtered = _activeGalleryCategory == TemplateGalleryCategory.All
                    ? cachedSpecs.ToList()
                    : cachedSpecs.Where(s => s.Category == _activeGalleryCategory).ToList();

                string tplQuery = (myWidgetsSearchBox.Text ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(tplQuery))
                {
                    filtered = filtered.Where(s =>
                    {
                        string title = ResolveCardTitle(s);
                        string desc = ResolveCardDescription(s);
                        return title.Contains(tplQuery, StringComparison.OrdinalIgnoreCase) ||
                               desc.Contains(tplQuery, StringComparison.OrdinalIgnoreCase) ||
                               s.Id.Contains(tplQuery, StringComparison.OrdinalIgnoreCase);
                    }).ToList();
                }

                cardsHostContainer.Child = CreateVirtualizedCardsRepeater(
                    filtered,
                    runId,
                    cardsHostContainer.ActualWidth,
                    (spec, slotState) => CreateTemplateCard(spec, slotState, runId));
            }

            if (animate)
            {
                FluentMotionHelper.PlayEntranceSlideAndFade(cardsHostContainer, offsetY: 6f, durationMs: 150);
            }
        }

        void RenderGallerySection(bool animate)
        {
            DisposeTemplatePreviews();
            int runId = ++_galleryHydrationRunId;
            _ = HydrateGallerySectionAsync(runId, animate);
        }

        requestRender = RenderGallerySection;
        myWidgetsSearchBox.TextChanged += (_, _) =>
        {
            _pendingSearchRenderAction = requestRender;
            if (_searchDebounceTimer is null)
            {
                _searchDebounceTimer = _shell.DispatcherQueue.CreateTimer();
                _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(180);
                _searchDebounceTimer.IsRepeating = false;
                _searchDebounceTimer.Tick += (_, _) =>
                {
                    if (!_shell.IsClosed && _session.Page == StudioPage.Start)
                    {
                        _pendingSearchRenderAction?.Invoke(false);
                    }
                };
            }

            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        };

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

        syncCatalogButton.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "SyncTemplateCatalog", async () =>
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

                if (_shell.IsClosed || _session.Page != StudioPage.Start)
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
                if (!_shell.IsClosed)
                {
                    syncCatalogButton.Content = CreateSyncIdleIcon();
                    syncCatalogButton.IsEnabled = true;
                }
            }
        });

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
            Thickness targetPadding = w switch
            {
                < 860 => new Thickness(24, 20, 24, 28),
                < 1220 => new Thickness(32, 24, 32, 32),
                _ => new Thickness(40, 28, 40, 36)
            };
            if (column.Padding != targetPadding)
            {
                column.Padding = targetPadding;
            }
        };

        _shell.SetPage(scroller);

        _shell.DispatcherQueue.TryEnqueue(() =>
        {
            ideaBox.Focus(FocusState.Programmatic);
            ideaBox.SelectionStart = ideaBox.Text.Length;
        });
    }

    private FrameworkElement CreateVirtualizedCardsRepeater<TItem>(
        IReadOnlyList<TItem> items,
        int runId,
        double initialAvailableWidth,
        Func<TItem, GallerySlotState, FrameworkElement> buildCard)
    {
        // Row-virtualized StackLayout (instead of UniformGridLayout) avoids WinUI 3's
        // UniformGridLayout anchor-modulo misalignment (empty top-right slots when recycling
        // top items) and eliminates ScrollViewer scroll-anchoring snap-back on slow wheel ticks
        // because every row has an exact invariant height (GalleryCardRowHeight = 254px).
        int activeColumns = CustomWidgetWorkshopFlow.ResolveGalleryColumnCount(
            initialAvailableWidth > 0 ? initialAvailableWidth : 960);

        var rowLayout = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 14
        };

        var repeater = new ItemsRepeater
        {
            Layout = rowLayout,
            VerticalCacheLength = 2.0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = _shell.ShellRootGrid.Resources.TryGetValue("StudioGalleryCardSlotTemplate", out object? tplObj) &&
                           tplObj is DataTemplate slotTemplate
                ? slotTemplate
                : null,
            // Use primitive int[] row indices so WinUI 3 ItemsRepeater never marshals custom managed types under NativeAOT.
            ItemsSource = Enumerable.Range(0, StudioGalleryThumbnailCache.ComputeRowCount(items.Count, activeColumns)).ToArray()
        };

        void ClearRowSlot(Border slot)
        {
            if (slot.Tag is GalleryRowSlotState rowState)
            {
                foreach (GallerySlotState cardSlot in rowState.CardSlots)
                {
                    cardSlot.Generation++;
                    if (cardSlot.AttachedImage is { } attachedImg)
                    {
                        attachedImg.Source = null;
                        cardSlot.AttachedImage = null;
                    }

                    if (_activeLiveCapture is { } active && ReferenceEquals(active.Slot, cardSlot))
                    {
                        DisposeActiveLiveCapture();
                    }

                    cardSlot.PreviewWell = null;
                }

                rowState.CardSlots.Clear();
            }

            slot.Child = null;
        }

        repeater.ElementPrepared += (_, args) =>
        {
            if (args.Element is not Border slot)
            {
                return;
            }

            ClearRowSlot(slot);

            (int startIndex, int count) = StudioGalleryThumbnailCache.ResolveRowSlice(items.Count, activeColumns, args.Index);
            if (count <= 0)
            {
                return;
            }

            if (slot.Tag is not GalleryRowSlotState rowState)
            {
                rowState = new GalleryRowSlotState();
                slot.Tag = rowState;
            }

            slot.Height = StudioGalleryThumbnailCache.GalleryCardRowHeight;
            var rowGrid = new Grid
            {
                Height = StudioGalleryThumbnailCache.GalleryCardRowHeight,
                ColumnSpacing = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            for (int c = 0; c < activeColumns; c++)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (int col = 0; col < count; col++)
            {
                var cardSlot = new GallerySlotState
                {
                    RunId = runId,
                    Generation = 1
                };
                rowState.CardSlots.Add(cardSlot);

                FrameworkElement card = buildCard(items[startIndex + col], cardSlot);
                Grid.SetColumn(card, col);
                rowGrid.Children.Add(card);
            }

            slot.Child = rowGrid;
        };

        repeater.ElementClearing += (_, args) =>
        {
            if (args.Element is Border slot)
            {
                ClearRowSlot(slot);
            }
        };

        var widthHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = repeater
        };
        widthHost.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0)
            {
                return;
            }

            int targetColumns = CustomWidgetWorkshopFlow.ResolveGalleryColumnCount(e.NewSize.Width);
            if (targetColumns == activeColumns)
            {
                return;
            }

            activeColumns = targetColumns;
            repeater.ItemsSource = Enumerable.Range(
                0,
                StudioGalleryThumbnailCache.ComputeRowCount(items.Count, activeColumns)).ToArray();
        };

        return widthHost;
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
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "CardBackgroundFillColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var stack = new StackPanel { Spacing = 8 };
        var skelBar1 = new Border
        {
            Width = 88,
            Height = 10,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush")
        };
        FluentMotionHelper.StartSkeletonPulse(skelBar1, delayMs: 0);
        stack.Children.Add(skelBar1);

        var skelBar2 = new Border
        {
            Height = 42,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush")
        };
        FluentMotionHelper.StartSkeletonPulse(skelBar2, delayMs: 110);
        stack.Children.Add(skelBar2);

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var leftBar = new Border
        {
            Height = 24,
            CornerRadius = new CornerRadius(5),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush")
        };
        FluentMotionHelper.StartSkeletonPulse(leftBar, delayMs: 210);
        row.Children.Add(leftBar);
        var rightBar = new Border
        {
            Height = 24,
            CornerRadius = new CornerRadius(5),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush")
        };
        FluentMotionHelper.StartSkeletonPulse(rightBar, delayMs: 290);
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
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
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
                Background = StudioUiPrimitives.ThemeBrush(_shell, "LayerOnMicaBaseAltFillColorSecondaryBrush"),
                BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush"),
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
                Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush")
            });
            textStack.Children.Add(new Border
            {
                Height = 11,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush")
            });
            textStack.Children.Add(new Border
            {
                Width = 180,
                Height = 11,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorTertiaryBrush")
            });
            Grid.SetRow(textStack, 1);
            cardGrid.Children.Add(textStack);

            cards.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
                Background = StudioUiPrimitives.ThemeBrush(_shell, "CardBackgroundFillColorDefaultBrush"),
                Child = cardGrid
            });
        }

        rootStack.Children.Add(CreateResponsiveCardsGrid(cards));
        return rootStack;
    }

    private string ResolveCardTitle(WorkshopCardSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.DirectLabel))
        {
            return spec.DirectLabel!;
        }

        if (ResolveCardLocaleEntry(spec.Locales, "meta.name", "widgetName", "name") is { } localeTitle)
        {
            return localeTitle;
        }

        return string.IsNullOrWhiteSpace(spec.LabelKey) ? spec.Id : _loc.T(spec.LabelKey);
    }

    private string ResolveCardDescription(WorkshopCardSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.DirectDescription))
        {
            return spec.DirectDescription!;
        }

        if (ResolveCardLocaleEntry(spec.Locales, "meta.description", "widgetDescription", "description") is { } localeDesc)
        {
            return localeDesc;
        }

        return string.IsNullOrWhiteSpace(spec.DescriptionKey) ? string.Empty : _loc.T(spec.DescriptionKey);
    }

    private string? ResolveCardLocaleEntry(CustomWidgetLocaleCatalog? catalog, params string[] keys)
    {
        if (catalog is not { HasAnyLocale: true } || keys.Length == 0)
        {
            return null;
        }

        string activeCulture = _loc.CurrentCultureName;
        IReadOnlyDictionary<string, string>? dict = null;
        foreach ((string code, IReadOnlyDictionary<string, string> candidate) in catalog.Locales)
        {
            if (string.Equals(code, activeCulture, StringComparison.OrdinalIgnoreCase))
            {
                dict = candidate;
                break;
            }
        }

        dict ??= activeCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? (catalog.Locales.TryGetValue(LocalizationService.IsTraditionalChineseCulture(activeCulture) ? "zh-TW" : "zh-CN", out var zhDict) ? zhDict : null)
            : (catalog.Locales.TryGetValue("en-US", out var enDict) ? enDict : null);

        if (dict is null && catalog.Locales.TryGetValue("zh-CN", out var fallbackDict))
        {
            dict = fallbackDict;
        }

        if (dict is not null)
        {
            foreach (string key in keys)
            {
                if (dict.TryGetValue(key, out string? val) && !string.IsNullOrWhiteSpace(val))
                {
                    return val;
                }
            }
        }

        return null;
    }

    private FrameworkElement CreateTemplateCard(WorkshopCardSpec spec, GallerySlotState slotState, int runId)
    {
        string label = ResolveCardTitle(spec);
        string description = ResolveCardDescription(spec);
        string cardBodyText = spec.RequiresClientUpgrade
            ? _loc.Format("CustomWidget.ClientUpgrade.CardHint", spec.UpgradeRequirementLabel)
            : description;
        string cleanSpecVer = StudioManifestInspector.TrimLeadingVersionPrefix(spec.Version);
        string sourceLabel = _loc.T(spec.IsRemoteOnly
            ? "CustomWidget.Workshop.Card.SourceRemoteNew"
            : spec.IsRemoteUpgraded
                ? "CustomWidget.Workshop.Card.SourceRemoteUpdated"
                : "CustomWidget.Workshop.Card.SourceBuiltIn");
        string tooltipHeader = $"{label} · v{cleanSpecVer} · {sourceLabel}";

        void OpenTemplateInStudio()
        {
            if (spec.RequiresClientUpgrade || string.IsNullOrWhiteSpace(spec.ManifestJson))
            {
                return;
            }

            _session.SelectedEmoji = WidgetTitleIconKindNames.FromCustomTemplateId(spec.Id) is not null
                ? null
                : (CustomWidgetTemplates.IsEmojiIcon(spec.Emoji) ? spec.Emoji : null);
            _session.PendingPrompt = string.Empty;
            _session.PendingDisplayPrompt = null;
            _session.AddRevision(
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
            _shell.StageViewport.ShowPreviewPage(justLoaded: true);
            _shell.DispatcherQueue.TryEnqueue(() => _shell.ChatPanel.RefineTextBox?.Focus(FocusState.Programmatic));
        }

        (Grid container, Button card, Border previewWell, StackPanel rightMetaCluster) = BuildGalleryCardShell(
            emoji: spec.Emoji,
            title: label,
            bodyText: cardBodyText,
            tooltip: string.IsNullOrWhiteSpace(cardBodyText) ? tooltipHeader : $"{tooltipHeader}\n{cardBodyText}",
            isEnabled: !spec.RequiresClientUpgrade,
            onPrimaryClick: OpenTemplateInStudio);

        if (!spec.RequiresClientUpgrade && (spec.IsRemoteOnly || spec.IsRemoteUpgraded))
        {
            rightMetaCluster.Children.Insert(0, CreateGalleryCardMetaBadge(
                _loc.T(spec.IsRemoteOnly ? "CustomWidget.Workshop.Card.BadgeNew" : "CustomWidget.Workshop.Card.BadgeUpdated"),
                accentForeground: true));
        }

        FrameworkElement? previewScaleTarget = null;
        slotState.PreviewWell = previewWell;
        if (!string.IsNullOrWhiteSpace(spec.ManifestJson))
        {
            ElementTheme explicitTheme = StudioUiPrimitives.GetStudioExplicitTheme(_shell);
            string materialFp = StudioGalleryThumbnailCache.BuildMaterialFingerprint(_hostBridge.GetShellVisualSettings());
            string cachePngPath = StudioGalleryThumbnailCache.ResolveTemplateThumbCachePath(
                spec.Id,
                cleanSpecVer,
                explicitTheme,
                _loc.CurrentCultureName,
                materialFp);
            HydrateGalleryCardThumbnail(
                runId: runId,
                slotState: slotState,
                cachePngPath: cachePngPath,
                userWidgetIdForStalePrune: null,
                label: label,
                emoji: spec.Emoji,
                manifestJson: spec.ManifestJson,
                logicJs: spec.LogicJs,
                locales: spec.Locales,
                existingWidgetId: null,
                previewWell: previewWell,
                setScaleTarget: target => previewScaleTarget = target);
        }
        else
        {
            previewWell.Child = CreateEmojiFallbackWellContent(spec.Emoji);
        }

        if (!spec.RequiresClientUpgrade && !string.IsNullOrWhiteSpace(spec.ManifestJson))
        {
            Button? quickPlaceBtn = null;
            quickPlaceBtn = CreateHoverQuickIconButton(
                glyph: "\uE710",
                rightMargin: 10,
                tooltip: _loc.T("CustomWidget.Workshop.Card.QuickAddTip"),
                itemTitle: label,
                isCritical: false,
                toggleHitTest: false,
                onClick: () => _ = StudioUiPrimitives.SafeInvokeAsync(
                    _shell,
                    "QuickPlaceTemplate",
                    () => _shell.CommitAndWorkspace.PlaceTemplateDirectlyAsync(
                        spec.ManifestJson,
                        spec.Emoji,
                        quickPlaceBtn!,
                        spec.Locales,
                        spec.LogicJs)));
            container.Children.Add(quickPlaceBtn);

            string remixLabel = _loc.T("CustomWidget.Workshop.Card.RemixAction");
            var remixBtn = new Button
            {
                MinHeight = 28,
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(6),
                IsTabStop = false,
                Content = StudioUiPrimitives.CreateSmallIconLabel("\uE70F", remixLabel)
            };
            AutomationProperties.SetName(remixBtn, $"{label} — {remixLabel}");
            remixBtn.Click += (_, _) => OpenTemplateInStudio();

            var remixButtonHost = new Border
            {
                Margin = new Thickness(0, 0, 10, 8),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = StudioUiPrimitives.ThemeBrush(_shell, "SolidBackgroundFillColorBaseBrush"),
                IsHitTestVisible = false,
                Opacity = 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
                Child = remixBtn
            };
            Canvas.SetZIndex(remixButtonHost, 2);
            container.Children.Add(remixButtonHost);

            WireGalleryCardHoverChrome(container, card, () => previewScaleTarget, [quickPlaceBtn], toggleButtonHitTest: false, remixButtonHost);
        }

        return container;
    }

    private void HydrateGalleryCardThumbnail(
        int runId,
        GallerySlotState slotState,
        string cachePngPath,
        string? userWidgetIdForStalePrune,
        string label,
        string emoji,
        string manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        string? existingWidgetId,
        Border previewWell,
        Action<FrameworkElement> setScaleTarget)
    {
        int capturedGen = slotState.Generation;
        var captureJob = new PendingThumbnailCaptureJob(
            RunId: runId,
            SlotGeneration: capturedGen,
            Slot: slotState,
            CachePngPath: cachePngPath,
            UserWidgetIdForStalePrune: userWidgetIdForStalePrune,
            Label: label,
            Emoji: emoji,
            ManifestJson: manifestJson,
            LogicJs: logicJs,
            Locales: locales,
            ExistingWidgetId: existingWidgetId,
            PreviewWell: previewWell,
            SetScaleTarget: setScaleTarget);

        if (File.Exists(cachePngPath))
        {
            _ = LoadCachedGalleryThumbnailAsync(
                cachePngPath,
                previewWell,
                emoji,
                runId,
                capturedGen,
                slotState,
                setScaleTarget,
                fallbackEnqueue: () => EnqueueThumbnailCapture(captureJob));
            return;
        }

        EnqueueThumbnailCapture(captureJob);
    }

    private void EnqueueThumbnailCapture(PendingThumbnailCaptureJob job)
    {
        if (_shell.IsClosed || _session.Page != StudioPage.Start || job.RunId != _galleryHydrationRunId || !job.Slot.IsAlive(job.RunId, job.SlotGeneration))
        {
            return;
        }

        _thumbnailCaptureQueue.Enqueue(job);
        if (!_isThumbnailCapturePumpRunning)
        {
            _isThumbnailCapturePumpRunning = true;
            _ = PumpThumbnailCaptureQueueAsync();
        }
    }

    private async Task PumpThumbnailCaptureQueueAsync()
    {
        try
        {
            while (_thumbnailCaptureQueue.Count > 0)
            {
                PendingThumbnailCaptureJob job = _thumbnailCaptureQueue.Dequeue();
                if (_shell.IsClosed ||
                    _session.Page != StudioPage.Start ||
                    job.RunId != _galleryHydrationRunId ||
                    !job.Slot.IsAlive(job.RunId, job.SlotGeneration))
                {
                    continue;
                }

                if (File.Exists(job.CachePngPath))
                {
                    await LoadCachedGalleryThumbnailAsync(
                        job.CachePngPath,
                        job.PreviewWell,
                        job.Emoji,
                        job.RunId,
                        job.SlotGeneration,
                        job.Slot,
                        job.SetScaleTarget,
                        fallbackEnqueue: null);
                    continue;
                }

                await CaptureSwapAndDisposeSingleSlotAsync(job);

                if (_thumbnailCaptureQueue.Count > 0 && !_shell.IsClosed && _session.Page == StudioPage.Start)
                {
                    await Task.Delay(16);
                }
            }
        }
        finally
        {
            _isThumbnailCapturePumpRunning = false;
            if (_thumbnailCaptureQueue.Count > 0 && !_shell.IsClosed && _session.Page == StudioPage.Start)
            {
                _isThumbnailCapturePumpRunning = true;
                _ = PumpThumbnailCaptureQueueAsync();
            }
        }
    }

    private async Task CaptureSwapAndDisposeSingleSlotAsync(PendingThumbnailCaptureJob job)
    {
        CustomWidgetViewModel? vm = null;
        CustomWidgetContent? content = null;
        try
        {
            var thumbConfig = new WidgetConfig
            {
                Id = (job.ExistingWidgetId is not null ? "__studio_mywidget_" : "__studio_template_") + (job.ExistingWidgetId ?? job.Label),
                Name = job.Label,
                WidgetKind = WidgetKind.Custom,
                Width = 324,
                Height = 250
            };
            vm = new CustomWidgetViewModel(
                thumbConfig,
                _loc,
                store: null,
                dispatcherQueue: _shell.DispatcherQueue,
                notificationServiceProvider: null,
                isPreviewOnly: true);
            vm.ApplyPreviewBundle(job.ManifestJson, job.LogicJs, job.Locales, preserveRuntimeData: false);
            CustomWidgetSizeSpec rawSize = vm.ManifestInternal.DefaultSize;
            var thumbSize = new CustomWidgetSizeSpec
            {
                Width = Math.Clamp(rawSize.Width, 300, 356),
                Height = Math.Clamp(rawSize.Height, 224, 268)
            };
            thumbConfig.Width = thumbSize.Width;
            thumbConfig.Height = thumbSize.Height;
            content = new CustomWidgetContent(vm);
            _activeLiveCapture = (vm, content, job.Slot);

            Border thumbFrame = _shell.PreviewTheme.CreateWidgetFrame(
                job.Emoji,
                job.Label,
                content,
                thumbSize,
                clipViewport: true,
                titleIcon: out _,
                titleText: out _,
                existingWidgetId: job.ExistingWidgetId);
            var previewViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(14, 12, 14, 12),
                Opacity = 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
                Child = thumbFrame
            };
            job.PreviewWell.Child = previewViewbox;
            previewViewbox.Opacity = 1;
            job.SetScaleTarget(previewViewbox);

            await WaitForThumbFrameLoadedAsync(thumbFrame, timeoutMs: 320);
            if (_shell.IsClosed ||
                _session.Page != StudioPage.Start ||
                job.RunId != _galleryHydrationRunId ||
                !job.Slot.IsAlive(job.RunId, job.SlotGeneration) ||
                !thumbFrame.IsLoaded)
            {
                return;
            }

            thumbFrame.UpdateLayout();
            await Task.Delay(90);
            if (_shell.IsClosed ||
                _session.Page != StudioPage.Start ||
                job.RunId != _galleryHydrationRunId ||
                !job.Slot.IsAlive(job.RunId, job.SlotGeneration) ||
                !thumbFrame.IsLoaded)
            {
                return;
            }

            byte[]? pngBytes = await RenderFrameToPngBytesAsync(thumbFrame);
            if (pngBytes is null || pngBytes.Length == 0)
            {
                if (job.Slot.IsAlive(job.RunId, job.SlotGeneration))
                {
                    job.PreviewWell.Child = CreateEmojiFallbackWellContent(job.Emoji);
                }
                return;
            }

            // Persist thumbnail off the UI thread and clean stale user-widget hashes.
            _ = Task.Run(async () =>
            {
                await CustomWidgetAgentStageStore.WriteCachedThumbnailPngAtomicallyAsync(job.CachePngPath, pngBytes);
                if (!string.IsNullOrWhiteSpace(job.UserWidgetIdForStalePrune))
                {
                    StudioGalleryThumbnailCache.DeleteStaleUserWidgetThumbs(job.UserWidgetIdForStalePrune!, job.CachePngPath);
                }
            });

            // Immediately swap the live WinUI/QuickJS tree for a lightweight static Image before disposing vm & content.
            Image? staticImg = await CreateStaticThumbnailImageAsync(pngBytes);
            if (!_shell.IsClosed &&
                _session.Page == StudioPage.Start &&
                job.RunId == _galleryHydrationRunId &&
                job.Slot.IsAlive(job.RunId, job.SlotGeneration))
            {
                if (staticImg is not null)
                {
                    job.PreviewWell.Child = staticImg;
                    job.Slot.AttachedImage = staticImg;
                    job.SetScaleTarget(staticImg);
                }
                else
                {
                    job.PreviewWell.Child = CreateEmojiFallbackWellContent(job.Emoji);
                }
            }
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Gallery thumbnail capture failed for {job.Label}: {ex.Message}");
            if (!_shell.IsClosed && job.Slot.IsAlive(job.RunId, job.SlotGeneration))
            {
                job.PreviewWell.Child = CreateEmojiFallbackWellContent(job.Emoji);
            }
        }
        finally
        {
            if (_activeLiveCapture is { } active && ReferenceEquals(active.Vm, vm))
            {
                DisposeActiveLiveCapture();
            }
            else if (content is null && vm is not null)
            {
                try { vm.Dispose(); } catch { }
            }
        }
    }

    private static async Task WaitForThumbFrameLoadedAsync(FrameworkElement frame, int timeoutMs)
    {
        if (frame.IsLoaded)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs e) => tcs.TrySetResult();
        frame.Loaded += OnLoaded;
        try
        {
            if (!frame.IsLoaded)
            {
                await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            }
        }
        finally
        {
            frame.Loaded -= OnLoaded;
        }
    }

    private static async Task<byte[]?> RenderFrameToPngBytesAsync(Border thumbFrame)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(thumbFrame);
        if (rtb.PixelWidth <= 0 || rtb.PixelHeight <= 0)
        {
            return null;
        }

        IBuffer pixelBuf = await rtb.GetPixelsAsync();
        byte[] bgra = pixelBuf.ToArray();
        if (bgra.Length == 0)
        {
            return null;
        }

        using var memStream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memStream);
        double dpi = 96.0 * (thumbFrame.XamlRoot?.RasterizationScale ?? 1.0);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            dpi,
            dpi,
            bgra);
        await encoder.FlushAsync();

        byte[] pngBytes = new byte[(int)memStream.Size];
        memStream.Seek(0);
        using Stream readStream = memStream.AsStreamForRead();
        int offset = 0;
        while (offset < pngBytes.Length)
        {
            int read = await readStream.ReadAsync(pngBytes.AsMemory(offset, pngBytes.Length - offset));
            if (read <= 0)
            {
                break;
            }
            offset += read;
        }

        return pngBytes;
    }

    private static async Task<Image?> CreateStaticThumbnailImageAsync(byte[] pngBytes, bool fadeIn = false)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);

        var bmp = new BitmapImage { DecodePixelWidth = StudioGalleryThumbnailCache.ThumbnailDecodePixelWidth };
        await bmp.SetSourceAsync(stream);

        var img = new Image
        {
            Source = bmp,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(14, 12, 14, 12),
            Opacity = fadeIn ? 0 : 1
        };
        if (fadeIn)
        {
            img.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) };
        }

        return img;
    }

    private async Task LoadCachedGalleryThumbnailAsync(
        string cachePngPath,
        Border previewWell,
        string fallbackEmoji,
        int runId,
        int slotGeneration,
        GallerySlotState slotState,
        Action<FrameworkElement> setScaleTarget,
        Action? fallbackEnqueue)
    {
        try
        {
            byte[] pngBytes = await File.ReadAllBytesAsync(cachePngPath);
            if (_shell.IsClosed ||
                _session.Page != StudioPage.Start ||
                runId != _galleryHydrationRunId ||
                !slotState.IsAlive(runId, slotGeneration) ||
                pngBytes.Length == 0)
            {
                return;
            }

            Image? img = await CreateStaticThumbnailImageAsync(pngBytes, fadeIn: true);
            if (img is null ||
                _shell.IsClosed ||
                _session.Page != StudioPage.Start ||
                runId != _galleryHydrationRunId ||
                !slotState.IsAlive(runId, slotGeneration))
            {
                return;
            }

            previewWell.Child = img;
            img.Opacity = 1;
            slotState.AttachedImage = img;
            setScaleTarget(img);
        }
        catch
        {
            if (!_shell.IsClosed &&
                _session.Page == StudioPage.Start &&
                runId == _galleryHydrationRunId &&
                slotState.IsAlive(runId, slotGeneration))
            {
                if (fallbackEnqueue is not null)
                {
                    fallbackEnqueue();
                }
                else
                {
                    previewWell.Child = CreateEmojiFallbackWellContent(fallbackEmoji);
                }
            }
        }
    }

    private FrameworkElement CreateExistingWidgetCard(
        ExistingWidgetCardItem item,
        GallerySlotState slotState,
        int runId,
        Action? onWidgetRemoved = null)
    {
        string cleanManifestVer = StudioManifestInspector.TrimLeadingVersionPrefix(item.ManifestVersion);
        string myCardHeader = item.LastUpdatedUtcTicks > 0
            ? _loc.Format(
                "CustomWidget.Workshop.MyWidgets.CardTooltipMeta",
                item.Name,
                cleanManifestVer,
                FormatGalleryRelativeTimestamp(new DateTimeOffset(item.LastUpdatedUtcTicks, TimeSpan.Zero)))
            : $"{item.Name} · v{cleanManifestVer}";

        void OpenWidgetInStudio()
        {
            _activeStartGalleryMode = StartGallerySourceMode.MyWidgets;
            _shell.Initialize(item.Id, initialIdea: null);
            _shell.DispatcherQueue.TryEnqueue(() => _shell.ChatPanel.RefineTextBox?.Focus(FocusState.Programmatic));
        }

        void ForkWidgetInStudio()
        {
            _shell.InitializeForkFromWidget(item.Id, initialIdea: null);
            _shell.DispatcherQueue.TryEnqueue(() => _shell.ChatPanel.RefineTextBox?.Focus(FocusState.Programmatic));
        }

        (Grid container, Button card, Border previewWell, StackPanel rightMetaRow) = BuildGalleryCardShell(
            emoji: item.Emoji,
            title: item.Name,
            bodyText: item.Summary,
            tooltip: string.IsNullOrWhiteSpace(item.Summary) ? myCardHeader : $"{myCardHeader}\n{item.Summary}",
            isEnabled: true,
            onPrimaryClick: OpenWidgetInStudio);

        bool isVisibleLive = item.IsVisibleOnDesktop;
        var statusText = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        var statusBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = statusText
        };
        void RefreshStatusVisual()
        {
            statusText.Text = _loc.T(isVisibleLive ? "CustomWidget.Studio.Card.StatusVisible" : "CustomWidget.Studio.Card.StatusHidden");
            statusText.Foreground = StudioUiPrimitives.ThemeBrush(_shell, isVisibleLive ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
            statusBadge.Background = StudioUiPrimitives.ThemeBrush(_shell, isVisibleLive ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush");
        }
        RefreshStatusVisual();
        int metaInsertIdx = 0;
        rightMetaRow.Children.Insert(metaInsertIdx++, statusBadge);
        if (item.RevisionCount > 1)
        {
            rightMetaRow.Children.Insert(metaInsertIdx, CreateGalleryCardMetaBadge($"v{item.RevisionCount}", accentForeground: false, monospace: true));
        }

        FrameworkElement? previewScaleTarget = null;
        slotState.PreviewWell = previewWell;
        ElementTheme explicitTheme = StudioUiPrimitives.GetStudioExplicitTheme(_shell);
        string materialFp = StudioGalleryThumbnailCache.BuildMaterialFingerprint(_hostBridge.GetShellVisualSettings());
        string contentHash = StudioGalleryThumbnailCache.ComputeUserWidgetContentHash(
            item.ManifestJson,
            item.LogicJs,
            item.Locales,
            _loc.CurrentCultureName);
        string cachePngPath = StudioGalleryThumbnailCache.ResolveUserWidgetThumbCachePath(
            item.Id,
            contentHash,
            explicitTheme,
            _loc.CurrentCultureName,
            materialFp);
        HydrateGalleryCardThumbnail(
            runId: runId,
            slotState: slotState,
            cachePngPath: cachePngPath,
            userWidgetIdForStalePrune: item.Id,
            label: item.Name,
            emoji: item.Emoji,
            manifestJson: item.ManifestJson,
            logicJs: item.LogicJs,
            locales: item.Locales,
            existingWidgetId: item.Id,
            previewWell: previewWell,
            setScaleTarget: target => previewScaleTarget = target);

        async Task LocateMyWidgetAsync(Button? disableBtn = null)
        {
            if (!_hostBridge.HasWidgetManager)
            {
                return;
            }

            if (disableBtn is not null)
            {
                disableBtn.IsEnabled = false;
            }
            try
            {
                if (!isVisibleLive)
                {
                    await _hostBridge.SetCustomWidgetInstanceEnabledAsync(item.Id, true);
                    isVisibleLive = true;
                    RefreshStatusVisual();
                }
                await _hostBridge.LocateCustomWidgetAsync(item.Id);
            }
            finally
            {
                if (disableBtn is not null)
                {
                    disableBtn.IsEnabled = true;
                }
            }
        }

        async Task ConfirmAndRemoveMyWidgetAsync()
        {
            if (_shell.IsDialogOpen || _shell.IsClosed || _shell.ShellRootGrid.XamlRoot is null || !_hostBridge.HasWidgetManager)
            {
                return;
            }

            _shell.IsDialogOpen = true;
            try
            {
                var confirmDialog = new ContentDialog
                {
                    XamlRoot = _shell.ShellRootGrid.XamlRoot,
                    RequestedTheme = StudioUiPrimitives.GetStudioExplicitTheme(_shell),
                    Title = _loc.Format("CustomWidget.DeleteConfirm.Title", item.Name),
                    Content = new TextBlock { Text = _loc.T("CustomWidget.DeleteConfirm.Note"), TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = _loc.T("Common.Delete"),
                    CloseButtonText = _loc.T("Common.Cancel"),
                    DefaultButton = ContentDialogButton.Close
                };
                if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) &&
                    style is Style dialogStyle)
                {
                    confirmDialog.Style = dialogStyle;
                }

                if (await confirmDialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await _hostBridge.RemoveCustomWidgetAsync(item.Id);
                    onWidgetRemoved?.Invoke();
                }
            }
            finally
            {
                _shell.IsDialogOpen = false;
            }
        }

        var myCardMenu = new MenuFlyout();
        myCardMenu.Opening += (_, _) =>
        {
            var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
            presenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, StudioUiPrimitives.GetStudioExplicitTheme(_shell)));
            myCardMenu.MenuFlyoutPresenterStyle = presenterStyle;
        };
        AddCardContextItem(myCardMenu, "\uE70F", _loc.T("CustomWidget.Action.EditInStudio"), OpenWidgetInStudio);
        AddCardContextItem(myCardMenu, "\uE8C8", _loc.T("CustomWidget.Action.ForkInStudio"), ForkWidgetInStudio);
        AddCardContextItem(myCardMenu, "\uE8A7", _loc.T("CustomWidget.MyWidgets.LocateButton"),
            () => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "LocateMyWidgetMenu", () => LocateMyWidgetAsync()));
        AddCardContextItem(myCardMenu, "\uEDE1", _loc.T("CustomWidget.Workshop.More.ExportPackage"),
            () => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "ExportMyWidgetMenu",
                () => _shell.CommitAndWorkspace.ExportWidgetBundleInteractiveAsync(item.ManifestJson, item.LogicJs, item.Locales, item.Name)));
        myCardMenu.Items.Add(new MenuFlyoutSeparator());
        AddCardContextItem(myCardMenu, "\uE74D", _loc.T("CustomWidget.MyWidgets.DeleteMenu"),
            () => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "DeleteMyWidgetMenu", ConfirmAndRemoveMyWidgetAsync));

        card.ContextFlyout = myCardMenu;
        container.ContextFlyout = myCardMenu;

        Button quickExportBtn = CreateHoverQuickIconButton(
            "\uEDE1", 124, _loc.T("CustomWidget.Workshop.More.ExportPackage"), item.Name, isCritical: false, toggleHitTest: true,
            () => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "QuickExportMyWidget",
                () => _shell.CommitAndWorkspace.ExportWidgetBundleInteractiveAsync(item.ManifestJson, item.LogicJs, item.Locales, item.Name)));
        Button quickForkBtn = CreateHoverQuickIconButton(
            "\uE8C8", 86, _loc.T("CustomWidget.Action.ForkInStudio"), item.Name, isCritical: false, toggleHitTest: true, ForkWidgetInStudio);
        Button? quickLocateBtn = null;
        quickLocateBtn = CreateHoverQuickIconButton(
            "\uE8A7", 48, _loc.T("CustomWidget.Workshop.MyWidgets.LocateTip"), item.Name, isCritical: false, toggleHitTest: true,
            () => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "QuickLocateMyWidget", () => LocateMyWidgetAsync(quickLocateBtn)));
        Button quickDeleteBtn = CreateHoverQuickIconButton(
            "\uE74D", 10, _loc.T("Common.Delete"), item.Name, isCritical: true, toggleHitTest: true,
            () => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "QuickDeleteMyWidget", ConfirmAndRemoveMyWidgetAsync));

        container.Children.Add(quickExportBtn);
        container.Children.Add(quickForkBtn);
        container.Children.Add(quickLocateBtn);
        container.Children.Add(quickDeleteBtn);

        WireGalleryCardHoverChrome(
            container,
            card,
            () => previewScaleTarget,
            [quickExportBtn, quickForkBtn, quickLocateBtn, quickDeleteBtn],
            toggleButtonHitTest: true);

        return container;
    }

    private (Grid Container, Button Card, Border PreviewWell, StackPanel RightMetaCluster) BuildGalleryCardShell(
        string emoji,
        string title,
        string bodyText,
        string tooltip,
        bool isEnabled,
        Action onPrimaryClick)
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
            CornerRadius = new CornerRadius(8),
            IsEnabled = isEnabled
        };
        AutomationProperties.SetName(card, title);
        ToolTipService.SetToolTip(card, tooltip);
        card.Click += (_, _) => onPrimaryClick();

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(164) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewWell = new Border
        {
            Background = StudioUiPrimitives.ThemeBrush(_shell, "LayerOnMicaBaseAltFillColorSecondaryBrush"),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            IsHitTestVisible = false,
            Child = CreateCardPreviewSkeleton()
        };
        cardGrid.Children.Add(previewWell);

        var textStack = new StackPanel { Spacing = 4, Padding = new Thickness(14, 12, 14, 14) };
        var titleRow = new Grid { ColumnSpacing = 6 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = emoji + "  " + title,
            Style = StudioUiPrimitives.TextStyle("BodyStrongTextBlockStyle"),
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
        rightMetaCluster.Children.Add(new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 11,
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(rightMetaCluster, 1);
        titleRow.Children.Add(rightMetaCluster);
        textStack.Children.Add(titleRow);

        textStack.Children.Add(new TextBlock
        {
            Text = bodyText,
            Style = StudioUiPrimitives.TextStyle("CaptionTextBlockStyle"),
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            MinHeight = 34
        });

        Grid.SetRow(textStack, 1);
        cardGrid.Children.Add(textStack);
        card.Content = cardGrid;
        container.Children.Add(card);
        return (container, card, previewWell, rightMetaCluster);
    }

    private Border CreateGalleryCardMetaBadge(string text, bool accentForeground, bool monospace = false) =>
        new()
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SubtleFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                FontFamily = monospace ? new FontFamily("Consolas, Cascadia Mono, monospace") : FontFamily.XamlAutoFontFamily,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, accentForeground ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush")
            }
        };

    private static TextBlock CreateEmojiFallbackWellContent(string emoji) =>
        new()
        {
            Text = emoji,
            FontSize = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

    private static void AddCardContextItem(MenuFlyout menu, string glyph, string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    private Button CreateHoverQuickIconButton(
        string glyph,
        double rightMargin,
        string tooltip,
        string itemTitle,
        bool isCritical,
        bool toggleHitTest,
        Action onClick)
    {
        var btn = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 10, rightMargin, 0),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = StudioUiPrimitives.ThemeBrush(_shell, "SolidBackgroundFillColorBaseBrush"),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "ControlElevationBorderBrush"),
            BorderThickness = new Thickness(1),
            Opacity = 0,
            IsHitTestVisible = !toggleHitTest,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
            Content = new FontIcon
            {
                Glyph = glyph,
                FontSize = 12.5,
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, isCritical ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush")
            }
        };
        Canvas.SetZIndex(btn, 2);
        ToolTipService.SetToolTip(btn, tooltip);
        AutomationProperties.SetName(btn, $"{itemTitle} — {tooltip}");
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static void WireGalleryCardHoverChrome(
        Grid container,
        Button card,
        Func<FrameworkElement?> getScaleTarget,
        IReadOnlyList<Button> quickButtons,
        bool toggleButtonHitTest,
        UIElement? extraHoverOverlay = null)
    {
        bool isPointerOver = false;
        void Refresh()
        {
            bool show = isPointerOver ||
                        card.FocusState != FocusState.Unfocused ||
                        quickButtons.Any(static b => b.FocusState != FocusState.Unfocused);
            foreach (Button b in quickButtons)
            {
                b.Opacity = show ? 1 : 0;
                if (toggleButtonHitTest)
                {
                    b.IsHitTestVisible = show;
                }
            }
            if (extraHoverOverlay is not null)
            {
                extraHoverOverlay.Opacity = show ? 1 : 0;
                extraHoverOverlay.IsHitTestVisible = show;
            }

            FluentMotionHelper.AnimateCardElevation(container, offsetY: isPointerOver ? -2f : 0f, scale: 1f, durationMs: 150);
            if (getScaleTarget() is { } scaleTarget)
            {
                FluentMotionHelper.AnimateHoverScale(scaleTarget, isPointerOver ? 1.018f : 1.0f, 150);
            }
        }

        container.PointerEntered += (_, _) => { isPointerOver = true; Refresh(); };
        container.PointerExited += (_, _) => { isPointerOver = false; Refresh(); };
        card.GotFocus += (_, _) => Refresh();
        card.LostFocus += (_, _) => Refresh();
        foreach (Button b in quickButtons)
        {
            b.GotFocus += (_, _) => Refresh();
            b.LostFocus += (_, _) => Refresh();
        }
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
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE8A9", _loc.T("CustomWidget.Workshop.MyWidgets.BrowseTemplates"))
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
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorTertiaryBrush"),
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
            Style = StudioUiPrimitives.TextStyle("CaptionTextBlockStyle"),
            Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        centerStack.Children.Add(browseBtn);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = StudioUiPrimitives.ThemeBrush(_shell, "CardStrokeColorDefaultBrush"),
            Background = StudioUiPrimitives.ThemeBrush(_shell, "CardBackgroundFillColorDefaultBrush"),
            Padding = new Thickness(24, 42, 24, 42),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = centerStack
        };
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
            return local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (local.Year == now.Year)
        {
            return local.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        return local.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void DisposeActiveLiveCapture()
    {
        if (_activeLiveCapture is not { } active)
        {
            return;
        }

        _activeLiveCapture = null;
        try { active.Content.Dispose(); } catch { }
        try { active.Vm.Dispose(); } catch { }
    }

    internal void DisposeTemplatePreviews()
    {
        _thumbnailCaptureQueue.Clear();
        DisposeActiveLiveCapture();
    }

    public void Dispose()
    {
        _galleryHydrationRunId++;
        if (_searchDebounceTimer is not null)
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer = null;
        }
        _pendingSearchRenderAction = null;
        _refreshAgentStageBanner = null;
        _refreshTemplateGalleryFromRemote = null;
        DisposeTemplatePreviews();
    }
}
