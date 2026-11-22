using DeskBox.Controls.WidgetContents;
using DeskBox.Services;
using Microsoft.UI.Xaml;
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
    public void MusicTransportControls_UseNativeFluentSolidBoldGlyphsAndSubtleButtonStates()
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
        string transportCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicTransportIcon.xaml.cs"));

        Assert.Equal("\uF8AC", MusicTransportIcon.PreviousGlyph);
        Assert.Equal("\uF5B0", MusicTransportIcon.PlayGlyph);
        Assert.Equal("\uF8AE", MusicTransportIcon.PauseGlyph);
        Assert.Equal("\uF8AD", MusicTransportIcon.NextGlyph);

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

        Assert.Contains("<FontIcon", transportXaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"{ThemeResource SymbolThemeFontFamily}\"", transportXaml, StringComparison.Ordinal);
        Assert.Contains("UseLayoutRounding=\"True\"", transportXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Viewbox", transportXaml, StringComparison.Ordinal);
        Assert.Contains("\\uF8AC", transportCode, StringComparison.Ordinal);
        Assert.Contains("\\uF5B0", transportCode, StringComparison.Ordinal);
        Assert.Contains("\\uF8AE", transportCode, StringComparison.Ordinal);
        Assert.Contains("\\uF8AD", transportCode, StringComparison.Ordinal);
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

        // 32 paths: SystemVolumeLabel and TimelineRowVisibility are bound in XAML,
        // while InlineSystemVolumeSlider.Value is synchronized imperatively after
        // reading the OS audio snapshot so opening the panel never pushes default 0.5.
        Assert.Equal(32, dataContextPaths.Length);
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
        string marqueeCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.Marquee.cs"));
        Assert.Contains("UpdateLyricMarquee", marqueeCode, StringComparison.Ordinal);
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

    [Fact]
    public void Win11NativeVisualPolish_UsesFrostedAcrylicLyricsCenteredLinesAndCoordinatedVolumeBubble()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string musicCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));
        string viewModelCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/MusicWidgetViewModel.cs"));

        // Frosted glass mask on fullscreen lyrics + centered alignment + edge fade
        Assert.Contains("x:Key=\"MusicLyricsCoverContrastBrush\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("<AcrylicBrush", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LyricsOverlayFrostLayer\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LyricsOverlayAmbientWash\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("TextAlignment=\"Center\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("UpdateLyricsViewportEdgeAttenuation", musicCode, StringComparison.Ordinal);

        // Win11 native volume bubble: acrylic surface, mute toggle button + centered slider + clean integer
        Assert.Contains("x:Key=\"MusicVolumeBubbleAcrylicBrush\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InlineSystemMuteButton\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InlineSessionMuteButton\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SystemMuteButton_Click\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("SyncInlineVolumeSliderAccent", musicCode, StringComparison.Ordinal);

        // Elevated inline lyric typography above secondary artist text
        Assert.Contains("LyricTextSize => Math.Min(SettingsService.MaxTextSize + 1.5, TextSize + 0.5);", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("MinimalLyricTextSize => Math.Min(SettingsService.MaxTextSize + 1.5, TextSize + 0.5);", viewModelCode, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("QQMusic.exe", "QQ音乐", "QQMusic.exe")]
    [InlineData("Tencent.QQMusic", "QQ音乐", "QQMusic.exe")]
    [InlineData("com.netease.cloudmusic", "网易云音乐", "cloudmusic.exe")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify", "Spotify.exe")]
    [InlineData("Brave", "Brave", "brave.exe")]
    [InlineData("Chrome", "Google Chrome", "chrome.exe")]
    [InlineData("MSEdge", "Microsoft Edge", "msedge.exe")]
    [InlineData("308046B0AF4A39CB", "Mozilla Firefox", "firefox.exe")]
    public void MusicSourceAppIconResolver_ResolvesExpectedCandidateExecutableNames(
        string aumid,
        string displayName,
        string expectedExe)
    {
        IReadOnlyList<string> candidates = MusicSourceAppIconResolver.GetCandidateExecutableNames(aumid, displayName);
        Assert.Contains(candidates, item => string.Equals(item, expectedExe, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SourceChip_DisplaysSourceAppIconAndStealsZeroDedicatedRowsAcrossLayouts()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string musicCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));

        // Source app icon bound in all 4 layouts + fallback icon preserved
        Assert.Equal(4, CountOccurrences(musicXaml, "Source=\"{x:Bind ViewModel.SourceAppIcon, Mode=OneWay}\""));
        Assert.Equal(4, CountOccurrences(musicXaml, "Visibility=\"{x:Bind ViewModel.SourceAppIconVisibility, Mode=OneWay}\""));
        Assert.Equal(4, CountOccurrences(musicXaml, "Visibility=\"{x:Bind ViewModel.SourceFallbackIconVisibility, Mode=OneWay}\""));
        Assert.Equal(4, CountOccurrences(musicXaml, "<local:MusicSourceIcon"));
        Assert.Contains("PopulateSourceFlyoutItemIconAsync", musicCode, StringComparison.Ordinal);

        // Dedicated full-width source header rows removed so cover/vinyl/text never shrink for the source chip
        Assert.DoesNotContain("ControlsSourceHeaderRow", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordSourceHeaderRow", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordHorizontalSourceHeaderRow", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceHeaderHeight", musicCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineVolumePanel_StaysOpenDuringClicksAndSliderDrags()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string musicCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));

        Assert.Contains("InlineVolumePanel.AddHandler(", musicCode, StringComparison.Ordinal);
        Assert.Contains("IsPointerPointInside(e, InlineVolumePanel)", musicCode, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(current, target) || Equals(current, target)", musicCode, StringComparison.Ordinal);
        Assert.Contains("InlineSystemVolumeSlider?.PointerCaptures is { Count: > 0 }", musicCode, StringComparison.Ordinal);
        Assert.DoesNotContain("<ThemeShadow", musicXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineVolumePanel_NeverPushesDefaultVolumeBeforeSnapshotAndLayoutReady()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string sessionCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/MusicSessionService.cs"));

        int panelStart = musicXaml.IndexOf("x:Name=\"InlineVolumePanel\"", StringComparison.Ordinal);
        Assert.True(panelStart >= 0);
        int panelTagEnd = musicXaml.IndexOf('>', panelStart);
        Assert.True(panelTagEnd > panelStart);
        string panelTag = musicXaml[panelStart..panelTagEnd];
        Assert.Contains("Visibility=\"Collapsed\"", panelTag, StringComparison.Ordinal);

        int systemSliderStart = musicXaml.IndexOf("x:Name=\"InlineSystemVolumeSlider\"", StringComparison.Ordinal);
        Assert.True(systemSliderStart >= 0);
        int systemSliderEnd = musicXaml.IndexOf("/>", systemSliderStart, StringComparison.Ordinal);
        Assert.True(systemSliderEnd > systemSliderStart);
        string systemSliderTag = musicXaml[systemSliderStart..systemSliderEnd];
        Assert.DoesNotContain("Value=", systemSliderTag, StringComparison.Ordinal);

        Assert.False(MusicWidgetContent.ShouldApplyInlineVolumeSliderChange(
            isRefreshing: true,
            isReadyForUserInput: true,
            isPanelOpen: true,
            Visibility.Visible,
            hasViewModel: true));
        Assert.False(MusicWidgetContent.ShouldApplyInlineVolumeSliderChange(
            isRefreshing: false,
            isReadyForUserInput: false,
            isPanelOpen: true,
            Visibility.Visible,
            hasViewModel: true));
        Assert.False(MusicWidgetContent.ShouldApplyInlineVolumeSliderChange(
            isRefreshing: false,
            isReadyForUserInput: true,
            isPanelOpen: true,
            Visibility.Collapsed,
            hasViewModel: true));
        Assert.True(MusicWidgetContent.ShouldApplyInlineVolumeSliderChange(
            isRefreshing: false,
            isReadyForUserInput: true,
            isPanelOpen: true,
            Visibility.Visible,
            hasViewModel: true));

        // SMTC WinRT activation must stay isolated out-of-process in MediaSessionProxyClient.
        Assert.Contains("MediaSessionProxyClient", sessionCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows.Media.Control", sessionCode, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalSystemMediaTransportControls", sessionCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineLyricMarquee_UsesStretchGridVerticalCenteringAndNonLinearCubicBezierGlide()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string marqueeCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.Marquee.cs"));

        // Never bind marquee track Height to ActualHeight (non-notifying DP in WinUI 3 causes 0-height vertical half-clipping).
        Assert.DoesNotContain("Height=\"{Binding ActualHeight", musicXaml, StringComparison.Ordinal);
        // 11 marquee viewports total: 4 Title + 3 Artist + 4 Lyric across all layouts.
        Assert.Equal(11, CountOccurrences(musicXaml, "Rect=\"0,-12,4096,128\""));
        Assert.Contains("CreateHorizontalViewportClipRect(", marqueeCode, StringComparison.Ordinal);

        // Overflowing Title, Artist, and Lyric share the non-linear CubicBezier S-curve glide without Clone snap jumps.
        int startGlideIdx = marqueeCode.IndexOf("private static bool TryStartOverflowGlideAnimation(", StringComparison.Ordinal);
        Assert.True(startGlideIdx >= 0);
        int stopCanvasIdx = marqueeCode.IndexOf("private static void StopAnimatedCanvas(", startGlideIdx, StringComparison.Ordinal);
        Assert.True(stopCanvasIdx > startGlideIdx);
        string glideBody = marqueeCode[startGlideIdx..stopCanvasIdx];

        Assert.Contains("CreateCubicBezierEasingFunction(", glideBody, StringComparison.Ordinal);
        Assert.Contains("new Vector2(0.42f, 0.0f)", glideBody, StringComparison.Ordinal);
        Assert.Contains("new Vector2(0.18f, 1.0f)", glideBody, StringComparison.Ordinal);
        Assert.Contains("elements.Clone.Visibility = Visibility.Collapsed;", glideBody, StringComparison.Ordinal);
        Assert.Contains("AnimationStopBehavior.LeaveCurrentValue", glideBody, StringComparison.Ordinal);
        Assert.DoesNotContain("elements.Canvas.Translation = new Vector3(", glideBody, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateLinearEasingFunction", glideBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LyricsTypography_UsesNormalWeightInlineAcrossAllLayoutsAndSemiBoldActiveOverlay()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string musicCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));

        int minimalLyricStart = musicXaml.IndexOf("x:Name=\"MinimalLyricRow\"", StringComparison.Ordinal);
        int minimalSpectrumStart = musicXaml.IndexOf("x:Name=\"MinimalSpectrumBar\"", minimalLyricStart, StringComparison.Ordinal);
        string minimalLyricXaml = musicXaml[minimalLyricStart..minimalSpectrumStart];
        Assert.DoesNotContain("FontWeight=\"SemiBold\"", minimalLyricXaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(minimalLyricXaml, "FontWeight=\"Normal\""));

        int recordLyricStart = musicXaml.IndexOf("x:Name=\"RecordLyricRow\"", StringComparison.Ordinal);
        int recordProgressStart = musicXaml.IndexOf("x:Name=\"RecordProgressRow\"", recordLyricStart, StringComparison.Ordinal);
        string recordLyricXaml = musicXaml[recordLyricStart..recordProgressStart];
        Assert.DoesNotContain("FontWeight=\"Medium\"", recordLyricXaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(recordLyricXaml, "FontWeight=\"Normal\""));

        int horizLyricStart = musicXaml.IndexOf("x:Name=\"RecordHorizontalLyricRow\"", StringComparison.Ordinal);
        int horizProgressStart = musicXaml.IndexOf("x:Name=\"RecordHorizontalProgressHost\"", horizLyricStart, StringComparison.Ordinal);
        string horizLyricXaml = musicXaml[horizLyricStart..horizProgressStart];
        Assert.DoesNotContain("FontWeight=\"Medium\"", horizLyricXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HasActiveSyncedLyricVisibility", horizLyricXaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(horizLyricXaml, "FontWeight=\"Normal\""));

        int overlayStyleStart = musicCode.IndexOf("private void ApplyLyricContainerStyle(", StringComparison.Ordinal);
        int overlayStyleEnd = musicCode.IndexOf("private static Grid? FindDescendantGrid(", overlayStyleStart, StringComparison.Ordinal);
        string overlayStyleBody = musicCode[overlayStyleStart..overlayStyleEnd];
        Assert.DoesNotContain("FontWeights.Bold", overlayStyleBody, StringComparison.Ordinal);
        Assert.Contains("FontWeights.SemiBold", overlayStyleBody, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveSpectrum_ScalesFullWidthAndSoftCropsNarrowWithoutWaveCompression()
    {
        // Narrow viewport (< 256px) keeps baseline 15 anchors (uncompressed virtual wavelength)
        Assert.Equal(15, MusicSpectrumControl.ResolveAuroraAnchorCount(150));
        Assert.Equal(15, MusicSpectrumControl.ResolveAuroraAnchorCount(256));

        // Wide viewports increase odd anchor count proportionally to keep physical curvature constant
        int wide520 = MusicSpectrumControl.ResolveAuroraAnchorCount(520);
        int ultraWide860 = MusicSpectrumControl.ResolveAuroraAnchorCount(860);
        Assert.True(wide520 > 15 && (wide520 & 1) == 1);
        Assert.True(ultraWide860 > wide520 && (ultraWide860 & 1) == 1);

        // Out-of-viewport cropped points sit cleanly on baseline (0 amplitude), while center preserves full envelope
        Assert.Equal(0.0, MusicSpectrumControl.ComputeAuroraEnvelope(0.15, -12.0, 160.0), precision: 6);
        Assert.Equal(0.0, MusicSpectrumControl.ComputeAuroraEnvelope(0.85, 172.0, 160.0), precision: 6);
        double nearLeftEdge = MusicSpectrumControl.ComputeAuroraEnvelope(0.25, 5.0, 160.0);
        double center = MusicSpectrumControl.ComputeAuroraEnvelope(0.50, 80.0, 160.0);
        Assert.True(nearLeftEdge > 0.0 && nearLeftEdge < center);
        Assert.Equal(1.0, center, precision: 6);
    }

    [Theory]
    [InlineData(240, 320, SettingsService.MusicSpectrumStyleVinylRadialHalo)]
    [InlineData(280, 360, SettingsService.MusicSpectrumStyleAuroraSilk)]
    [InlineData(210, 260, SettingsService.MusicSpectrumStyleSymmetricWave)]
    [InlineData(240, 320, SettingsService.MusicSpectrumStylePeakHoldBars)]
    public void RecordVerticalLayout_ReservesConsistentVinylSizeWithoutEmptyStateBallooning(
        double width,
        double height,
        string spectrumStyle)
    {
        double sizeWithLyrics = MusicWidgetContent.CalculateRecordVinylSize(
            width,
            height,
            showLyricsSlot: true,
            spectrumStyle);
        Assert.InRange(sizeWithLyrics, 52, 210);
        // Must leave room for controls/title/timeline/halo rather than inflating to 230px in empty state.
        Assert.True(sizeWithLyrics <= height - 120);

        if (spectrumStyle != SettingsService.MusicSpectrumStyleVinylRadialHalo)
        {
            double sizeWithSpectrumOff = MusicWidgetContent.CalculateRecordVinylSize(
                width,
                height,
                showLyricsSlot: true,
                SettingsService.MusicSpectrumStyleOff);
            Assert.Equal(sizeWithSpectrumOff, sizeWithLyrics);
        }
    }

    [Fact]
    public void RecordVerticalSpectrum_DocksToBottomEdgeAndPeakHoldCapsPreserveBottomAlignment()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string spectrumCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicSpectrumControl.cs"));

        int recordStart = musicXaml.IndexOf("x:Name=\"RecordLayout\"", StringComparison.Ordinal);
        int recordHorizStart = musicXaml.IndexOf("x:Name=\"RecordHorizontalLayout\"", recordStart, StringComparison.Ordinal);
        Assert.True(recordStart > 0 && recordHorizStart > recordStart);
        string recordXaml = musicXaml[recordStart..recordHorizStart];

        int progressIndex = recordXaml.IndexOf("x:Name=\"RecordProgressRow\"", StringComparison.Ordinal);
        int spectrumIndex = recordXaml.IndexOf("x:Name=\"RecordSpectrumBar\"", StringComparison.Ordinal);
        int controlsIndex = recordXaml.IndexOf("x:Name=\"RecordControlsPanel\"", StringComparison.Ordinal);
        Assert.True(progressIndex > 0 && spectrumIndex > progressIndex && controlsIndex > spectrumIndex);

        string spectrumTag = recordXaml[spectrumIndex..controlsIndex];
        Assert.Contains("Grid.Row=\"0\"", spectrumTag, StringComparison.Ordinal);
        Assert.Contains("Grid.RowSpan=\"5\"", spectrumTag, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Bottom\"", spectrumTag, StringComparison.Ordinal);

        Assert.Contains("ElementCompositionPreview.SetIsTranslationEnabled(cap, true);", spectrumCode, StringComparison.Ordinal);
        Assert.DoesNotContain("capVisual.Offset =", spectrumCode, StringComparison.Ordinal);
        Assert.DoesNotContain("capVis.Offset =", spectrumCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SpectrumAndAudioContracts_ReleaseTransientTimersAndSupportConsoleEndpointFallback()
    {
        string spectrumCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicSpectrumControl.cs"));
        string volumeCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/MusicVolumeService.cs"));
        string nativeVolumeCode = File.ReadAllText(TestPaths.FromRepository(
            "native/deskbox-native/src/music_volume.rs"));
        string musicCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));

        Assert.Contains("UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken)", spectrumCode, StringComparison.Ordinal);
        Assert.Contains("timer.Tick -= FrameTimer_Tick;", spectrumCode, StringComparison.Ordinal);
        Assert.Contains("PerformanceLogger.RecordTransientUiTimerCreated();", spectrumCode, StringComparison.Ordinal);
        Assert.Contains("PerformanceLogger.RecordTransientUiTimerReleased();", spectrumCode, StringComparison.Ordinal);

        Assert.Contains("MusicVolumeNativeBackend.GetSnapshot(", volumeCode, StringComparison.Ordinal);
        Assert.Contains("eConsole", nativeVolumeCode, StringComparison.Ordinal);
        Assert.Contains("RefreshInlineVolumeForActiveSourceAsync", musicCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlsLayout_UsesWin11TopHeaderRightThumbnailSubtleLyricsAndBelowBarTimestamps()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string musicCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));
        string viewModelCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/MusicWidgetViewModel.cs"));

        int contentStart = musicXaml.IndexOf("x:Name=\"ContentGrid\"", StringComparison.Ordinal);
        int recordStart = musicXaml.IndexOf("x:Name=\"RecordLayout\"", StringComparison.Ordinal);
        Assert.True(contentStart > 0 && recordStart > contentStart);
        string controlsXaml = musicXaml[contentStart..recordStart];

        // 1. Top utility bar: SourceButton left, PlaybackModeButton + VolumeButton right;
        //    PlaybackModeButton collapses directly when unsupported by the player.
        Assert.Contains("x:Name=\"ControlsTopBar\"", controlsXaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{x:Bind ViewModel.PlaybackModeButtonVisibility, Mode=OneWay}\"", controlsXaml, StringComparison.Ordinal);
        Assert.Contains("PlaybackModeButtonVisibility =>", viewModelCode, StringComparison.Ordinal);

        // 2. Body row: TrackInfoGrid on Column 0 (left), compact AlbumArtSurface on Column 1 (right).
        Assert.Contains("x:Name=\"ControlsBodyGrid\"", controlsXaml, StringComparison.Ordinal);
        int trackInfoIndex = controlsXaml.IndexOf("x:Name=\"TrackInfoGrid\"", StringComparison.Ordinal);
        int albumSurfaceIndex = controlsXaml.IndexOf("x:Name=\"AlbumArtSurface\"", StringComparison.Ordinal);
        Assert.True(trackInfoIndex > 0 && albumSurfaceIndex > trackInfoIndex);
        Assert.DoesNotContain("AlbumArtInnerBorder", controlsXaml, StringComparison.Ordinal);

        // 3. Inline lyric in Controls mode: no left "|" accent bar, 12px secondary size, ~50% opacity.
        int lyricStart = controlsXaml.IndexOf("x:Name=\"ControlsLyricRow\"", StringComparison.Ordinal);
        int lyricEnd = controlsXaml.IndexOf("x:Name=\"AlbumArtShadow\"", lyricStart, StringComparison.Ordinal);
        Assert.True(lyricStart > 0 && lyricEnd > lyricStart);
        string controlsLyricSection = controlsXaml[lyricStart..lyricEnd];
        Assert.DoesNotContain("HasActiveSyncedLyricVisibility", controlsLyricSection, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"{x:Bind ViewModel.ControlsSecondaryTextSize, Mode=OneWay}\"", controlsLyricSection, StringComparison.Ordinal);
        Assert.Contains("0.50", viewModelCode, StringComparison.Ordinal);

        // 4. Progress bar: slim 2.5px track with 10px low-opacity timestamps below both ends.
        Assert.Contains("x:Name=\"ControlsPositionText\"", controlsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ControlsDurationText\"", controlsXaml, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(controlsXaml, "FontSize=\"10\""));
        Assert.Contains("buttonOrigin.Y + anchor.ActualHeight + 6", musicCode, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}
