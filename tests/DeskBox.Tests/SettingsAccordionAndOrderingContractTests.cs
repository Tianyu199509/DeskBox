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

    // Spec A1: 「开机自动启动」is a single flat SettingsCard (the legacy
    // Standard vs ScheduledTask mode picker was retired under Scheme A).
    [Fact]
    public void General_AutoStart_IsASingleFlatCardWithoutModePicker()
    {
        string general = GeneralSectionSlice();

        Match? card = FindOpeningTag(
            general, "toolkit:SettingsCard", "HeaderKey=\"Settings.AutoStart.Title\"");
        Assert.NotNull(card);
        Assert.Null(FindOpeningTag(
            general, "toolkit:SettingsExpander", "HeaderKey=\"Settings.AutoStart.Title\""));
        Assert.DoesNotContain("HeaderKey=\"Settings.AutoStart.Mode.Title\"", general, StringComparison.Ordinal);
    }

    // Spec A2: pinned GeneralSection order
    // Language < AutoStart < SilentStartup < Performance < Nav.AiSettings < Onboarding.
    [Fact]
    public void General_Cards_FollowThePinnedOrder()
    {
        string general = GeneralSectionSlice();

        AssertAscending(general, "GeneralSection ordering",
            "HeaderKey=\"Settings.Language.Title\"",
            "HeaderKey=\"Settings.AutoStart.Title\"",
            "HeaderKey=\"Settings.SilentStartup.Title\"",
            "HeaderKey=\"Settings.Performance.Title\"",
            "HeaderKey=\"Settings.Nav.AiSettings\"",
            "HeaderKey=\"Settings.Onboarding.Title\"");
        Assert.DoesNotContain("HeaderKey=\"Settings.AutoStart.Mode.Title\"", general, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.AttachmentStorageMode.Title\"", general, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.Displays.Title\"", general, StringComparison.Ordinal);
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

    // Spec E1 + E2 + E3: AppearanceMaterialSettingsSectionTemplate and MaterialSubPagePanel
    // are retired; Appearance overview hosts MaterialExpander directly, grouping Opacity,
    // MaterialIntensity, SystemTransparency, and WidgetBackground cards without a sub-page hop.
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
        Assert.DoesNotContain("x:Name=\"MaterialSubPagePanel\"", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"MaterialEntryCard\"", appearanceXaml, StringComparison.Ordinal);

        Match? materialExpander = FindOpeningTag(
            appearanceXaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Material.Title\"");
        Assert.NotNull(materialExpander);
        Assert.Contains("x:Name=\"MaterialExpander\"", materialExpander.Value, StringComparison.Ordinal);

        Assert.NotNull(FindOpeningTag(
            appearanceXaml, "toolkit:SettingsCard", "HeaderKey=\"Settings.WidgetForeground.Title\""));
    }

    [Fact]
    public void AppearanceMaterial_CardsAreFlatAndCornerLivesOnOverview()
    {
        string appearanceXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        Match? materialExpander = FindOpeningTag(
            appearanceXaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Material.Title\"");
        Assert.NotNull(materialExpander);
        AssertInsideExpander(appearanceXaml, materialExpander, "HeaderKey=\"Settings.Opacity.Title\"", "Material");
        AssertInsideExpander(appearanceXaml, materialExpander, "HeaderKey=\"Settings.MaterialIntensity.Title\"", "Material");
        AssertInsideExpander(appearanceXaml, materialExpander, "HeaderKey=\"Settings.SystemTransparency.Title\"", "Material");
        AssertInsideExpander(appearanceXaml, materialExpander, "HeaderKey=\"Settings.WidgetBackground.Title\"", "Material");
        AssertInsideExpander(appearanceXaml, materialExpander, "HeaderKey=\"Settings.WidgetBackground.Image.Title\"", "Material");
        AssertInsideExpander(appearanceXaml, materialExpander, "HeaderKey=\"Settings.WidgetBackground.Fit.Title\"", "Material");
        AssertInsideExpander(appearanceXaml, materialExpander, "HeaderKey=\"Settings.WidgetBackground.Dim.Title\"", "Material");
        AssertInsideExpander(appearanceXaml, materialExpander, "HeaderKey=\"Settings.WidgetBackground.PerWidget.Title\"", "Material");

        AssertAscending(appearanceXaml, "Appearance Material expander ordering",
            "HeaderKey=\"Settings.Material.Title\"",
            "HeaderKey=\"Settings.Opacity.Title\"",
            "HeaderKey=\"Settings.MaterialIntensity.Title\"",
            "HeaderKey=\"Settings.SystemTransparency.Title\"",
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

    // Spec I: Invocation (GlobalHotkey + WidgetLayerMode expander [ClickRaiseScope,
    // ShowDesktopBehavior, DesktopDoubleClick]) <
    // Layout (CapsuleModeSection + WidgetGroups + Displays) <
    // WindowBehavior (ResizeSnap + Interaction.Hover).
    [Fact]
    public void Interaction_Section_FollowsThePinnedOrder()
    {
        string slice = InteractionSlice();

        Match? layerExpander = FindOpeningTag(
            slice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.WidgetLayerMode.Title\"");
        Assert.NotNull(layerExpander);
        AssertInsideExpander(slice, layerExpander, "HeaderKey=\"Settings.ClickRaiseScope.Title\"", "WidgetLayerMode");
        AssertInsideExpander(slice, layerExpander, "HeaderKey=\"Settings.ShowDesktopBehavior.Title\"", "WidgetLayerMode");
        AssertInsideExpander(slice, layerExpander, "HeaderKey=\"Settings.DesktopDoubleClick.Title\"", "WidgetLayerMode");

        AssertAscending(slice, "Interaction ordering",
            "HeaderKey=\"Settings.GlobalHotkey.Title\"",
            "HeaderKey=\"Settings.WidgetLayerMode.Title\"",
            "HeaderKey=\"Settings.ClickRaiseScope.Title\"",
            "HeaderKey=\"Settings.ShowDesktopBehavior.Title\"",
            "HeaderKey=\"Settings.DesktopDoubleClick.Title\"",
            "x:Name=\"CapsuleModeEntryCard\"",
            "HeaderKey=\"Settings.Section.WidgetGroups\"",
            "HeaderKey=\"Settings.Displays.Title\"",
            "HeaderKey=\"Settings.ResizeSnap.Title\"",
            "HeaderKey=\"Settings.WidgetSnap.Spacing.Title\"",
            "HeaderKey=\"Settings.Interaction.Hover.Title\"");

        Assert.DoesNotContain("HeaderKey=\"Settings.OpenMethod.Title\"", slice, StringComparison.Ordinal);
        Assert.Contains(
            "svc:InfoTip.TitleKey=\"Settings.GlobalHotkey.ElevatedNotice.Title\"",
            slice,
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- J. 格子标题图标（精简后独立卡片）

    // Spec J: Redundant per-widget chrome & preset window-size controls are removed from
    // Appearance settings (configured directly on each widget); TitleIcon is promoted to a
    // standalone top-level SettingsCard without the AppVisual expander wrapper.
    [Fact]
    public void AppearanceWindow_DefaultSizeCards_AreMergedAndTitleIconLeads()
    {
        string slice = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        Assert.DoesNotContain("x:Name=\"DefaultWidthBox\"", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DefaultHeightBox\"", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.DisplayWidgetChrome.Title\"", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.InteractiveWidgetChrome.Title\"", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderKey=\"Settings.Group.AppVisual.Title\"", slice, StringComparison.Ordinal);

        Match? titleIconCard = FindOpeningTag(
            slice, "toolkit:SettingsCard", "HeaderKey=\"Settings.WidgetTitleIcon.Title\"");
        Assert.NotNull(titleIconCard);
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
            "\"Settings\\.Density\\.Description\"\\s*:\\s*\"[^\"]*间距",
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

    // Spec L1: ThemeExpander (TrayIcon < WidgetTitleIcon < Accent < Accent.CustomPalette,
    // without nested Accent expander) < Material < CornerAndBorder < Density < Group.Animation,
    // while WidgetGroups lives in Interaction.
    [Fact]
    public void AppearanceSection_FollowsThePinnedOrder()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        Match? themeExpander = FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Theme.Title\"");
        Assert.NotNull(themeExpander);
        Assert.Null(FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Accent.Title\""));
        Assert.NotNull(FindOpeningTag(
            xaml, "toolkit:SettingsCard", "HeaderKey=\"Settings.Accent.Title\""));
        Assert.NotNull(FindOpeningTag(
            xaml, "toolkit:SettingsCard", "HeaderKey=\"Settings.Accent.CustomPalette.Title\""));

        AssertInsideExpander(xaml, themeExpander, "HeaderKey=\"Settings.TrayIcon.Title\"", "Theme");
        AssertInsideExpander(xaml, themeExpander, "HeaderKey=\"Settings.WidgetTitleIcon.Title\"", "Theme");
        AssertInsideExpander(xaml, themeExpander, "HeaderKey=\"Settings.Accent.Title\"", "Theme");
        AssertInsideExpander(xaml, themeExpander, "HeaderKey=\"Settings.Accent.CustomPalette.Title\"", "Theme");

        AssertAscending(xaml, "AppearanceSection top-to-bottom ordering",
            "HeaderKey=\"Settings.Theme.Title\"",
            "HeaderKey=\"Settings.TrayIcon.Title\"",
            "HeaderKey=\"Settings.WidgetTitleIcon.Title\"",
            "HeaderKey=\"Settings.Accent.Title\"",
            "AutomationNameKey=\"Settings.Accent.Source.Title\"",
            "HeaderKey=\"Settings.Accent.CustomPalette.Title\"",
            "HeaderKey=\"Settings.Material.Title\"",
            "HeaderKey=\"Settings.CornerAndBorder.Title\"",
            "AutomationNameKey=\"Settings.Corner.Title\"",
            "HeaderKey=\"Settings.Density.Title\"",
            "HeaderKey=\"Settings.Group.Animation.Title\"");

        Assert.DoesNotContain("HeaderKey=\"Settings.Section.WidgetGroups\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FileWidgetSection_OpenAndContextMenu_AreGroupedInOneExpander()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");

        Match? expander = FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.FileWidget.OpenAndMenu.Title\"");
        Assert.NotNull(expander);
        Assert.Contains(
            "DescriptionKey=\"Settings.FileWidget.OpenAndMenu.Description\"",
            expander.Value,
            StringComparison.Ordinal);

        AssertInsideExpander(xaml, expander, "HeaderKey=\"Settings.OpenMethod.Title\"", "FileOpenAndMenu");
        AssertInsideExpander(xaml, expander, "HeaderKey=\"Settings.FileWidget.FolderOpenBehavior.Title\"", "FileOpenAndMenu");
        AssertInsideExpander(xaml, expander, "HeaderKey=\"Settings.FileItemContextMenu.Title\"", "FileOpenAndMenu");
    }

    [Fact]
    public void SearchSection_FollowsThePinnedOrderWithoutNestingRecommendationsOrWebFallback()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml");

        Assert.NotNull(FindOpeningTag(
            xaml, "toolkit:SettingsCard", "HeaderKey=\"Settings.Search.Recommendations.Title\""));
        Assert.Null(FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Search.Recommendations.Title\""));

        Match? everythingExpander = FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Search.Everything.Title\"");
        Assert.NotNull(everythingExpander);
        int everythingClose = xaml.IndexOf("</toolkit:SettingsExpander>", everythingExpander.Index, StringComparison.Ordinal);
        int webFallbackStart = IndexOfOrFail(xaml, "HeaderKey=\"Settings.Search.Scope.WebFallback\"", "SearchSection");
        Assert.True(webFallbackStart > everythingClose, "WebFallback expander must be a sibling after EverythingConnectionExpander, not nested inside it.");

        AssertAscending(xaml, "SearchSection ordering",
            "HeaderKey=\"Settings.Search.Hotkey.Title\"",
            "HeaderKey=\"Settings.Search.Recommendations.Title\"",
            "HeaderKey=\"Settings.Search.Everything.Title\"",
            "HeaderKey=\"Settings.Search.Scope.WebFallback\"",
            "HeaderKey=\"Settings.Search.LocalScope.Title\"",
            "HeaderKey=\"Settings.Search.QuickCreate.Title\"",
            "HeaderKey=\"Settings.Search.ResultsView.Title\"");
    }

    [Fact]
    public void AboutSection_UpdateExpander_ExpandsByDefault()
    {
        string aboutSlice = Slice(
            ReadRepositoryFile(SettingsWindowXaml),
            "x:Key=\"AboutSectionTemplate\"",
            "x:Name=\"GeneralSection\"");

        Match? updateExpander = FindOpeningTag(
            aboutSlice, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Update.Title\"");
        Assert.NotNull(updateExpander);
        Assert.Contains("IsExpanded=\"True\"", updateExpander.Value, StringComparison.Ordinal);
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

    // Spec L4: DesktopOrganization overview presents two flat top-level cards (OrganizeNowCard
    // with AccentActionButtonStyle and drill-in AutoOrganizationEntryCard), and the 2nd-level
    // AutoOrganizationSubPagePanel consolidates AutoToggleCard, AutoIntervalCard, and
    // RulesViewSelectorBar (ByType / ByWidget) without a 3rd-level hop.
    [Fact]
    public void DesktopOrganization_AutoCard_IsFlatWithIntervalSibling()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml");

        Assert.DoesNotContain("x:Name=\"AutoToggleExpander\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"RuleDetailPanel\"", xaml, StringComparison.Ordinal);

        Match? organizeNowCard = FindOpeningTag(
            xaml, "toolkit:SettingsCard", "x:Name=\"OrganizeNowCard\"");
        Assert.NotNull(organizeNowCard);
        Assert.Contains("Style=\"{StaticResource AccentActionButtonStyle}\"", xaml, StringComparison.Ordinal);

        Match? autoEntryCard = FindOpeningTag(
            xaml, "toolkit:SettingsCard", "x:Name=\"AutoOrganizationEntryCard\"");
        Assert.NotNull(autoEntryCard);
        Assert.Contains("IsClickEnabled=\"True\"", autoEntryCard.Value, StringComparison.Ordinal);
        Assert.Contains("HeaderKey=\"DesktopOrganization.Auto.Title\"", autoEntryCard.Value, StringComparison.Ordinal);

        AssertAscending(xaml, "DesktopOrganization overview and L2 subpage ordering",
            "x:Name=\"OrganizeNowCard\"",
            "x:Name=\"AutoOrganizationEntryCard\"",
            "x:Name=\"AutoOrganizationSubPagePanel\"",
            "x:Name=\"AutoToggleCard\"",
            "x:Name=\"AutoIntervalCard\"",
            "x:Name=\"RulesViewSelectorBar\"",
            "x:Name=\"ByTypeViewTab\"",
            "x:Name=\"ByWidgetViewTab\"");
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
                int unconditionalCount = items is null
                    ? 0
                    : items.Elements().Count(static item => item.Attribute("Visibility") is null);
                Assert.True(
                    unconditionalCount <= 5,
                    $"{file}: SettingsExpander (HeaderKey='{expander.Attributes().FirstOrDefault(a => a.Name.LocalName == "HeaderKey")?.Value}') has {unconditionalCount} unconditional direct items (must be <= 5).");
            }
        }
    }

    [Fact]
    public void Round2_SubPagesWithAtMostThreeExpanders_ExpandAllByDefault()
    {
        (string FilePath, string? PanelName)[] subPages =
        [
            ("src/DeskBox/Views/SettingsSections/AiSettingsSection.xaml", null),
            ("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml", "FileDisplaySubPagePanel"),
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
    public void SubPagesWithInlineEntryControl_RetainEntryOptionCardAtTop()
    {
        static void AssertFirstCardHasHeaderKey(string block, string expectedHeaderKey)
        {
            int entryIdx = block.IndexOf($"HeaderKey=\"{expectedHeaderKey}\"", StringComparison.Ordinal);
            Assert.True(entryIdx >= 0, $"Missing card HeaderKey='{expectedHeaderKey}'");
            int firstCardIdx = block.IndexOf("<toolkit:SettingsCard", StringComparison.Ordinal);
            int firstExpanderIdx = block.IndexOf("<toolkit:SettingsExpander", StringComparison.Ordinal);
            int earliestControlIdx = firstExpanderIdx >= 0 && firstExpanderIdx < firstCardIdx
                ? firstExpanderIdx
                : firstCardIdx;
            int entryCardStartIdx = block.LastIndexOf("<toolkit:SettingsCard", entryIdx, StringComparison.Ordinal);
            Assert.Equal(earliestControlIdx, entryCardStartIdx);
        }

        string capsuleXaml = ReadRepositoryFile("src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml");
        string windowXaml = ReadRepositoryFile(SettingsWindowXaml);

        AssertFirstCardHasHeaderKey(capsuleXaml, "Settings.CollapseBehavior.Title");

        string autoBackupSubPage = Slice(windowXaml, "x:Name=\"AutomaticBackupSubPagePanel\"", "x:Name=\"CloudBackupSubPagePanel\"");
        AssertFirstCardHasHeaderKey(autoBackupSubPage, "Settings.DataBackup.AutomaticBackup.Title");

        string cloudBackupSubPage = Slice(windowXaml, "x:Name=\"CloudBackupSubPagePanel\"", "x:Key=\"AboutSectionTemplate\"");
        AssertFirstCardHasHeaderKey(cloudBackupSubPage, "Settings.CloudBackup.Title");
    }

    [Fact]
    public void EntryCardsWithInlineSelectors_ExposeDirectComboBoxes()
    {
        string windowXaml = ReadRepositoryFile(SettingsWindowXaml);
        string fileWidgetXaml = ReadRepositoryFile("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");

        string general = GeneralSectionSlice();
        Assert.Contains("x:Name=\"AiSettingsEntryComboBox\"", general, StringComparison.Ordinal);

        string interaction = InteractionSlice();
        Assert.Contains("x:Name=\"WidgetGroupsEntryComboBox\"", interaction, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding CanShowHotkeyWarning, Mode=OneWay, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            interaction,
            StringComparison.Ordinal);

        Assert.Contains("ItemsSource=\"{x:Bind FileStack.AvailableOpenModeOptions, Mode=OneWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsComboBox.Value=\"{x:Bind FileStack.OpenMode, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
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
