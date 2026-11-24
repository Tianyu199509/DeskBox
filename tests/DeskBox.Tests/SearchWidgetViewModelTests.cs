using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class SearchWidgetViewModelTests : IDisposable
{
    private readonly string _tempRoot;

    public SearchWidgetViewModelTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.SearchWidgetViewModelTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(140, 140, SearchWidgetLayoutTier.MicroTile)]
    [InlineData(210, 120, SearchWidgetLayoutTier.MicroTile)]
    [InlineData(300, 60, SearchWidgetLayoutTier.CompactBar)]
    [InlineData(460, 80, SearchWidgetLayoutTier.CompactBar)]
    [InlineData(430, 96, SearchWidgetLayoutTier.WideStrip)]
    [InlineData(360, 96, SearchWidgetLayoutTier.WideStrip)]
    [InlineData(360, 130, SearchWidgetLayoutTier.WideStrip)]
    [InlineData(480, 130, SearchWidgetLayoutTier.WideStrip)]
    [InlineData(340, 160, SearchWidgetLayoutTier.MediumCard)]
    [InlineData(480, 160, SearchWidgetLayoutTier.LargeHub)]
    [InlineData(340, 260, SearchWidgetLayoutTier.MediumCard)]
    [InlineData(480, 260, SearchWidgetLayoutTier.LargeHub)]
    public void ResolveLayoutTier_MapsBoundsToExpectedTier(
        double width,
        double height,
        SearchWidgetLayoutTier expectedTier)
    {
        Assert.Equal(expectedTier, SearchWidgetViewModel.ResolveLayoutTier(width, height));
    }

    [Fact]
    public void PrefixTypingAndBackspace_LatchesAndUnlatchesQuickModesCleanly()
    {
        using var vm = CreateViewModel();

        vm.QueryText = "+ ";
        Assert.Equal(SearchQuickMode.Todo, vm.ActiveQuickMode);
        Assert.Equal(string.Empty, vm.QueryText);

        Assert.True(vm.HandleEmptyBackspace());
        Assert.Equal(SearchQuickMode.None, vm.ActiveQuickMode);

        vm.QueryText = "; ";
        Assert.Equal(SearchQuickMode.Note, vm.ActiveQuickMode);
        Assert.Equal(string.Empty, vm.QueryText);

        vm.ToggleQuickMode(SearchQuickMode.Note);
        Assert.Equal(SearchQuickMode.None, vm.ActiveQuickMode);

        vm.QueryText = "> ";
        Assert.Equal(SearchQuickMode.Command, vm.ActiveQuickMode);
        Assert.Equal(string.Empty, vm.QueryText);
    }

    [Fact]
    public async Task SubmitAsync_RoutesQuickTodoQuickNoteCommandCalcAndRegularQuery()
    {
        string? openedPopupQuery = null;
        int emptyPopupCount = 0;
        var executedActions = new List<string>();
        string? copiedValue = null;
        WidgetFeedbackRequest? feedback = null;

        using var vm = CreateViewModel(
            openSearchPopup: () => emptyPopupCount++,
            openSearchPopupWithQuery: q => openedPopupQuery = q,
            executeSearchAction: actionId =>
            {
                executedActions.Add(actionId);
                return Task.CompletedTask;
            },
            copyText: val => copiedValue = val,
            isTodoEnabled: () => true,
            isQuickCaptureEnabled: () => true);

        vm.FeedbackRequested += (_, e) => feedback = e.Request;

        // 1. Empty submit opens popup
        await vm.SubmitAsync();
        Assert.Equal(1, emptyPopupCount);

        // 2. Quick todo latched submit
        vm.ToggleQuickMode(SearchQuickMode.Todo);
        vm.QueryText = "提交搜索桌面入口原型";
        await vm.SubmitAsync();
        Assert.Equal(["quick-todo:提交搜索桌面入口原型"], executedActions);
        Assert.Equal(SearchQuickMode.None, vm.ActiveQuickMode);
        Assert.Equal(string.Empty, vm.QueryText);

        // 3. Quick note latched submit
        vm.ToggleQuickMode(SearchQuickMode.Note);
        vm.QueryText = "默认 Overlay 模式";
        await vm.SubmitAsync();
        Assert.Equal(2, executedActions.Count);
        Assert.Equal("quick-note:默认 Overlay 模式", executedActions[1]);

        // 4. Command latched submit hands off '> ...' to popup
        vm.ToggleQuickMode(SearchQuickMode.Command);
        vm.QueryText = "theme";
        await vm.SubmitAsync();
        Assert.Equal("> theme", openedPopupQuery);

        // 5. Calculator expression copies result and emits feedback
        vm.QueryText = "128*3.5";
        Assert.True(vm.HasCalcResult);
        Assert.Equal("= 448", vm.CalcResultText);
        await vm.SubmitAsync();
        Assert.Equal("448", copiedValue);
        Assert.NotNull(feedback);

        // 6. Regular query hands off directly
        vm.QueryText = "DeskBox";
        await vm.SubmitAsync();
        Assert.Equal("DeskBox", openedPopupQuery);
    }

    [Fact]
    public void UnpinQuery_RemovesPinnedItemEvenAfterRecentHistoryIsCleared()
    {
        var history = new SearchHistoryService(_tempRoot);
        history.RecordQuery("DeskBox 发版检查");
        history.ToggleFavorite("DeskBox 发版检查");
        history.RecordQuery("临时记录");

        using var vm = CreateViewModel(historyService: history);

        Assert.Single(vm.PinnedQueryItems);
        Assert.Single(vm.RecentQueryItems);

        vm.ClearRecentQueries();
        Assert.Empty(vm.RecentQueryItems);
        Assert.Single(vm.PinnedQueryItems);

        vm.UnpinQuery("DeskBox 发版检查");
        Assert.Empty(vm.PinnedQueryItems);
        Assert.False(history.IsFavorite("DeskBox 发版检查"));
    }

    [Fact]
    public void PrivacyMask_RevealsOnHoverAndMasksCompactCapsule()
    {
        var settingsService = new SettingsService(_tempRoot);
        settingsService.Settings.Search.SearchDesktopPrivacyMask = true;

        var history = new SearchHistoryService(_tempRoot);
        history.RecordQuery("机密合同草稿");
        history.ToggleFavorite("机密合同草稿");

        using var vm = CreateViewModel(settingsService: settingsService, historyService: history);

        Assert.True(vm.IsPrivacyMaskEnabled);
        Assert.True(vm.EffectivePrivacyMaskActive);
        Assert.All(vm.PinnedQueryItems[0].DisplayText, ch => Assert.Equal('•', ch));

        vm.SetPointerHovering(true);
        Assert.False(vm.EffectivePrivacyMaskActive);
        Assert.Equal("机密合同草稿", vm.PinnedQueryItems[0].DisplayText);

        vm.SetPointerHovering(false);
        Assert.True(vm.EffectivePrivacyMaskActive);

        (string primaryMasked, string fallbackMasked) = vm.BuildCapsuleSummary(hidesSensitiveContent: false);
        Assert.Equal(string.Empty, primaryMasked);
        Assert.False(fallbackMasked.Contains("机密", StringComparison.Ordinal));

        settingsService.Settings.Search.SearchDesktopPrivacyMask = false;
        vm.RefreshState();
        (string primaryVisible, _) = vm.BuildCapsuleSummary(hidesSensitiveContent: false);
        Assert.Contains("机密合同草稿", primaryVisible, StringComparison.Ordinal);

        (string forcedMaskPrimary, _) = vm.BuildCapsuleSummary(hidesSensitiveContent: true);
        Assert.Equal(string.Empty, forcedMaskPrimary);
    }

    [Fact]
    public void Win10CompatibilityAndAppGlyph_UseSafeGlyphs()
    {
        Assert.Equal("\uE890", SearchWidgetContent.ResolvePrivacyMaskGlyph(privacyOn: false, isWindows11OrLater: false));
        Assert.Equal("\uE890", SearchWidgetContent.ResolvePrivacyMaskGlyph(privacyOn: false, isWindows11OrLater: true));
        Assert.Equal("\uE72E", SearchWidgetContent.ResolvePrivacyMaskGlyph(privacyOn: true, isWindows11OrLater: false));
        Assert.Equal("\uED1A", SearchWidgetContent.ResolvePrivacyMaskGlyph(privacyOn: true, isWindows11OrLater: true));

        var appItem = new SearchRecommendationItem
        {
            Kind = SearchResultKind.File,
            Title = "Visual Studio Code",
            DetailPath = @"C:\Program Files\Microsoft VS Code\Code.exe"
        };
        Assert.Equal("\uE71D", SearchWidgetViewModel.ResolveItemGlyph(appItem));
    }

    [Fact]
    public void RecentOpenedFormatting_StripsShortcutExtensionsAndCompactsLocationPaths()
    {
        Assert.Equal("Microsoft Edge", SearchWidgetViewModel.FormatRecentOpenedTitle(new SearchRecommendationItem
        {
            Kind = SearchResultKind.File,
            Title = "Microsoft Edge.lnk",
            DetailPath = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Microsoft Edge.lnk"
        }));
        Assert.Equal("Docs", SearchWidgetViewModel.FormatRecentOpenedTitle(new SearchRecommendationItem
        {
            Kind = SearchResultKind.File,
            Title = "Docs.URL",
            DetailPath = @"C:\Users\simon\Desktop\Docs.URL"
        }));
        Assert.Equal(".lnk", SearchWidgetViewModel.FormatRecentOpenedTitle(new SearchRecommendationItem
        {
            Kind = SearchResultKind.File,
            Title = ".lnk"
        }));
        Assert.Equal("app用户评价-1.5.3.4.rp", SearchWidgetViewModel.FormatRecentOpenedTitle(new SearchRecommendationItem
        {
            Kind = SearchResultKind.File,
            Title = "app用户评价-1.5.3.4.rp"
        }));

        Assert.Equal("桌面备份", SearchWidgetViewModel.FormatCompactLocation(@"E:\桌面备份", null));
        Assert.Equal("Desktop", SearchWidgetViewModel.FormatCompactLocation(@"C:\Users\simon\Desktop\", null));
        Assert.Equal(@"E:\", SearchWidgetViewModel.FormatCompactLocation(@"E:\", null));
        Assert.Equal("AI工具", SearchWidgetViewModel.FormatCompactLocation("AI工具", @"C:\Desktop\Devin.lnk"));
        Assert.Equal("Desktop", SearchWidgetViewModel.FormatCompactLocation(null, @"C:\Users\simon\Desktop\report.pdf"));

        Assert.Equal(("+", "待办"), SearchWidgetContent.SplitQuickPillText("+ 待办"));
        Assert.Equal((";", "随记"), SearchWidgetContent.SplitQuickPillText("; 随记"));
        Assert.Equal((">", "命令"), SearchWidgetContent.SplitQuickPillText("> 命令"));
        Assert.Equal((string.Empty, "Compact"), SearchWidgetContent.SplitQuickPillText("Compact"));

        var settingsService = new SettingsService(_tempRoot);
        var history = new SearchHistoryService(Path.Combine(_tempRoot, "search-history.json"));
        history.RecordResult(new SearchResultItem
        {
            Kind = SearchResultKind.Folder,
            Title = "给麦mv风格",
            Subtitle = @"E:\桌面备份",
            DetailPath = @"E:\桌面备份"
        });
        history.RecordResult(new SearchResultItem
        {
            Kind = SearchResultKind.File,
            Title = "Microsoft Edge.lnk",
            Subtitle = "常用",
            DetailPath = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Microsoft Edge.lnk"
        });

        using var vm = CreateViewModel(settingsService: settingsService, historyService: history);
        Assert.Equal(2, vm.RecentOpenedItems.Count);
        Assert.Equal("Microsoft Edge", vm.RecentOpenedItems[0].DisplayTitle);
        Assert.Contains("常用", vm.RecentOpenedItems[0].DisplaySubtitle, StringComparison.Ordinal);
        Assert.Equal("给麦mv风格", vm.RecentOpenedItems[1].DisplayTitle);
        Assert.Contains("桌面备份", vm.RecentOpenedItems[1].DisplaySubtitle, StringComparison.Ordinal);
        Assert.DoesNotContain(@"E:\桌面备份", vm.RecentOpenedItems[1].DisplaySubtitle, StringComparison.Ordinal);

        settingsService.Settings.Search.SearchDesktopPrivacyMask = true;
        vm.RefreshState();
        Assert.All(vm.RecentOpenedItems[0].DisplayTitle, ch => Assert.Equal('•', ch));
        Assert.DoesNotContain("常用", vm.RecentOpenedItems[0].DisplaySubtitle, StringComparison.Ordinal);
        Assert.DoesNotContain("桌面备份", vm.RecentOpenedItems[1].DisplaySubtitle, StringComparison.Ordinal);

        var zhSettings = new SettingsService(_tempRoot);
        zhSettings.Settings.Core.Language = "zh-CN";
        var zhLocalization = new LocalizationService(zhSettings);
        Assert.Equal("全局搜索…", zhLocalization.T("Search.Widget.Placeholder"));
        Assert.Equal("搜索历史", zhLocalization.T("Search.Widget.PinnedAndRecent"));
        Assert.Equal("最近打开", zhLocalization.T("Search.Widget.RecentOpened"));
    }

    private SearchWidgetViewModel CreateViewModel(
        SettingsService? settingsService = null,
        SearchHistoryService? historyService = null,
        Action? openSearchPopup = null,
        Action<string>? openSearchPopupWithQuery = null,
        Func<string, Task>? executeSearchAction = null,
        Action<string>? copyText = null,
        Func<bool>? isTodoEnabled = null,
        Func<bool>? isQuickCaptureEnabled = null)
    {
        var settings = settingsService ?? new SettingsService(_tempRoot);
        var localization = new LocalizationService(settings);
        var config = new WidgetConfig
        {
            Id = "search-test",
            Name = "Search",
            WidgetKind = WidgetKind.Search
        };

        return new SearchWidgetViewModel(
            config,
            localization,
            settings,
            historyService,
            isEverythingConnected: () => true,
            openSearchPopup: openSearchPopup,
            openSearchPopupWithQuery: openSearchPopupWithQuery,
            executeSearchAction: executeSearchAction,
            copyText: copyText,
            isTodoEnabled: isTodoEnabled,
            isQuickCaptureEnabled: isQuickCaptureEnabled);
    }
}
