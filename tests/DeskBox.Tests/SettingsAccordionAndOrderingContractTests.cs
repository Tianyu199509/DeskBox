using System.Text.RegularExpressions;

namespace DeskBox.Tests;

/// <summary>
/// Contract tests for the 1.6.0 accordion grouping and section ordering across
/// the settings window and its consolidated section views.
/// </summary>
public sealed class SettingsAccordionAndOrderingContractTests
{
    private const string SettingsWindowXaml = "src/DeskBox/Views/SettingsWindow.xaml";
    private const string SettingsWindowCodeBehind = "src/DeskBox/Views/SettingsWindow.xaml.cs";
    private const string SettingsWindowNavigation = "src/DeskBox/Views/SettingsWindow.Navigation.cs";
    private const string SearchCatalog = "src/DeskBox/Services/SettingsSearchCatalog.cs";

    // ---------------------------------------------------------------- A. 常规页

    // Spec A1: 「开机自动启动」is a flat SettingsCard followed by a conditional
    // 启动方式 sibling card (single-child pseudo-accordion flattened).
    [Fact]
    public void General_AutoStart_IsAFlatCardWithConditionalModeSibling()
    {
        string general = GeneralSectionSlice();

        Match? card = FindOpeningTag(
            general, "toolkit:SettingsCard", "HeaderKey=\"Settings.AutoStart.Title\"");
        Assert.NotNull(card);
        Assert.Null(FindOpeningTag(
            general, "toolkit:SettingsExpander", "HeaderKey=\"Settings.AutoStart.Title\""));

        int mode = IndexOfOrFail(general, "HeaderKey=\"Settings.AutoStart.Mode.Title\"", "GeneralSection");
        Assert.True(card.Index < mode,
            "GeneralSection: Settings.AutoStart.Mode.Title must follow Settings.AutoStart.Title.");
    }

    // Spec A2: pinned GeneralSection order
    // Language < AutoStart < AutoStart.Mode < SilentStartup < Performance < Nav.AiSettings.
    [Fact]
    public void General_Cards_FollowThePinnedOrder()
    {
        string general = GeneralSectionSlice();

        AssertAscending(general, "GeneralSection ordering",
            "HeaderKey=\"Settings.Language.Title\"",
            "HeaderKey=\"Settings.AutoStart.Title\"",
            "HeaderKey=\"Settings.AutoStart.Mode.Title\"",
            "HeaderKey=\"Settings.SilentStartup.Title\"",
            "HeaderKey=\"Settings.Performance.Title\"",
            "HeaderKey=\"Settings.Nav.AiSettings\"");
        Assert.DoesNotContain("HeaderKey=\"Settings.AttachmentStorageMode.Title\"", general, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.Displays.Title\"", general, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.Onboarding.Title\"", general, StringComparison.Ordinal);
    }

    // -------------------------------------------------- B. 文件叠放（文件堆叠）

    // Spec B1 + B2: FileWidgetSettingsSection.xaml hosts a direct drill-in
    // SettingsCard to FileStackSettings, while FileStackSettingsSectionTemplate
    // hosts the flattened AutoStacking, OpenMode/PopoverLayout/PopoverStyle and rule cards.
    [Fact]
    public void FileStack_DirectOverviewCard_AndFlattenedDetailPage()
    {
        string overview = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");
        string slice = FileStackSlice();

        Match? overviewCard = FindOpeningTag(
            overview, "toolkit:SettingsCard", "HeaderKey=\"Settings.FileStacks.Title\"");
        Assert.NotNull(overviewCard);
        Assert.Contains("Tag=\"FileStackSettings\"", overviewCard.Value, StringComparison.Ordinal);
        Assert.Null(FindOpeningTag(
            overview, "toolkit:SettingsExpander", "HeaderKey=\"Settings.FileStacks.Title\""));
        Assert.DoesNotContain("HeaderKey=\"Settings.FileStacks.MoreSettings\"", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.FileStacks.Auto.Title\"", overview, StringComparison.Ordinal);

        Assert.Equal(0, CountOpeningTags(slice, "toolkit:SettingsExpander"));
        AssertAscending(slice, "FileStack section ordering",
            "HeaderKey=\"Settings.FileStacks.Auto.Title\"",
            "HeaderKey=\"Settings.FileStacks.OpenMode.Title\"",
            "HeaderKey=\"Settings.FileStacks.PopoverLayout.Title\"",
            "HeaderKey=\"Settings.FileStacks.PopoverStyle.Title\"",
            "HeaderKey=\"Settings.FileStacks.Threshold.Title\"",
            "HeaderKey=\"Settings.FileStacks.GroupBy.Title\"",
            "HeaderKey=\"Settings.FileStacks.Unmatched.Title\"",
            "HeaderKey=\"Settings.FileStacks.OrderBy.Title\"");

        Assert.DoesNotContain("HeaderKey=\"Settings.FileStacks.Enable.Title\"", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.FileStacks.Mode.Title\"", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding StacksEnabled", slice, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsPopoverOpenMode, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"", slice, StringComparison.Ordinal);
        Assert.Contains("SettingsCard.HeaderIcon", slice, StringComparison.Ordinal);
    }

    // Spec B3: the custom-rules editor stays inside the template.
    [Fact]
    public void FileStack_CustomRulesEditor_StaysInTheTemplate()
    {
        string slice = FileStackSlice();

        Assert.Contains("FileStacks.Custom.Rules", slice, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FileStackRulesListView\"", slice, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- C. 胶囊子页合并

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

    // Spec C4: capsule per-widget overrides are consolidated inline inside
    // CapsuleModeSettingsSection.xaml (hosted in Interaction), while legacy
    // CapsuleOverridesSettings deep links normalize to Interaction.
    [Fact]
    public void Capsule_OverridesRoute_Survives()
    {
        string codeBehind = ReadRepositoryFile(SettingsWindowCodeBehind);
        string navigation = ReadRepositoryFile(SettingsWindowNavigation);
        string windowXaml = ReadRepositoryFile(SettingsWindowXaml);
        string capsuleXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml");

        Assert.DoesNotContain("[\"CapsuleOverridesSettings\"]", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CapsuleOverridesSettingsSectionTemplate", windowXaml, StringComparison.Ordinal);
        Assert.Contains("\"CapsuleOverridesSettings\"", navigation, StringComparison.Ordinal);
        Assert.Contains("=> \"Interaction\"", navigation, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.Capsule.Overrides.Title\"", capsuleXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding OverrideItems}\"", capsuleXaml, StringComparison.Ordinal);
        Assert.Contains("ResetAllCapsuleOverridesCommand", capsuleXaml, StringComparison.Ordinal);
    }

    // Spec C5: InteractionSection hosts a clickable CapsuleModeEntryCard (with inline
    // CollapseBehavior combo), while CapsuleModeSettingsSection.xaml is a flat 3rd-level
    // sub-page starting with the retained CollapseBehavior card and zero SettingsExpanders.
    [Fact]
    public void CapsuleModeSection_HasArrangementExpanderAndFlatPresetCards()
    {
        string interaction = InteractionSlice();
        Match? entryCard = FindOpeningTag(
            interaction, "toolkit:SettingsCard", "x:Name=\"CapsuleModeEntryCard\"");
        Assert.NotNull(entryCard);
        Assert.Contains("HeaderKey=\"Settings.Section.CapsuleMode\"", entryCard.Value, StringComparison.Ordinal);

        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml");

        int expanderCount = CountOpeningTags(xaml, "toolkit:SettingsExpander");
        Assert.True(expanderCount == 0,
            $"CapsuleModeSettingsSection.xaml: expected 0 SettingsExpander elements " +
            $"on the flat 3rd-level sub-page, found {expanderCount}.");
        Assert.NotNull(FindOpeningTag(
            xaml, "toolkit:SettingsCard", "HeaderKey=\"Settings.CollapseBehavior.Title\""));

        Match? hover = FindOpeningTag(
            xaml, "toolkit:SettingsCard", "HeaderKey=\"Settings.Capsule.HoverResponse.Title\"");
        Assert.NotNull(hover);

        int expandDelay = IndexOfOrFail(
            xaml, "HeaderKey=\"Settings.Capsule.ExpandDelay.Title\"", "CapsuleMode section");
        Assert.True(hover.Index < expandDelay,
            "CapsuleMode section: the expand-delay sub-card must sit after the " +
            "Settings.Capsule.HoverResponse.Title card.");

        Assert.Contains("HeaderKey=\"Settings.Capsule.CollapseDelay.Title\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.Capsule.Spacing.Title\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.Capsule.Direction.Title\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.Capsule.AnimationDuration.Title\"", xaml, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ D. 外观动画与折叠子页退役

    // Spec D: the three folded sub-page templates/routes (Density, Window,
    // Animation) are retired, and AppearanceSettingsSection.xaml owns the
    // animation expander with Preset + Direction/Speed/Easing/Stagger in order.
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
        Assert.DoesNotContain("HeaderKey=\"Settings.Animation.Preset.Title\"", appearanceXaml, StringComparison.Ordinal);
        AssertAscending(appearanceXaml, "Appearance animation expander card order",
            "HeaderKey=\"Settings.Group.Animation.Title\"",
            "HeaderKey=\"Settings.Animation.Direction.Title\"",
            "HeaderKey=\"Settings.Animation.Speed.Title\"",
            "HeaderKey=\"Settings.Animation.Easing.Title\"",
            "HeaderKey=\"Settings.Animation.Stagger.Title\"");
    }

    // -------------------------------------------------------------- E. 材质

    // Spec E1 + E3: AppearanceMaterialSettingsSectionTemplate is retired; Appearance
    // overview hosts clickable MaterialEntryCard, and MaterialSubPagePanel hosts the
    // retained MaterialType card + 2 default-expanded SettingsExpanders (Effect + WidgetBackground).
    [Fact]
    public void AppearanceMaterial_IsFlattenedWithoutNestedExpanders()
    {
        string windowXaml = ReadRepositoryFile(SettingsWindowXaml);
        string codeBehind = ReadRepositoryFile(SettingsWindowCodeBehind);
        string catalog = ReadRepositoryFile(SearchCatalog);
        string appearanceXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        Assert.DoesNotContain("AppearanceMaterialSettingsSectionTemplate", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AppearanceMaterialSettings\"", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AppearanceMaterialSettings\"", catalog, StringComparison.Ordinal);

        Match? materialEntryCard = FindOpeningTag(
            appearanceXaml, "toolkit:SettingsCard", "x:Name=\"MaterialEntryCard\"");
        Assert.NotNull(materialEntryCard);
        Assert.Contains("HeaderKey=\"Settings.Material.Title\"", materialEntryCard.Value, StringComparison.Ordinal);
        Assert.Null(FindOpeningTag(
            appearanceXaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Material.Title\""));

        string subPageSlice = Slice(appearanceXaml, "x:Name=\"MaterialSubPagePanel\"", "</Grid>");
        Assert.Equal(2, CountOpeningTags(subPageSlice, "toolkit:SettingsExpander"));
        Assert.NotNull(FindOpeningTag(
            subPageSlice, "toolkit:SettingsCard", "HeaderKey=\"Settings.Material.Title\""));

        Assert.NotNull(FindOpeningTag(
            appearanceXaml, "toolkit:SettingsCard", "HeaderKey=\"Settings.WidgetForeground.Title\""));
    }

    // Spec E2 + E3: MaterialSubPagePanel groups Effect (3 rows) and WidgetBackground (5 rows)
    // in default-expanded accordions, while CornerAndBorder and Density stay on AppearanceOverviewPanel.
    [Fact]
    public void AppearanceMaterial_CardsAreFlatAndCornerLivesOnOverview()
    {
        string appearanceXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");
        string subPageSlice = Slice(appearanceXaml, "x:Name=\"MaterialSubPagePanel\"", "</Grid>");

        Match? effectExpander = FindOpeningTag(
            subPageSlice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Material.Group.Effect\"");
        Assert.NotNull(effectExpander);
        Assert.Contains("IsExpanded=\"True\"", effectExpander.Value, StringComparison.Ordinal);
        AssertInsideExpander(subPageSlice, effectExpander, "HeaderKey=\"Settings.Opacity.Title\"", "Material.Effect");
        AssertInsideExpander(subPageSlice, effectExpander, "HeaderKey=\"Settings.SystemTransparency.Title\"", "Material.Effect");

        Match? backgroundExpander = FindOpeningTag(
            subPageSlice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Material.Group.WidgetBackground\"");
        Assert.NotNull(backgroundExpander);
        Assert.Contains("IsExpanded=\"True\"", backgroundExpander.Value, StringComparison.Ordinal);

        AssertAscending(subPageSlice, "Appearance Material sub-page ordering",
            "HeaderKey=\"Settings.Material.Title\"",
            "HeaderKey=\"Settings.Material.Group.Effect\"",
            "HeaderKey=\"Settings.Opacity.Title\"",
            "HeaderKey=\"Settings.MaterialIntensity.Title\"",
            "HeaderKey=\"Settings.SystemTransparency.Title\"",
            "HeaderKey=\"Settings.Material.Group.WidgetBackground\"",
            "HeaderKey=\"Settings.WidgetBackground.Title\"",
            "HeaderKey=\"Settings.WidgetBackground.Image.Title\"",
            "HeaderKey=\"Settings.WidgetBackground.Fit.Title\"",
            "HeaderKey=\"Settings.WidgetBackground.Dim.Title\"",
            "HeaderKey=\"Settings.WidgetBackground.PerWidget.Title\"");

        Match? cornerExpander = FindOpeningTag(
            appearanceXaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.CornerAndBorder.Title\"");
        Assert.NotNull(cornerExpander);
        AssertAscending(appearanceXaml, "Appearance CornerAndBorder expander ordering",
            "HeaderKey=\"Settings.CornerAndBorder.Title\"",
            "AutomationNameKey=\"Settings.Corner.Title\"",
            "HeaderKey=\"Settings.BorderColor.Title\"",
            "HeaderKey=\"Settings.Border.Title\"");

        Match? densityExpander = FindOpeningTag(
            appearanceXaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Density.Title\"");
        Assert.NotNull(densityExpander);
        AssertAscending(appearanceXaml, "Appearance Density & Typography expander ordering",
            "HeaderKey=\"Settings.Density.Title\"",
            "HeaderKey=\"Settings.Density.Scale.Title\"",
            "HeaderKey=\"Settings.TextSize.Title\"",
            "HeaderKey=\"Settings.WidgetForeground.Title\"",
            "HeaderKey=\"Settings.WidgetForeground.CustomColor.Title\"",
            "HeaderKey=\"Settings.TextShadow.Title\"");
    }

    // ------------------------------------------------------------ F. 文件显示

    // Spec F1: FileDisplaySettingsSectionTemplate is consolidated into
    // FileWidgetSettingsSection.xaml, where the file-extension card is flat and
    // the conditional .lnk card follows it.
    [Fact]
    public void FileDisplay_ExtensionsCard_IsFollowedByTheLnkCard()
    {
        string windowXaml = ReadRepositoryFile(SettingsWindowXaml);
        string fileWidgetXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");

        Assert.DoesNotContain("FileDisplaySettingsSectionTemplate", windowXaml, StringComparison.Ordinal);

        Match? card = FindOpeningTag(
            fileWidgetXaml, "toolkit:SettingsCard", "HeaderKey=\"Settings.ShowFileExtensions.Title\"");
        Assert.NotNull(card);

        int lnk = IndexOfOrFail(
            fileWidgetXaml, "HeaderKey=\"Settings.HideShortcutExtension.Title\"", "FileWidget section");
        Assert.True(card.Index < lnk,
            "FileWidget section: the .lnk sub-card must come after the " +
            "ShowFileExtensions card declaration.");
    }

    // Spec F2: FileNameWidth/FileNameLines live in FileWidgetSettingsSection.xaml
    // rather than AppearanceSettingsSection.xaml.
    [Fact]
    public void FileDisplay_ReceivesTheFileNameCardsFromDensity()
    {
        string display = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");
        string density = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        Assert.Contains("HeaderKey=\"Settings.FileNameWidth.Title\"", display, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"Settings.FileNameLines.Title\"", display, StringComparison.Ordinal);

        Assert.DoesNotContain("HeaderKey=\"Settings.FileNameWidth.Title\"", density, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.FileNameLines.Title\"", density, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- G. 本地与云备份（数据与重置）

    // Spec G: MaintenanceSectionTemplate consolidates local backup, cloud backup,
    // and settings reset; AutomaticBackup is an expander grouping Interval,
    // Retention, Directory, and Snapshots.
    [Fact]
    public void BackupRestore_AutomaticBackupExpander_GroupsTheChildCards()
    {
        string windowXaml = ReadRepositoryFile(SettingsWindowXaml);
        Assert.DoesNotContain("BackupRestoreSettingsSectionTemplate", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CloudBackupSettingsSectionTemplate", windowXaml, StringComparison.Ordinal);

        string slice = Slice(
            windowXaml,
            "x:Key=\"MaintenanceSectionTemplate\"",
            "x:Key=\"AboutSectionTemplate\"");

        string overviewSlice = Slice(
            slice,
            "x:Name=\"MaintenanceOverviewPanel\"",
            "x:Name=\"AutomaticBackupSubPagePanel\"");
        Assert.NotNull(FindOpeningTag(
            overviewSlice, "toolkit:SettingsCard", "x:Name=\"AutomaticBackupEntryCard\""));
        Assert.NotNull(FindOpeningTag(
            overviewSlice, "toolkit:SettingsCard", "x:Name=\"CloudBackupEntryCard\""));

        AssertAscending(overviewSlice, "Maintenance overview ordering",
            "HeaderKey=\"Settings.DataBackup.Manual.Title\"",
            "HeaderKey=\"Settings.DataBackup.AutomaticBackup.Title\"",
            "HeaderKey=\"Settings.CloudBackup.Title\"",
            "HeaderKey=\"Settings.Restore.Title\"");

        string autoBackupSubPage = Slice(
            slice,
            "x:Name=\"AutomaticBackupSubPagePanel\"",
            "x:Name=\"CloudBackupSubPagePanel\"");
        Assert.Equal(0, CountOpeningTags(autoBackupSubPage, "toolkit:SettingsExpander"));
        AssertAscending(autoBackupSubPage, "AutomaticBackup sub-page ordering",
            "HeaderKey=\"Settings.DataBackup.AutomaticBackup.Title\"",
            "HeaderKey=\"Settings.DataBackup.AutomaticBackupInterval.Title\"",
            "HeaderKey=\"Settings.DataBackup.AutomaticBackupRetention.Title\"",
            "HeaderKey=\"Settings.DataBackup.AutomaticBackupDirectory.Title\"",
            "HeaderKey=\"Settings.DataBackup.Snapshots.Title\"");
    }

    // ----------------------------------------------------------------- H. 天气

    // Spec H1 + H2: the display group is a single compact WrapFlowPanel
    // SettingsCard hosting all seven CheckBox options.
    [Fact]
    public void Weather_DisplayGroup_IsACompactWrapFlowCardWithSevenCheckBoxes()
    {
        string slice = WeatherSlice();

        Match? card = FindOpeningTag(
            slice, "toolkit:SettingsCard", "HeaderKey=\"Settings.Weather.Group.Display.Title\"");
        Assert.NotNull(card);
        Assert.Null(FindOpeningTag(
            slice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Weather.Group.Display.Title\""));

        foreach (string key in new[]
                 {
                     "svc:Localized.Key=\"Settings.Weather.ShowForecast.Title\"",
                     "svc:Localized.Key=\"Settings.Weather.ShowHumidity.Title\"",
                     "svc:Localized.Key=\"Settings.Weather.ShowPrecipitation.Title\"",
                     "svc:Localized.Key=\"Settings.Weather.ShowPressure.Title\"",
                     "svc:Localized.Key=\"Settings.Weather.ShowSunrise.Title\"",
                     "svc:Localized.Key=\"Settings.Weather.ShowUvIndex.Title\"",
                     "svc:Localized.Key=\"Settings.Weather.ShowWind.Title\"",
                 })
        {
            int at = IndexOfOrFail(slice, key, "Weather section");
            Assert.True(card.Index < at,
                $"Weather section: {key} must live inside the display-group card.");
        }
    }

    // Spec H3: Enable < LocationMode < CityName < Group.Display < Skin < IconStyle <
    // Group.UnitsAndSource (TemperatureUnit < WindSpeedUnit < DataSource < RefreshInterval) < ResetWidget.
    [Fact]
    public void Weather_Cards_FollowThePinnedOrder()
    {
        string slice = WeatherSlice();

        Assert.DoesNotContain("HeaderKey=\"Settings.Weather.DefaultView.Title\"", slice, StringComparison.Ordinal);

        AssertAscending(slice, "Weather section ordering",
            "HeaderKey=\"Settings.Weather.Enable.Title\"",
            "HeaderKey=\"Settings.Weather.LocationMode.Title\"",
            "HeaderKey=\"Settings.Weather.CityName.Title\"",
            "HeaderKey=\"Settings.Weather.Group.Display.Title\"",
            "HeaderKey=\"Settings.Weather.Skin.Title\"",
            "HeaderKey=\"Settings.Weather.IconStyle.Title\"",
            "HeaderKey=\"Settings.Weather.Group.UnitsAndSource.Title\"",
            "HeaderKey=\"Settings.Weather.TemperatureUnit.Title\"",
            "HeaderKey=\"Settings.Weather.WindSpeedUnit.Title\"",
            "HeaderKey=\"Settings.Weather.DataSource.Title\"",
            "HeaderKey=\"Settings.Weather.RefreshInterval.Title\"",
            "HeaderKey=\"Settings.Weather.ResetWidget.Title\"");
    }

    // ------------------------------------------------------------- I. 交互页排序

    // Spec I: Invocation (GlobalHotkey + DesktopDoubleClick) <
    // Layout (CapsuleModeSection + WidgetGroups + Displays) <
    // WindowBehavior (WidgetLayerMode + ResizeSnap + Interaction.Hover).
    [Fact]
    public void Interaction_Section_FollowsThePinnedOrder()
    {
        string slice = InteractionSlice();

        AssertAscending(slice, "Interaction ordering",
            "HeaderKey=\"Settings.GlobalHotkey.Title\"",
            "HeaderKey=\"Settings.DesktopDoubleClick.Title\"",
            "x:Name=\"CapsuleModeEntryCard\"",
            "HeaderKey=\"Settings.Section.WidgetGroups\"",
            "HeaderKey=\"Settings.Displays.Title\"",
            "HeaderKey=\"Settings.WidgetLayerMode.Title\"",
            "HeaderKey=\"Settings.ClickRaiseScope.Title\"",
            "HeaderKey=\"Settings.ShowDesktopBehavior.Title\"",
            "HeaderKey=\"Settings.ResizeSnap.Title\"",
            "HeaderKey=\"Settings.WidgetSnap.Spacing.Title\"",
            "HeaderKey=\"Settings.Interaction.Hover.Title\"");

        Assert.DoesNotContain("HeaderKey=\"Settings.OpenMethod.Title\"", slice, StringComparison.Ordinal);
        Assert.Contains(
            "svc:InfoTip.TitleKey=\"Settings.GlobalHotkey.ElevatedNotice.Title\"",
            slice,
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- J. 窗口子页

    // Spec J: DefaultWidth and DefaultHeight share one card (both NumberBoxes
    // within 1200 chars of each other), and the app-visual header leads them.
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

    // Spec L1: Theme < TrayIcon < Accent < Material < CornerAndBorder < Density <
    // Group.AppVisual < Group.Animation, while WidgetGroups lives in Interaction.
    [Fact]
    public void AppearanceSection_FollowsThePinnedOrder()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        AssertAscending(xaml, "AppearanceSection top-to-bottom ordering",
            "HeaderKey=\"Settings.Theme.Title\"",
            "HeaderKey=\"Settings.TrayIcon.Title\"",
            "HeaderKey=\"Settings.Accent.Title\"",
            "AutomationNameKey=\"Settings.Accent.Source.Title\"",
            "HeaderKey=\"Settings.Material.Title\"",
            "HeaderKey=\"Settings.CornerAndBorder.Title\"",
            "AutomationNameKey=\"Settings.Corner.Title\"",
            "HeaderKey=\"Settings.Density.Title\"",
            "HeaderKey=\"Settings.Group.AppVisual.Title\"",
            "HeaderKey=\"Settings.Group.Animation.Title\"");

        Assert.DoesNotContain("HeaderKey=\"Settings.Section.WidgetGroups\"", xaml, StringComparison.Ordinal);
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
    public void GlanceSection_KeepsTransitionInsideTheAdvancedGroup()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml");

        Assert.NotNull(FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Glance.Group.Advanced\""));
        Assert.NotNull(FindOpeningTag(
            xaml, "toolkit:SettingsCard", "HeaderKey=\"Glance.Transition.Title\""));
        Assert.True(
            xaml.IndexOf("HeaderKey=\"Settings.Glance.Group.Advanced\"", StringComparison.Ordinal) <
            xaml.IndexOf("HeaderKey=\"Glance.Transition.Title\"", StringComparison.Ordinal));
    }

    // Spec L4 (Round 2): DesktopOrganization overview collapses into AutoToggleExpander
    // (IsExpanded="False") with 2 items: AutoIntervalCard and OrganizationRulesEntryCard.
    [Fact]
    public void DesktopOrganization_AutoCard_IsFlatWithIntervalSibling()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml");

        Match? expander = FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "x:Name=\"AutoToggleExpander\"");
        Assert.NotNull(expander);
        Assert.Contains("IsExpanded=\"False\"", expander.Value, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"DesktopOrganization.Auto.Title\"", expander.Value, StringComparison.Ordinal);
        AssertInsideExpander(xaml, expander, "HeaderKey=\"DesktopOrganization.Auto.Interval.Title\"", "AutoToggleExpander");
        AssertInsideExpander(xaml, expander, "HeaderKey=\"DesktopOrganization.Rules.SectionTitle\"", "AutoToggleExpander");

        AssertAscending(xaml, "DesktopOrganization overview expander ordering",
            "x:Name=\"AutoToggleExpander\"",
            "HeaderKey=\"DesktopOrganization.Auto.Interval.Title\"",
            "HeaderKey=\"DesktopOrganization.Rules.SectionTitle\"");
    }

    // ------------------------------------------------------------ M. Round 2 Contracts

    [Fact]
    public void Round2_SettingsWindowSegmentedControls_DoNotSetFixedMinWidth()
    {
        var doc = System.Xml.Linq.XDocument.Parse(ReadRepositoryFile(SettingsWindowXaml));
        System.Xml.Linq.XNamespace toolkit = "using:CommunityToolkit.WinUI.Controls";

        foreach (System.Xml.Linq.XElement segmented in doc.Descendants(toolkit + "Segmented"))
        {
            Assert.Null(segmented.Attribute("MinWidth"));
        }
    }

    [Fact]
    public void Round2_AllSettingsExpanders_ContainAtMostFiveDirectSettingsCards()
    {
        string[] xamlFiles =
        {
            SettingsWindowXaml,
            "src/DeskBox/Views/SettingsSections/AiSettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/ClipboardSettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/CustomWidgetSettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/DisplaySettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml",
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml",
        };

        System.Xml.Linq.XNamespace toolkit = "using:CommunityToolkit.WinUI.Controls";
        foreach (string file in xamlFiles)
        {
            var doc = System.Xml.Linq.XDocument.Parse(ReadRepositoryFile(file));
            foreach (System.Xml.Linq.XElement expander in doc.Descendants(toolkit + "SettingsExpander"))
            {
                System.Xml.Linq.XElement? items = expander.Element(toolkit + "SettingsExpander.Items");
                int itemCount = items is null ? 0 : items.Elements().Count();
                Assert.True(
                    itemCount <= 5,
                    $"{file}: SettingsExpander (HeaderKey='{expander.Attributes().FirstOrDefault(a => a.Name.LocalName == "HeaderKey")?.Value}') has {itemCount} direct items (must be <= 5).");
            }
        }
    }

    [Fact]
    public void Round2_SubPagesWithAtMostThreeExpanders_ExpandAllByDefault()
    {
        (string FilePath, string? PanelName)[] subPages =
        [
            ("src/DeskBox/Views/SettingsSections/AiSettingsSection.xaml", null),
            ("src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml", "RuleDetailPanel"),
            ("src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml", "MaterialSubPagePanel"),
            ("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml", "FileDisplaySubPagePanel"),
            (SettingsWindowXaml, "WidgetGroupDetailSubPagePanel"),
            (SettingsWindowXaml, "CloudBackupSubPagePanel"),
        ];

        System.Xml.Linq.XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        System.Xml.Linq.XNamespace toolkit = "using:CommunityToolkit.WinUI.Controls";
        foreach ((string filePath, string? panelName) in subPages)
        {
            var doc = System.Xml.Linq.XDocument.Parse(ReadRepositoryFile(filePath));
            System.Xml.Linq.XElement? scope = panelName is null
                ? doc.Root
                : doc.Descendants().FirstOrDefault(e => (string?)e.Attribute(xNs + "Name") == panelName);
            Assert.NotNull(scope);

            var expanders = scope.Descendants(toolkit + "SettingsExpander").ToList();
            Assert.InRange(expanders.Count, 1, 3);
            foreach (System.Xml.Linq.XElement expander in expanders)
            {
                Assert.Equal("True", (string?)expander.Attribute("IsExpanded"));
            }
        }
    }

    [Fact]
    public void Round2_SubPagesWithInlineEntryControl_RetainEntryOptionCardAtTop()
    {
        string appearanceXaml = ReadRepositoryFile("src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");
        string capsuleXaml = ReadRepositoryFile("src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml");
        string windowXaml = ReadRepositoryFile(SettingsWindowXaml);

        string materialSubPage = Slice(appearanceXaml, "x:Name=\"MaterialSubPagePanel\"", "</Grid>");
        Assert.NotNull(FindOpeningTag(materialSubPage, "toolkit:SettingsCard", "HeaderKey=\"Settings.Material.Title\""));

        Assert.NotNull(FindOpeningTag(capsuleXaml, "toolkit:SettingsCard", "HeaderKey=\"Settings.CollapseBehavior.Title\""));

        string autoBackupSubPage = Slice(windowXaml, "x:Name=\"AutomaticBackupSubPagePanel\"", "x:Name=\"CloudBackupSubPagePanel\"");
        Assert.NotNull(FindOpeningTag(autoBackupSubPage, "toolkit:SettingsCard", "HeaderKey=\"Settings.DataBackup.AutomaticBackup.Title\""));

        string cloudBackupSubPage = Slice(windowXaml, "x:Name=\"CloudBackupSubPagePanel\"", "x:Key=\"AboutSectionTemplate\"");
        Assert.NotNull(FindOpeningTag(cloudBackupSubPage, "toolkit:SettingsCard", "HeaderKey=\"Settings.CloudBackup.Title\""));
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
        return Slice(
            ReadRepositoryFile(SettingsWindowXaml),
            "x:Key=\"InteractionSectionTemplate\"",
            "x:Key=\"FeatureWidgetsSectionTemplate\"");
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

    [Fact]
    public void Expanders_NeverDeclareExpandedOrCollapsedHandlerBeforeIsExpanded()
    {
        string projectDirectory = TestPaths.FromRepository("src/DeskBox");
        string separator = Path.DirectorySeparatorChar.ToString();
        var expanderTagRegex = new Regex(
            @"<(?:toolkit:SettingsExpander|Expander)\b(?:(?!>).)*?>",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        foreach (string path in Directory.EnumerateFiles(projectDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            if (path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string xaml = File.ReadAllText(path);
            foreach (Match tag in expanderTagRegex.Matches(xaml))
            {
                int isExpandedIdx = tag.Value.IndexOf("IsExpanded=", StringComparison.Ordinal);
                if (isExpandedIdx < 0)
                {
                    continue;
                }

                int expandedEventIdx = tag.Value.IndexOf("Expanded=", StringComparison.Ordinal);
                if (expandedEventIdx == isExpandedIdx + 2)
                {
                    // Skip the "Expanded=" substring inside "IsExpanded=" and look for a standalone handler.
                    expandedEventIdx = tag.Value.IndexOf("Expanded=", isExpandedIdx + "IsExpanded=".Length, StringComparison.Ordinal);
                }

                // Match standalone Expanded="..." or Collapsed="..." event attributes.
                Match standaloneEvent = Regex.Match(
                    tag.Value,
                    @"(?<![A-Za-z])(?:Expanded|Collapsed)\s*=",
                    RegexOptions.CultureInvariant);
                if (standaloneEvent.Success)
                {
                    Assert.True(
                        isExpandedIdx < standaloneEvent.Index,
                        $"{Path.GetRelativePath(projectDirectory, path)}: IsExpanded must appear before {standaloneEvent.Value} so InitializeComponent does not invoke the handler while child named elements are still null. Tag: {tag.Value}");
                }
            }
        }
    }

    [Fact]
    public void AiSettingsSection_GuardsLifecycleAndDeepLinkNavigation()
    {
        string aiCode = ReadRepositoryFile("src/DeskBox/Views/SettingsSections/AiSettingsSection.xaml.cs");
        string navCode = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.Navigation.cs");

        Assert.Contains("if (!_isSectionLoaded || AgentHubDynamicContentPanel is null)", aiCode, StringComparison.Ordinal);
        Assert.Contains("RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);", aiCode, StringComparison.Ordinal);
        Assert.Contains("\"CustomWidgetSettings:ai-settings\"", navCode, StringComparison.Ordinal);
        Assert.Contains("CustomWidgetSettingsSection { IsDetailViewOpen: true }", navCode, StringComparison.Ordinal);
    }
}
