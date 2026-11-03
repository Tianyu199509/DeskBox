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
        string musicCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));
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
        Assert.Contains("Background=\"{ThemeResource MusicFrostedBarScrimBrush}\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource MusicLyricsCoverContrastBrush}\"", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemControlAcrylicElementBrush", musicXaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"40\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Padding=\"6,5\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("TextAlignment=\"Left\"", musicXaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(musicXaml, "<Grid Width=\"26\" Height=\"26\">"));
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"14\" />", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SubtleFillColorSecondaryBrush}\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SubtleFillColorTertiaryBrush}\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"6\" />", musicXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"8\" />", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"CornerRadius\" Value=\"18\" />", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("size / 2.0", musicCode, StringComparison.Ordinal);
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

        // 36 paths: the system volume slider's accessible name binds
        // SystemVolumeLabel, and the timeline row hides through
        // TimelineRowVisibility — both registered in the AOT bindable list.
        Assert.Equal(36, dataContextPaths.Length);
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

    [Fact]
    public void LyricsPresentation_ExistsAcrossAllStylesAndRespectsMasterToggle()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string musicCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));
        string contentWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string settingsXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsWindow.xaml"));

        // Redundant lyrics icon buttons are removed from all four layouts;
        // entering the frosted lyrics overlay happens directly by tapping the
        // inline lyric row or album/vinyl artwork.
        Assert.DoesNotContain("MinimalLyricsButton", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LyricsToggleButton", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordLyricsButton", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordHorizontalLyricsButton", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LyricsToggleButton_Click", musicXaml, StringComparison.Ordinal);

        // Cover / MinimalLayout: left-aligned lyric floats over the cover scrim outside MinimalControlPanel
        int lyricRowIndex = musicXaml.IndexOf("x:Name=\"MinimalLyricRow\"", StringComparison.Ordinal);
        int controlPanelIndex = musicXaml.IndexOf("x:Name=\"MinimalControlPanel\"", StringComparison.Ordinal);
        Assert.True(lyricRowIndex > 0 && lyricRowIndex < controlPanelIndex);
        Assert.Contains("x:Name=\"MinimalCoverLyricScrim\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MinimalLyricMarqueeHost\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MinimalLyricText\"", musicXaml, StringComparison.Ordinal);

        // Controls / Standard ContentGrid
        Assert.Contains("x:Name=\"ControlsLyricRow\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ControlsLyricMarqueeHost\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SubtitleTextBlock\"", musicXaml, StringComparison.Ordinal);

        // RecordVertical / RecordLayout
        Assert.Contains("x:Name=\"RecordLyricRow\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordLyricMarqueeHost\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordLyricText\"", musicXaml, StringComparison.Ordinal);

        // RecordHorizontal / RecordHorizontalLayout
        Assert.Contains("x:Name=\"RecordHorizontalLyricRow\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordHorizontalLyricMarqueeHost\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordHorizontalLyricText\"", musicXaml, StringComparison.Ordinal);

        // Compositor marquee for overflowing single-line lyrics + pure native-material poster lyrics mode
        Assert.Contains("UpdateLyricMarquee", musicCode, StringComparison.Ordinal);
        Assert.Contains("ApplyActivePlayerLayoutVisibility", musicCode, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LyricsOverlayPanel\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LyricsOverlayClipHost\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Tapped=\"LyricsOverlayPanel_Tapped\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("DoubleTapped=\"LyricLineItem_DoubleTapped\"", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BlurredThumbnailImage", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LyricsOffsetPill", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LyricsCloseButton_Click", musicXaml, StringComparison.Ordinal);
        Assert.Equal(8, CountOccurrences(musicXaml, "Tapped=\"ArtworkLyricsTrigger_Tapped\""));
        Assert.Equal(4, CountOccurrences(musicXaml, "Visibility=\"{x:Bind ViewModel.InlineLyricVisibility, Mode=OneWay}\""));

        // Capsule compact mode gates on ShowLyrics && ShowCapsuleLyrics
        Assert.Contains("music.ViewModel.ShowLyrics &&", contentWindow, StringComparison.Ordinal);
        Assert.Contains("music.ViewModel.ShowCapsuleLyrics &&", contentWindow, StringComparison.Ordinal);

        // Settings master switch is prominently placed in MusicSettingsSection
        Assert.Contains("svc:Localized.HeaderKey=\"Settings.Music.ShowLyrics.Title\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{x:Bind ShowLyrics, Mode=TwoWay}\"", settingsXaml, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}
