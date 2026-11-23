using System.IO.Compression;
using System.Text;
using Windows.Graphics;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchRedesignAndEnhancementsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeskBox-SearchRedesign-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("=128*3.5", "448")]
    [InlineData("(1920-660)/2", "630")]
    [InlineData("2^10", "1024")]
    [InlineData("200*15%", "30")]
    [InlineData("（１００＋５０）×２", "300")]
    [InlineData("=1,5 + 2,5", "4")]
    [InlineData("19,99 * 2", "39.98")]
    [InlineData("0,125 * 8", "1")]
    [InlineData("1,000 + 2,500", "3500")]
    [InlineData("1.234,56 + 0,44", "1235")]
    [InlineData("1,234.56 + 0.44", "1235")]
    [InlineData("1_000_000 / 1_000", "1000")]
    public void CalculatorEvaluator_EvaluatesSupportedExpressions(string query, string expected)
    {
        bool evaluated = SearchCalculatorEvaluator.TryEvaluate(query, out SearchCalculationResult result);

        Assert.True(evaluated);
        Assert.Equal(expected, result.FormattedValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("1,5")]
    [InlineData("1,2,3 + 1")]
    [InlineData("2026-04-20")]
    [InlineData("2026/11/04")]
    [InlineData("v1.2.3")]
    [InlineData("report-2026")]
    public void CalculatorEvaluator_RejectsDatesIdentifiersAndBareNumbers(string query)
    {
        bool evaluated = SearchCalculatorEvaluator.TryEvaluate(query, out _);

        Assert.False(evaluated);
    }

    [Fact]
    public void QuerySyntaxParser_ParsesInlineScopesQuickCommandsAndPreservesDrivePaths()
    {
        ParsedSearchQuery appScope = SearchQuerySyntaxParser.Parse("app:vscode");
        Assert.Equal(SearchInlineScope.Apps, appScope.Scope);
        Assert.Equal("app:", appScope.ScopeBadgeText);
        Assert.Equal("vscode", appScope.EffectiveQuery);

        ParsedSearchQuery clipScope = SearchQuerySyntaxParser.Parse("clip:token");
        Assert.Equal(SearchInlineScope.Clipboard, clipScope.Scope);
        Assert.Equal("clip:", clipScope.ScopeBadgeText);
        Assert.Equal("token", clipScope.EffectiveQuery);

        ParsedSearchQuery cmdScope = SearchQuerySyntaxParser.Parse(">reload");
        Assert.Equal(SearchInlineScope.Commands, cmdScope.Scope);
        Assert.Equal(">", cmdScope.ScopeBadgeText);
        Assert.Equal("reload", cmdScope.EffectiveQuery);

        ParsedSearchQuery quickTodo = SearchQuerySyntaxParser.Parse("待办 下午三点评审");
        Assert.Equal("下午三点评审", quickTodo.QuickTodoText);

        ParsedSearchQuery quickNote = SearchQuerySyntaxParser.Parse("note 临时记录");
        Assert.Equal("临时记录", quickNote.QuickNoteText);

        ParsedSearchQuery drivePath = SearchQuerySyntaxParser.Parse(@"C:\Windows\System32");
        Assert.Equal(SearchInlineScope.None, drivePath.Scope);
        Assert.Null(drivePath.ScopeBadgeText);
        Assert.Equal(@"C:\Windows\System32", drivePath.EffectiveQuery);
    }

    [Fact]
    public void SurfacePolicy_Uses16PxCornerRadiusAndThemeAwareTintWhenFullyTransparent()
    {
        var accent = Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);

        SearchPopupMaterialResolution solidDarkZero = SearchPopupSurfacePolicy.ResolveMaterial(
            SettingsService.WidgetMaterialTypeSolid,
            widgetOpacity: 0.0,
            widgetMaterialIntensity: 0.35,
            isDark: true,
            accentColor: accent);
        SearchPopupMaterialResolution solidLightZero = SearchPopupSurfacePolicy.ResolveMaterial(
            SettingsService.WidgetMaterialTypeSolid,
            widgetOpacity: 0.0,
            widgetMaterialIntensity: 0.35,
            isDark: false,
            accentColor: accent);

        Assert.Equal(16.0, solidDarkZero.OuterCornerRadius);
        Assert.Equal(12.0, solidDarkZero.InputCapsuleCornerRadius);
        Assert.Equal(10.0, solidDarkZero.ResultRowCornerRadius);
        Assert.True(solidDarkZero.UseTransparentTintFallback);
        Assert.True(solidLightZero.UseTransparentTintFallback);
        Assert.True(solidDarkZero.SurfacePlateColor.A >= 200);
        Assert.True(solidLightZero.SurfacePlateColor.A >= 200);
        Assert.True(solidLightZero.SurfacePlateColor.R > solidDarkZero.SurfacePlateColor.R);

        SearchPopupMaterialResolution acrylicDarkZero = SearchPopupSurfacePolicy.ResolveMaterial(
            SettingsService.WidgetMaterialTypeAcrylic,
            widgetOpacity: 0.0,
            widgetMaterialIntensity: 0.5,
            isDark: true,
            accentColor: accent);
        SearchPopupMaterialResolution acrylicLightZero = SearchPopupSurfacePolicy.ResolveMaterial(
            SettingsService.WidgetMaterialTypeAcrylic,
            widgetOpacity: 0.0,
            widgetMaterialIntensity: 0.5,
            isDark: false,
            accentColor: accent);

        Assert.True(acrylicDarkZero.UseTransparentTintFallback);
        Assert.True(acrylicLightZero.UseTransparentTintFallback);
        Assert.True(acrylicDarkZero.EffectiveAcrylicSurfaceOpacity >= 0.5);
        Assert.True(acrylicLightZero.SurfacePlateColor.R > acrylicDarkZero.SurfacePlateColor.R);
    }

    [Fact]
    public void SurfacePolicy_CapsuleOnlyWhenRecommendationsOffAndExpandsDownwardFromTopAnchor()
    {
        var capsuleState = new SearchPopupLayoutState(
            HasQuery: false,
            IsSearching: false,
            ResultCount: 0,
            ShowFilterBar: false,
            ShowSortHeader: false,
            ShowEverythingBanner: false,
            ShowRecommendations: false,
            RecommendationEntryCount: 0,
            ShowAllHiddenRecommendationsHint: false,
            ShowNoResults: false,
            ShowFooter: false);

        int capsuleHeight = SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(capsuleState, 1040);
        Assert.Equal(SearchPopupSurfacePolicy.CapsuleOnlyHeightLogical, capsuleHeight);

        int twoResultsHeight = SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(
            capsuleState with
            {
                HasQuery = true,
                ResultCount = 2,
                ShowFilterBar = true,
                ShowSortHeader = true,
                ShowFooter = true
            },
            1040);
        int fortyResultsHeight = SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(
            capsuleState with
            {
                HasQuery = true,
                ResultCount = 40,
                ShowFilterBar = true,
                ShowSortHeader = true,
                ShowFooter = true
            },
            1040);

        Assert.True(twoResultsHeight > capsuleHeight);
        Assert.Equal(SearchPopupSurfacePolicy.ComputeMaxLogicalHeight(1040), fortyResultsHeight);

        var workArea = new RectInt32(0, 0, 1920, 1040);
        RectInt32 collapsedBounds = SearchPopupSurfacePolicy.ResolveAnchoredWindowBounds(
            workArea,
            dpiScale: 1.0,
            desiredLogicalHeight: capsuleHeight,
            customXPhysical: null,
            customYPhysical: null);
        RectInt32 expandedBounds = SearchPopupSurfacePolicy.ResolveAnchoredWindowBounds(
            workArea,
            dpiScale: 1.0,
            desiredLogicalHeight: fortyResultsHeight,
            customXPhysical: null,
            customYPhysical: null);

        // Top Y stays anchored so expansion goes strictly downward.
        Assert.Equal(collapsedBounds.Y, expandedBounds.Y);
        Assert.Equal(collapsedBounds.X, expandedBounds.X);
        Assert.True(expandedBounds.Height > collapsedBounds.Height);
    }

    [Fact]
    public void HistoryService_ComputesFrecencyBoostAndNeverPersistsClipboardResults()
    {
        Directory.CreateDirectory(_root);
        string storePath = Path.Combine(_root, "search-history.json");
        var history = new SearchHistoryService(storePath);

        history.RecordResult(new SearchResultItem
        {
            Kind = SearchResultKind.Clipboard,
            ClipboardItemId = "clip-secret-1",
            Title = "sk-live-secret-value"
        });
        Assert.Empty(history.RecentResults);

        var fileItem = new SearchResultItem
        {
            Kind = SearchResultKind.File,
            Title = "Architecture.md",
            DetailPath = @"D:\docs\Architecture.md"
        };
        string identity = SearchResultRanker.GetIdentityKey(fileItem);

        history.RecordResult(fileItem);
        double firstBoost = history.GetFrecencyBoost(identity, DateTimeOffset.UtcNow);
        history.RecordResult(fileItem);
        history.RecordResult(fileItem);
        double repeatedBoost = history.GetFrecencyBoost(identity, DateTimeOffset.UtcNow);

        Assert.True(firstBoost > 0);
        Assert.True(repeatedBoost > firstBoost);
    }

    [Fact]
    public async Task SearchEngine_SupportsCalculatorQuickTodoPinyinSettingsAndMaskedClipboard()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.Language = "zh-CN";
        settings.Settings.SearchIncludeDeskBoxContent = true;

        var localization = new LocalizationService(settings);
        var store = new QuickCaptureStore(Path.Combine(_root, "quick-capture"));
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem
                {
                    Id = "note-pinyin",
                    Title = "项目周报汇总",
                    Body = "本周搜索重构进度",
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ]
        });
        var quickCapture = new QuickCaptureService(store);
        var everything = new EverythingSearchService(settings);

        string rawSecret = "sk-proj-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var clipboardItems = new List<ClipboardHistoryItem>
        {
            new()
            {
                Id = "clip-1",
                Kind = ClipboardItemKind.Text,
                Summary = rawSecret,
                TextContent = rawSecret,
                IsSensitive = true,
                SourceAppName = "Terminal",
                CreatedAtUtcTicks = nowTicks,
                UpdatedAtUtcTicks = nowTicks
            }
        };

        using var engine = new SearchEngineService(
            settings,
            localization,
            everything,
            quickCapture,
            clipboardItemsProvider: _ => Task.FromResult<IReadOnlyList<ClipboardHistoryItem>>(clipboardItems));

        // 1. Inline calculator
        SearchResponse calcResponse = await engine.SearchAsync("(1920-660)/2");
        Assert.Contains(calcResponse.RankedItems, item => item.ActionId == "copy-calc:630");

        // 2. Quick-create Todo
        SearchResponse todoResponse = await engine.SearchAsync("todo 提交构建验证");
        Assert.Contains(todoResponse.RankedItems, item => item.ActionId == "quick-todo:提交构建验证");

        // 3. Pinyin initials matching on DeskBox QuickCapture notes ("项目周报汇总" -> "xmzbhz")
        SearchResponse pinyinResponse = await engine.SearchAsync("xmzbhz");
        Assert.Contains(pinyinResponse.RankedItems, item =>
            item.Kind == SearchResultKind.QuickCapture && item.QuickCaptureItemId == "note-pinyin");

        // 4. Settings deep-link search ("set:圆角")
        SearchResponse settingsResponse = await engine.SearchAsync("set:圆角");
        Assert.Contains(settingsResponse.RankedItems, item =>
            item.Kind == SearchResultKind.Action &&
            item.ActionId is not null &&
            item.ActionId.StartsWith("open-settings:", StringComparison.Ordinal));

        // 5. Clipboard history search with sensitive secret masking
        SearchResponse clipResponse = await engine.SearchAsync("clip:sk-proj");
        SearchResultItem clipResult = Assert.Single(
            clipResponse.RankedItems,
            item => item.Kind == SearchResultKind.Clipboard);
        Assert.Equal("clip-1", clipResult.ClipboardItemId);
        Assert.Equal("clipboard-copy:clip-1", clipResult.ActionId);
        Assert.DoesNotContain("GHIJKLMNOPQRST", clipResult.Title, StringComparison.Ordinal);

        // 6. QuickCapture note subtitle uses localized note label + preview, never raw "Text" enum
        SearchResultItem noteResult = Assert.Single(
            pinyinResponse.RankedItems,
            item => item.Kind == SearchResultKind.QuickCapture && item.QuickCaptureItemId == "note-pinyin");
        Assert.NotEqual("Text", noteResult.Subtitle);
        Assert.Contains("本周搜索重构进度", noteResult.Subtitle, StringComparison.Ordinal);

        // 7. Calculator and Clipboard toggles in Search Settings control default query inclusion
        settings.Settings.Search.SearchIncludeCalculator = false;
        SearchResponse disabledCalcResponse = await engine.SearchAsync("(1920-660)/2");
        Assert.DoesNotContain(disabledCalcResponse.RankedItems, item => item.ActionId == "copy-calc:630");

        settings.Settings.Search.SearchIncludeClipboard = false;
        SearchResponse disabledGeneralClipResponse = await engine.SearchAsync("sk-proj");
        Assert.DoesNotContain(disabledGeneralClipResponse.RankedItems, item => item.Kind == SearchResultKind.Clipboard);
    }

    [Fact]
    public void SurfacePolicy_ResolvesFullBleedHeaderDividerAndUnifiedResizableWidth()
    {
        Assert.Equal(56, SearchPopupSurfacePolicy.CapsuleOnlyHeightLogical);
        Assert.Equal(760, SearchPopupSurfacePolicy.DefaultPopupWidthLogical);
        Assert.Equal(520, SearchPopupSurfacePolicy.MinPopupWidthLogical);
        Assert.Equal(640, SearchPopupSurfacePolicy.DetailsPaneMinPopupWidthLogical);

        SearchPopupInputWellVisuals collapsedDark = SearchPopupSurfacePolicy.ResolveInputWellVisuals(
            isExpanded: false,
            isDark: true);
        SearchPopupInputWellVisuals collapsedLight = SearchPopupSurfacePolicy.ResolveInputWellVisuals(
            isExpanded: false,
            isDark: false);
        Assert.Equal(0, collapsedDark.BackgroundColor.A);
        Assert.Equal(0, collapsedDark.BorderColor.A);
        Assert.False(collapsedDark.ShowHeaderBottomDivider);
        Assert.Equal(0, collapsedLight.BackgroundColor.A);
        Assert.Equal(0, collapsedLight.BorderColor.A);
        Assert.False(collapsedLight.ShowHeaderBottomDivider);

        SearchPopupInputWellVisuals expandedDark = SearchPopupSurfacePolicy.ResolveInputWellVisuals(
            isExpanded: true,
            isDark: true);
        SearchPopupInputWellVisuals expandedLight = SearchPopupSurfacePolicy.ResolveInputWellVisuals(
            isExpanded: true,
            isDark: false);
        Assert.Equal(0, expandedDark.BackgroundColor.A);
        Assert.Equal(0, expandedDark.BorderColor.A);
        Assert.True(expandedDark.ShowHeaderBottomDivider);
        Assert.Equal(0, expandedLight.BackgroundColor.A);
        Assert.Equal(0, expandedLight.BorderColor.A);
        Assert.True(expandedLight.ShowHeaderBottomDivider);

        // Width resolution clamps to [520, 85% workArea] and defaults to 760 across all states.
        Assert.Equal(760, SearchPopupSurfacePolicy.ResolveEffectiveLogicalWidth(null, 1920));
        Assert.Equal(520, SearchPopupSurfacePolicy.ResolveEffectiveLogicalWidth(380, 1920));
        Assert.Equal(960, SearchPopupSurfacePolicy.ResolveEffectiveLogicalWidth(960, 1920));
        Assert.Equal(1632, SearchPopupSurfacePolicy.ResolveEffectiveLogicalWidth(2400, 1920));

        // Details pane auto-hides below 640px without altering user preference, and restores >= 640px.
        Assert.False(SearchPopupSurfacePolicy.ShouldShowDetailsPaneForWidth(true, 639));
        Assert.True(SearchPopupSurfacePolicy.ShouldShowDetailsPaneForWidth(true, 640));
        Assert.False(SearchPopupSurfacePolicy.ShouldShowDetailsPaneForWidth(false, 900));

        // Recommendation column estimation scales with resized width and updates row count height.
        Assert.Equal(4, SearchPopupSurfacePolicy.EstimateRecommendationColumns(520));
        Assert.Equal(6, SearchPopupSurfacePolicy.EstimateRecommendationColumns(660));
        Assert.Equal(7, SearchPopupSurfacePolicy.EstimateRecommendationColumns(760));
        Assert.Equal(10, SearchPopupSurfacePolicy.EstimateRecommendationColumns(1080));

        var narrowRecState = new SearchPopupLayoutState(
            HasQuery: false,
            IsSearching: false,
            ResultCount: 0,
            ShowFilterBar: false,
            ShowSortHeader: false,
            ShowEverythingBanner: false,
            ShowRecommendations: true,
            RecommendationEntryCount: 8,
            ShowAllHiddenRecommendationsHint: false,
            ShowNoResults: false,
            ShowFooter: true,
            PopupWidthLogical: 520);
        var wideRecState = narrowRecState with { PopupWidthLogical = 960 };
        Assert.True(
            SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(narrowRecState, 1080) >
            SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(wideRecState, 1080));

        var workArea = new RectInt32(0, 0, 1920, 1040);
        RectInt32 defaultBounds = SearchPopupSurfacePolicy.ResolveAnchoredWindowBounds(
            workArea,
            dpiScale: 1.0,
            desiredLogicalHeight: 420,
            customXPhysical: null,
            customYPhysical: null,
            minBottomHeightPhysical: null,
            desiredLogicalWidth: SearchPopupSurfacePolicy.DefaultPopupWidthLogical);
        RectInt32 customWideBounds = SearchPopupSurfacePolicy.ResolveAnchoredWindowBounds(
            workArea,
            dpiScale: 1.0,
            desiredLogicalHeight: 420,
            customXPhysical: null,
            customYPhysical: null,
            minBottomHeightPhysical: null,
            desiredLogicalWidth: 960);
        Assert.Equal(760, defaultBounds.Width);
        Assert.Equal(960, customWideBounds.Width);
        Assert.Equal(defaultBounds.Y, customWideBounds.Y);
    }

    [Theory]
    [InlineData(@"C:\Photos\hero.webp", true)]
    [InlineData(@"C:\Photos\banner.png", true)]
    [InlineData(@"C:\Videos\demo.mp4", true)]
    [InlineData(@"C:\Docs\deck.pdf", true)]
    [InlineData(@"C:\Docs\icon.svg", true)]
    [InlineData(@"C:\Docs\notes.md", false)]
    [InlineData(@"C:\Docs\report.docx", false)]
    [InlineData(@"C:\Code\Program.cs", false)]
    public void DetailsPreviewLoader_ExpectsVisualImagePreviewOnlyForVisualMedia(
        string filePath,
        bool expected)
    {
        var fileItem = new SearchResultItem
        {
            Title = Path.GetFileName(filePath),
            DetailPath = filePath,
            Kind = SearchResultKind.File
        };
        Assert.Equal(expected, SearchDetailsPreviewLoader.ExpectsVisualImagePreview(fileItem));

        var clipboardImageItem = new SearchResultItem
        {
            Title = "Clipboard Image",
            Kind = SearchResultKind.Clipboard,
            PreviewImagePath = @"C:\Temp\clip.png"
        };
        Assert.True(SearchDetailsPreviewLoader.ExpectsVisualImagePreview(clipboardImageItem));
    }

    [Theory]
    [InlineData("https://github.com/microsoft/PowerToys", "https://github.com/microsoft/PowerToys")]
    [InlineData("github.com/microsoft/PowerToys", "https://github.com/microsoft/PowerToys")]
    [InlineData("localhost:3000/api/health", "http://localhost:3000/api/health")]
    [InlineData("192.168.1.1:8080", "http://192.168.1.1:8080/")]
    [InlineData("ms-settings:display", "ms-settings:display")]
    [InlineData("mailto:dev@example.com", "mailto:dev@example.com")]
    public void WebAndUrlEvaluator_RecognizesDirectUrls(string query, string expectedUri)
    {
        bool matched = SearchWebAndUrlEvaluator.TryEvaluateDirectUrl(query, out DirectUrlMatch result);

        Assert.True(matched);
        Assert.Equal(expectedUri, result.NormalizedUri);
        Assert.True(SearchWebAndUrlEvaluator.TryNormalizeSafeExternalUri(result.NormalizedUri, out _));
    }

    [Theory]
    [InlineData("DeskBox.exe")]
    [InlineData("Program.cs")]
    [InlineData("Roadmap.pdf")]
    [InlineData("notes.md")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    public void WebAndUrlEvaluator_RejectsLocalFilenamesPathsAndUnsafeSchemes(string query)
    {
        Assert.False(SearchWebAndUrlEvaluator.TryEvaluateDirectUrl(query, out _));
        if (query.Contains(':', StringComparison.Ordinal))
        {
            Assert.False(SearchWebAndUrlEvaluator.TryNormalizeSafeExternalUri(query, out _));
        }
    }

    [Theory]
    [InlineData("? winui 3 acrylic", "Bing", "https://www.bing.com/search?q=winui%203%20acrylic")]
    [InlineData("？ 桌面收纳", "Bing", "https://www.bing.com/search?q=%E6%A1%8C%E9%9D%A2%E6%94%B6%E7%BA%B3")]
    [InlineData("g: powertoys cmdpal", "Google", "https://www.google.com/search?q=powertoys%20cmdpal")]
    [InlineData("bd: 天气预报", "Baidu", "https://www.baidu.com/s?wd=%E5%A4%A9%E6%B0%94%E9%A2%84%E6%8A%A5")]
    [InlineData("gh: microsoft/PowerToys", "GitHub", "https://github.com/search?q=microsoft%2FPowerToys")]
    [InlineData("bili: WinUI教程", "Bilibili", "https://search.bilibili.com/all?keyword=WinUI%E6%95%99%E7%A8%8B")]
    public void WebAndUrlEvaluator_ParsesExplicitWebSearchShortcuts(
        string query,
        string expectedEngine,
        string expectedUri)
    {
        bool matched = SearchWebAndUrlEvaluator.TryEvaluateExplicitWebSearch(
            query,
            out ExplicitWebSearchMatch result);

        Assert.True(matched);
        Assert.Equal(expectedEngine, result.EngineName);
        Assert.Equal(expectedUri, result.TargetUri);
    }

    [Fact]
    public async Task SearchEngine_SupportsDirectUrlExplicitWebSearchAndBottomRankedFallback()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings-web"));
        settings.Settings.Language = "zh-CN";
        settings.Settings.SearchIncludeDeskBoxContent = true;
        settings.Settings.SearchIncludeWebFallback = true;

        var localization = new LocalizationService(settings);
        var store = new QuickCaptureStore(Path.Combine(_root, "quick-capture-web"));
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem
                {
                    Id = "note-alpha",
                    Title = "Alpha Design Spec",
                    Body = "Details pane layout notes",
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ]
        });
        var quickCapture = new QuickCaptureService(store);
        var everything = new EverythingSearchService(settings);

        using var engine = new SearchEngineService(
            settings,
            localization,
            everything,
            quickCapture);

        // 1. Direct URL becomes instant top hit without redundant web fallback
        SearchResponse urlResponse = await engine.SearchAsync("github.com/microsoft/PowerToys");
        Assert.NotEmpty(urlResponse.RankedItems);
        Assert.Equal(
            "open-url:https://github.com/microsoft/PowerToys",
            urlResponse.RankedItems[0].ActionId);
        Assert.DoesNotContain(urlResponse.RankedItems, item => item.IsFallback);

        // 2. Explicit web prefix ("g: ...") becomes instant top hit
        SearchResponse explicitWebResponse = await engine.SearchAsync("g: winui command palette");
        Assert.NotEmpty(explicitWebResponse.RankedItems);
        Assert.StartsWith("web-search:https://www.google.com/search?q=", explicitWebResponse.RankedItems[0].ActionId);

        // 3. Local match ranks ABOVE fallback web search; fallback stays pinned at bottom
        SearchResponse mixedResponse = await engine.SearchAsync("Alpha Design");
        Assert.True(mixedResponse.RankedItems.Count >= 2);
        Assert.Equal("note-alpha", mixedResponse.RankedItems[0].QuickCaptureItemId);
        Assert.Equal("Details pane layout notes", mixedResponse.RankedItems[0].PreviewText);
        SearchResultItem lastItem = mixedResponse.RankedItems[^1];
        Assert.True(lastItem.IsFallback);
        Assert.StartsWith("web-search:https://www.bing.com/search?q=", lastItem.ActionId);

        // 4. Zero-local-hit query still returns one-keystroke web fallback when enabled
        SearchResponse zeroLocalResponse = await engine.SearchAsync("zzzxxyy_nonexistent_query_2026");
        SearchResultItem singleFallback = Assert.Single(zeroLocalResponse.RankedItems);
        Assert.True(singleFallback.IsFallback);

        // 5. Turning off SearchIncludeWebFallback suppresses automatic fallback while keeping explicit shortcuts
        settings.Settings.Search.SearchIncludeWebFallback = false;
        SearchResponse disabledFallbackResponse = await engine.SearchAsync("zzzxxyy_nonexistent_query_2026");
        Assert.Empty(disabledFallbackResponse.RankedItems);

        SearchResponse explicitStillWorks = await engine.SearchAsync("? zzzxxyy_nonexistent_query_2026");
        Assert.Single(explicitStillWorks.RankedItems);
    }

    [Fact]
    public void CompactFileSubtitle_ComposesTypeSizeAndDirectoryWhenSortColumnsAreHidden()
    {
        var fileItem = new SearchResultItem
        {
            Kind = SearchResultKind.File,
            Title = "Roadmap.pdf",
            Subtitle = @"D:\Projects\Specs",
            TypeDisplay = "PDF",
            SizeDisplay = "2.4 MB"
        };

        string compact = DeskBox.Controls.SearchResultRowControl.ComposeCompactFileSubtitle(
            fileItem.TypeDisplay,
            fileItem.SizeDisplay,
            fileItem.Subtitle);
        Assert.Equal(@"PDF · 2.4 MB · D:\Projects\Specs", compact);

        var folderItem = new SearchResultItem
        {
            Kind = SearchResultKind.Folder,
            Title = "Specs",
            Subtitle = @"D:\Projects"
        };
        Assert.Equal(
            @"D:\Projects",
            DeskBox.Controls.SearchResultRowControl.ComposeCompactFileSubtitle(
                folderItem.TypeDisplay,
                folderItem.SizeDisplay,
                folderItem.Subtitle));
    }

    [Theory]
    [InlineData("bing", "", "deskbox ui", "https://www.bing.com/search?q=deskbox%20ui")]
    [InlineData("google", "", "deskbox ui", "https://www.google.com/search?q=deskbox%20ui")]
    [InlineData("baidu", "", "桌面盒子", "https://www.baidu.com/s?wd=%E6%A1%8C%E9%9D%A2%E7%9B%92%E5%AD%90")]
    [InlineData("duckduckgo", "", "winui 3", "https://duckduckgo.com/?q=winui%203")]
    [InlineData("custom", "https://www.perplexity.ai/search?q={q}", "agentic coding", "https://www.perplexity.ai/search?q=agentic%20coding")]
    [InlineData("custom", "https://kagi.com/search?q=%s", "rust aot", "https://kagi.com/search?q=rust%20aot")]
    [InlineData("custom", "javascript:alert(1)", "fallback", "https://www.bing.com/search?q=fallback")]
    [InlineData("custom", "", "fallback", "https://www.bing.com/search?q=fallback")]
    public void WebAndUrlEvaluator_BuildsConfiguredAndCustomWebSearchUris(
        string engineId,
        string customTemplate,
        string query,
        string expectedUri)
    {
        string actual = SearchWebAndUrlEvaluator.BuildFallbackWebSearchUri(query, engineId, customTemplate);
        Assert.Equal(expectedUri, actual);
    }

    [Fact]
    public async Task SearchEngine_HonorsConfiguredDefaultAndCustomWebSearchEngines()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings-engine-choice"));
        settings.Settings.Language = "zh-CN";
        settings.Settings.SearchIncludeWebFallback = true;
        settings.Settings.SearchDefaultWebEngine = "google";

        var localization = new LocalizationService(settings);
        var store = new QuickCaptureStore(Path.Combine(_root, "quick-capture-engine-choice"));
        await store.SaveAsync(new QuickCaptureStoreData());
        var quickCapture = new QuickCaptureService(store);
        var everything = new EverythingSearchService(settings);

        using var engine = new SearchEngineService(settings, localization, everything, quickCapture);

        SearchResponse googleFallback = await engine.SearchAsync("zzzxxyy_google_fallback_test");
        SearchResultItem googleItem = Assert.Single(googleFallback.RankedItems, item => item.IsFallback);
        Assert.StartsWith("web-search:https://www.google.com/search?q=", googleItem.ActionId);
        Assert.Contains("Google", googleItem.Subtitle, StringComparison.Ordinal);

        SearchResponse questionGoogle = await engine.SearchAsync("? winui spring animation");
        Assert.StartsWith(
            "web-search:https://www.google.com/search?q=",
            questionGoogle.RankedItems[0].ActionId);

        settings.Settings.SearchDefaultWebEngine = "custom";
        settings.Settings.SearchCustomWebUrl = "https://www.perplexity.ai/search?q={q}";

        SearchResponse customFallback = await engine.SearchAsync("zzzxxyy_custom_fallback_test");
        SearchResultItem customItem = Assert.Single(customFallback.RankedItems, item => item.IsFallback);
        Assert.StartsWith("web-search:https://www.perplexity.ai/search?q=", customItem.ActionId);
        Assert.Contains("www.perplexity.ai", customItem.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsPreviewLoader_ClassifiesSupportedFormatsAndExtractsSnippetsWithoutLockingFiles()
    {
        Assert.True(SearchDetailsPreviewLoader.CanAttemptPathPreview(@"C:\Videos\demo.mp4"));
        Assert.True(SearchDetailsPreviewLoader.CanAttemptPathPreview(@"C:\Videos\clip.mkv"));
        Assert.True(SearchDetailsPreviewLoader.CanAttemptPathPreview(@"C:\Docs\report.pdf"));
        Assert.True(SearchDetailsPreviewLoader.CanAttemptPathPreview(@"C:\Docs\proposal.docx"));
        Assert.True(SearchDetailsPreviewLoader.CanAttemptPathPreview(@"C:\Code\Program.cs"));
        Assert.True(SearchDetailsPreviewLoader.CanAttemptPathPreview(@"C:\Code\Dockerfile"));
        Assert.False(SearchDetailsPreviewLoader.CanAttemptPathPreview(@"C:\Windows\System32\notepad.exe"));
        Assert.False(SearchDetailsPreviewLoader.CanAttemptPathPreview(@"C:\Windows\System32\kernel32.dll"));

        Directory.CreateDirectory(_root);

        // 1. Plain text / Markdown snippet with concurrent write sharing
        string mdPath = Path.Combine(_root, "notes.md");
        File.WriteAllText(mdPath, "\n\n# DeskBox Command Palette\n\n\n- Smooth spring height\n- Static video poster\n", Encoding.UTF8);
        using (var writerLockCheck = new FileStream(mdPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            string? mdSnippet = SearchDetailsPreviewLoader.TryReadPlainTextSnippet(mdPath);
            Assert.NotNull(mdSnippet);
            Assert.Contains("# DeskBox Command Palette", mdSnippet!, StringComparison.Ordinal);
            Assert.Contains("- Smooth spring height", mdSnippet!, StringComparison.Ordinal);
        }

        // 2. Binary file masquerading as .txt is cleanly rejected
        string binaryFakeTxt = Path.Combine(_root, "binary.txt");
        File.WriteAllBytes(binaryFakeTxt, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00]);
        Assert.Null(SearchDetailsPreviewLoader.TryReadPlainTextSnippet(binaryFakeTxt));

        // 3. Word .docx streaming XML paragraph snippet without locking
        string docxPath = Path.Combine(_root, "spec.docx");
        using (var fs = new FileStream(docxPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("word/document.xml");
            using var sw = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            sw.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p><w:r><w:t>季度产品规划文档</w:t></w:r></w:p>
                    <w:p><w:r><w:t>第一部分：搜索侧栏轻量预览与弹簧过渡体验优化。</w:t></w:r></w:p>
                  </w:body>
                </w:document>
                """);
        }

        string? docxSnippet = SearchDetailsPreviewLoader.TryExtractDocxSnippet(docxPath);
        Assert.NotNull(docxSnippet);
        Assert.Contains("季度产品规划文档", docxSnippet!, StringComparison.Ordinal);
        Assert.Contains("第一部分：搜索侧栏轻量预览与弹簧过渡体验优化。", docxSnippet!, StringComparison.Ordinal);
    }

    [Fact]
    public void HeightSpringStepper_ProvidesGentleElasticOvershootPreservesMomentumAndSettlesCleanly()
    {
        // 1. Expanding from 56 -> 280 exhibits gentle elastic overshoot (> 280, <= 287) and settles cleanly at 280
        double h = 56.0;
        double v = 0.0;
        double peak = h;
        bool settled = false;
        for (int frame = 0; frame < 60; frame++)
        {
            SearchPopupHeightSpringStep step = SearchPopupSurfacePolicy.StepHeightSpring(
                h, v, targetHeight: 280.0, deltaSeconds: 0.012, expandingDirection: true);
            h = step.Height;
            v = step.Velocity;
            peak = Math.Max(peak, h);
            if (step.IsSettled)
            {
                settled = true;
                break;
            }
        }

        Assert.True(settled);
        Assert.Equal(280.0, h);
        Assert.True(peak > 280.5 && peak <= 287.0, $"Expected gentle elastic overshoot in (280.5, 287.0], actual peak={peak:F2}");

        // 2. Shrinking mid-flight preserves momentum and never dips below CapsuleOnlyHeightLogical (56)
        h = 340.0;
        v = 0.0;
        SearchPopupHeightSpringStep midFlight = SearchPopupSurfacePolicy.StepHeightSpring(
            h, v, targetHeight: 180.0, deltaSeconds: 0.036, expandingDirection: false);
        Assert.True(midFlight.Velocity < -100.0);

        // Retarget mid-flight to 120 while keeping current velocity
        h = midFlight.Height;
        v = midFlight.Velocity;
        settled = false;
        for (int frame = 0; frame < 60; frame++)
        {
            SearchPopupHeightSpringStep step = SearchPopupSurfacePolicy.StepHeightSpring(
                h, v, targetHeight: 120.0, deltaSeconds: 0.012, expandingDirection: false);
            h = step.Height;
            v = step.Velocity;
            Assert.True(h >= SearchPopupSurfacePolicy.CapsuleOnlyHeightLogical);
            if (step.IsSettled)
            {
                settled = true;
                break;
            }
        }

        Assert.True(settled);
        Assert.Equal(120.0, h);
    }

    [Fact]
    public void WidthSpringStepper_ProvidesGentleElasticOvershootPreservesMomentumAndAnchoredX()
    {
        const double narrowWidth = 600.0;
        const double wideWidth = 900.0;

        // 1. Expanding 600 -> 900 exhibits gentle elastic overshoot and settles cleanly at 900
        double w = narrowWidth;
        double v = 0.0;
        double peak = w;
        bool settled = false;
        for (int frame = 0; frame < 60; frame++)
        {
            SearchPopupWidthSpringStep step = SearchPopupSurfacePolicy.StepWidthSpring(
                w, v, targetWidth: wideWidth, deltaSeconds: 0.012, expandingDirection: true);
            w = step.Width;
            v = step.Velocity;
            peak = Math.Max(peak, w);
            if (step.IsSettled)
            {
                settled = true;
                break;
            }
        }

        Assert.True(settled);
        Assert.Equal(wideWidth, w);
        Assert.True(peak > wideWidth + 0.5 && peak <= wideWidth + 8.0, $"Expected gentle elastic overshoot, actual peak={peak:F2}");

        // 2. Shrinking 900 -> 600 preserves momentum across mid-flight updates and settles at 600
        w = wideWidth;
        v = 0.0;
        SearchPopupWidthSpringStep midFlight = SearchPopupSurfacePolicy.StepWidthSpring(
            w, v, targetWidth: narrowWidth, deltaSeconds: 0.036, expandingDirection: false);
        Assert.True(midFlight.Velocity < -100.0);

        w = midFlight.Width;
        v = midFlight.Velocity;
        double trough = w;
        settled = false;
        for (int frame = 0; frame < 60; frame++)
        {
            SearchPopupWidthSpringStep step = SearchPopupSurfacePolicy.StepWidthSpring(
                w, v, targetWidth: narrowWidth, deltaSeconds: 0.012, expandingDirection: false);
            w = step.Width;
            v = step.Velocity;
            trough = Math.Min(trough, w);
            Assert.True(w >= SearchPopupSurfacePolicy.MinPopupWidthLogical);
            if (step.IsSettled)
            {
                settled = true;
                break;
            }
        }

        Assert.True(settled);
        Assert.Equal(narrowWidth, w);
        Assert.True(trough >= narrowWidth - 6.0 && trough < narrowWidth - 0.5, $"Expected soft closing cushion, actual trough={trough:F2}");

        // 3. Left-anchored SetWindowPos(SWP_NOSIZE) X glide matches ResolveAnchoredWindowBounds at endpoints
        var workAreas = new[]
        {
            new Windows.Graphics.RectInt32(0, 0, 1920, 1032),
            new Windows.Graphics.RectInt32(0, 0, 2560, 1392),
            new Windows.Graphics.RectInt32(10, 0, 1919, 1032)
        };
        foreach (var workArea in workAreas)
        {
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var narrowBounds = SearchPopupSurfacePolicy.ResolveAnchoredWindowBounds(
                    workArea, scale, 360, null, null, null, (int)narrowWidth);
                var wideBounds = SearchPopupSurfacePolicy.ResolveAnchoredWindowBounds(
                    workArea, scale, 360, null, null, null, (int)wideWidth);

                Assert.Equal(
                    narrowBounds.X,
                    SearchPopupSurfacePolicy.ComputeAnchoredWindowX(
                        workArea, scale, narrowWidth, null, null));
                Assert.Equal(
                    wideBounds.X,
                    SearchPopupSurfacePolicy.ComputeAnchoredWindowX(
                        workArea, scale, wideWidth, null, null));

                int midX = SearchPopupSurfacePolicy.ComputeAnchoredWindowX(workArea, scale, SearchPopupSurfacePolicy.DefaultPopupWidthLogical, null, null);
                Assert.InRange(midX, wideBounds.X, narrowBounds.X);
            }
        }
    }

    [Fact]
    public async Task ClearSearch_CancelsPendingQueryAndLeavesViewModelInCleanRecommendationMode()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings-clear-race"));
        var localization = new LocalizationService(settings);
        var store = new QuickCaptureStore(Path.Combine(_root, "quick-capture-clear-race"));
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem
                {
                    Id = "note-11",
                    Title = "111",
                    Body = "111",
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ]
        });
        var quickCapture = new QuickCaptureService(store);
        var everything = new EverythingSearchService(settings);
        var history = new SearchHistoryService(Path.Combine(_root, "history-clear-race"));
        var fileMeta = new FileMetaService();

        using var engine = new SearchEngineService(settings, localization, everything, quickCapture);
        using var vm = new ViewModels.SearchPopupViewModel(engine, settings, localization, history, fileMeta);

        vm.Query = "11";
        vm.CancelPendingSearch();
        vm.ClearSearch();

        await Task.Delay(80);

        Assert.Equal(string.Empty, vm.Query);
        Assert.False(vm.IsQueryActive);
        Assert.False(vm.HasResults);
        Assert.False(vm.IsSearching);
        Assert.False(vm.IsBatchUpdatingResults);
    }

    [Fact]
    public void SearchTypingStabilityAndCornerRadii_RemainStrictlyPreserved()
    {
        // Corner radii are frozen per user requirement
        Assert.Equal(16.0, SearchPopupSurfacePolicy.OuterCornerRadius);
        Assert.Equal(12.0, SearchPopupSurfacePolicy.InputCapsuleCornerRadius);
        Assert.Equal(10.0, SearchPopupSurfacePolicy.ResultRowCornerRadius);

        string viewModelSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/SearchPopupViewModel.cs"));
        string popupSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Contains("bool preserveVisibleResults = IsQueryActive && HasResults;", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("preserveSelection: refreshKind != SearchRefreshKind.UserQuery", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("SearchPopupSurfacePolicy.ResolveEffectiveLogicalWidth(", popupSource, StringComparison.Ordinal);
        Assert.Contains("SearchPopupSurfacePolicy.StepHeightSpring", popupSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EverythingPromptBanner_MaintainsTopGapBelowHeaderBottomDivider()
    {
        Assert.Equal(56, SearchPopupSurfacePolicy.EverythingBannerHeightLogical);

        var withoutBanner = new SearchPopupLayoutState(
            HasQuery: false,
            IsSearching: false,
            ResultCount: 0,
            ShowFilterBar: false,
            ShowSortHeader: false,
            ShowEverythingBanner: false,
            ShowRecommendations: true,
            RecommendationEntryCount: 6,
            ShowAllHiddenRecommendationsHint: false,
            ShowNoResults: false,
            ShowFooter: true);
        var withBanner = withoutBanner with { ShowEverythingBanner = true };

        int delta = SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(withBanner, 1080)
                    - SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(withoutBanner, 1080);
        Assert.Equal(SearchPopupSurfacePolicy.EverythingBannerHeightLogical, delta);

        string popupXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        int bannerIdx = popupXaml.IndexOf("x:Name=\"EverythingPromptBanner\"", StringComparison.Ordinal);
        Assert.True(bannerIdx >= 0);
        string bannerRegion = popupXaml.Substring(bannerIdx, Math.Min(400, popupXaml.Length - bannerIdx));
        Assert.Contains("Margin=\"12,10,12,6\"", bannerRegion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchQuickCreate_SupportsSingleCharPrefixesInlineDisplayModesAndFeatureGating()
    {
        // 1. Single-character and scoped quick-create syntax parsing
        ParsedSearchQuery plusHalf = SearchQuerySyntaxParser.Parse("+ 下午三点评审");
        Assert.True(plusHalf.IsQuickTodoIntent);
        Assert.Equal("下午三点评审", plusHalf.QuickTodoText);

        ParsedSearchQuery plusFull = SearchQuerySyntaxParser.Parse("＋ 买牛奶");
        Assert.True(plusFull.IsQuickTodoIntent);
        Assert.Equal("买牛奶", plusFull.QuickTodoText);

        ParsedSearchQuery semiHalf = SearchQuerySyntaxParser.Parse("; 灵感草稿");
        Assert.True(semiHalf.IsQuickNoteIntent);
        Assert.Equal("灵感草稿", semiHalf.QuickNoteText);

        ParsedSearchQuery semiFull = SearchQuerySyntaxParser.Parse("； 会议随记");
        Assert.True(semiFull.IsQuickNoteIntent);
        Assert.Equal("会议随记", semiFull.QuickNoteText);

        ParsedSearchQuery tildeHalf = SearchQuerySyntaxParser.Parse("~ 随手记录");
        Assert.True(tildeHalf.IsQuickNoteIntent);
        Assert.Equal("随手记录", tildeHalf.QuickNoteText);

        // Paths starting with ~/ or ~\ are preserved as paths, not quick-notes
        ParsedSearchQuery tildePath = SearchQuerySyntaxParser.Parse(@"~\Documents\notes.txt");
        Assert.False(tildePath.IsQuickNoteIntent);
        Assert.Null(tildePath.QuickNoteText);

        // 2. Engine dual-track behavior: Track 1 inline + Track 2 prefix + settings & feature gating
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings-quick-create"));
        settings.Settings.Language = "zh-CN";
        settings.Settings.SearchIncludeDeskBoxContent = true;
        settings.Settings.SearchIncludeWebFallback = true;
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.Todo, true);
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, true);

        var localization = new LocalizationService(settings);
        var store = new QuickCaptureStore(Path.Combine(_root, "quick-capture-quick-create"));
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem
                {
                    Id = "note-existing",
                    Title = "Sprint Retrospective",
                    Body = "Action items for Q4",
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ]
        });
        var quickCapture = new QuickCaptureService(store);
        var everything = new EverythingSearchService(settings);

        using var engine = new SearchEngineService(settings, localization, everything, quickCapture);

        // Default ("always", default target = "todo"):
        // When local matches exist, local match ranks first, then quick-todo & quick-note, then web fallback last.
        SearchResponse withLocalHit = await engine.SearchAsync("Sprint Retrospective");
        Assert.Equal("note-existing", withLocalHit.RankedItems[0].QuickCaptureItemId);
        int todoIdx = withLocalHit.RankedItems.ToList().FindIndex(i => i.ActionId == "quick-todo:Sprint Retrospective");
        int noteIdx = withLocalHit.RankedItems.ToList().FindIndex(i => i.ActionId == "quick-note:Sprint Retrospective");
        int fallbackIdx = withLocalHit.RankedItems.ToList().FindIndex(i => i.IsFallback);
        Assert.True(todoIdx > 0);
        Assert.True(noteIdx > todoIdx);
        Assert.True(fallbackIdx > noteIdx);

        // Default target = "note" flips quick-note above quick-todo
        settings.Settings.Search.SearchQuickCreateDefaultTarget = "note";
        SearchResponse zeroHitNotePrimary = await engine.SearchAsync("zzzxxyy_unmatched_idea_2026");
        Assert.Equal("quick-note:zzzxxyy_unmatched_idea_2026", zeroHitNotePrimary.RankedItems[0].ActionId);
        Assert.Equal("quick-todo:zzzxxyy_unmatched_idea_2026", zeroHitNotePrimary.RankedItems[1].ActionId);

        // DisplayMode = "fallback" hides inline quick-create when local matches exist, but promotes them when zero hits
        settings.Settings.Search.SearchQuickCreateDefaultTarget = "todo";
        settings.Settings.Search.SearchQuickCreateDisplayMode = "fallback";
        SearchResponse fallbackModeWithHit = await engine.SearchAsync("Sprint Retrospective");
        Assert.DoesNotContain(fallbackModeWithHit.RankedItems, i => i.ActionId == "quick-todo:Sprint Retrospective");
        Assert.DoesNotContain(fallbackModeWithHit.RankedItems, i => i.ActionId == "quick-note:Sprint Retrospective");

        SearchResponse fallbackModeZeroHit = await engine.SearchAsync("zzzxxyy_unmatched_idea_2026");
        Assert.Equal("quick-todo:zzzxxyy_unmatched_idea_2026", fallbackModeZeroHit.RankedItems[0].ActionId);
        Assert.Equal("quick-note:zzzxxyy_unmatched_idea_2026", fallbackModeZeroHit.RankedItems[1].ActionId);

        // DisplayMode = "prefix" only offers quick-create when an explicit prefix is typed
        settings.Settings.Search.SearchQuickCreateDisplayMode = "prefix";
        SearchResponse prefixModePlain = await engine.SearchAsync("zzzxxyy_unmatched_idea_2026");
        Assert.DoesNotContain(prefixModePlain.RankedItems, i => i.ActionId == "quick-todo:zzzxxyy_unmatched_idea_2026");

        SearchResponse prefixModePlus = await engine.SearchAsync("+ 买咖啡");
        Assert.Equal("quick-todo:买咖啡", prefixModePlus.RankedItems[0].ActionId);

        SearchResponse prefixModeSemi = await engine.SearchAsync("; 记个灵感");
        Assert.Equal("quick-note:记个灵感", prefixModeSemi.RankedItems[0].ActionId);

        // Disabling SearchQuickCreateTodoEnabled or the feature widget itself suppresses quick-create
        settings.Settings.Search.SearchQuickCreateDisplayMode = "always";
        settings.Settings.Search.SearchQuickCreateTodoEnabled = false;
        SearchResponse todoDisabled = await engine.SearchAsync("+ 买咖啡");
        Assert.DoesNotContain(todoDisabled.RankedItems, i => i.ActionId == "quick-todo:买咖啡");

        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, false);
        SearchResponse noteFeatureDisabled = await engine.SearchAsync("; 记个灵感");
        Assert.DoesNotContain(noteFeatureDisabled.RankedItems, i => i.ActionId == "quick-note:记个灵感");
    }

    [Fact]
    public void SearchTodoAndQuickCaptureIcons_UseNativeColorfulWidgetSvgAssetsInsteadOfEmoji()
    {
        string rowControlSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/SearchResultRowControl.xaml.cs"));
        string cmdPaletteSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SearchPopupWindow.CommandPalette.cs"));
        string popupSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SearchPopupWindow.ResultsAndPreview.cs"));

        Assert.Contains("ms-appx:///Assets/WidgetTitleIcons/todo.svg", rowControlSource, StringComparison.Ordinal);
        Assert.Contains("ms-appx:///Assets/WidgetTitleIcons/quick-capture.svg", rowControlSource, StringComparison.Ordinal);
        Assert.Contains("ResolveNativeWidgetSvgIcon", rowControlSource, StringComparison.Ordinal);
        Assert.Contains("GetTodoSvgIcon()", cmdPaletteSource, StringComparison.Ordinal);
        Assert.Contains("GetQuickCaptureSvgIcon()", cmdPaletteSource, StringComparison.Ordinal);
        Assert.Contains("GetTodoSvgIcon()", popupSource, StringComparison.Ordinal);
        Assert.Contains("GetQuickCaptureSvgIcon()", popupSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchCategoryLayout_ParsesDefaultOrderAutoLinksSearchAndTabAndSupportsReorder()
    {
        Assert.Equal(14, SearchSettingsSlice.DefaultCategoryOrder.Count);
        Assert.True(SearchSettingsSlice.IsDefaultCategoryLayout(null));
        Assert.True(SearchSettingsSlice.IsDefaultCategoryLayout(SearchSettingsSlice.DefaultCategoryLayout));

        IReadOnlyList<SearchCategoryEntry> defaults = SearchSettingsSlice.ParseCategoryLayout(null);
        Assert.Equal(14, defaults.Count);
        Assert.All(defaults, entry => Assert.True(entry.SearchEnabled));
        Assert.Equal(
            [SearchSettingsSlice.CategoryFile, SearchSettingsSlice.CategoryImage],
            defaults.Where(static e => e.TabShown).Select(static e => e.Id).ToArray());

        // Turning off Search automatically turns off TabShown.
        string fileSearchOff = SearchSettingsSlice.SetCategorySearchEnabled(
            SearchSettingsSlice.DefaultCategoryLayout,
            SearchSettingsSlice.CategoryFile,
            searchEnabled: false);
        SearchCategoryEntry fileAfterOff = SearchSettingsSlice.ParseCategoryLayout(fileSearchOff)
            .Single(e => e.Id == SearchSettingsSlice.CategoryFile);
        Assert.False(fileAfterOff.SearchEnabled);
        Assert.False(fileAfterOff.TabShown);

        // Turning on TabShown automatically turns on SearchEnabled even when previously off.
        string fileTabBackOn = SearchSettingsSlice.SetCategoryTabShown(
            fileSearchOff,
            SearchSettingsSlice.CategoryFile,
            tabShown: true);
        SearchCategoryEntry fileAfterTabOn = SearchSettingsSlice.ParseCategoryLayout(fileTabBackOn)
            .Single(e => e.Id == SearchSettingsSlice.CategoryFile);
        Assert.True(fileAfterTabOn.SearchEnabled);
        Assert.True(fileAfterTabOn.TabShown);

        // Reorder and move operations preserve per-category states while changing order.
        string withSettingsAndTodoTabs = SearchSettingsSlice.SetCategoryTabShown(
            SearchSettingsSlice.SetCategoryTabShown(
                SearchSettingsSlice.DefaultCategoryLayout,
                SearchSettingsSlice.CategorySettings,
                tabShown: true),
            SearchSettingsSlice.CategoryTodo,
            tabShown: true);
        string reordered = SearchSettingsSlice.ReorderCategories(
            withSettingsAndTodoTabs,
            [SearchSettingsSlice.CategorySettings, SearchSettingsSlice.CategoryTodo, SearchSettingsSlice.CategoryFile]);
        IReadOnlyList<SearchCategoryEntry> reorderedParsed = SearchSettingsSlice.ParseCategoryLayout(reordered);
        Assert.Equal(SearchSettingsSlice.CategorySettings, reorderedParsed[0].Id);
        Assert.Equal(SearchSettingsSlice.CategoryTodo, reorderedParsed[1].Id);
        Assert.Equal(SearchSettingsSlice.CategoryFile, reorderedParsed[2].Id);
        Assert.Equal(14, reorderedParsed.Count);

        string movedDown = SearchSettingsSlice.MoveCategory(reordered, SearchSettingsSlice.CategorySettings, +1);
        IReadOnlyList<SearchCategoryEntry> movedParsed = SearchSettingsSlice.ParseCategoryLayout(movedDown);
        Assert.Equal(SearchSettingsSlice.CategoryTodo, movedParsed[0].Id);
        Assert.Equal(SearchSettingsSlice.CategorySettings, movedParsed[1].Id);

        // Legacy "deskbox" default tab migrates cleanly to "all".
        Assert.Equal(SearchSettingsSlice.CategoryAll, SearchSettingsSlice.NormalizeDefaultTabId("deskbox"));
        Assert.Equal(SearchSettingsSlice.CategorySettings, SearchSettingsSlice.NormalizeDefaultTabId("SETTINGS"));
    }

    [Fact]
    public async Task SearchCategoriesAndTabs_SupportSettingsTabGranularSplitOrderAndFeatureWidgetLinkage()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings-categories"));
        settings.Settings.Language = "zh-CN";
        settings.Settings.SearchIncludeWebFallback = false;
        settings.Settings.Search.SearchQuickCreateTodoEnabled = false;
        settings.Settings.Search.SearchQuickCreateNoteEnabled = false;
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.Todo, true);
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, true);
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.Clipboard, true);

        var localization = new LocalizationService(settings);
        Assert.Equal("搜索文件、设置、应用", localization.T("Search.Placeholder"));
        Assert.Equal("设置", localization.T("Search.Tab.Settings"));

        var store = new QuickCaptureStore(Path.Combine(_root, "quick-capture-categories"));
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem
                {
                    Id = "note-theme",
                    Title = "主题配色灵感",
                    Body = "深色主题圆角笔记",
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ]
        });
        var quickCapture = new QuickCaptureService(store);
        var everything = new EverythingSearchService(settings);
        var history = new SearchHistoryService(Path.Combine(_root, "history-categories"));
        var fileMeta = new FileMetaService();

        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var clipboardItems = new List<ClipboardHistoryItem>
        {
            new()
            {
                Id = "clip-theme",
                Kind = ClipboardItemKind.Text,
                Summary = "主题设置备份片段",
                TextContent = "主题设置备份片段",
                SourceAppName = "Code",
                CreatedAtUtcTicks = nowTicks,
                UpdatedAtUtcTicks = nowTicks
            }
        };
        string todoWidgetsRoot = CustomWidgetHostSnapshotService.ResolveWidgetsRootForSettings(settings);
        settings.Settings.WidgetLayout.Widgets.Add(new WidgetConfig
        {
            Id = "todo-widget-1",
            Name = "今日待办",
            WidgetKind = WidgetKind.Todo
        });
        await new TodoWidgetStore(todoWidgetsRoot, "todo-widget-1").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "todo-theme",
                    Text = "确认主题设置细节",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        using var engine = new SearchEngineService(
            settings,
            localization,
            everything,
            quickCapture,
            clipboardItemsProvider: _ => Task.FromResult<IReadOnlyList<ClipboardHistoryItem>>(clipboardItems));
        using var vm = new ViewModels.SearchPopupViewModel(engine, settings, localization, history, fileMeta);

        // 1. Default layout: All 14 types participate in search, while visible Tabs default to ["all", "file", "image"].
        vm.Query = "主题";
        await vm.RefreshSearchAsync();

        Assert.Equal(
            [SearchSettingsSlice.CategoryAll, SearchSettingsSlice.CategoryFile, SearchSettingsSlice.CategoryImage],
            vm.Tabs.Select(static t => t.Id).ToArray());
        Assert.True(vm.ShouldShowCategoryBar);
        Assert.Contains(vm.CurrentResults, static i => SearchSettingsSlice.IsSettingsActionItem(i));
        Assert.Contains(vm.CurrentResults, static i => i.Kind == SearchResultKind.Todo && i.TodoItemId == "todo-theme");
        Assert.Contains(vm.CurrentResults, static i => i.Kind == SearchResultKind.QuickCapture && i.QuickCaptureItemId == "note-theme");
        Assert.Contains(vm.CurrentResults, static i => i.Kind == SearchResultKind.Clipboard && i.ClipboardItemId == "clip-theme");

        // Single-character CJK query ("暗" / "圆") also matches DeskBox settings catalog items.
        SearchResponse oneCharSettings = await engine.SearchAsync("圆");
        Assert.Contains(oneCharSettings.RankedItems, static i => SearchSettingsSlice.IsSettingsActionItem(i));

        // 2. Enabling granular Tabs (settings, todo, note, clipboard) in custom order exposes exact filtered Tabs.
        string customLayout =
            "settings:tab,todo:tab,note:tab,clipboard:tab,file:tab,image:search,app:search,document:search,folder:search,widget:search,command:search,video:search,music:search,archive:search";
        using var coordinator = new SearchSettingsCoordinator(
            settings,
            localization,
            () => null,
            () => null,
            () => null,
            _ => { });
        coordinator.UpdatePreferences(new(DefaultTab: SearchSettingsSlice.CategorySettings, CategoryLayout: customLayout));
        vm.RefreshActiveSearchAndTabs(rerunSearch: false);

        Assert.Equal(
            [
                SearchSettingsSlice.CategoryAll,
                SearchSettingsSlice.CategorySettings,
                SearchSettingsSlice.CategoryTodo,
                SearchSettingsSlice.CategoryNote,
                SearchSettingsSlice.CategoryClipboard,
                SearchSettingsSlice.CategoryFile
            ],
            vm.Tabs.Select(static t => t.Id).ToArray());

        // Selecting Settings Tab isolates settings items (excludes non-settings commands like toggle-theme).
        vm.SelectedTab = vm.Tabs.Single(t => t.Id == SearchSettingsSlice.CategorySettings);
        Assert.NotEmpty(vm.CurrentResults);
        Assert.All(vm.CurrentResults, static i => Assert.True(SearchSettingsSlice.IsSettingsActionItem(i)));

        // Selecting Todo / Note / Clipboard Tabs isolates each split DeskBox module.
        vm.SelectedTab = vm.Tabs.Single(t => t.Id == SearchSettingsSlice.CategoryTodo);
        Assert.Single(vm.CurrentResults, static i => i.TodoItemId == "todo-theme");

        vm.SelectedTab = vm.Tabs.Single(t => t.Id == SearchSettingsSlice.CategoryNote);
        Assert.Single(vm.CurrentResults, static i => i.QuickCaptureItemId == "note-theme");

        vm.SelectedTab = vm.Tabs.Single(t => t.Id == SearchSettingsSlice.CategoryClipboard);
        Assert.Single(vm.CurrentResults, static i => i.ClipboardItemId == "clip-theme");

        // 3. Hiding the DefaultTab ("settings") in CategoryLayout automatically resets SearchDefaultTab to "all".
        string hideSettingsTab = SearchSettingsSlice.SetCategoryTabShown(
            customLayout,
            SearchSettingsSlice.CategorySettings,
            tabShown: false);
        coordinator.UpdatePreferences(new(CategoryLayout: hideSettingsTab));
        Assert.Equal(SearchSettingsSlice.CategoryAll, settings.Settings.Search.SearchDefaultTab);

        // 4. Turning off SearchEnabled on "todo" omits Todo results from search altogether.
        string disableTodoSearch = SearchSettingsSlice.SetCategorySearchEnabled(
            customLayout,
            SearchSettingsSlice.CategoryTodo,
            searchEnabled: false);
        coordinator.UpdatePreferences(new(CategoryLayout: disableTodoSearch));
        await vm.RefreshSearchAsync();
        vm.SelectedTab = vm.Tabs.Single(t => t.Id == SearchSettingsSlice.CategoryAll);
        Assert.DoesNotContain(vm.CurrentResults, static i => i.Kind == SearchResultKind.Todo);
        Assert.DoesNotContain(vm.Tabs, static t => t.Id == SearchSettingsSlice.CategoryTodo);

        // 5. FeatureWidgets linkage: disabling Clipboard in FeatureWidgets hides its Tab and search results
        // without erasing the user's saved SearchCategoryLayout, and re-enabling restores both.
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.Clipboard, false);
        await vm.RefreshSearchAsync();
        Assert.DoesNotContain(vm.Tabs, static t => t.Id == SearchSettingsSlice.CategoryClipboard);
        Assert.DoesNotContain(vm.CurrentResults, static i => i.Kind == SearchResultKind.Clipboard);
        Assert.True(SearchSettingsSlice.ParseCategoryLayout(settings.Settings.Search.SearchCategoryLayout)
            .Single(e => e.Id == SearchSettingsSlice.CategoryClipboard).TabShown);

        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.Clipboard, true);
        await vm.RefreshSearchAsync();
        Assert.Contains(vm.Tabs, static t => t.Id == SearchSettingsSlice.CategoryClipboard);
        Assert.Contains(vm.CurrentResults, static i => i.Kind == SearchResultKind.Clipboard);
    }

    [Fact]
    public async Task SearchRegressionAudit_DefaultHotkeyOnInlineScopesCategoryModesAndDetailsPreview()
    {
        Directory.CreateDirectory(_root);

        // 1. New user install defaults SearchHotkeyEnabled to true (Alt+D), while explicit false in JSON is preserved.
        var freshSettings = new SettingsService(Path.Combine(_root, "settings-fresh-install"));
        Assert.True(freshSettings.Settings.SearchHotkeyEnabled);
        Assert.Equal((int)HotkeyModifierKeys.Alt, freshSettings.Settings.SearchHotkeyModifiers);
        Assert.Equal(0x44, freshSettings.Settings.SearchHotkeyKey);

        string optedOutDir = Path.Combine(_root, "settings-opted-out");
        Directory.CreateDirectory(optedOutDir);
        await File.WriteAllTextAsync(
            Path.Combine(optedOutDir, "settings.json"),
            """{"schemaVersion":18,"searchHotkeyEnabled":false}""");
        var optedOutSettings = new SettingsService(optedOutDir);
        await optedOutSettings.LoadAsync();
        Assert.False(optedOutSettings.Settings.SearchHotkeyEnabled);

        // 2. Full 14-category inline scope prefixes parse cleanly.
        Assert.Equal(SearchInlineScope.Folders, SearchQuerySyntaxParser.Parse("folder:Specs").Scope);
        Assert.Equal(SearchInlineScope.Images, SearchQuerySyntaxParser.Parse("img:hero").Scope);
        Assert.Equal(SearchInlineScope.Documents, SearchQuerySyntaxParser.Parse("doc:report").Scope);
        Assert.Equal(SearchInlineScope.Videos, SearchQuerySyntaxParser.Parse("video:demo").Scope);
        Assert.Equal(SearchInlineScope.Music, SearchQuerySyntaxParser.Parse("music:bgm").Scope);
        Assert.Equal(SearchInlineScope.Archives, SearchQuerySyntaxParser.Parse("zip:release").Scope);
        Assert.Equal(SearchInlineScope.Widgets, SearchQuerySyntaxParser.Parse("widget:pomodoro").Scope);

        // 3. HotkeyHint formats chords, Win+Space, and DoubleControl cleanly without "VK:00".
        freshSettings.Settings.Language = "zh-CN";
        freshSettings.Settings.SearchIncludeWebFallback = false;
        var localization = new LocalizationService(freshSettings);
        var store = new QuickCaptureStore(Path.Combine(_root, "quick-capture-audit"));
        await store.SaveAsync(new QuickCaptureStoreData());
        var quickCapture = new QuickCaptureService(store);
        var everything = new EverythingSearchService(freshSettings);
        var history = new SearchHistoryService(Path.Combine(_root, "history-audit"));
        var fileMeta = new FileMetaService();

        using var engine = new SearchEngineService(freshSettings, localization, everything, quickCapture);
        using var vm = new ViewModels.SearchPopupViewModel(engine, freshSettings, localization, history, fileMeta);

        Assert.Equal("Alt+D", vm.HotkeyHint);

        freshSettings.Settings.Search.SearchHotkeyModifiers = (int)HotkeyModifierKeys.Windows;
        freshSettings.Settings.Search.SearchHotkeyKey = 0x20;
        Assert.Equal("Win+Space", vm.HotkeyHint);

        freshSettings.Settings.Search.SearchHotkeyActivationKind = HotkeyActivationKind.DoubleControl;
        freshSettings.Settings.Search.SearchHotkeyModifiers = 0;
        freshSettings.Settings.Search.SearchHotkeyKey = 0;
        Assert.DoesNotContain("VK:", vm.HotkeyHint, StringComparison.Ordinal);
        Assert.NotEmpty(vm.HotkeyHint);

        // 4. Settings results populate PreviewText description for DetailsPane, and quick-create items match Todo/Note tabs.
        SearchResponse settingsResp = await engine.SearchAsync("set:搜索");
        Assert.Contains(settingsResp.RankedItems, static i =>
            SearchSettingsSlice.IsSettingsActionItem(i) && !string.IsNullOrWhiteSpace(i.PreviewText));

        var quickTodoItem = new SearchResultItem { Kind = SearchResultKind.Action, ActionId = "quick-todo:买牛奶", Title = "买牛奶" };
        var quickNoteItem = new SearchResultItem { Kind = SearchResultKind.Action, ActionId = "quick-note:记灵感", Title = "记灵感" };
        Assert.True(SearchSettingsSlice.MatchesCategory(quickTodoItem, SearchSettingsSlice.CategoryTodo));
        Assert.False(SearchSettingsSlice.MatchesCategory(quickTodoItem, SearchSettingsSlice.CategoryCommand));
        Assert.True(SearchSettingsSlice.MatchesCategory(quickNoteItem, SearchSettingsSlice.CategoryNote));
        Assert.False(SearchSettingsSlice.MatchesCategory(quickNoteItem, SearchSettingsSlice.CategoryCommand));

        // 5. CategoryBarMode: "auto" hides on 0 results, "always" stays visible on 0 results, "hidden" always hides.
        freshSettings.Settings.Search.SearchQuickCreateTodoEnabled = false;
        freshSettings.Settings.Search.SearchQuickCreateNoteEnabled = false;
        freshSettings.Settings.Search.SearchCategoryBarMode = "auto";
        vm.Query = "zzzxxyy_zero_hits_audit_2026";
        await vm.RefreshSearchAsync();
        Assert.False(vm.ShouldShowCategoryBar);

        freshSettings.Settings.Search.SearchCategoryBarMode = "always";
        vm.RefreshActiveSearchAndTabs(rerunSearch: false);
        Assert.True(vm.ShouldShowCategoryBar);

        freshSettings.Settings.Search.SearchCategoryBarMode = "hidden";
        vm.RefreshActiveSearchAndTabs(rerunSearch: false);
        Assert.False(vm.ShouldShowCategoryBar);
    }

    [Fact]
    public void SortHeaderResponsivePolicy_SuppressesRowColumnsWhenDetailsOpenBelow880AndSharesSplitHeight()
    {
        Assert.False(SearchPopupSurfacePolicy.ShouldShowFullFileColumns(
            showSortHeader: false,
            detailsPaneVisible: false,
            popupWidthLogical: 960));

        // Without DetailsPane open, default 760px width shows full 4-column table.
        Assert.True(SearchPopupSurfacePolicy.ShouldShowFullFileColumns(
            showSortHeader: true,
            detailsPaneVisible: false,
            popupWidthLogical: SearchPopupSurfacePolicy.DefaultPopupWidthLogical));

        // With DetailsPane open below 880px, suppress 260px secondary row columns so filenames stay readable.
        Assert.False(SearchPopupSurfacePolicy.ShouldShowFullFileColumns(
            showSortHeader: true,
            detailsPaneVisible: true,
            popupWidthLogical: SearchPopupSurfacePolicy.DefaultPopupWidthLogical));
        Assert.False(SearchPopupSurfacePolicy.ShouldShowFullFileColumns(
            showSortHeader: true,
            detailsPaneVisible: true,
            popupWidthLogical: SearchPopupSurfacePolicy.FullTableColumnsWithDetailsMinWidthLogical - 1));

        // Wide popup (>= 880px) fits both full 4-column table and DetailsPane cleanly.
        Assert.True(SearchPopupSurfacePolicy.ShouldShowFullFileColumns(
            showSortHeader: true,
            detailsPaneVisible: true,
            popupWidthLogical: SearchPopupSurfacePolicy.FullTableColumnsWithDetailsMinWidthLogical));

        var baseState = new SearchPopupLayoutState(
            HasQuery: true,
            IsSearching: false,
            ResultCount: 2,
            ShowFilterBar: true,
            ShowSortHeader: false,
            ShowEverythingBanner: false,
            ShowRecommendations: false,
            RecommendationEntryCount: 0,
            ShowAllHiddenRecommendationsHint: false,
            ShowNoResults: false,
            ShowFooter: true,
            ShowDetailsPane: false);

        int withoutSortNoDetails = SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(baseState, 1080);
        int withSortNoDetails = SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(
            baseState with { ShowSortHeader = true },
            1080);
        Assert.Equal(SearchPopupSurfacePolicy.SortHeaderHeightLogical, withSortNoDetails - withoutSortNoDetails);

        int withoutSortWithDetails = SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(
            baseState with { ShowDetailsPane = true },
            1080);
        int withSortWithDetails = SearchPopupSurfacePolicy.ComputeDesiredLogicalHeight(
            baseState with { ShowDetailsPane = true, ShowSortHeader = true },
            1080);
        Assert.Equal(withoutSortWithDetails, withSortWithDetails);

        var settings = new SettingsService(Path.Combine(_root, "sort-header-pref"));
        Assert.False(settings.Settings.Search.SearchShowSortHeader);
        settings.Settings.Search.SearchShowSortHeader = true;
        Assert.True(settings.Settings.SearchShowSortHeader);
    }

    [Fact]
    public async Task SortStateMachine_CyclesThreeStatesBackToRelevanceAndStampsModifiedDateImmediately()
    {
        var settings = new SettingsService(Path.Combine(_root, "sort-cycle-audit"));
        settings.Settings.Language = "zh-CN";
        settings.Settings.Search.SearchShowSortHeader = true;
        settings.Settings.Search.SearchDefaultSortColumn = "relevance";
        settings.Settings.Search.SearchIncludeWebFallback = false;

        var localization = new LocalizationService(settings);
        var store = new QuickCaptureStore(Path.Combine(_root, "qc-sort-cycle"));
        var older = new DateTimeOffset(2025, 3, 10, 9, 0, 0, TimeSpan.FromHours(8));
        var newer = new DateTimeOffset(2025, 11, 20, 14, 30, 0, TimeSpan.FromHours(8));
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem { Id = "n1", Title = "预算报表A", Body = "预算报表A", CreatedAt = older, UpdatedAt = older },
                new QuickCaptureItem { Id = "n2", Title = "预算报表B", Body = "预算报表B", CreatedAt = newer, UpdatedAt = newer }
            ]
        });
        var quickCapture = new QuickCaptureService(store);
        var everything = new EverythingSearchService(settings);
        var history = new SearchHistoryService(Path.Combine(_root, "hist-sort-cycle"));
        var fileMeta = new FileMetaService();

        using var engine = new SearchEngineService(settings, localization, everything, quickCapture);
        using var vm = new ViewModels.SearchPopupViewModel(engine, settings, localization, history, fileMeta);

        vm.Query = "预算报表";
        await vm.RefreshSearchAsync();

        // DateDisplay is stamped immediately on first frame from ModifiedAt without waiting 220ms.
        Assert.Equal(2, vm.CurrentResults.Count(static i => i.Kind == SearchResultKind.QuickCapture));
        Assert.All(
            vm.CurrentResults.Where(static i => i.Kind == SearchResultKind.QuickCapture),
            static item => Assert.False(string.IsNullOrWhiteSpace(item.DateDisplay)));

        // 3-state ToggleSort cycle when DefaultSortColumn == "relevance":
        // Click 1 on Size -> Size Descending; Click 2 -> Size Ascending; Click 3 -> Relevance.
        Assert.Equal(ResultSortColumn.Relevance, vm.SortColumn);
        vm.ToggleSort(ResultSortColumn.Size);
        Assert.Equal(ResultSortColumn.Size, vm.SortColumn);
        Assert.False(vm.SortAscending);

        vm.ToggleSort(ResultSortColumn.Size);
        Assert.Equal(ResultSortColumn.Size, vm.SortColumn);
        Assert.True(vm.SortAscending);

        vm.ToggleSort(ResultSortColumn.Size);
        Assert.Equal(ResultSortColumn.Relevance, vm.SortColumn);

        // Turning off SearchShowSortHeader resets any manual column sort back to default.
        vm.ToggleSort(ResultSortColumn.Name);
        Assert.Equal(ResultSortColumn.Name, vm.SortColumn);
        settings.Settings.Search.SearchShowSortHeader = false;
        await settings.SaveAsync();
        Assert.Equal(ResultSortColumn.Relevance, vm.SortColumn);

        // FileMetaService formats ModifiedAt (LastWriteTime) rather than CreationTime.
        string sampleFile = Path.Combine(_root, "modified_vs_created.txt");
        await File.WriteAllTextAsync(sampleFile, "hello");
        var createdDate = new DateTime(2021, 1, 15, 10, 0, 0, DateTimeKind.Local);
        var modifiedDate = new DateTime(2024, 8, 22, 16, 45, 0, DateTimeKind.Local);
        File.SetCreationTime(sampleFile, createdDate);
        File.SetLastWriteTime(sampleFile, modifiedDate);

        var probeItem = new SearchResultItem
        {
            Kind = SearchResultKind.File,
            Title = "modified_vs_created.txt",
            DetailPath = sampleFile
        };
        await fileMeta.EnrichAsync([probeItem]);
        Assert.Equal(FileMetaService.FormatDate(modifiedDate), probeItem.DateDisplay);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp directory cleanup.
        }
    }
}
