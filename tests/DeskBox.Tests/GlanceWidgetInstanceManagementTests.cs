using DeskBox.Models;
using DeskBox.Services;
using System.Text.Json;

namespace DeskBox.Tests;

public sealed class GlanceWidgetInstanceManagementTests
{
    [Fact]
    public void MasterState_EnablesAndDisablesEveryGlanceInstance()
    {
        WidgetConfig[] configs =
        [
            new() { WidgetKind = WidgetKind.Glance, IsVisible = false, IsDisabled = true },
            new() { WidgetKind = WidgetKind.Glance, IsVisible = false, IsDisabled = true }
        ];

        WidgetManager.ApplyGlanceMasterState(configs, enabled: true);

        Assert.All(configs, config =>
        {
            Assert.True(config.IsVisible);
            Assert.False(config.IsDisabled);
        });

        WidgetManager.ApplyGlanceMasterState(configs, enabled: false);

        Assert.All(configs, config =>
        {
            Assert.False(config.IsVisible);
            Assert.True(config.IsDisabled);
        });
    }

    [Fact]
    public void StartupDedup_PreservesEveryGlanceInstance()
    {
        Assert.False(WidgetManager.RequiresSingletonFeatureWidgetConfig(WidgetKind.Glance));
        Assert.False(WidgetManager.RequiresSingletonFeatureWidgetConfig(WidgetKind.File));
        Assert.True(WidgetManager.RequiresSingletonFeatureWidgetConfig(WidgetKind.Weather));
        Assert.True(WidgetManager.RequiresSingletonFeatureWidgetConfig(WidgetKind.Music));
    }

    [Fact]
    public void SettingsPage_ExposesOneInstanceManagerWithAllRequiredActions()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml"));
        string codeBehind = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"));
        string windowCommands = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string settingsMenu = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetSettingsMenuHelper.cs"));

        Assert.Contains("x:Name=\"InstanceManagerCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InstanceComboBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InstanceEnabledToggle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AddInstanceButton_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("DuplicateInstanceMenuItem_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("DeleteInstanceMenuItem_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("RenameInstanceMenuItem_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("LocateInstanceMenuItem_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("GlanceWidgetStore.ForWidget(selected!.Id)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SetGlanceWidgetInstanceEnabledAsync", windowCommands, StringComparison.Ordinal);
        Assert.Contains("ShowGlanceSettings(widgetId)", settingsMenu, StringComparison.Ordinal);
    }

    [Fact]
    public void RightClickClose_UsesStableShellAnchorAndNamesTheGlanceInstance()
    {
        string windowCommands = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string featureWidgets = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"));
        using JsonDocument chineseStrings = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Strings/zh-CN.json")));

        Assert.Contains(
            "private MenuFlyout CreateMoreFlyout()",
            windowCommands,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowCloseWidgetFlyout(ContentWidgetShell)",
            windowCommands,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DispatcherQueue.TryEnqueue(ShowCloseWidgetFlyout)",
            windowCommands,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text = GetFeatureWidgetCloseMenuText()",
            windowCommands,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (enabled && !GetFeatureWidgetEnabledState(WidgetKind.Glance))",
            featureWidgets,
            StringComparison.Ordinal);
        Assert.Equal(
            "关闭格子“{0}”",
            chineseStrings.RootElement
                .GetProperty("Widget.FeatureWidget.DisableConfirmTitle")
                .GetString());
    }

    [Fact]
    public void SettingsPage_UsesAotSafeNativeComboBoxItems()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml"));
        string codeBehind = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"));

        Assert.DoesNotContain("DisplayMemberPath=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(".ItemsSource =", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private sealed partial class SelectionItem : ComboBoxItem", codeBehind, StringComparison.Ordinal);
        Assert.Contains("comboBox.Items.Add(new SelectionItem(label, value))", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SynchronizeInstanceOptions", codeBehind, StringComparison.Ordinal);
        Assert.Contains("InstanceComboBox.Items.Add(new SelectionItem(instance.Name, instance.Id))", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_InstanceSwitchLoadsOnlyTheSelectedStore()
    {
        string codeBehind = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"));
        string selectionHandler = ExtractMethod(
            codeBehind,
            "private async void InstanceComboBox_SelectionChanged",
            "private async void InstanceEnabledToggle_Toggled");

        Assert.Contains("_instanceSelectionGate.WaitAsync()", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("selectionVersion != _instanceSelectionVersion", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("LoadSelectedInstanceAsync(selectedWidgetId, selectionVersion)", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshInstancesAsync(", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("Items.Clear()", selectionHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("PopulateOptions()", selectionHandler, StringComparison.Ordinal);
        Assert.Contains("private void EnsureOptionsPopulated()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_optionsLocalizationSignature", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_RefreshesExternalInstanceStateAndOnlyCollapsesSecondaryGroups()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml"));
        string codeBehind = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"));

        var document = System.Xml.Linq.XDocument.Parse(xaml);
        System.Xml.Linq.XElement[] expanders = document.Descendants()
            .Where(element => string.Equals(
                element.Name.LocalName,
                "SettingsExpander",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, expanders.Length);
        System.Xml.Linq.XElement typography = Assert.Single(expanders.Where(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Localized.HeaderKey" &&
                attribute.Value == "Glance.Typography.Font")));
        System.Xml.Linq.XElement background = Assert.Single(expanders.Where(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Localized.HeaderKey" &&
                attribute.Value == "Glance.Background.Title")));
        Assert.True(ContainsNamedElement(typography, "TimeScaleSlider"));
        Assert.True(ContainsNamedElement(background, "RotationComboBox"));
        Assert.True(ContainsNamedElement(background, "RandomOrderToggle"));
        Assert.True(ContainsNamedElement(background, "CacheSizeText"));

        string[] orderedControlNames =
        [
            "BackgroundSourceComboBox",
            "FontComboBox",
            "DisplayContentDropDownButton",
            "LayoutComboBox",
        ];
        int previousControlIndex = -1;
        foreach (string controlName in orderedControlNames)
        {
            int controlIndex = xaml.IndexOf($"x:Name=\"{controlName}\"", StringComparison.Ordinal);
            Assert.True(controlIndex > previousControlIndex, $"{controlName} is not in the expected settings row order.");
            previousControlIndex = controlIndex;
        }

        string[] fixedWidthSelectorNames =
        [
            "DisplayContentDropDownButton",
            "BackgroundSourceComboBox",
            "OnlineImageCategoryComboBox",
            "RotationComboBox",
            "FontComboBox",
            "LayoutComboBox",
            "CalendarMaterialComboBox",
            "TraditionalCalendarComboBox",
            "TransitionComboBox",
            "SpeedComboBox",
            "ReadabilityComboBox",
        ];
        foreach (string controlName in fixedWidthSelectorNames)
        {
            System.Xml.Linq.XElement control = Assert.Single(document.Descendants().Where(element =>
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name" &&
                    attribute.Value == controlName)));
            Assert.Equal("220", control.Attribute("Width")?.Value);
        }

        Assert.Contains("svc:Localized.HeaderKey=\"Glance.Typography.Font\"", xaml, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.HeaderKey=\"Glance.Typography.Title\"", xaml, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.HeaderKey=\"Glance.Background.Title\"", xaml, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.HeaderKey=\"Glance.Speed.Title\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glance.LayoutGroup.Title", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glance.AppearanceGroup.Title", xaml, StringComparison.Ordinal);
        Assert.Contains("SettingsChanged += OnAppSettingsChanged", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SettingsChanged -= OnAppSettingsChanged", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateInstanceStateSignature", codeBehind, StringComparison.Ordinal);
    }

    private static bool ContainsNamedElement(
        System.Xml.Linq.XElement container,
        string name)
    {
        return container.Descendants().Any(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" &&
                attribute.Value == name));
    }

    private static string ExtractMethod(
        string source,
        string methodStart,
        string nextMethodStart)
    {
        int start = source.IndexOf(methodStart, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method start: {methodStart}");
        int end = source.IndexOf(nextMethodStart, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find method boundary: {nextMethodStart}");
        return source[start..end];
    }
}
