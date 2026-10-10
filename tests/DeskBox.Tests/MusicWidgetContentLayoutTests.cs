using DeskBox.Controls.WidgetContents;
using DeskBox.Services;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBox.Tests;

public sealed class MusicWidgetContentLayoutTests
{
    [Theory]
    [InlineData(150, 150)]
    [InlineData(179.9, 240)]
    [InlineData(320, 179.9)]
    public void ShouldUseMinimalLayout_UsesCoverLayoutBelow180(double width, double height)
    {
        Assert.True(MusicWidgetContent.ShouldUseMinimalLayout(width, height));
    }

    [Theory]
    [InlineData(180, 180)]
    [InlineData(320, 190)]
    [InlineData(400, 260)]
    public void ShouldUseMinimalLayout_UsesFullControlsFrom180(double width, double height)
    {
        Assert.False(MusicWidgetContent.ShouldUseMinimalLayout(width, height));
    }

    [Theory]
    [InlineData(150, 150)]
    [InlineData(400, 260)]
    public void ShouldUseMinimalLayout_CoverModeAlwaysUsesCover(double width, double height)
    {
        Assert.True(MusicWidgetContent.ShouldUseMinimalLayout(
            width,
            height,
            SettingsService.MusicDisplayModeCover));
    }

    [Theory]
    [InlineData(150, 150)]
    [InlineData(400, 260)]
    public void ShouldUseMinimalLayout_ControlsModeAlwaysUsesControls(double width, double height)
    {
        Assert.False(MusicWidgetContent.ShouldUseMinimalLayout(
            width,
            height,
            SettingsService.MusicDisplayModeControls));
    }

    [Theory]
    [InlineData(180, 28)]
    [InlineData(250, 30)]
    [InlineData(320, 32)]
    [InlineData(480, 32)]
    public void ResolveTransportButtonSize_UsesOneResponsiveSizeForEveryControl(
        double width,
        double expected)
    {
        Assert.Equal(expected, MusicWidgetContent.ResolveTransportButtonSize(width));
    }

    [Theory]
    [InlineData(219.9, false)]
    [InlineData(220, true)]
    public void ShouldShowHorizontalVolumeControl_ProtectsTheThreeTransportButtons(
        double width,
        bool expected)
    {
        Assert.Equal(expected, MusicWidgetContent.ShouldShowHorizontalVolumeControl(width));
    }

    [Theory]
    [InlineData(291.9, false)]
    [InlineData(292, true)]
    public void ShouldShowHorizontalPlaybackModeControl_RequiresWideLayout(
        double width,
        bool expected)
    {
        Assert.Equal(expected, MusicWidgetContent.ShouldShowHorizontalPlaybackModeControl(width));
    }

    [Theory]
    [InlineData(150, 138)]
    [InlineData(180, 168)]
    [InlineData(240, 228)]
    [InlineData(250, 238)]
    [InlineData(480, 238)]
    public void ResolveInlineVolumePanelWidth_StaysInsideEveryWidgetWidth(
        double width,
        double expected)
    {
        Assert.Equal(expected, MusicWidgetContent.ResolveInlineVolumePanelWidth(width));
    }

    [Theory]
    [InlineData(150, 138)]
    [InlineData(240, 228)]
    [InlineData(320, 280)]
    public void ResolveSourceFlyoutMaxWidth_StaysInsideTheWidget(
        double width,
        double expected)
    {
        Assert.Equal(expected, MusicWidgetContent.ResolveSourceFlyoutMaxWidth(width));
    }

    [Theory]
    [InlineData(150, 138)]
    [InlineData(240, 228)]
    public void ResolveSourceFlyoutMaxHeight_StaysInsideTheWidget(
        double height,
        double expected)
    {
        Assert.Equal(expected, MusicWidgetContent.ResolveSourceFlyoutMaxHeight(height));
    }

    [Fact]
    public void InlineVolumePanel_UsesTheRootBlankSurfaceAndHandledPointerRoute()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));

        Assert.Contains("x:Name=\"RootGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PointerPressed=\"RootGrid_PointerPressed\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("RootGrid.AddHandler(", code, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", code, StringComparison.Ordinal);
        Assert.Contains("RootGrid.RemoveHandler(", code, StringComparison.Ordinal);
        Assert.Contains("UpdateInlineVolumePanelLayout(width);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SourcePicker_ExistsInAllFourLayoutsAndUsesAnImmediateNativeMenu()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));
        string icon = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicSourceIcon.xaml"));

        Assert.Contains("x:Name=\"MinimalSourceButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SourceButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordSourceButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordHorizontalSourceButton\"", xaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(xaml, "Click=\"SourceButton_Click\""));
        Assert.Equal(4, CountOccurrences(xaml, "<local:MusicSourceIcon"));
        Assert.DoesNotContain("{Binding SourcePickerTooltip}", xaml, StringComparison.Ordinal);

        Assert.Contains("new RadioMenuFlyoutItem", code, StringComparison.Ordinal);
        Assert.Contains("MenuFlyoutPresenterStyle = CreateSourceFlyoutPresenterStyle()", code, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(button, label);", code, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip(button, label);", code, StringComparison.Ordinal);
        Assert.Contains("flyout.Hide();", code, StringComparison.Ordinal);
        Assert.Contains("++_sourceFlyoutGeneration;", code, StringComparison.Ordinal);
        Assert.Contains("ResolveSourceFlyoutMaxWidth(width)", code, StringComparison.Ordinal);
        Assert.Contains("ResolveSourceFlyoutMaxHeight(height)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentDialog", code, StringComparison.Ordinal);

        Assert.Contains("Fluent System Icons: LiveFilled", icon, StringComparison.Ordinal);
        Assert.Contains("M4.34239 4.34305", icon, StringComparison.Ordinal);
        Assert.Contains("Fill=\"{x:Bind Foreground, Mode=OneWay}\"", icon, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceSelection_PreservesFollowSystemModeUntilTheUserPinsASource()
    {
        string mediaInfoCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/MusicWidgetViewModel.MediaInfo.cs"));
        string playbackCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/MusicWidgetViewModel.Playback.cs"));

        Assert.DoesNotContain("_preferredSessionId ??=", mediaInfoCode, StringComparison.Ordinal);
        Assert.Contains("SelectSessionAsync(string? sessionId)", playbackCode, StringComparison.Ordinal);
        Assert.Contains("_preferredSessionId = null;", playbackCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SourcePickerLocalization_ExistsInEveryJsonLocale()
    {
        string stringsDirectory = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] localePaths = Directory
            .EnumerateFiles(stringsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] requiredKeys =
        [
            "Music.SwitchSource",
            "Music.FollowSystemSource",
            "Music.NoAvailableSources"
        ];

        Assert.NotEmpty(localePaths);
        foreach (string localePath in localePaths)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(localePath));
            foreach (string key in requiredKeys)
            {
                Assert.True(
                    document.RootElement.TryGetProperty(key, out JsonElement value),
                    $"{Path.GetFileName(localePath)} is missing {key}.");
                Assert.False(
                    string.IsNullOrWhiteSpace(value.GetString()),
                    $"{Path.GetFileName(localePath)} has an empty {key} value.");
            }
        }
    }

    [Fact]
    public void MusicTransportControls_UseFilledVectorPathsAndSubtleButtonStates()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string shellXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml"));
        string shellCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string transportXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicTransportIcon.xaml"));

        Assert.Equal(3, CountOccurrences(musicXaml, "Kind=\"Previous\""));
        Assert.Equal(4, CountOccurrences(musicXaml, "Kind=\"Play\""));
        Assert.Equal(4, CountOccurrences(musicXaml, "Kind=\"Pause\""));
        Assert.Equal(4, CountOccurrences(musicXaml, "Kind=\"Next\""));
        Assert.Equal(15, CountOccurrences(musicXaml, "<local:MusicTransportIcon"));
        Assert.DoesNotContain("MusicTransportFontIconStyle", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE768;\"", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE769;\"", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE892;\"", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE893;\"", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"MinimalPreviousButton\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MinimalControlPanel\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SystemControlAcrylicElementBrush}\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"40\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Padding=\"6,5\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("TextAlignment=\"Left\"", musicXaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(musicXaml, "<Grid Width=\"26\" Height=\"26\">"));
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"14\" />", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SubtleFillColorSecondaryBrush}\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SubtleFillColorTertiaryBrush}\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"4\" />", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MusicPlayVectorIconStyle", musicXaml, StringComparison.Ordinal);

        Assert.Equal(3, CountOccurrences(shellXaml, "<widgetContents:MusicTransportIcon"));
        Assert.Contains("Kind=\"Previous\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("Kind=\"Next\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactPlayPauseIcon\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("MusicTransportIconKind.Pause", shellCode, StringComparison.Ordinal);
        Assert.Contains("MusicTransportIconKind.Play", shellCode, StringComparison.Ordinal);

        Assert.Contains("Width=\"26\"", transportXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"26\"", transportXaml, StringComparison.Ordinal);
        Assert.Contains("UseLayoutRounding=\"True\"", transportXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Viewbox", transportXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"120\"", transportXaml, StringComparison.Ordinal);
        Assert.Contains("Data=\"M20 14.5L9 20.75", transportXaml, StringComparison.Ordinal);
        Assert.Contains("Data=\"M9.5 14.5L17.75 19.25", transportXaml, StringComparison.Ordinal);
        Assert.Contains("Data=\"M16.25 14.5L8.125 19.25", transportXaml, StringComparison.Ordinal);
        Assert.Equal(7, CountOccurrences(transportXaml, "Fill=\"{x:Bind Foreground, Mode=OneWay}\""));
        Assert.DoesNotContain("Stroke=", transportXaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeAotBindingProvider_CoversEveryMusicDataContextBinding()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string bindableSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/MusicWidgetViewModel.AotBindableProperties.cs"));

        string[] dataContextPaths = Regex.Matches(
                musicXaml,
                @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)(?<options>[^}]*)\}")
            .Where(match => !match.Groups["options"].Value.Contains(
                "ElementName=",
                StringComparison.Ordinal))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // 35 paths: the system volume slider's accessible name binds
        // SystemVolumeLabel, which is registered in the AOT bindable list.
        Assert.Equal(35, dataContextPaths.Length);
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", bindableSource, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class MusicWidgetViewModel", bindableSource, StringComparison.Ordinal);
        foreach (string path in dataContextPaths)
        {
            Assert.Contains($"nameof({path})", bindableSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NativeAotBindingProvider_CoversWidgetTitleAndRenameRefresh()
    {
        string shellXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml"));
        string contentWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string commands = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));

        Assert.Contains("LabelText=\"{Binding DisplayName}\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayName}\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", contentWindow, StringComparison.Ordinal);
        Assert.Contains("private sealed partial class ContentWidgetTitleViewModel", contentWindow, StringComparison.Ordinal);
        Assert.Contains("nameof(DisplayName)", contentWindow, StringComparison.Ordinal);
        Assert.Contains("nameof(TitleIconSize)", contentWindow, StringComparison.Ordinal);
        Assert.Contains("nameof(TitleTextSize)", contentWindow, StringComparison.Ordinal);
        Assert.Contains("ContentWidgetShell.DataContext = _titleViewModel", contentWindow, StringComparison.Ordinal);
        Assert.Contains("_titleViewModel.RefreshDisplayName()", commands, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}
