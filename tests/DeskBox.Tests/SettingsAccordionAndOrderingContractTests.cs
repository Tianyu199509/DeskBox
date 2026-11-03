using System.Text.RegularExpressions;

namespace DeskBox.Tests;

/// <summary>
/// Contract tests for the "accordion grouping + reordering" pass over the
/// settings window: long flat card runs are folded into
/// toolkit:SettingsExpander accordions (parent header card + child cards in
/// Items), sections are reordered to the pinned page order, the three
/// capsule sub-pages collapse into CapsuleModeSettingsSection, the default
/// window width/height cards merge into one card, and the auto-start mode
/// copy stops recommending the standard mode. These tests assert the target
/// shape only; they are expected to fail until the refactor lands.
/// </summary>
public sealed class SettingsAccordionAndOrderingContractTests
{
    private const string SettingsWindowXaml = "src/DeskBox/Views/SettingsWindow.xaml";
    private const string SettingsWindowCodeBehind = "src/DeskBox/Views/SettingsWindow.xaml.cs";
    private const string SearchCatalog = "src/DeskBox/Services/SettingsSearchCatalog.cs";

    // ---------------------------------------------------------------- A. 常规页

    // Spec A1: 「开机自动启动」is a SettingsExpander whose Items contain the
    // 启动方式 sub-card.
    [Fact]
    public void General_AutoStart_IsAnExpanderWithModeChildCard()
    {
        string general = GeneralSectionSlice();

        Match? expander = FindOpeningTag(
            general, "toolkit:SettingsExpander", "HeaderKey=\"Settings.AutoStart.Title\"");
        Assert.NotNull(expander);

        // The mode sub-card must live inside the expander block (before the
        // expander's closing tag), not merely after it.
        int openEnd = expander.Index + expander.Length;
        int close = general.IndexOf("</toolkit:SettingsExpander>", openEnd, StringComparison.Ordinal);
        Assert.True(close >= 0, "GeneralSection: the AutoStart expander is missing its closing tag.");
        int mode = IndexOfOrFail(general, "HeaderKey=\"Settings.AutoStart.Mode.Title\"", "GeneralSection");
        Assert.True(openEnd <= mode && mode < close,
            "GeneralSection: Settings.AutoStart.Mode.Title must be an item of the " +
            "Settings.AutoStart.Title expander (between its opening and closing tags).");
    }

    // Spec A2: pinned GeneralSection order
    // Language < AutoStart < SilentStartup < Performance < Displays < AttachmentStorageMode < Onboarding.
    [Fact]
    public void General_Cards_FollowThePinnedOrder()
    {
        string general = GeneralSectionSlice();

        AssertAscending(general, "GeneralSection ordering",
            "HeaderKey=\"Settings.Language.Title\"",
            "HeaderKey=\"Settings.AutoStart.Title\"",
            "HeaderKey=\"Settings.SilentStartup.Title\"",
            "HeaderKey=\"Settings.Performance.Title\"",
            "HeaderKey=\"Settings.Displays.Title\"",
            "HeaderKey=\"Settings.AttachmentStorageMode.Title\"",
            "HeaderKey=\"Settings.Onboarding.Title\"");
    }

    // -------------------------------------------------- B. 文件叠放（文件堆叠）

    // Spec B1 + B2: OpenMode + PopoverLayout/PopoverStyle live inline in
    // FileWidgetSettingsSection.xaml, while FileStackSettingsSectionTemplate
    // owns the expanded AutoStacking expander (Threshold < GroupBy <
    // Unmatched < OrderBy) and custom rules with zero control duplication.
    [Fact]
    public void FileStack_TwoExpanders_GroupTheirChildCards()
    {
        string overview = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");
        string slice = FileStackSlice();

        Match? openModeExpander = FindOpeningTag(
            overview, "toolkit:SettingsExpander", "HeaderKey=\"Settings.FileStacks.Title\"");
        Assert.NotNull(openModeExpander);
        int openModeClose = overview.IndexOf(
            "</toolkit:SettingsExpander>", openModeExpander.Index + openModeExpander.Length, StringComparison.Ordinal);
        Assert.True(openModeClose >= 0);

        foreach (string header in new[]
        {
            "HeaderKey=\"Settings.FileStacks.PopoverLayout.Title\"",
            "HeaderKey=\"Settings.FileStacks.PopoverStyle.Title\"",
            "HeaderKey=\"Settings.FileStacks.MoreSettings\""
        })
        {
            int index = IndexOfOrFail(overview, header, "FileWidget overview");
            Assert.True(openModeExpander.Index + openModeExpander.Length <= index && index < openModeClose,
                $"FileWidget overview: {header} must be an item of the inline file-stacks expander.");
        }
        Assert.DoesNotContain("HeaderKey=\"Settings.FileStacks.Auto.Title\"", overview, StringComparison.Ordinal);

        Match? autoExpander = FindOpeningTag(
            slice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.FileStacks.Auto.Title\"");
        Assert.NotNull(autoExpander);
        Assert.Contains("IsExpanded=\"True\"", autoExpander.Value, StringComparison.Ordinal);
        int autoClose = slice.IndexOf(
            "</toolkit:SettingsExpander>", autoExpander.Index + autoExpander.Length, StringComparison.Ordinal);
        Assert.True(autoClose >= 0);

        foreach (string header in new[]
        {
             "HeaderKey=\"Settings.FileStacks.Threshold.Title\"",
             "HeaderKey=\"Settings.FileStacks.GroupBy.Title\"",
             "HeaderKey=\"Settings.FileStacks.Unmatched.Title\"",
             "HeaderKey=\"Settings.FileStacks.OrderBy.Title\""
        })
        {
            int index = IndexOfOrFail(slice, header, "FileStack section");
            Assert.True(autoExpander.Index + autoExpander.Length <= index && index < autoClose,
                $"FileStack section: {header} must be an item of the auto-stacking expander.");
        }

        Assert.DoesNotContain("HeaderKey=\"Settings.FileStacks.OpenMode.Title\"", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.FileStacks.Enable.Title\"", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.FileStacks.Mode.Title\"", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding StacksEnabled", slice, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{x:Bind FileStack.IsPopoverOpenMode, Mode=OneWay}\"", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsCard.HeaderIcon", slice, StringComparison.Ordinal);
    }

    // Spec B3: the custom-rules editor stays inside the template.
    [Fact]
    public void FileStack_CustomRulesEditor_StaysInTheTemplate()
    {
        string slice = FileStackSlice();

        Assert.Contains("FileStacks.Custom.Rules", slice, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FileStackRulesListView\"", slice, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- C. 胶囊子页删除

    // Spec C1: the three capsule sub-page templates are gone from the window XAML.
    [Fact]
    public void Capsule_SubPageTemplates_AreRemovedFromWindowXaml()
    {
        string xaml = ReadRepositoryFile(SettingsWindowXaml);

        Assert.DoesNotContain("CapsuleBehaviorSettingsSectionTemplate", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CapsuleArrangementSettingsSectionTemplate", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CapsuleAnimationSettingsSectionTemplate", xaml, StringComparison.Ordinal);
    }

    // Spec C2 + C3: SectionRoutes and the search catalog drop the three
    // sub-page keys/tags.
    [Fact]
    public void Capsule_SubPageRoutesAndSearchTags_AreRemoved()
    {
        string codeBehind = ReadRepositoryFile(SettingsWindowCodeBehind);
        string catalog = ReadRepositoryFile(SearchCatalog);

        Assert.DoesNotContain("\"CapsuleBehaviorSettings\"", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CapsuleArrangementSettings\"", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CapsuleAnimationSettings\"", codeBehind, StringComparison.Ordinal);

        Assert.DoesNotContain("\"CapsuleBehaviorSettings\"", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CapsuleArrangementSettings\"", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CapsuleAnimationSettings\"", catalog, StringComparison.Ordinal);
    }

    // Spec C4: the capsule overrides page survives the collapse.
    [Fact]
    public void Capsule_OverridesRoute_Survives()
    {
        string codeBehind = ReadRepositoryFile(SettingsWindowCodeBehind);
        string xaml = ReadRepositoryFile(SettingsWindowXaml);

        Assert.Contains("[\"CapsuleOverridesSettings\"]", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CapsuleOverridesSettingsSectionTemplate", xaml, StringComparison.Ordinal);
    }

    // Spec C5: the merged capsule main page has exactly three expanders, with
    // the hover-response expander hosting the expand/collapse delay sub-cards
    // and the arrangement/animation detail cards folded in as well.
    [Fact]
    public void CapsuleModeSection_HasThreeExpandersWithMergedChildren()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml");

        int expanderCount = CountOpeningTags(xaml, "toolkit:SettingsExpander");
        Assert.True(expanderCount == 3,
            $"CapsuleModeSettingsSection.xaml: expected exactly 3 SettingsExpander elements " +
            $"(hover response, arrangement, animation), found {expanderCount}.");

        Match? hover = FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Capsule.HoverResponse.Title\"");
        Assert.NotNull(hover);

        int expandDelay = IndexOfOrFail(
            xaml, "HeaderKey=\"Settings.Capsule.ExpandDelay.Title\"", "CapsuleMode section");
        Assert.True(hover.Index < expandDelay,
            "CapsuleMode section: the expand-delay sub-card must sit after the " +
            "Settings.Capsule.HoverResponse.Title expander header.");

        // The three deleted sub-pages' cards must have a home in this file.
        Assert.Contains("HeaderKey=\"Settings.Capsule.CollapseDelay.Title\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.Capsule.Spacing.Title\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.Capsule.Direction.Title\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.Capsule.AnimationDuration.Title\"", xaml, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ D. 外观动画与折叠子页退役

    // Spec D: the three folded sub-page templates/routes (Density, Window,
    // Animation) are retired, and AppearanceSettingsSection.xaml owns the
    // animation expander with Preview + Direction/Speed/Easing/Stagger in order.
    [Fact]
    public void AppearanceAnimation_DetailCards_AreFlattenedWithoutExpander()
    {
        string windowXaml = ReadRepositoryFile(SettingsWindowXaml);
        string codeBehind = ReadRepositoryFile(SettingsWindowCodeBehind);
        string catalog = ReadRepositoryFile(SearchCatalog);
        string appearanceXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        Assert.DoesNotContain("AppearanceDensitySettingsSectionTemplate", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AppearanceWindowSettingsSectionTemplate", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AppearanceAnimationSettingsSectionTemplate", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AppearanceDensitySettings\"", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AppearanceWindowSettings\"", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AppearanceAnimationSettings\"", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AppearanceDensitySettings\"", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AppearanceWindowSettings\"", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AppearanceAnimationSettings\"", catalog, StringComparison.Ordinal);

        Assert.DoesNotContain("PreviewWidgetAnimationButton_Click", appearanceXaml, StringComparison.Ordinal);
        AssertAscending(appearanceXaml, "Appearance animation expander card order",
            "HeaderKey=\"Settings.Group.Animation.Title\"",
            "HeaderKey=\"Settings.Animation.Direction.Title\"",
            "HeaderKey=\"Settings.Animation.Speed.Title\"",
            "HeaderKey=\"Settings.Animation.Easing.Title\"",
            "HeaderKey=\"Settings.Animation.Stagger.Title\"");
    }

    // -------------------------------------------------------------- E. 材质

    // Spec E1 + E3: Material/Opacity/MaterialIntensity live inline on
    // AppearanceSettingsSection.xaml, while AppearanceMaterialSettingsSectionTemplate
    // keeps the 2 advanced expanders (WidgetForeground + WidgetBackground)
    // and flat border/corner/system-effect cards with zero duplication.
    [Fact]
    public void AppearanceMaterial_HasExactlyTheThreeParentExpanders()
    {
        string slice = MaterialSlice();
        string appearanceXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        int expanderCount = CountOpeningTags(slice, "toolkit:SettingsExpander");
        Assert.True(expanderCount == 2,
            $"AppearanceMaterial section: expected exactly 2 SettingsExpander elements " +
            $"(text parent + widget background), found {expanderCount}.");

        Assert.Null(FindOpeningTag(
            slice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Material.Title\""));
        Assert.NotNull(FindOpeningTag(
            appearanceXaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Material.Title\""));
        Assert.NotNull(FindOpeningTag(
            slice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.WidgetForeground.Title\""));
    }

    // Spec E2 + E3: Opacity and MaterialIntensity are children of the inline
    // material expander on AppearanceSettingsSection.xaml, CustomColor is a
    // child of the text expander, while border color, border style and corner
    // stay flat cards in AppearanceMaterialSettingsSectionTemplate.
    [Fact]
    public void AppearanceMaterial_ChildCards_FollowTheirParentsAndBordersStayFlat()
    {
        string appearanceXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");
        string slice = MaterialSlice();

        Match? material = FindOpeningTag(
            appearanceXaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Material.Title\"");
        Match? text = FindOpeningTag(
            slice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.WidgetForeground.Title\"");
        Assert.NotNull(material);
        Assert.NotNull(text);

        AssertInsideExpander(appearanceXaml, material, "HeaderKey=\"Settings.Opacity.Title\"", "material");
        AssertInsideExpander(appearanceXaml, material, "HeaderKey=\"Settings.MaterialIntensity.Title\"", "material");
        AssertInsideExpander(slice, text, "HeaderKey=\"Settings.WidgetForeground.CustomColor.Title\"", "text");

        Assert.DoesNotContain("HeaderKey=\"Settings.Opacity.Title\"", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.MaterialIntensity.Title\"", slice, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.BorderColor.Title\"", slice, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.Border.Title\"", slice, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.Corner.Title\"", slice, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ F. 文件显示

    // Spec F1: the file-extension card is an expander and the .lnk sub-card
    // follows it.
    [Fact]
    public void FileDisplay_ExtensionsExpander_HostsTheLnkChildCard()
    {
        string slice = Slice(
            ReadRepositoryFile(SettingsWindowXaml),
            "x:Key=\"FileDisplaySettingsSectionTemplate\"",
            "x:Key=\"FileStorageSettingsSectionTemplate\"");

        Match? expander = FindOpeningTag(
            slice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.ShowFileExtensions.Title\"");
        Assert.NotNull(expander);

        int lnk = IndexOfOrFail(
            slice, "HeaderKey=\"Settings.HideShortcutExtension.Title\"", "FileDisplay section");
        Assert.True(expander.Index < lnk,
            "FileDisplay section: the .lnk sub-card must come after the " +
            "ShowFileExtensions expander declaration.");
    }

    // Spec F2: FileNameWidth/FileNameLines move from the density template into
    // the file-display template.
    [Fact]
    public void FileDisplay_ReceivesTheFileNameCardsFromDensity()
    {
        string display = Slice(
            ReadRepositoryFile(SettingsWindowXaml),
            "x:Key=\"FileDisplaySettingsSectionTemplate\"",
            "x:Key=\"FileStorageSettingsSectionTemplate\"");
        string density = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        Assert.Contains("HeaderKey=\"Settings.FileNameWidth.Title\"", display, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.FileNameLines.Title\"", display, StringComparison.Ordinal);

        Assert.DoesNotContain("HeaderKey=\"Settings.FileNameWidth.Title\"", density, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.FileNameLines.Title\"", density, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- G. 本地备份

    // Spec G: AutomaticBackup is an expander; Interval/Retention/Directory are
    // its child cards; the snapshots card is keyed by
    // Settings.DataBackup.Snapshots.Title.
    [Fact]
    public void BackupRestore_AutomaticBackupExpander_GroupsTheChildCards()
    {
        string slice = Slice(
            ReadRepositoryFile(SettingsWindowXaml),
            "x:Key=\"BackupRestoreSettingsSectionTemplate\"",
            "x:Key=\"CloudBackupSettingsSectionTemplate\"");

        Match? expander = FindOpeningTag(
            slice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.DataBackup.AutomaticBackup.Title\"");
        Assert.NotNull(expander);

        foreach (string key in new[]
                 {
                     "HeaderKey=\"Settings.DataBackup.AutomaticBackupInterval.Title\"",
                     "HeaderKey=\"Settings.DataBackup.AutomaticBackupRetention.Title\"",
                     "HeaderKey=\"Settings.DataBackup.AutomaticBackupDirectory.Title\"",
                 })
        {
            int at = IndexOfOrFail(slice, key, "BackupRestore section");
            Assert.True(expander.Index < at,
                $"BackupRestore section: {key} must come after the AutomaticBackup expander.");
        }

        Assert.Contains(
            "HeaderKey=\"Settings.DataBackup.Snapshots.Title\"", slice, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- H. 天气

    // Spec H1 + H2: the display group is a SettingsExpander with
    // IsExpanded="True", and the seven display toggles follow it.
    [Fact]
    public void Weather_DisplayGroup_IsAnExpandedAccordionWithSevenToggles()
    {
        string slice = WeatherSlice();

        Match? expander = FindOpeningTag(
            slice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Weather.Group.Display.Title\"");
        Assert.NotNull(expander);
        Assert.Contains("IsExpanded=\"True\"", expander.Value, StringComparison.Ordinal);

        foreach (string key in new[]
                 {
                     "HeaderKey=\"Settings.Weather.ShowForecast.Title\"",
                     "HeaderKey=\"Settings.Weather.ShowHumidity.Title\"",
                     "HeaderKey=\"Settings.Weather.ShowPrecipitation.Title\"",
                     "HeaderKey=\"Settings.Weather.ShowPressure.Title\"",
                     "HeaderKey=\"Settings.Weather.ShowSunrise.Title\"",
                     "HeaderKey=\"Settings.Weather.ShowUvIndex.Title\"",
                     "HeaderKey=\"Settings.Weather.ShowWind.Title\"",
                 })
        {
            int at = IndexOfOrFail(slice, key, "Weather section");
            Assert.True(expander.Index < at,
                $"Weather section: {key} must come after the display-group expander.");
        }
    }

    // Spec H3: Group.Display before Weather.DefaultView, DataSource after
    // TemperatureUnit, RefreshInterval last.
    [Fact]
    public void Weather_Cards_FollowThePinnedOrder()
    {
        string slice = WeatherSlice();

        AssertAscending(slice, "Weather display group before default view",
            "HeaderKey=\"Settings.Weather.Group.Display.Title\"",
            "HeaderKey=\"Settings.Weather.DefaultView.Title\"");

        AssertAscending(slice, "Weather units before data source",
            "HeaderKey=\"Settings.Weather.TemperatureUnit.Title\"",
            "HeaderKey=\"Settings.Weather.DataSource.Title\"");

        int refresh = IndexOfOrFail(
            slice, "HeaderKey=\"Settings.Weather.RefreshInterval.Title\"", "Weather section");
        foreach (string key in new[]
                 {
                     "HeaderKey=\"Settings.Weather.LocationMode.Title\"",
                     "HeaderKey=\"Settings.Weather.CityName.Title\"",
                     "HeaderKey=\"Settings.Weather.TemperatureUnit.Title\"",
                     "HeaderKey=\"Settings.Weather.DataSource.Title\"",
                     "HeaderKey=\"Settings.Weather.WindSpeedUnit.Title\"",
                     "HeaderKey=\"Settings.Weather.DefaultView.Title\"",
                     "HeaderKey=\"Settings.Weather.Skin.Title\"",
                     "HeaderKey=\"Settings.Weather.IconStyle.Title\"",
                     "HeaderKey=\"Settings.Weather.Group.Display.Title\"",
                 })
        {
            int at = IndexOfOrFail(slice, key, "Weather section");
            Assert.True(at < refresh,
                $"Weather section: RefreshInterval must be the last card, but it comes before {key}.");
        }
    }

    // ------------------------------------------------------------- I. 交互页排序

    // Spec I: WidgetLayerMode < GlobalHotkey < ShowDesktop < OpenMethod <
    // HoverButtons < ResizeSnap < DesktopDoubleClick, with the elevated-notice
    // InfoBar between the hotkey expander and the show-desktop card.
    [Fact]
    public void Interaction_Section_FollowsThePinnedOrder()
    {
        string slice = InteractionSlice();

        AssertAscending(slice, "Interaction ordering",
            "HeaderKey=\"Settings.WidgetLayerMode.Title\"",
            "HeaderKey=\"Settings.GlobalHotkey.Title\"",
            "HeaderKey=\"Settings.ShowDesktopBehavior.Title\"",
            "HeaderKey=\"Settings.OpenMethod.Title\"",
            "HeaderKey=\"Settings.Interaction.Hover.Title\"",
            "HeaderKey=\"Settings.ResizeSnap.Title\"",
            "HeaderKey=\"Settings.DesktopDoubleClick.Title\"");

        AssertAscending(slice, "Interaction elevated notice placement",
            "HeaderKey=\"Settings.GlobalHotkey.Title\"",
            "HeaderKey=\"Settings.GlobalHotkey.ElevatedNotice.Title\"",
            "HeaderKey=\"Settings.ShowDesktopBehavior.Title\"");
    }

    // -------------------------------------------------------------- J. 窗口子页

    // Spec J: DefaultWidth and DefaultHeight share one card (both NumberBoxes
    // within 800 chars of each other), and the title-icon card leads the page.
    [Fact]
    public void AppearanceWindow_DefaultSizeCards_AreMergedAndTitleIconLeads()
    {
        string slice = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        int width = IndexOfOrFail(slice, "x:Name=\"DefaultWidthBox\"", "Appearance section");
        int height = IndexOfOrFail(slice, "x:Name=\"DefaultHeightBox\"", "Appearance section");
        Assert.True(Math.Abs(height - width) < 1200,
            $"Appearance section: DefaultWidthBox and DefaultHeightBox must share one " +
            $"SettingsCard (distance {Math.Abs(height - width)} chars >= 1200).");

        int titleIcon = IndexOfOrFail(
            slice, "HeaderKey=\"Settings.Group.AppVisual.Title\"", "Appearance section");
        Assert.True(titleIcon < width && titleIcon < height,
            "Appearance section: the app-visual expander header must come before the merged default-size card.");
    }

    // ---------------------------------------------------------------- K. 词典

    // Spec K (zh-CN): ScheduledTask mentions 较快, Standard stops recommending,
    // density description mentions 自定义, presets title mentions 预设.
    [Fact]
    public void Dictionary_ZhCN_AutoStartDensityAndPresetWording()
    {
        string json = ReadRepositoryFile("src/DeskBox/Strings/zh-CN.json");

        Assert.Matches(new Regex(
            "\"Settings\\.AutoStart\\.Mode\\.ScheduledTask\"\\s*:\\s*\"[^\"]*较快",
            RegexOptions.CultureInvariant), json);
        Assert.DoesNotMatch(new Regex(
            "\"Settings\\.AutoStart\\.Mode\\.Standard\"\\s*:\\s*\"[^\"]*推荐",
            RegexOptions.CultureInvariant), json);
        Assert.Matches(new Regex(
            "\"Settings\\.Density\\.Description\"\\s*:\\s*\"[^\"]*自定义",
            RegexOptions.CultureInvariant), json);
        Assert.Matches(new Regex(
            "\"Settings\\.GlobalHotkey\\.PresetsTitle\"\\s*:\\s*\"[^\"]*预设",
            RegexOptions.CultureInvariant), json);
    }

    // Spec K (en-US): ScheduledTask says "faster", Standard drops "recommended".
    [Fact]
    public void Dictionary_EnUS_AutoStartWording()
    {
        string json = ReadRepositoryFile("src/DeskBox/Strings/en-US.json");

        Assert.Matches(new Regex(
            "\"Settings\\.AutoStart\\.Mode\\.ScheduledTask\"\\s*:\\s*\"[^\"]*faster",
            RegexOptions.CultureInvariant), json);
        Assert.DoesNotMatch(new Regex(
            "\"Settings\\.AutoStart\\.Mode\\.Standard\"\\s*:\\s*\"[^\"]*recommended",
            RegexOptions.CultureInvariant), json);
    }

    // ------------------------------------------------------------ L. 其他 Section

    // Spec L1: Theme < TrayIcon < Accent < Material (the tray-icon card sits
    // between the theme and the accent colors), and the widget-groups card
    // after animation.
    [Fact]
    public void AppearanceSection_FollowsThePinnedOrder()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        AssertAscending(xaml, "AppearanceSection ordering (colors)",
            "HeaderKey=\"Settings.Theme.Title\"",
            "HeaderKey=\"Settings.TrayIcon.Title\"",
            "HeaderKey=\"Settings.Accent.Title\"",
            "HeaderKey=\"Settings.Material.Title\"");

        AssertAscending(xaml, "AppearanceSection ordering (groups)",
            "HeaderKey=\"Settings.Group.Animation.Title\"",
            "HeaderKey=\"Settings.Section.WidgetGroups\"");
    }

    // Spec L2: the managed-storage section is no longer force-inserted at index 1.
    [Fact]
    public void FileWidgetSection_ManagedStorage_InsertIsNotPinnedToIndexOne()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml.cs");

        Assert.DoesNotContain("Math.Min(1,", source, StringComparison.Ordinal);
        Assert.Contains("AttachManagedStorageSection", source, StringComparison.Ordinal);
        // The desktop-shortcut card must be detached from the storage panel
        // before joining this panel: UIElementCollection.Add does not reparent
        // and throws 0x800F1000 otherwise, aborting the first navigation.
        Assert.Contains(
            "storagePanel.Children.Remove(shortcutCard);",
            source,
            StringComparison.Ordinal);
    }

    // Spec L3: the glance section exposes a 切换-related SettingsExpander
    // (Glance.Transition.Title = 切换动效).
    [Fact]
    public void GlanceSection_HasATransitionExpander()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml");

        Assert.NotNull(FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"Glance.Transition.Title\""));
    }

    // Spec L4: the desktop-organization auto card is an expander hosting the
    // 整理时机 (interval) sub-card.
    [Fact]
    public void DesktopOrganization_AutoCard_IsAnExpanderWithIntervalChild()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml");

        Match? expander = FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"DesktopOrganization.Auto.Title\"");
        Assert.NotNull(expander);

        int interval = IndexOfOrFail(
            xaml, "HeaderKey=\"DesktopOrganization.Auto.Interval.Title\"", "DesktopOrganization section");
        Assert.True(expander.Index < interval,
            "DesktopOrganization section: the 整理时机 sub-card must come after the " +
            "auto-organize expander declaration.");
    }

    // Spec Z1: expanders must never carry HorizontalContentAlignment="Right".
    // The toolkit forwards that property into the inner muxc:Expander content
    // area, right-aligning the whole expanded item block (the "empty left,
    // content crammed right" layout bug); sub-cards keep Right themselves.
    [Fact]
    public void Expanders_NeverCarryRightContentAlignment()
    {
        string[] xamlFiles =
        {
            SettingsWindowXaml,
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml",
        };

        foreach (string file in xamlFiles)
        {
            string xaml = ReadRepositoryFile(file);
            Match? offender = FindOpeningTag(
                xaml, "toolkit:SettingsExpander", "HorizontalContentAlignment=\"Right\"");
            Assert.Null(offender);
        }
    }

    // ------------------------------------------------------------- helpers

    private static string GeneralSectionSlice()
    {
        // GeneralSection lives at the end of the content host, right before the
        // about dialog, so the dialog's opening tag bounds the slice.
        return Slice(ReadRepositoryFile(SettingsWindowXaml), "x:Name=\"GeneralSection\"", "<ContentDialog");
    }

    private static string MaterialSlice()
    {
        return Slice(
            ReadRepositoryFile(SettingsWindowXaml),
            "x:Key=\"AppearanceMaterialSettingsSectionTemplate\"",
            "x:Key=\"WidgetGroupsSectionTemplate\"");
    }

    private static string FileStackSlice()
    {
        return Slice(
            ReadRepositoryFile(SettingsWindowXaml),
            "x:Key=\"FileStackSettingsSectionTemplate\"",
            "x:Key=\"InteractionSectionTemplate\"");
    }

    private static string WeatherSlice()
    {
        return Slice(
            ReadRepositoryFile(SettingsWindowXaml),
            "x:Key=\"WeatherSettingsSectionTemplate\"",
            "x:Key=\"GlanceSettingsSectionTemplate\"");
    }

    private static string InteractionSlice()
    {
        // Bounded by the managed-storage template that follows the interaction
        // family, so the slice keeps covering the interaction cards whether or
        // not the old InteractionWindowSettingsSectionTemplate survives.
        return Slice(
            ReadRepositoryFile(SettingsWindowXaml),
            "x:Key=\"InteractionSectionTemplate\"",
            "x:Key=\"ManagedStorageSectionTemplate\"");
    }

    private static string Slice(string content, string startMarker, string endMarker)
    {
        int start = IndexOfOrFail(content, startMarker, "SettingsWindow.xaml");
        int end = content.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            end = content.Length;
        }

        return content.Substring(start, end - start);
    }

    private static int IndexOfOrFail(string content, string needle, string context)
    {
        int index = content.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{context}: expected to find '{needle}'.");
        return index;
    }

    private static void AssertAscending(string content, string context, params string[] needles)
    {
        int previousIndex = -1;
        string previous = "(slice start)";
        foreach (string needle in needles)
        {
            int index = IndexOfOrFail(content, needle, context);
            Assert.True(index > previousIndex,
                $"{context}: '{needle}' (at {index}) must come after '{previous}' (at {previousIndex}).");
            previousIndex = index;
            previous = needle;
        }
    }

    /// <summary>
    /// Asserts that <paramref name="needle"/> sits inside the expander block
    /// opened by <paramref name="expander"/> (between its opening tag and the
    /// first matching closing tag).
    /// </summary>
    private static void AssertInsideExpander(
        string content, Match expander, string needle, string expanderName)
    {
        int openEnd = expander.Index + expander.Length;
        int close = content.IndexOf("</toolkit:SettingsExpander>", openEnd, StringComparison.Ordinal);
        Assert.True(close >= 0, $"{expanderName} expander: missing closing tag.");
        int at = IndexOfOrFail(content, needle, expanderName + " expander");
        Assert.True(openEnd <= at && at < close,
            $"{needle} must be an item of the {expanderName} expander " +
            "(between its opening and closing tags).");
    }

    /// <summary>
    /// Finds the first opening tag of <paramref name="elementName"/> whose
    /// attribute list contains <paramref name="requiredContent"/> (for example
    /// a specific svc:Localized.HeaderKey), or null when no such tag exists.
    /// </summary>
    private static Match? FindOpeningTag(string content, string elementName, string requiredContent)
    {
        var tagRegex = new Regex(
            "<" + Regex.Escape(elementName) + "(?:(?!>).)*?>",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        foreach (Match tag in tagRegex.Matches(content))
        {
            if (tag.Value.Contains(requiredContent, StringComparison.Ordinal))
            {
                return tag;
            }
        }

        return null;
    }

    /// <summary>
    /// Counts opening tags (including self-closing ones) of the element,
    /// excluding property-element usages such as SettingsExpander.Items.
    /// </summary>
    private static int CountOpeningTags(string content, string elementName)
    {
        var tagRegex = new Regex(
            "<" + Regex.Escape(elementName) + "(?=[\\s/>])",
            RegexOptions.CultureInvariant);
        return tagRegex.Matches(content).Count;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
