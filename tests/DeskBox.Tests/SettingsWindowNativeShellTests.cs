using System.Text.RegularExpressions;
using DeskBox.Services;
using DeskBox.Views;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Tests;

public sealed class SettingsWindowNativeShellTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlacementStore_RoundTripsNormalBoundsAndMaximizedFlag(bool isMaximized)
    {
        string filePath = Path.Combine(_tempRoot, "settings-window-placement.json");
        var store = new SettingsWindowPlacementStore(filePath);
        var expected = new SettingsWindowPlacement(240, 140, 1120, 780, isMaximized);

        store.Save(expected);

        Assert.True(store.TryLoad(out SettingsWindowPlacement actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PlacementStore_RejectsMinimizedPlaceholderAndNonPositiveSizes()
    {
        string filePath = Path.Combine(_tempRoot, "settings-window-placement.json");
        var store = new SettingsWindowPlacementStore(filePath);

        store.Save(new SettingsWindowPlacement(-32000, -32000, 1000, 760, false));
        Assert.False(store.TryLoad(out _));

        store.Save(new SettingsWindowPlacement(100, 100, 0, 760, false));
        Assert.False(store.TryLoad(out _));

        Directory.CreateDirectory(_tempRoot);
        File.WriteAllText(
            filePath,
            """{"x":-32000,"y":-32000,"widthDip":1000,"heightDip":760,"isMaximized":false}""");
        Assert.False(store.TryLoad(out _));
    }

    [Fact]
    public void ResolveInitialBounds_CentersDefaultWindowWhenNoSavedPlacementExists()
    {
        var workArea = new SettingsWindowWorkArea(0, 0, 1920, 1040);

        SettingsWindowResolvedBounds resolved = SettingsWindowPlacementStore.ResolveInitialBounds(
            savedPlacement: null,
            workArea,
            dpiScale: 1.0,
            defaultWidthDip: SettingsWindow.DefaultWindowWidth,
            defaultHeightDip: SettingsWindow.DefaultWindowHeight,
            minWidthDip: SettingsWindow.MinWindowWidth,
            minHeightDip: SettingsWindow.MinWindowHeight,
            workAreaMarginDip: 48);

        Assert.Equal(1000, resolved.WidthPx);
        Assert.Equal(760, resolved.HeightPx);
        Assert.Equal((1920 - 1000) / 2, resolved.X);
        Assert.Equal((1040 - 760) / 2, resolved.Y);
        Assert.False(resolved.IsMaximized);
    }

    [Fact]
    public void ResolveInitialBounds_ClampsOffscreenSavedBoundsAndFitsSmallHighDpiWorkAreas()
    {
        // 1366x768 @ 150% DPI leaves roughly 1366x720 physical px (~910x480 DIP).
        var smallLaptopWorkArea = new SettingsWindowWorkArea(0, 0, 1366, 720);
        var saved = new SettingsWindowPlacement(2400, 900, 1200, 840, IsMaximized: true);

        SettingsWindowResolvedBounds resolved = SettingsWindowPlacementStore.ResolveInitialBounds(
            saved,
            smallLaptopWorkArea,
            dpiScale: 1.5,
            defaultWidthDip: SettingsWindow.DefaultWindowWidth,
            defaultHeightDip: SettingsWindow.DefaultWindowHeight,
            minWidthDip: SettingsWindow.MinWindowWidth,
            minHeightDip: SettingsWindow.MinWindowHeight,
            workAreaMarginDip: 48);

        Assert.Equal(1366, resolved.WidthPx);
        Assert.Equal(720, resolved.HeightPx);
        Assert.Equal(0, resolved.X);
        Assert.Equal(0, resolved.Y);
        Assert.True(resolved.IsMaximized);
    }

    [Theory]
    [InlineData(500, NavigationViewPaneDisplayMode.LeftMinimal, 20)]
    [InlineData(639, NavigationViewPaneDisplayMode.LeftMinimal, 20)]
    [InlineData(640, NavigationViewPaneDisplayMode.LeftCompact, 28)]
    [InlineData(859, NavigationViewPaneDisplayMode.LeftCompact, 28)]
    [InlineData(860, NavigationViewPaneDisplayMode.Left, 36)]
    [InlineData(1440, NavigationViewPaneDisplayMode.Left, 36)]
    public void ResponsiveNavigationTiers_FollowWindows11Breakpoints(
        double width,
        NavigationViewPaneDisplayMode expectedMode,
        double expectedSidePadding)
    {
        Assert.Equal(expectedMode, SettingsWindow.ResolveResponsivePaneDisplayMode(width));
        Assert.Equal(expectedSidePadding, SettingsWindow.ResolveResponsivePageSidePadding(width));
    }

    [Fact]
    public void SettingsShellXaml_AlignsWithWindows11NativeShellInvariants()
    {
        Assert.Equal(500, SettingsWindow.MinWindowWidth);
        Assert.Equal(400, SettingsWindow.MinWindowHeight);
        Assert.Equal(1000, SettingsWindow.ContentMaxWidth);

        string xaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));

        // Root grid must not re-lock minimum bounds above 500x400.
        int rootEnd = xaml.IndexOf("<Grid.KeyboardAccelerators>", StringComparison.Ordinal);
        string rootTag = xaml[..rootEnd];
        Assert.DoesNotContain("MinWidth=", rootTag, StringComparison.Ordinal);
        Assert.DoesNotContain("MinHeight=", rootTag, StringComparison.Ordinal);

        // Search box lives in NavigationView.AutoSuggestBox, not AppTitleBar.
        int titleBarStart = xaml.IndexOf("x:Name=\"AppTitleBar\"", StringComparison.Ordinal);
        int navViewStart = xaml.IndexOf("x:Name=\"SettingsNavigationView\"", StringComparison.Ordinal);
        int autoSuggestSlot = xaml.IndexOf("<NavigationView.AutoSuggestBox>", StringComparison.Ordinal);
        int searchBoxPos = xaml.IndexOf("x:Name=\"SettingsSearchBox\"", StringComparison.Ordinal);
        Assert.True(titleBarStart > 0 && navViewStart > titleBarStart);
        Assert.True(autoSuggestSlot > navViewStart && searchBoxPos > autoSuggestSlot);
        Assert.Contains("x:Name=\"TitleBarLeftInsetColumn\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TitleBarRightInsetColumn\"", xaml, StringComparison.Ordinal);

        // ContentHost and SettingsBreadcrumbBar are left-aligned with 1000px MaxWidth.
        Assert.Contains("<x:Double x:Key=\"SettingsContentMaxWidth\">1000</x:Double>", xaml, StringComparison.Ordinal);
        int contentHostPos = xaml.IndexOf("x:Name=\"ContentHost\"", StringComparison.Ordinal);
        string contentHostWindow = xaml.Substring(contentHostPos, 240);
        Assert.Contains("HorizontalAlignment=\"Left\"", contentHostWindow, StringComparison.Ordinal);

        // SaveFailedInfoBar occupies its own flow row above SettingsNavigationView.
        int saveFailedPos = xaml.IndexOf("x:Name=\"SaveFailedInfoBar\"", StringComparison.Ordinal);
        string saveFailedWindow = xaml.Substring(saveFailedPos, 260);
        Assert.Contains("Grid.Row=\"1\"", saveFailedWindow, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", saveFailedWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Canvas.ZIndex", saveFailedWindow, StringComparison.Ordinal);

        // UpdateActivityCard dismiss button carries accessible name & tooltip.
        int dismissPos = xaml.IndexOf("Click=\"DismissUpdateActivity_Click\"", StringComparison.Ordinal);
        string dismissWindow = xaml[xaml.LastIndexOf("<Button", dismissPos, StringComparison.Ordinal)..xaml.IndexOf("/>", dismissPos, StringComparison.Ordinal)];
        Assert.Contains("svc:Localized.AutomationNameKey=\"Common.Close\"", dismissWindow, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.ToolTipKey=\"Common.Close\"", dismissWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedBackNavigationAndAccessibilityContracts_HoldAcrossSections()
    {
        string searchSectionXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml"));
        Assert.Contains(
            "<ToggleSwitch svc:Localized.AutomationNameKey=\"Settings.Search.Everything.AdvancedSyntax.Title\"",
            searchSectionXaml,
            StringComparison.Ordinal);

        string orgSectionXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml"));
        Assert.DoesNotContain("CloseRuleDetailButton_Click", orgSectionXaml, StringComparison.Ordinal);
        Assert.Contains(
            "ToolTipService.ToolTip=\"{x:Bind RuleDetailPath.Text, Mode=OneWay}\"",
            orgSectionXaml,
            StringComparison.Ordinal);

        string navCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.Navigation.cs"));
        Assert.Contains("orgSection.TryNavigateBackToRuleList()", navCode, StringComparison.Ordinal);
        Assert.Contains("\"DesktopOrganizationSettings:__detail__\"", navCode, StringComparison.Ordinal);

        string pointerCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs"));
        Assert.Contains("PointerUpdateKind.XButton1Pressed", pointerCode, StringComparison.Ordinal);

        string keyboardCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.KeyboardNavigation.cs"));
        Assert.Contains("VirtualKey.GoBack", keyboardCode, StringComparison.Ordinal);

        string settingsWindowXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        Assert.DoesNotContain("<KeyboardAccelerator Key=\"GoBack\"", settingsWindowXaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mica", "窗口材质", "外观", "控制窗口背景云母与亚克力效果")]
    [InlineData("acrylic", "窗口材质", "外观", "使用亚克力毛玻璃背景")]
    [InlineData("opacity", "透明度", "外观 / 窗口材质", "调整格子背景不透明程度")]
    [InlineData("hotkey", "全局快捷键", "交互", "使用键盘组合键唤起桌面格子")]
    [InlineData("autostart", "开机自动启动", "常规", "登录 Windows 时自动运行")]
    [InlineData("webdav", "云备份与同步", "数据与维护", "通过远程服务器同步配置快照")]
    public void SearchMatcher_ExpandsBilingualSynonymsAndKeepsDirectRankHigher(
        string query,
        string title,
        string breadcrumb,
        string description)
    {
        int synonymScore = SettingsSearchMatcher.GetScore(query, title, breadcrumb, description);
        Assert.NotEqual(SettingsSearchMatcher.NoMatch, synonymScore);

        int directScore = SettingsSearchMatcher.GetScore(title, title, breadcrumb, description);
        Assert.True(directScore < synonymScore);
    }

    [Theory]
    [InlineData("tmd", "透明度", "外观 / 窗口材质", "调整背景透明程度")]
    [InlineData("toumingdu", "透明度", "外观 / 窗口材质", "调整背景透明程度")]
    [InlineData("ym", "窗口材质", "外观", "支持云母与亚克力")]
    [InlineData("yunmu", "窗口材质", "外观", "支持云母与亚克力")]
    [InlineData("kjj", "全局快捷键", "交互", "自定义唤起热键")]
    [InlineData("kuaijiejian", "全局快捷键", "交互", "自定义唤起热键")]
    [InlineData("cz", "重置默认设置", "数据与维护", "恢复初始状态")]
    public void SearchMatcher_MatchesCjkPinyinInitialsAndSyllableBoundaries(
        string query,
        string title,
        string breadcrumb,
        string description)
    {
        int score = SettingsSearchMatcher.GetScore(query, title, breadcrumb, description);
        Assert.NotEqual(SettingsSearchMatcher.NoMatch, score);

        // Mid-syllable fragments (like "ang" inside "tan'chuang") must not false-match.
        Assert.Equal(
            SettingsSearchMatcher.NoMatch,
            SettingsSearchMatcher.GetScore("ang", "弹窗样式", "文件格子", "控制弹出面板外观"));
    }

    [Fact]
    public void Phase2_SixDualHotspotOverviewCards_AreConvertedToProgressiveExpanders()
    {
        string settingsWindowXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string appearanceXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml"));
        string fileWidgetXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));

        // 1. General -> PerformanceInlineExpander
        int generalStart = settingsWindowXaml.IndexOf("x:Name=\"GeneralSection\"", StringComparison.Ordinal);
        string generalSlice = settingsWindowXaml[generalStart..settingsWindowXaml.IndexOf("<ContentDialog", generalStart, StringComparison.Ordinal)];
        Assert.Contains("x:Name=\"PerformanceInlineExpander\"", generalSlice, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.HeaderKey=\"Settings.Performance.MoreSettings\"", generalSlice, StringComparison.Ordinal);

        // 2-5. Appearance -> Material, Density, AppVisual, Animation expanders
        foreach (string headerKey in new[]
                 {
                     "Settings.Material.Title",
                     "Settings.Density.Title",
                     "Settings.Group.AppVisual.Title",
                     "Settings.Group.Animation.Title"
                 })
        {
            Assert.Matches(
                new Regex($"<toolkit:SettingsExpander(?:(?!>).)*?svc:Localized\\.HeaderKey=\"{Regex.Escape(headerKey)}\"", RegexOptions.Singleline),
                appearanceXaml);
        }
        Assert.Contains("svc:Localized.HeaderKey=\"Settings.Material.MoreSettings\"", appearanceXaml, StringComparison.Ordinal);

        // 6. FileWidget -> FileStacks expander
        Assert.Matches(
            new Regex("<toolkit:SettingsExpander(?:(?!>).)*?svc:Localized\\.HeaderKey=\"Settings\\.FileStacks\\.Title\"", RegexOptions.Singleline),
            fileWidgetXaml);
        Assert.Contains("svc:Localized.HeaderKey=\"Settings.FileStacks.MoreSettings\"", fileWidgetXaml, StringComparison.Ordinal);

        // None of the overview section files may put HorizontalContentAlignment="Right" on SettingsExpander.
        foreach (string xaml in new[] { appearanceXaml, fileWidgetXaml })
        {
            Assert.DoesNotMatch(
                new Regex("<toolkit:SettingsExpander(?:(?!>).)*?HorizontalContentAlignment=\"Right\"", RegexOptions.Singleline),
                xaml);
        }
    }

    [Fact]
    public void Phase2_AdaptiveFlow_SearchRetention_AndDestructiveSafetyContracts_Hold()
    {
        string settingsWindowXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string overviewStylesXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Styles/SettingsOverviewResources.xaml"));
        string hotkeyDialogXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/Dialogs/HotkeyRecorderDialog.xaml"));
        string navCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.Navigation.cs"));
        string appearanceCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs"));
        string widgetsCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.LocalizationAndWidgets.cs"));

        // WrapFlowPanel & text-scale elasticity
        Assert.Contains("<controls:WrapFlowPanel", settingsWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"Height\" Value=\"34\" />", overviewStylesXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth=\"460\"", hotkeyDialogXaml, StringComparison.Ordinal);
        Assert.Contains("GotFocus=\"SettingsSearchBox_GotFocus\"", settingsWindowXaml, StringComparison.Ordinal);

        // Search query retention, themed 2px accent border highlight, and catalog coverage
        Assert.Contains("sender.IsSuggestionListOpen = false;", navCode, StringComparison.Ordinal);
        Assert.DoesNotContain("sender.Text = string.Empty;", navCode, StringComparison.Ordinal);
        Assert.Contains("\"AccentFillColorDefaultBrush\"", navCode, StringComparison.Ordinal);
        Assert.Contains("ResolveVisibleSettingsSearchTarget", navCode, StringComparison.Ordinal);
        Assert.Contains(
            SettingsSearchCatalog.Entries,
            entry => string.Equals(entry.HeaderKey, "Settings.Search.Everything.AdvancedSyntax.Title", StringComparison.Ordinal));

        // Destructive action confirmation & unified dialog coordinator
        Assert.Contains("Settings.WidgetBackground.PerWidget.ConfirmPrompt", appearanceCode, StringComparison.Ordinal);
        Assert.Contains("ShowDialogWhenFreeAsync", widgetsCode, StringComparison.Ordinal);
        Assert.Contains("ShowDialogWhenFreeAsync", navCode, StringComparison.Ordinal);

        // Themed breadcrumb brushes
        Assert.Contains("TextFillColorSecondaryBrush", settingsWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Binding IsAncestor", settingsWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Binding IsCurrent", settingsWindowXaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ttsy", "饕餮盛宴", "自定义主题", "冷僻汉字覆盖测试")]
    [InlineData("taotieshengyan", "饕餮盛宴", "自定义主题", "冷僻汉字覆盖测试")]
    [InlineData("cmwl", "魑魅魍魉", "扩展词条", "字典外生僻字测试")]
    [InlineData("chimeiwangliang", "魑魅魍魉", "扩展词条", "字典外生僻字测试")]
    [InlineData("ccxq", "璀璨星穹", "视觉特效", "星轨粒子渲染")]
    [InlineData("cuicanxingqiong", "璀璨星穹", "视觉特效", "星轨粒子渲染")]
    [InlineData("jmss", "静谧深邃", "色彩方案", "暗夜护眼配色")]
    [InlineData("jingmishensui", "静谧深邃", "色彩方案", "暗夜护眼配色")]
    public void SearchMatcher_CoversFullCjkUnifiedIdeographs_IncludingUncommonHanzi(
        string query,
        string title,
        string breadcrumb,
        string description)
    {
        Assert.Equal(20_992, SettingsSearchMatcher.CjkUnifiedCount);
        Assert.True(SettingsSearchMatcher.MappedCjkCount >= 20_950);
        Assert.True(SettingsSearchMatcher.TryGetCjkReadings('\u9FA5', out string[] yuReadings) && yuReadings.Contains("yu"));
        Assert.True(SettingsSearchMatcher.TryGetCjkReadings('\u9FF4', out string[] chaReadings) && chaReadings.Contains("cha"));

        int score = SettingsSearchMatcher.GetScore(query, title, breadcrumb, description);
        Assert.NotEqual(SettingsSearchMatcher.NoMatch, score);
    }

    [Theory]
    [InlineData("czmr", "重置默认", "数据与维护", "恢复出厂配置")]
    [InlineData("chongzhimoren", "重置默认", "数据与维护", "恢复出厂配置")]
    [InlineData("ztzl", "字体重量", "外观与主题", "调整字重粗细")]
    [InlineData("zitizhongliang", "字体重量", "外观与主题", "调整字重粗细")]
    [InlineData("zdhs", "最大行数", "文件显示", "文件名折行上限")]
    [InlineData("zuidahangshu", "最大行数", "文件显示", "文件名折行上限")]
    [InlineData("htzx", "后台执行", "性能与资源", "空闲队列调度")]
    [InlineData("houtaizhixing", "后台执行", "性能与资源", "空闲队列调度")]
    [InlineData("sdtz", "色调调整", "外观与主题", "冷暖色偏微调")]
    [InlineData("sediaotiaozheng", "色调调整", "外观与主题", "冷暖色偏微调")]
    [InlineData("jkdy", "接口调用", "高级", "组件脚本桥接")]
    [InlineData("jiekoudiaoyong", "接口调用", "高级", "组件脚本桥接")]
    [InlineData("tcdh", "弹窗动画", "文件格", "面板展开动效")]
    [InlineData("tanchuangdonghua", "弹窗动画", "文件格", "面板展开动效")]
    [InlineData("dm", "弹幕样式", "组件", "浮动提示层")]
    [InlineData("danmuyangshi", "弹幕样式", "组件", "浮动提示层")]
    [InlineData("dhsc", "动画时长", "胶囊模式", "过渡持续毫秒")]
    [InlineData("donghuashichang", "动画时长", "胶囊模式", "过渡持续毫秒")]
    [InlineData("zdzz", "自动增长", "布局", "随内容扩展高度")]
    [InlineData("zidongzengzhang", "自动增长", "布局", "随内容扩展高度")]
    [InlineData("zdyc", "自动隐藏", "交互", "边缘贴靠收起")]
    [InlineData("zidongyincang", "自动隐藏", "交互", "边缘贴靠收起")]
    [InlineData("bzzt", "宝藏主题", "外观", "精选配色合集")]
    [InlineData("baozangzhuti", "宝藏主题", "外观", "精选配色合集")]
    [InlineData("jtbzt", "剪贴板粘贴", "剪贴板", "快捷键直接上屏")]
    [InlineData("jiantiebanzhantie", "剪贴板粘贴", "剪贴板", "快捷键直接上屏")]
    [InlineData("jtbnt", "剪贴板粘贴", "剪贴板", "快捷键直接上屏")]
    [InlineData("jiantiebanniantie", "剪贴板粘贴", "剪贴板", "快捷键直接上屏")]
    [InlineData("ydmy", "云端密钥", "云备份", "加密访问凭据")]
    [InlineData("yunduanmiyao", "云端密钥", "云备份", "加密访问凭据")]
    [InlineData("yunduanmiyue", "云端密钥", "云备份", "加密访问凭据")]
    [InlineData("tbzg", "图标栅格", "外观", "对齐网格间距")]
    [InlineData("tubiaozhage", "图标栅格", "外观", "对齐网格间距")]
    [InlineData("tbsg", "图标栅格", "外观", "对齐网格间距")]
    [InlineData("tubiaoshange", "图标栅格", "外观", "对齐网格间距")]
    [InlineData("slthc", "缩略图缓存", "性能与资源", "图像解码池上限")]
    [InlineData("suolvetuhuancun", "缩略图缓存", "性能与资源", "图像解码池上限")]
    [InlineData("suoluetuhuancun", "缩略图缓存", "性能与资源", "图像解码池上限")]
    [InlineData("sxl", "刷新率上限", "性能与资源", "帧率节流策略")]
    [InlineData("shuaxinlvshangxian", "刷新率上限", "性能与资源", "帧率节流策略")]
    [InlineData("shuaxinlushangxian", "刷新率上限", "性能与资源", "帧率节流策略")]
    [InlineData("nvxing", "女性语音", "播报", "合成音色选择")]
    [InlineData("nuxing", "女性语音", "播报", "合成音色选择")]
    public void SearchMatcher_MatchesPolyphoneAlternates_AndUmlautTypingVariants(
        string query,
        string title,
        string breadcrumb,
        string description)
    {
        int score = SettingsSearchMatcher.GetScore(query, title, breadcrumb, description);
        Assert.NotEqual(SettingsSearchMatcher.NoMatch, score);
    }

    [Fact]
    public void SingleOwnerCatalog_HasZeroRetiredRoutesAndZeroParentChildDuplicateControls()
    {
        var routesField = typeof(SettingsWindow).GetField(
            "SectionRoutes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(routesField);

        var normalizeMethod = typeof(SettingsWindow).GetMethod(
            "NormalizeSettingsSectionTag",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(normalizeMethod);

        foreach (string retiredTag in new[]
                 {
                     "AppearanceDensitySettings",
                     "AppearanceWindowSettings",
                     "AppearanceAnimationSettings"
                 })
        {
            Assert.Equal("Appearance", (string?)normalizeMethod.Invoke(null, [retiredTag]));
            Assert.DoesNotContain(SettingsSearchCatalog.Entries, entry => entry.SectionTag == retiredTag);
        }

        var routesDict = (System.Collections.IDictionary)routesField.GetValue(null)!;
        var parentByTag = new Dictionary<string, string?>(StringComparer.Ordinal);
        var titleKeyByTag = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry kv in routesDict)
        {
            string tag = (string)kv.Key;
            object routeObj = kv.Value!;
            Type routeType = routeObj.GetType();
            parentByTag[tag] = (string?)routeType.GetProperty("ParentTag")!.GetValue(routeObj);
            titleKeyByTag[tag] = (string)routeType.GetProperty("TitleKey")!.GetValue(routeObj)!;
        }

        Assert.False(parentByTag.ContainsKey("AppearanceDensitySettings"));
        Assert.False(parentByTag.ContainsKey("AppearanceWindowSettings"));
        Assert.False(parentByTag.ContainsKey("AppearanceAnimationSettings"));

        var sectionControls = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (SettingsSearchCatalogEntry entry in SettingsSearchCatalog.Entries)
        {
            string destinationTag = (string)normalizeMethod.Invoke(null, [entry.SectionTag])!;
            if (!titleKeyByTag.TryGetValue(destinationTag, out string? routeTitleKey) ||
                string.Equals(entry.HeaderKey, routeTitleKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!sectionControls.TryGetValue(destinationTag, out HashSet<string>? keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                sectionControls[destinationTag] = keys;
            }

            keys.Add(entry.HeaderKey);
        }

        var overlaps = new List<string>();
        foreach ((string childTag, string? parentTag) in parentByTag)
        {
            if (parentTag is null ||
                !sectionControls.TryGetValue(childTag, out HashSet<string>? childKeys) ||
                !sectionControls.TryGetValue(parentTag, out HashSet<string>? parentKeys))
            {
                continue;
            }

            foreach (string key in childKeys)
            {
                if (parentKeys.Contains(key))
                {
                    overlaps.Add($"{parentTag} <-> {childTag}: {key}");
                }
            }
        }

        Assert.Empty(overlaps);

        string navCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.Navigation.cs"));
        Assert.DoesNotContain("sectionHeaderKeys", navCode, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
