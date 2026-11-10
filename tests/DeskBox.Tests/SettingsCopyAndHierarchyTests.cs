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
        string navigation = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.Navigation.cs"));

        int menuStart = windowXaml.IndexOf(
            "<NavigationView.MenuItems>",
            StringComparison.Ordinal);
        int menuEnd = windowXaml.IndexOf(
            "</NavigationView.MenuItems>",
            menuStart,
            StringComparison.Ordinal);
        Assert.True(menuStart >= 0 && menuEnd > menuStart);
        string primaryMenu = windowXaml[menuStart..menuEnd];

        Assert.Contains("Tag=\"Interaction\"", primaryMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"CapsuleMode\"", primaryMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"WidgetGroups\"", primaryMenu, StringComparison.Ordinal);

        System.Xml.Linq.XNamespace svg = "http://www.w3.org/2000/svg";
        Assert.Equal(5, capsuleIcon.Descendants(svg + "path").Count());
        Assert.Equal(
            5,
            capsuleIcon.Descendants().Count(element =>
                element.Name == svg + "linearGradient" ||
                element.Name == svg + "radialGradient"));

        // Both CapsuleMode and WidgetGroups are consolidated under Interaction;
        // Appearance no longer hosts either of them.
        Assert.DoesNotContain("CapsuleMode", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"WidgetGroups\"", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetCapsuleModeEnabled", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ItemsSource=\"{Binding AvailableWidgetCollapseBehaviorOptions}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Capsule.Enabled.Title", capsuleXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetCapsuleModeEnabled", capsuleXaml, StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding CollapseBehavior, Mode=TwoWay}\"",
            capsuleXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IsWidgetGroupsEnabled", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsWidgetGroupsEnabled", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ItemsSource=\"{Binding AvailableGroupNavigationStyleOptions}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "controls:SettingsComboBox.Value=\"{Binding GroupNavigationStyle, Mode=TwoWay}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SettingValueTextStyle\"", overviewResources, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ExistingGroups}\"", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.Existing.Name.Title", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetGroupNameTextBox_LostFocus", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<toolkit:SettingsExpander", capsuleXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.CollapseBehavior.Title", capsuleXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CapsuleModeEntryCard\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.Section.CapsuleMode", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.PageDescription", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.Default.Title", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.Default.Description", windowXaml, StringComparison.Ordinal);
        Assert.Contains("WidgetGroupNavigationComboBox_SelectionChanged", windowXaml, StringComparison.Ordinal);
        Assert.Contains("DissolveWidgetGroupButton_Click", windowXaml, StringComparison.Ordinal);

        string interaction = SliceSection(
            windowXaml,
            "x:Name=\"InteractionSection\"",
            "x:Name=\"FeatureWidgetsSection\"");
        int capsuleIndex = interaction.IndexOf("x:Name=\"CapsuleModeEntryCard\"", StringComparison.Ordinal);
        int widgetGroupsIndex = interaction.IndexOf("Tag=\"WidgetGroups\"", StringComparison.Ordinal);
        int displaysIndex = interaction.IndexOf("Tag=\"Displays\"", StringComparison.Ordinal);
        Assert.True(capsuleIndex >= 0 && widgetGroupsIndex > capsuleIndex && displaysIndex > widgetGroupsIndex);

        int accentColor = appearanceXaml.IndexOf("Settings.Accent.Source.Title", StringComparison.Ordinal);
        int material = appearanceXaml.IndexOf("Settings.Material.Title", StringComparison.Ordinal);
        Assert.True(accentColor >= 0 && material > accentColor);

        Assert.DoesNotContain("[\"CapsuleMode\"]", routes, StringComparison.Ordinal);
        Assert.Contains(
            "\"CapsuleMode\" or \"CapsuleOverridesSettings\" or \"InteractionWindowSettings\" => \"Interaction\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"WidgetGroups\"] = new(\"WidgetGroups\", \"Settings.Section.WidgetGroups\", \"Interaction\", \"Interaction\")",
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
        string fileWidgetSettings = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Models/FileWidgetSettingsSlice.cs"));
        string routes = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml.cs"));

        Assert.Contains("AccentColorSource", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding UseSystemAccentColor", appearanceXaml, StringComparison.Ordinal);

        Assert.Contains("{x:Bind Interaction.FileOpenMethod, Mode=TwoWay}", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ShowDesktopBehavior, Mode=TwoWay}", windowXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedLocationMode", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding DoubleClickToOpen", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding DoubleClickToOpen", fileWidgetXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding KeepWidgetsVisibleOnShowDesktop", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding WeatherAutoLocation", windowXaml, StringComparison.Ordinal);

        // File stacking splits cleanly: FileWidgetSettingsSection hosts the direct drill-in card,
        // while AutoStacking + OpenMode/Popover* + rules live flat inside FileStackSettingsSectionTemplate.
        Assert.Contains("IsOn=\"{Binding AutoStacking, Mode=TwoWay}\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains(
            "HeaderKey=\"Settings.FileStacks.Title\"",
            fileWidgetXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "HeaderKey=\"Settings.FileStacks.Auto.Title\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "HeaderKey=\"Settings.FileStacks.OpenMode.Title\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsSegmented.Value=\"{x:Bind OpenMode, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool FileStacksEnabled { get; set; } = true;",
            fileWidgetSettings,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "public bool WidgetCapsuleModeEnabled",
            appSettings,
            StringComparison.Ordinal);
        Assert.Contains(
            "{x:Bind FeatureWidgets.FolderOpenBehavior, Mode=TwoWay}",
            fileWidgetXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{x:Bind Interaction.FileItemContextMenuEnabled, Mode=TwoWay}\"",
            fileWidgetXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool FileItemSystemContextMenuEnabled { get; set; }",
            fileWidgetSettings,
            StringComparison.Ordinal);

        Assert.Contains("{Binding HoverButtonActionsSummary}", windowXaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{x:Bind ShowHoverButtons, Mode=TwoWay}\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{x:Bind ShowHoverActionLockPosition, Mode=TwoWay}\"", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"HoverButtonActionsDropDown_Click\"", windowXaml, StringComparison.Ordinal);
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
            ["Widget.CollapseBehavior.Click"] = "单击展开",
            ["Widget.CollapseBehavior.Smart"] = "悬停自动展开",
            ["Settings.Capsule.Overrides.FollowGlobal"] = "恢复默认",
            ["Widget.OpenStorageFolder"] = "打开格子文件夹",
            ["Widget.ShowInExplorer"] = "打开文件所在的位置",
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
            ["Settings.WidgetGroupSwitchAnimation.Auto"] = "自动",
            ["Settings.WidgetGroupSwitchAnimation.Description"] = "切换格子组内格子时的过渡效果，自动时按标题栏布局选择方向",
            ["Settings.WidgetGroupSwitchAnimation.Fade"] = "淡入淡出",
            ["Settings.WidgetGroupSwitchAnimation.Horizontal"] = "左右滑动",
            ["Settings.WidgetGroupSwitchAnimation.None"] = "无动画",
            ["Settings.WidgetGroupSwitchAnimation.Title"] = "切换动画",
            ["Settings.WidgetGroupSwitchAnimation.Vertical"] = "上下滑动",
            ["Settings.WidgetGroups.PageDescription"] = "将多个格子放在同一位置，通过标题栏切换。每个格子的内容仍然相互独立。",
            ["Settings.WidgetGroups.FollowDefaultWithValue"] = "跟随默认({0})",
            ["Widget.Group.Join"] = "组合格子…",
            ["Widget.DeleteFolderToRecycleBin"] = "同时移入回收站",
            ["Search.Delete.Action"] = "移入回收站",
            ["Settings.QuickCapture.Format.Title"] = "编辑格式",
            ["Settings.QuickCapture.Format.Description"] = "影响编辑器语法与显示样式",
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
            ["Settings.Todo.Group.FooterActions.Description"] = "选择底部显示的剩余待办数量和清除已完成按钮",
            ["Settings.Todo.FooterDisplay.Title"] = "显示内容",
            ["Settings.Todo.ShowFooterStats.Title"] = "剩余待办数量",
            ["Settings.Todo.ShowClearCompleted.Title"] = "清除已完成按钮",
            ["Settings.Onboarding.Description"] = "重新查看格子创建、文件收纳、功能格子、外观和快捷键说明",
            ["Settings.Weather.LocationMode.Title"] = "位置来源",
            ["Settings.FileStacks.Title"] = "文件叠放",
            ["Settings.FileStacks.Auto.Title"] = "自动叠放",
            ["Settings.FileStacks.Status.Manual"] = "仅手动叠放",
            ["Settings.HoverButtonActions.None"] = "不显示"
        };

        foreach ((string key, string value) in expected)
        {
            Assert.Equal(value, strings.RootElement.GetProperty(key).GetString());
        }

        foreach (JsonProperty prop in strings.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith("CustomWidget.ApiDocs.", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain("...", prop.Value.GetString()!, StringComparison.Ordinal);
        }

        string json = strings.RootElement.GetRawText();
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
            "Description=\"{Binding WindowChromeSummaryText}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "svc:Localized.HeaderKey=\"Settings.WidgetTitleIcon.Title\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding AvailableTitleIconModeOptions}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding TitleIconMode, Mode=TwoWay}\"",
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

        // The colorful kind icon rides in the composed header (WidgetTitleIcon
        // is a UserControl and cannot fill the native HeaderIcon slot).
        Assert.Contains("new WidgetTitleIcon", source, StringComparison.Ordinal);
        Assert.Contains(
            "IconKind = WidgetTitleIconKindNames.FromWidgetKind(entry.Kind)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Mode = WidgetTitleIconModeNames.Color", source, StringComparison.Ordinal);
        Assert.Contains("IconSize = 16", source, StringComparison.Ordinal);
        Assert.Contains("card.Header = headerPanel;", source, StringComparison.Ordinal);
        Assert.Contains("new SettingsCard", source, StringComparison.Ordinal);
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

        // The shared metrics/styles live only in SettingsOverviewResources.xaml;
        // SettingsWindow.xaml merges that dictionary instead of duplicating it.
        Assert.Contains(
            "ms-appx:///Styles/SettingsOverviewResources.xaml",
            windowXaml,
            StringComparison.Ordinal);
        foreach (string resources in new[] { overviewResources })
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

        // Drill-down rows are native SettingsCards now; the hand-built
        // identity-grid scaffolding must not come back.
        Assert.Equal(
            0,
            CountOccurrences(
                appearanceXaml,
                "Style=\"{StaticResource SettingCardIdentityGridStyle}\""));
        Assert.Equal(
            0,
            CountOccurrences(
                fileWidgetXaml,
                "Style=\"{StaticResource SettingCardIdentityGridStyle}\""));

        string interaction = SliceSection(
            windowXaml,
            "x:Name=\"InteractionSection\"",
            "x:Name=\"FeatureWidgetsSection\"");
        Assert.Equal(
            0,
            CountOccurrences(
                interaction,
                "Style=\"{StaticResource SettingCardIdentityGridStyle}\""));

        string maintenance = SliceSection(
            windowXaml,
            "x:Name=\"MaintenanceSection\"",
            "x:Name=\"AboutSection\"");
        Assert.Equal(
            0,
            CountOccurrences(
                maintenance,
                "Style=\"{StaticResource SettingCardIdentityGridStyle}\""));
        // Feature rows are native SettingsCards now; the identity-grid style
        // lookup must not come back.
        Assert.DoesNotContain(
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

        AssertInOrder(
            fileWidgetXaml,
            "x:Name=\"DesktopOrganizationPart\"",
            "svc:Localized.HeaderKey=\"Settings.ManagedPath.Title\"",
            "svc:Localized.HeaderKey=\"Settings.DragBehavior.Group.Title\"",
            "svc:Localized.HeaderKey=\"Settings.AppearanceDetail.Title\"",
            "Tag=\"FileStackSettings\"",
            "svc:Localized.HeaderKey=\"Settings.OpenMethod.Title\"",
            "FeatureWidgets.FolderOpenBehavior",
            "Interaction.FileItemContextMenuEnabled");
        Assert.Contains("Click=\"NestedSettingsButton_Click\"", fileWidgetXaml, StringComparison.Ordinal);

        string interaction = SliceSection(
            windowXaml,
            "x:Name=\"InteractionSection\"",
            "x:Name=\"FeatureWidgetsSection\"");
        AssertInOrder(
            interaction,
            "svc:Localized.HeaderKey=\"Settings.GlobalHotkey.Title\"",
            "svc:Localized.HeaderKey=\"Settings.DesktopDoubleClick.Title\"",
            "x:Name=\"CapsuleModeEntryCard\"",
            "Tag=\"WidgetGroups\"",
            "Tag=\"Displays\"",
            "svc:Localized.HeaderKey=\"Settings.WidgetLayerMode.Title\"",
            "svc:Localized.HeaderKey=\"Settings.ShowDesktopBehavior.Title\"",
            "svc:Localized.HeaderKey=\"Settings.ResizeSnap.Title\"",
            "svc:Localized.HeaderKey=\"Settings.Interaction.Hover.Title\"");

        string maintenance = SliceSection(
            windowXaml,
            "x:Name=\"MaintenanceSection\"",
            "x:Name=\"AboutSection\"");
        AssertInOrder(
            maintenance,
            "svc:Localized.HeaderKey=\"Settings.DataBackup.Manual.Title\"",
            "svc:Localized.HeaderKey=\"Settings.DataBackup.AutomaticBackup.Title\"",
            "svc:Localized.HeaderKey=\"Settings.CloudBackup.Title\"",
            "svc:Localized.HeaderKey=\"Settings.Restore.Title\"");

        string about = SliceSection(
            windowXaml,
            "x:Name=\"AboutSection\"",
            "x:Name=\"GeneralSection\"");
        Assert.Contains("svc:Localized.HeaderKey=\"Settings.Onboarding.Title\"", about, StringComparison.Ordinal);

        int generalSection = windowXaml.IndexOf(
            "x:Name=\"GeneralSection\"",
            StringComparison.Ordinal);
        int language = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.Language.Title\"",
            generalSection,
            StringComparison.Ordinal);
        int autoStart = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.AutoStart.Title\"",
            generalSection,
            StringComparison.Ordinal);
        int performance = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.Performance.Title\"",
            generalSection,
            StringComparison.Ordinal);
        int aiSettings = windowXaml.IndexOf(
            "Tag=\"AiSettings\"",
            generalSection,
            StringComparison.Ordinal);

        Assert.True(generalSection >= 0);
        Assert.True(generalSection < language);
        Assert.True(language < autoStart);
        Assert.True(autoStart < performance);
        Assert.True(performance < aiSettings);
        Assert.DoesNotContain("InteractionHotkeySettings", windowXaml, StringComparison.Ordinal);
        // Two occurrences are expected: the expander HeaderKey plus the combo box
        // AutomationNameKey, which deliberately reuses the same header key so
        // the accessible name stays same-sourced with the visible label.
        Assert.Equal(
            2,
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

        Assert.Contains("Settings.Weather.Group.Display.Title", weather, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ShowForecast", weather, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ShowPressure", weather, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchSettings_IsPromotedToTopLevelSidebarBelowFileWidget()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string windowCodeBehind = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml.cs"));
        string searchXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml"));
        string searchCodeBehind = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml.cs"));

        int fileWidgetNav = windowXaml.IndexOf("x:Name=\"FileWidgetNavItem\"", StringComparison.Ordinal);
        int featureWidgetsNav = windowXaml.IndexOf("x:Name=\"FeatureWidgetsNavItem\"", StringComparison.Ordinal);
        int searchNav = windowXaml.IndexOf("x:Name=\"SearchNavItem\"", StringComparison.Ordinal);
        int maintenanceNav = windowXaml.IndexOf("x:Name=\"MaintenanceNavItem\"", StringComparison.Ordinal);
        Assert.True(
            fileWidgetNav >= 0 &&
            featureWidgetsNav > fileWidgetNav &&
            searchNav > featureWidgetsNav &&
            maintenanceNav > searchNav);
        Assert.Contains(
            "[\"SearchSettings\"] = new(\"SearchSettings\", \"Settings.Search.Title\", null, \"SearchSettings\")",
            windowCodeBehind,
            StringComparison.Ordinal);

        Assert.DoesNotContain("SearchHotkeyToggle2", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Search.Hotkey.Title", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource SectionTitleTextStyle}\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.Key=\"Settings.Search.Title\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SearchHotkeyCard\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SearchHotkeyToggle\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.Search.Hotkey.Title", searchXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Search.Scope.Title", searchXaml, StringComparison.Ordinal);
        Assert.Contains("_viewModel.State.FeatureEnabled", searchCodeBehind, StringComparison.Ordinal);
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
            "TrayCommandMapFolder,",
            StringComparison.Ordinal);
        int trayFeatureEntry = traySource.IndexOf(
            "TrayCommandAddFeatureWidget,",
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
            "Settings.CompactContent.Smart",
            "Settings.Capsule.Direction.Auto",
            "Settings.Capsule.Animation.Smooth",
            "Settings.Capsule.HoverResponse.Balanced",
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

        Assert.Contains("IsOn=\"{Binding Enabled, Mode=TwoWay}\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{Binding Enabled, Mode=TwoWay}\"", todo, StringComparison.Ordinal);
        Assert.Equal(0, CountOccurrences(quickCapture, "Loaded=\"FeatureSettingsExpander_Loaded\""));
        Assert.Equal(0, CountOccurrences(todo, "Loaded=\"FeatureSettingsExpander_Loaded\""));

        AssertInOrder(
            quickCapture,
            "Settings.QuickCapture.Group.Layout",
            "Settings.QuickCapture.DefaultView.Title",
            "Settings.QuickCapture.Tabs.Title",
            "Settings.QuickCapture.WideLayout.Title",
            "Settings.QuickCapture.WideOpen.Title",
            "Settings.QuickCapture.Group.Display",
            "Settings.ContentEditor.Group.Title",
            "Settings.QuickCapture.Group.Data.Title",
            "Settings.AttachmentStorageMode.Title",
            "Settings.QuickCapture.MirrorFolder.Title");
        AssertInOrder(
            todo,
            "Settings.QuickCapture.Group.Layout",
            "Settings.Todo.DefaultFilter.Title",
            "Settings.Todo.Tabs.Title",
            "Settings.QuickCapture.WideLayout.Title",
            "Settings.Todo.AutoSelectFirst.Title",
            "Settings.Todo.Group.Display",
            "Settings.Todo.FooterDisplay.Title",
            "Settings.ContentEditor.Group.Title",
            "Settings.Todo.NewTaskPosition.Title",
            "Settings.Todo.Group.Reminders",
            "Settings.Todo.ReminderEnabled.Title",
            "Settings.Todo.Group.Data",
            "Settings.AttachmentStorageMode.Title");

        Assert.DoesNotContain("Click=\"QuickCaptureTabsDropDown_Click\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{x:Bind ShowRecordsTab, Mode=TwoWay}\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{x:Bind ShowPinnedTab, Mode=TwoWay}\"", quickCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"TodoTabsDropDown_Click\"", todo, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"TodoFooterDisplayDropDown_Click\"", todo, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{x:Bind ShowAllTab, Mode=TwoWay}\"", todo, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{x:Bind ShowFooterStats, Mode=TwoWay}\"", todo, StringComparison.Ordinal);
        Assert.Contains("SelectedIndex=\"{x:Bind NewTaskPositionIndex, Mode=TwoWay}\"", todo, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsSegmented.Options=\"{Binding AvailableDefaultViewOptionItems}\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsSegmented.Value=\"{Binding SelectedDefaultViewKey, Mode=TwoWay}\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding AllDefaultFilterOptions}\"", todo, StringComparison.Ordinal);

        Assert.DoesNotContain("IsOn=\"{Binding ShowRecordsTab", quickCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding TodoShowAllTab", todo, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding TodoShowTabBar", todo, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding SelectedTodo", todo, StringComparison.Ordinal);
        Assert.Contains(
            "SelectedIndex=\"{x:Bind WideOpenModeIndex, Mode=TwoWay}\"",
            quickCapture,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedIndex=\"{x:Bind EditorFormatIndex, Mode=TwoWay}\"",
            quickCapture,
            StringComparison.Ordinal);
        Assert.DoesNotContain("QuickCaptureDefaultFormat", quickCapture, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapturePreviewLineCount_IsBoundInTheSharedSurfaceHost()
    {
        string root = FindRepositoryRoot();
        string sharedSurface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));

        Assert.Contains(
            "MaxLines=\"{Binding ElementName=ItemsList, Path=DataContext.ItemPreviewLineCount}\"",
            sharedSurface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TextSize}\" MaxLines=\"3\"",
            sharedSurface,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedStorageDesktopShortcutToggleMirrorsFileSystemState()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string fileWidgetXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));
        string fileWidgetCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml.cs"));
        string storageCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.StorageAndUpdates.cs"));

        Assert.Contains(
            "x:Name=\"ManagedStorageDesktopShortcutToggle\"",
            fileWidgetXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Toggled=\"ManagedStorageDesktopShortcutToggle_Toggled\"",
            fileWidgetXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsOn=\"{Binding ManagedStorageDesktopShortcutEnabled",
            fileWidgetXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsOn=\"{Binding ManagedStorageDesktopShortcutEnabled",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "DesktopShortcutToggled=\"AppearanceDetailSection_DesktopShortcutToggled\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains("_isSyncingDesktopShortcutToggle", fileWidgetCode, StringComparison.Ordinal);
        Assert.Contains("section.SetDesktopShortcutState(", storageCode, StringComparison.Ordinal);
        // The toggle mirrors real file-system state through the settings app
        // facade (the guarantee is unchanged; the calls moved behind it).
        Assert.Contains("_appActions.HasManagedStorageDesktopShortcut()", storageCode, StringComparison.Ordinal);
        Assert.Contains("_appActions.CreateManagedStorageDesktopShortcutAsync()", storageCode, StringComparison.Ordinal);
        Assert.Contains("_appActions.RemoveManagedStorageDesktopShortcutAsync()", storageCode, StringComparison.Ordinal);
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
