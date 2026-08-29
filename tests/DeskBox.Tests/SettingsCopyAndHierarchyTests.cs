using System.Text.Json;

namespace DeskBox.Tests;

public sealed class SettingsCopyAndHierarchyTests
{
    [Fact]
    public void CapsuleModeIsTopLevelAndWidgetGroupsRemainUnderAppearance()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string appearanceXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml"));
        string overviewResources = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Styles/SettingsOverviewResources.xaml"));
        string capsuleXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml"));
        System.Xml.Linq.XDocument capsuleIcon = System.Xml.Linq.XDocument.Load(
            Path.Combine(
                root,
                "src/DeskBox/Assets/SettingsNavIcons/capsule-mode.svg"));
        string routes = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml.cs"));

        int menuStart = windowXaml.IndexOf(
            "<NavigationView.MenuItems>",
            StringComparison.Ordinal);
        int menuEnd = windowXaml.IndexOf(
            "</NavigationView.MenuItems>",
            menuStart,
            StringComparison.Ordinal);
        Assert.True(menuStart >= 0 && menuEnd > menuStart);
        string primaryMenu = windowXaml[menuStart..menuEnd];

        Assert.Contains("Tag=\"CapsuleMode\"", primaryMenu, StringComparison.Ordinal);
        Assert.Contains("capsule-mode.svg", primaryMenu, StringComparison.Ordinal);
        System.Xml.Linq.XNamespace svg = "http://www.w3.org/2000/svg";
        Assert.Equal(5, capsuleIcon.Descendants(svg + "path").Count());
        Assert.Equal(
            5,
            capsuleIcon.Descendants().Count(element =>
                element.Name == svg + "linearGradient" ||
                element.Name == svg + "radialGradient"));
        // Capsule mode is now a top-level page: the Appearance section must
        // not reference it at all anymore.
        Assert.DoesNotContain("CapsuleMode", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"WidgetGroups\"", primaryMenu, StringComparison.Ordinal);
        Assert.Contains("Tag=\"WidgetGroups\"", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetCapsuleModeEnabled", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ItemsSource=\"{Binding AvailableWidgetCollapseBehaviorOptions}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Capsule.Enabled.Title", capsuleXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetCapsuleModeEnabled", capsuleXaml, StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedWidgetCollapseBehavior, Mode=TwoWay}\"",
            capsuleXaml,
            StringComparison.Ordinal);
        Assert.Contains("Tag=\"WidgetGroups\"", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsWidgetGroupsEnabled", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsWidgetGroupsEnabled", windowXaml, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding AvailableWidgetGroupNavigationStyleOptions}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedWidgetGroupDefaultNavigationStyle, Mode=TwoWay}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SettingValueTextStyle\"", overviewResources, StringComparison.Ordinal);
        Assert.Contains("ExistingWidgetGroupItems", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.Existing.Name.Title", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetGroupNameTextBox_LostFocus", windowXaml, StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource SectionTitleTextStyle}\"",
            capsuleXaml,
            StringComparison.Ordinal);
        Assert.Contains("Settings.Section.CapsuleMode", capsuleXaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,8,0,0\"", capsuleXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.PageDescription", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.Default.Title", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.Default.Description", windowXaml, StringComparison.Ordinal);
        Assert.Contains("WidgetGroupNavigationComboBox_SelectionChanged", windowXaml, StringComparison.Ordinal);
        Assert.Contains("DissolveWidgetGroupButton_Click", windowXaml, StringComparison.Ordinal);
        int accentColor = appearanceXaml.IndexOf("Settings.Accent.Source.Title", StringComparison.Ordinal);
        int widgetGroups = appearanceXaml.IndexOf("Tag=\"WidgetGroups\"", StringComparison.Ordinal);
        int material = appearanceXaml.IndexOf("Tag=\"AppearanceMaterialSettings\"", StringComparison.Ordinal);
        Assert.True(accentColor >= 0 && widgetGroups > accentColor);
        Assert.True(widgetGroups < material);
        // Capsule mode is a top-level page now.
        Assert.Contains(
            "[\"CapsuleMode\"] = new(\"CapsuleMode\", \"Settings.Section.CapsuleMode\", null, \"CapsuleMode\")",
            routes,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"WidgetGroups\"] = new(\"WidgetGroups\", \"Settings.Section.WidgetGroups\", \"Appearance\", \"Appearance\")",
            routes,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OutcomeBasedSettings_UseDirectChoicesInsteadOfAmbiguousMasterSwitches()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string appearanceXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml"));
        string fileWidgetXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));
        string appSettings = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Models/AppSettings.cs"));
        string routes = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml.cs"));

        Assert.Contains("SelectedAccentColorSource", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding UseSystemAccentColor", appearanceXaml, StringComparison.Ordinal);

        Assert.Contains("SelectedFileOpenMethod", windowXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedShowDesktopBehavior", windowXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedWeatherLocationMode", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding DoubleClickToOpen", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding KeepWidgetsVisibleOnShowDesktop", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding WeatherAutoLocation", windowXaml, StringComparison.Ordinal);

        // File stacking is redesigned around an explicit master switch plus
        // an automatic-grouping sub-switch, so the plain dropdown is gone.
        Assert.Contains("IsOn=\"{x:Bind ViewModel.FileStacksEnabled, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.FileStacks.Mode.Title", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.FileStacks.Mode.Description", windowXaml, StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{Binding FileStacksEnabled, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool FileStacksEnabled { get; set; } = true;",
            appSettings,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "public bool WidgetCapsuleModeEnabled",
            appSettings,
            StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{Binding FileStackAutoStacking, Mode=TwoWay}\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains(
            "SelectedFileWidgetFolderOpenBehavior",
            fileWidgetXaml,
            StringComparison.Ordinal);

        Assert.Contains("HoverButtonActionsSummaryText", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"HoverButtonActionsDropDown_Click\"", windowXaml, StringComparison.Ordinal);
        Assert.Equal(
            1,
            windowXaml.Split(
                "Click=\"HoverButtonActionsDropDown_Click\"",
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("InteractionHoverSettings", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("InteractionHoverSettings", routes, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding ShowHoverButtons", windowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedChineseCopy_UsesPreciseTerms()
    {
        string root = FindRepositoryRoot();
        using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Strings/zh-CN.json")));

        IReadOnlyDictionary<string, string> expected = new Dictionary<string, string>
        {
            ["Settings.Nav.CapsuleMode"] = "胶囊模式",
            ["Settings.CollapseBehavior.Title"] = "默认显示方式",
            ["Settings.CollapseBehavior.Expanded"] = "始终展开",
            ["Widget.CollapseBehavior.Title"] = "展开方式",
            ["Widget.CollapseBehavior.System"] = "跟随默认",
            ["Widget.CollapseBehavior.Click"] = "点击展开",
            ["Widget.CollapseBehavior.Smart"] = "悬停自动展开",
            ["Settings.Capsule.Overrides.FollowGlobal"] = "恢复默认",
            ["Widget.OpenStorageFolder"] = "打开格子文件夹",
            ["Widget.ShowInExplorer"] = "在文件资源管理器中显示",
            ["Search.Menu.AttachToTodo"] = "添加到待办",
            ["QuickCapture.SaveToRecords"] = "保存为随记",
            ["QuickCapture.PinToRecords"] = "固定这条随记",
            ["Widget.Stack.FollowDefaults"] = "跟随默认设置",
            ["Widget.Stack.DisableGroup"] = "不再自动叠放此类",
            ["Widget.Stack.Dissolve"] = "解散叠放",
            ["Widget.Stack.RemoveItem"] = "移出叠放",
            ["Settings.FileStacks.Threshold.Title"] = "自动叠放数量",
            ["Widget.Group.NavigationStyle"] = "标题栏布局",
            ["Settings.WidgetGroupNavigation.Stack"] = "折叠显示",
            ["Settings.WidgetGroupNavigation.Tabs"] = "并排显示",
            ["Settings.WidgetGroupNavigation.Title"] = "标题栏布局",
            ["Settings.WidgetGroups.PageDescription"] = "将多个格子放在同一位置，通过标题栏切换。每个格子的内容仍然相互独立。",
            ["Settings.WidgetGroups.FollowDefaultWithValue"] = "跟随默认（{0}）",
            ["Widget.Group.Join"] = "组合格子…",
            ["Widget.DeleteFolderToRecycleBin"] = "同时移入回收站",
            ["Search.Delete.Action"] = "移入回收站",
            ["Settings.QuickCapture.Format.Title"] = "编辑格式",
            ["Settings.QuickCapture.Format.Description"] = "选择随记编辑器使用 Markdown 或纯文本",
            ["Settings.Accent.Source.Title"] = "主题色来源",
            ["Settings.OpenMethod.Title"] = "打开方式",
            ["Settings.ShowDesktopBehavior.Title"] = "按 Win+D 后",
            ["Settings.WidgetLayerMode.Title"] = "格子层级",
            ["Settings.WidgetLayerMode.Dynamic"] = "动态层级",
            ["Settings.WidgetLayerMode.DesktopPinned"] = "桌面固定层",
            ["Settings.WidgetLayerMode.QuickReveal"] = "快捷唤起层",
            ["Settings.AttachmentStorageMode.Title"] = "附件保存方式",
            ["Settings.ShowImageFilesAsIcons.Title"] = "图片和视频只显示图标",
            ["Settings.Capsule.WidthMode.Title"] = "展开后的宽度",
            ["Settings.Capsule.WidthMode.Aligned"] = "沿用胶囊宽度",
            ["Settings.Capsule.WidthMode.Independent"] = "使用格子宽度",
            ["Settings.ManagedStorage.ListTitle"] = "已关闭格子的收纳文件夹",
            ["Settings.AttachmentHealth.Title"] = "检查附件",
            ["Settings.RuntimeHealth.Title"] = "后台服务状态",
            ["Settings.RuntimeHealth.Resync"] = "重新同步",
            ["Settings.DataBackup.Description"] = "备份设置、格子、随记、待办和附件副本，不含文件格子中的文件",
            ["Settings.Restore.Description"] = "恢复默认设置，保留语言、开机启动、格子开关和已有内容",
            ["Settings.Restore.Tooltip"] = "恢复默认设置，不会删除已有内容",
            ["Settings.Todo.Group.FooterActions.Title"] = "底部栏",
            ["Settings.Todo.Group.FooterActions.Description"] = "选择底部显示的剩余任务数量和清除已完成按钮",
            ["Settings.Todo.FooterDisplay.Title"] = "显示内容",
            ["Settings.Todo.ShowFooterStats.Title"] = "剩余任务数量",
            ["Settings.Todo.ShowClearCompleted.Title"] = "清除已完成按钮",
            ["Settings.Onboarding.Description"] = "重新查看格子创建、文件收纳、功能格子、外观和快捷键说明",
            ["Settings.Weather.LocationMode.Title"] = "位置来源",
            ["Settings.FileStacks.Title"] = "文件叠放",
            ["Settings.FileStacks.Mode.Title"] = "叠放模式",
            ["Settings.FileStacks.Mode.Description"] = "开启手动叠放和自动叠放；关闭后隐藏所有叠放",
            ["Settings.FileStacks.Auto.Title"] = "自动叠放",
            ["Settings.FileStacks.Status.Manual"] = "仅手动叠放",
            ["Settings.HoverButtonActions.None"] = "不显示"
        };

        foreach ((string key, string value) in expected)
        {
            Assert.Equal(value, strings.RootElement.GetProperty(key).GetString());
        }

        string json = strings.RootElement.GetRawText();
        Assert.DoesNotContain("...", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Deskbox", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AppearanceSummary_UsesTitleIconSelector()
    {
        string root = FindRepositoryRoot();
        string appearanceXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml"));

        Assert.Contains(
            "Text=\"{Binding SelectedWidgetTitleIconModeText}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding AvailableWidgetTitleIconModeOptions}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedWidgetTitleIconMode, Mode=TwoWay}\"",
            appearanceXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureWidgetOverview_UsesColorTitleIconsAndStandardCardSpacing()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.LocalizationAndWidgets.cs"));

        Assert.Contains("new WidgetTitleIcon", source, StringComparison.Ordinal);
        Assert.Contains(
            "IconKind = WidgetTitleIconKindNames.FromWidgetKind(entry.Kind)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Mode = WidgetTitleIconModeNames.Color", source, StringComparison.Ordinal);
        Assert.Contains("IconSize = 16", source, StringComparison.Ordinal);
        Assert.Contains("Margin = new Thickness(4)", source, StringComparison.Ordinal);
        Assert.Contains("ColumnSpacing = 12", source, StringComparison.Ordinal);
        Assert.Contains(
            "Style = (Style)SettingsRoot.Resources[\"SettingCardIdentityGridStyle\"]",
            source,
            StringComparison.Ordinal);
        Assert.Contains("identity.Padding = new Thickness(16, 10, 16, 10)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var icon = new FontIcon", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsOverviewCards_MatchLanguageCardTypographyAndIdentitySpacing()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string overviewResources = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Styles/SettingsOverviewResources.xaml"));
        string appearanceXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml"));
        string fileWidgetXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));
        string featureRows = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.LocalizationAndWidgets.cs"));

        foreach (string resources in new[] { windowXaml, overviewResources })
        {
            string titleStyle = SliceSection(
                resources,
                "x:Key=\"SettingTitleTextStyle\"",
                "</Style>");
            Assert.Contains(
                "<Setter Property=\"FontWeight\" Value=\"Normal\" />",
                titleStyle,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Value=\"SemiBold\"", titleStyle, StringComparison.Ordinal);
            Assert.Contains(
                "<x:Double x:Key=\"SettingsHeaderIconTextSpacing\">20</x:Double>",
                resources,
                StringComparison.Ordinal);
            Assert.Contains(
                "x:Key=\"SettingCardIdentityGridStyle\"",
                resources,
                StringComparison.Ordinal);
        }

        Assert.Equal(
            5,
            CountOccurrences(
                appearanceXaml,
                "Style=\"{StaticResource SettingCardIdentityGridStyle}\""));
        Assert.Equal(
            4,
            CountOccurrences(
                fileWidgetXaml,
                "Style=\"{StaticResource SettingCardIdentityGridStyle}\""));

        string interaction = SliceSection(
            windowXaml,
            "x:Name=\"InteractionSection\"",
            "x:Name=\"InteractionWindowSettingsSection\"");
        Assert.Equal(
            0,
            CountOccurrences(
                interaction,
                "Style=\"{StaticResource SettingCardIdentityGridStyle}\""));

        string maintenance = SliceSection(
            windowXaml,
            "x:Name=\"MaintenanceSection\"",
            "x:Name=\"BackupRestoreSettingsSection\"");
        Assert.Equal(
            3,
            CountOccurrences(
                maintenance,
                "Style=\"{StaticResource SettingCardIdentityGridStyle}\""));
        Assert.Contains(
            "SettingsRoot.Resources[\"SettingCardIdentityGridStyle\"]",
            featureRows,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsOverview_PrioritizesOrganizationAndWidgetLayer()
    {
        string root = FindRepositoryRoot();
        string fileWidgetXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));

        Assert.True(
            fileWidgetXaml.IndexOf("Tag=\"DesktopOrganizationSettings\"", StringComparison.Ordinal) <
            fileWidgetXaml.IndexOf("Tag=\"FileDisplaySettings\"", StringComparison.Ordinal));
        Assert.Contains("Click=\"OrganizeDesktopButton_Click\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("DesktopOrganization.Settings.StartAction", fileWidgetXaml, StringComparison.Ordinal);
        Assert.True(
            fileWidgetXaml.IndexOf("Tag=\"FileStackSettings\"", StringComparison.Ordinal) <
            fileWidgetXaml.IndexOf("SelectedFileWidgetFolderOpenBehavior", StringComparison.Ordinal));
        Assert.Contains(
            "MinHeight=\"{StaticResource SettingsRowMinHeight}\"",
            fileWidgetXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Padding=\"{StaticResource SettingsRowPadding}\"",
            fileWidgetXaml,
            StringComparison.Ordinal);

        int interactionSection = windowXaml.IndexOf(
            "x:Name=\"InteractionSection\"",
            StringComparison.Ordinal);
        int widgetLayer = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.WidgetLayerMode.Title\"",
            interactionSection,
            StringComparison.Ordinal);
        int hoverActions = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.Interaction.Hover.Title\"",
            interactionSection,
            StringComparison.Ordinal);
        int interactionDetail = windowXaml.IndexOf(
            "x:Name=\"InteractionWindowSettingsSection\"",
            interactionSection,
            StringComparison.Ordinal);
        int openMethod = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.OpenMethod.Title\"",
            interactionDetail,
            StringComparison.Ordinal);
        int showDesktopBehavior = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.ShowDesktopBehavior.Title\"",
            interactionDetail,
            StringComparison.Ordinal);
        int globalHotkey = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.GlobalHotkey.Title\"",
            interactionDetail,
            StringComparison.Ordinal);
        int resizeSnap = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.ResizeSnap.Title\"",
            interactionDetail,
            StringComparison.Ordinal);
        int desktopDoubleClick = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.DesktopDoubleClick.Title\"",
            interactionDetail,
            StringComparison.Ordinal);

        int generalSection = windowXaml.IndexOf(
            "x:Name=\"GeneralSection\"",
            StringComparison.Ordinal);
        int language = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.Language.Title\"",
            generalSection,
            StringComparison.Ordinal);
        int attachmentStorage = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.AttachmentStorageMode.Title\"",
            generalSection,
            StringComparison.Ordinal);
        int autoStart = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.AutoStart.Title\"",
            generalSection,
            StringComparison.Ordinal);
        int onboarding = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.Onboarding.Title\"",
            generalSection,
            StringComparison.Ordinal);

        Assert.True(interactionSection >= 0);
        Assert.True(widgetLayer > interactionSection);
        Assert.True(widgetLayer < hoverActions);
        Assert.True(hoverActions < interactionDetail);
        Assert.True(interactionDetail < openMethod);
        Assert.True(openMethod < showDesktopBehavior);
        Assert.True(showDesktopBehavior < globalHotkey);
        Assert.True(globalHotkey < resizeSnap);
        Assert.True(resizeSnap < desktopDoubleClick);
        Assert.True(generalSection >= 0);
        Assert.True(generalSection < language);
        Assert.True(language < attachmentStorage);
        Assert.True(attachmentStorage < autoStart);
        Assert.True(autoStart < onboarding);
        Assert.DoesNotContain("InteractionHotkeySettings", windowXaml, StringComparison.Ordinal);
        Assert.Equal(
            1,
            windowXaml.Split("Settings.WidgetLayerMode.Title", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void WeatherDisplayOptions_UseSharedMultiSelectCard()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string navigation = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.Navigation.cs"));

        string weather = SliceSection(
            windowXaml,
            "x:Name=\"WeatherSettingsSection\"",
            "x:Name=\"GeneralSection\"");

        Assert.Contains("Click=\"WeatherDisplayOptionsDropDown_Click\"", weather, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding WeatherDisplayOptionsSummaryText}\"", weather, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(weather, "Settings.Weather.Group.Display.Title"));
        Assert.DoesNotContain("IsOn=\"{Binding WeatherShowForecast", weather, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding WeatherShowPressure", weather, StringComparison.Ordinal);
        Assert.Contains("SettingsMultiSelectMenu.Show(", navigation, StringComparison.Ordinal);
        Assert.Contains("ViewModel.AvailableWeatherDisplayOptions", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchHotkey_IsOwnedBySearchSettingsWithoutDuplicateTopHeading()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string searchXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml"));
        string searchCodeBehind = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml.cs"));

        Assert.DoesNotContain("SearchHotkeyToggle2", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Search.Hotkey.Title", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SearchHotkeyExpander\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SearchHotkeyToggle\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.Search.Hotkey.Title", searchXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Search.Scope.Title", searchXaml, StringComparison.Ordinal);
        Assert.Contains("FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Search)", searchCodeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetCreationEntries_AreAvailableFromTrayAndEveryWidgetTitleBar()
    {
        string root = FindRepositoryRoot();
        string traySource = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/App.Tray.cs"));
        string widgetCommands = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));

        int trayMapEntry = traySource.IndexOf(
            "contextMenu.Items.Add(mapFolderItem)",
            StringComparison.Ordinal);
        int trayFeatureEntry = traySource.IndexOf(
            "contextMenu.Items.Add(addFeatureWidgetItem)",
            StringComparison.Ordinal);

        Assert.True(trayMapEntry >= 0);
        Assert.True(trayFeatureEntry > trayMapEntry);
        Assert.Contains("OpenFeatureWidgetsFromTray", traySource, StringComparison.Ordinal);
        Assert.Contains("ShowSettings(\"FeatureWidgets\")", traySource, StringComparison.Ordinal);

        Assert.DoesNotContain("contentCanAdd", widgetCommands, StringComparison.Ordinal);
        Assert.DoesNotContain("if (_config.WidgetKind == WidgetKind.File)", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("ContentWidgetShell.ShowAddButton =", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("actions.Contains(SettingsService.WidgetHoverActionAdd)", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("ShowWidgetCreateFlyout();", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("Common.NewWidget", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("Common.NewFolderMapping", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("Common.AddFeatureWidget", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("CreateFolderWidgetFromPickerAsync", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("ShowSettings(\"FeatureWidgets\")", widgetCommands, StringComparison.Ordinal);
    }

    [Fact]
    public void DropdownOptionCopy_DoesNotUseParentheticalBadges()
    {
        string root = FindRepositoryRoot();
        string stringsDirectory = Path.Combine(root, "src/DeskBox/Strings");
        string[] optionKeys =
        [
            "Settings.WidgetLayerMode.DesktopPinned",
            "Settings.CollapseBehavior.Click",
            "Settings.CollapseBehavior.Manual",
            "Settings.Capsule.WidthMode.Aligned",
            "Settings.Capsule.ExpansionDirection.Auto",
            "Settings.Capsule.ExpansionDirection.Down",
            "Settings.Capsule.ExpansionDirection.Up",
            "Settings.CollapsedStyle.Smart",
            "Settings.CompactContent.Smart",
            "Settings.Capsule.Direction.Auto",
            "Settings.Capsule.Animation.Smooth",
            "Settings.Capsule.HoverResponse.Balanced",
            "Settings.Capsule.MediaCorner.FollowWidget",
            "Settings.Density.Standard",
            "Settings.Density.Custom",
            "Settings.Animation.Preset.Standard",
            "Settings.Animation.Preset.Custom",
            "Settings.WidgetGroupNavigation.Auto",
            "Settings.Accent.Source.System",
            "Settings.Accent.Source.Custom",
            "Settings.OpenMethod.SingleClick",
            "Settings.OpenMethod.DoubleClick",
            "Settings.ShowDesktopBehavior.KeepVisible",
            "Settings.ShowDesktopBehavior.HideWithWindows",
            "Settings.Weather.LocationMode.Auto",
            "Settings.Weather.LocationMode.Manual",
            "Settings.FileStacks.Status.Manual",
            "Settings.HoverButtonActions.None"
        ];

        foreach (string path in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(path));
            foreach (string optionKey in optionKeys)
            {
                string value = strings.RootElement.GetProperty(optionKey).GetString()!;
                Assert.DoesNotContain('(', value);
                Assert.DoesNotContain('（', value);
            }
        }
    }

    [Fact]
    public void QuickCaptureAndTodoSettings_UseTheSameCompactHierarchy()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));

        string quickCapture = SliceSection(
            windowXaml,
            "x:Name=\"QuickCaptureSettingsSection\"",
            "x:Name=\"TodoSettingsSection\"");
        string todo = SliceSection(
            windowXaml,
            "x:Name=\"TodoSettingsSection\"",
            "x:Name=\"MusicSettingsSection\"");

        Assert.Contains("IsOn=\"{Binding QuickCaptureEnabled, Mode=TwoWay}\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{Binding TodoEnabled, Mode=TwoWay}\"", todo, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(quickCapture, "Loaded=\"FeatureSettingsExpander_Loaded\""));
        Assert.Equal(5, CountOccurrences(todo, "Loaded=\"FeatureSettingsExpander_Loaded\""));

        AssertInOrder(
            quickCapture,
            "Settings.QuickCapture.WideLayout.Title",
            "Settings.QuickCapture.Tabs.Title",
            "Settings.ContentEditor.Group.Title",
            "Settings.QuickCapture.ClipboardTitle",
            "Settings.QuickCapture.Group.Data.Title");
        AssertInOrder(
            todo,
            "Settings.QuickCapture.WideLayout.Title",
            "Settings.Todo.Tabs.Title",
            "Settings.ContentEditor.Group.Title",
            "Settings.Todo.ReminderEnabled.Title",
            "Settings.Todo.Group.FooterActions.Title");

        Assert.Contains("Click=\"QuickCaptureTabsDropDown_Click\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("Click=\"TodoTabsDropDown_Click\"", todo, StringComparison.Ordinal);
        Assert.Contains("Click=\"TodoFooterDisplayDropDown_Click\"", todo, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding VisibleQuickCaptureDefaultViewOptions}\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding VisibleTodoDefaultFilterOptions}\"", todo, StringComparison.Ordinal);

        Assert.DoesNotContain("IsOn=\"{Binding QuickCaptureShowRecordsTab", quickCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding TodoShowAllTab", todo, StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding QuickCaptureEditorFormat, Mode=TwoWay}\"",
            quickCapture,
            StringComparison.Ordinal);
        Assert.DoesNotContain("QuickCaptureDefaultFormat", quickCapture, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapturePreviewLineCount_IsBoundInBothWindowHosts()
    {
        string root = FindRepositoryRoot();
        string standaloneWindow = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml"));
        string sharedSurface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));

        Assert.Contains(
            "MaxLines=\"{Binding ElementName=ItemsListView, Path=DataContext.ItemPreviewLineCount}\"",
            standaloneWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaxLines=\"{Binding ElementName=ItemsList, Path=DataContext.ItemPreviewLineCount}\"",
            sharedSurface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TextSize}\" MaxLines=\"3\"",
            sharedSurface,
            StringComparison.Ordinal);
    }

    private static string SliceSection(string xaml, string startToken, string endToken)
    {
        int start = xaml.IndexOf(startToken, StringComparison.Ordinal);
        int end = xaml.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return xaml[start..end];
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static void AssertInOrder(string source, params string[] values)
    {
        int lastIndex = -1;
        foreach (string value in values)
        {
            int index = source.IndexOf(value, lastIndex + 1, StringComparison.Ordinal);
            Assert.True(index > lastIndex, $"Expected '{value}' after index {lastIndex}.");
            lastIndex = index;
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "DeskBox repository root was not found.");
    }
}
