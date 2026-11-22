using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Services;
using DeskBox.Services.Audio;

namespace DeskBox.Tests;

public sealed class MusicSpectrumFftTests
{
    [Theory]
    [InlineData(85.0f, 0, 5)]      // Bass / kick fundamental -> low bands
    [InlineData(1100.0f, 10, 16)]  // Vocal / lead midrange -> mid bands
    [InlineData(8500.0f, 19, 23)]  // Hi-hat / treble air -> high bands
    public void ComputeBands_PureSineWave_ConcentratesEnergyInExpectedFrequencyBand(
        float frequencyHz,
        int expectedMinBand,
        int expectedMaxBand)
    {
        var fft = new MusicSpectrumFft();
        const int sampleRate = 48000;
        float[] pcm = GenerateSineWave(frequencyHz, amplitude: 0.75f, sampleRate, MusicSpectrumFft.FftSize);
        float[] bands = new float[MusicSpectrumFft.BandCount];

        fft.ComputeBands(
            pcm,
            sampleRate,
            isPlaying: true,
            captureAvailable: true,
            deltaTimeSeconds: 0.032f,
            bands);

        int peakBand = FindPeakIndex(bands);
        Assert.InRange(peakBand, expectedMinBand, expectedMaxBand);
        Assert.True(bands[peakBand] > 0.55f, $"Expected strong peak at band {peakBand}, got {bands[peakBand]:F3}.");
        Assert.False(fft.IsUsingProceduralFallback);
    }

    [Fact]
    public void ComputeBands_SustainedSilenceWhilePlaying_SmoothlyTransitionsToProceduralFallbackAndRecovers()
    {
        var fft = new MusicSpectrumFft();
        const int sampleRate = 48000;
        float[] silentPcm = new float[MusicSpectrumFft.FftSize];
        float[] bands = new float[MusicSpectrumFft.BandCount];

        // Simulate 1.6 seconds of WASAPI Exclusive / silent shared-mix while SMTC reports IsPlaying == true.
        for (int frame = 0; frame < 50; frame++)
        {
            fft.ComputeBands(
                silentPcm,
                sampleRate,
                isPlaying: true,
                captureAvailable: true,
                deltaTimeSeconds: 0.032f,
                bands);
        }

        Assert.True(fft.IsUsingProceduralFallback);
        float fallbackPeak = bands.Max();
        Assert.InRange(fallbackPeak, 0.08f, 0.58f);

        // Once shared-mode PCM audio returns, fallback must disengage within ~200ms.
        float[] livePcm = GenerateSineWave(440.0f, amplitude: 0.6f, sampleRate, MusicSpectrumFft.FftSize);
        for (int frame = 0; frame < 8; frame++)
        {
            fft.ComputeBands(
                livePcm,
                sampleRate,
                isPlaying: true,
                captureAvailable: true,
                deltaTimeSeconds: 0.032f,
                bands);
        }

        Assert.False(fft.IsUsingProceduralFallback);
        Assert.Equal(0.0f, fft.FallbackBlend, precision: 3);
        Assert.True(bands.Max() > 0.55f);
    }

    [Fact]
    public void ComputeBands_WhenCaptureUnavailableWhilePlaying_EngagesProceduralFallbackPromptly()
    {
        var fft = new MusicSpectrumFft();
        float[] emptyPcm = new float[MusicSpectrumFft.FftSize];
        float[] bands = new float[MusicSpectrumFft.BandCount];

        for (int frame = 0; frame < 24; frame++)
        {
            fft.ComputeBands(
                emptyPcm,
                sampleRate: 48000,
                isPlaying: true,
                captureAvailable: false,
                deltaTimeSeconds: 0.032f,
                bands);
        }

        Assert.True(fft.IsUsingProceduralFallback);
        Assert.Contains(bands, level => level > 0.12f);

        // Pausing immediately clears output bands.
        fft.ComputeBands(
            emptyPcm,
            sampleRate: 48000,
            isPlaying: false,
            captureAvailable: false,
            deltaTimeSeconds: 0.032f,
            bands);
        Assert.All(bands, level => Assert.Equal(0.0f, level));
    }

    [Fact]
    public void BandMappers_PreserveAcousticOrderingAndSeamless360HaloWrap()
    {
        float[] bands24 = new float[MusicSpectrumFft.BandCount];
        bands24[0] = 1.0f;
        bands24[1] = 0.9f;
        bands24[2] = 0.8f;
        bands24[3] = 0.75f;

        float[] wave24 = new float[24];
        MusicSpectrumFft.MapSymmetricWaveBars(bands24, wave24);

        // Bass/core energy maps to the middle of the symmetric wave strip while outer tips stay quiet.
        Assert.True(wave24[11] > 0.75f);
        Assert.True(wave24[12] > 0.75f);
        Assert.True(wave24[0] < 0.05f);
        Assert.True(wave24[23] < 0.05f);

        float[] eq16 = new float[16];
        MusicSpectrumFft.MapLinearEqualizerBars(bands24, eq16);
        Assert.True(eq16[0] > 0.80f);
        Assert.True(eq16[15] < 0.05f);

        float[] halo40 = new float[40];
        MusicSpectrumFft.MapRadialHaloBars(bands24, halo40);
        // 6 o'clock (index 20) carries bass core; 12 o'clock seam (index 0 vs index 39) wraps smoothly.
        Assert.True(halo40[20] > 0.80f);
        Assert.True(MathF.Abs(halo40[0] - halo40[39]) < 0.08f);

        float[] mini4 = new float[4];
        MusicSpectrumFft.MapMiniBadgeBars(bands24, mini4);
        Assert.True(mini4[0] > 0.7f);
        Assert.Equal(0.0f, mini4[3]);
    }

    [Fact]
    public void TryResolveSpectrumMode_CoversAutoSignaturePerLayout_Off_AndAllSixExplicitStyles()
    {
        Assert.False(MusicWidgetContent.TryResolveSpectrumMode(
            MusicSpectrumStyles.Off,
            MusicSpectrumStyles.SymmetricWave,
            out _));

        // A2: Controls -> AuroraSilk, B1: Cover -> SymmetricWave, C2: RecordHorizontal -> PeakHoldBars, D2: RecordVertical -> VinylRadialHalo
        Assert.True(MusicWidgetContent.TryResolveSpectrumMode(
            MusicSpectrumStyles.Auto,
            MusicSpectrumStyles.AuroraSilk,
            out MusicSpectrumMode a2Mode));
        Assert.Equal(MusicSpectrumMode.AuroraSilk, a2Mode);

        Assert.True(MusicWidgetContent.TryResolveSpectrumMode(
            MusicSpectrumStyles.Auto,
            MusicSpectrumStyles.SymmetricWave,
            out MusicSpectrumMode b1Mode));
        Assert.Equal(MusicSpectrumMode.SymmetricWave, b1Mode);

        Assert.True(MusicWidgetContent.TryResolveSpectrumMode(
            MusicSpectrumStyles.Auto,
            MusicSpectrumStyles.PeakHoldBars,
            out MusicSpectrumMode c2Mode));
        Assert.Equal(MusicSpectrumMode.PeakHoldBars, c2Mode);

        Assert.True(MusicWidgetContent.TryResolveSpectrumMode(
            MusicSpectrumStyles.Auto,
            MusicSpectrumStyles.VinylRadialHalo,
            out MusicSpectrumMode d2Mode));
        Assert.Equal(MusicSpectrumMode.VinylRadialHalo, d2Mode);

        // Explicit selections override Auto across every layout
        (string Style, MusicSpectrumMode Expected)[] explicitCases =
        [
            (MusicSpectrumStyles.SymmetricWave, MusicSpectrumMode.SymmetricWave),
            (MusicSpectrumStyles.WaveformScrub, MusicSpectrumMode.WaveformScrub),
            (MusicSpectrumStyles.PeakHoldBars, MusicSpectrumMode.PeakHoldBars),
            (MusicSpectrumStyles.AuroraSilk, MusicSpectrumMode.AuroraSilk),
            (MusicSpectrumStyles.MatrixLed, MusicSpectrumMode.MatrixLed),
            (MusicSpectrumStyles.VinylRadialHalo, MusicSpectrumMode.VinylRadialHalo)
        ];
        foreach ((string style, MusicSpectrumMode expected) in explicitCases)
        {
            Assert.True(MusicWidgetContent.TryResolveSpectrumMode(
                style,
                MusicSpectrumStyles.AuroraSilk,
                out MusicSpectrumMode resolved));
            Assert.Equal(expected, resolved);
        }
    }

    [Fact]
    public void MusicContextMenuBuilder_AppliesAndNormalizesSpectrumStyleAndDisplayMode()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settingsService = new SettingsService(tempRoot);
            int changed = 0;
            settingsService.SettingsChanged += () => changed++;

            Assert.True(MusicWidgetContextMenuBuilder.ApplySpectrumStyle(
                settingsService,
                MusicSpectrumStyles.VinylRadialHalo));
            Assert.Equal(MusicSpectrumStyles.VinylRadialHalo, settingsService.Settings.MusicSpectrumStyle);
            Assert.Equal(1, changed);

            // Duplicate selection is a no-op
            Assert.False(MusicWidgetContextMenuBuilder.ApplySpectrumStyle(
                settingsService,
                MusicSpectrumStyles.VinylRadialHalo));
            Assert.Equal(1, changed);

            // Unknown value normalizes to Auto
            Assert.True(MusicWidgetContextMenuBuilder.ApplySpectrumStyle(
                settingsService,
                "InvalidStyle"));
            Assert.Equal(MusicSpectrumStyles.Auto, settingsService.Settings.MusicSpectrumStyle);
            Assert.Equal(2, changed);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void MusicSpectrumVisualizer_IsWiredIntoAllFourAdaptiveLayoutsSettingsAndContextMenu()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs"));
        string settingsXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string commandsCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string serviceCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/Audio/MusicSpectrumService.cs"));

        // Wide adaptive hosts across B1 / A2 / D2 / C2 (no legacy tiny 4-bar badges)
        Assert.DoesNotContain("MinimalSpectrumBadge", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlsSpectrumBadge", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordHorizontalSpectrumBadge", xaml, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"MinimalSpectrumBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MinimalRadialHalo\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ControlsSpectrumBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ControlsArtworkHalo\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordVinylHalo\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordSpectrumBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordHorizontalSpectrumBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordHorizontalVinylHalo\"", xaml, StringComparison.Ordinal);

        // Settings UI + Right-click context menu both wired
        Assert.Contains("Settings.Music.SpectrumStyle.Title", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind AvailableSpectrumStyleOptions, Mode=OneWay}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsComboBox.Value=\"{x:Bind SpectrumStyle, Mode=TwoWay}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("MusicWidgetContextMenuBuilder.Append(", commandsCode, StringComparison.Ordinal);

        // Dispose releases all 8 spectrum hosts
        Assert.Contains("MinimalSpectrumBar?.ReleaseVisualResources();", code, StringComparison.Ordinal);
        Assert.Contains("ControlsSpectrumBar?.ReleaseVisualResources();", code, StringComparison.Ordinal);
        Assert.Contains("RecordVinylHalo?.ReleaseVisualResources();", code, StringComparison.Ordinal);
        Assert.Contains("RecordHorizontalSpectrumBar?.ReleaseVisualResources();", code, StringComparison.Ordinal);

        // Must stay 100% Native AOT compatible without legacy [ComImport] attributes.
        Assert.DoesNotContain("[ComImport]", serviceCode, StringComparison.Ordinal);
        Assert.Contains("HasDefaultRenderEndpointChanged(", serviceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Concat(sourceAppId", serviceCode, StringComparison.Ordinal);

        // Spectrum animation must not be falsely disabled by VinylRotationAnimationsEnabled,
        // and inactive spectrum controls must defer visual tree construction.
        string controlCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicSpectrumControl.cs"));
        Assert.Contains("RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);", controlCode, StringComparison.Ordinal);

        int updateSpectrumStart = code.IndexOf("private void UpdateSpectrumVisuals(", StringComparison.Ordinal);
        Assert.True(updateSpectrumStart >= 0);
        int stopSpectrumStart = code.IndexOf("private void StopSpectrumVisuals(", updateSpectrumStart, StringComparison.Ordinal);
        Assert.True(stopSpectrumStart > updateSpectrumStart);
        string updateSpectrumBody = code[updateSpectrumStart..stopSpectrumStart];
        Assert.DoesNotContain("VinylRotationAnimationsEnabled()", updateSpectrumBody, StringComparison.Ordinal);
    }

    private static float[] GenerateSineWave(float frequencyHz, float amplitude, int sampleRate, int length)
    {
        var samples = new float[length];
        double step = (2.0 * Math.PI * frequencyHz) / sampleRate;
        for (int i = 0; i < length; i++)
        {
            samples[i] = amplitude * (float)Math.Sin(step * i);
        }

        return samples;
    }

    private static int FindPeakIndex(ReadOnlySpan<float> values)
    {
        int bestIndex = 0;
        float bestValue = float.MinValue;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > bestValue)
            {
                bestValue = values[i];
                bestIndex = i;
            }
        }

        return bestIndex;
    }
}
