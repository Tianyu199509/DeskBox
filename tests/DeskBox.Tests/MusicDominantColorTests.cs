using DeskBox.Helpers;
using Windows.UI;

namespace DeskBox.Tests;

public sealed class MusicDominantColorTests
{
    [Fact]
    public void ExtractDominantColor_PicksVibrantAccentOverMajorityNearBlackBackground()
    {
        // 80 near-black pixels + 20 vibrant crimson pixels (BGRA8 order)
        byte[] bgra = new byte[100 * 4];
        for (int i = 0; i < 80; i++)
        {
            int o = i * 4;
            bgra[o] = 12;     // B
            bgra[o + 1] = 12; // G
            bgra[o + 2] = 14; // R
            bgra[o + 3] = 255;
        }

        for (int i = 80; i < 100; i++)
        {
            int o = i * 4;
            bgra[o] = 45;      // B
            bgra[o + 1] = 35;  // G
            bgra[o + 2] = 225; // R (crimson)
            bgra[o + 3] = 255;
        }

        Color? extracted = MusicDominantColorExtractor.ExtractDominantColor(bgra);
        Assert.NotNull(extracted);
        Assert.True(extracted.Value.R > 180, $"Expected crimson dominance, got R={extracted.Value.R}");
        Assert.True(extracted.Value.R > extracted.Value.B * 3);
    }

    [Fact]
    public void ExtractDominantColor_UnpremultipliesSemiTransparentPixels()
    {
        // Premultiplied 50% alpha cyan: R=0, G=100, B=110, A=128 -> unpremultiplied G≈200, B≈220
        byte[] bgra =
        [
            110, 100, 0, 128,
            110, 100, 0, 128
        ];

        Color? extracted = MusicDominantColorExtractor.ExtractDominantColor(bgra);
        Assert.NotNull(extracted);
        Assert.Equal(255, extracted.Value.A);
        Assert.True(extracted.Value.B >= 200);
        Assert.True(extracted.Value.G >= 180);
    }

    [Fact]
    public void ExtractDominantColor_ReturnsNullForFullyTransparentBuffer()
    {
        byte[] bgra = new byte[16 * 4];
        Assert.Null(MusicDominantColorExtractor.ExtractDominantColor(bgra));
    }

    [Fact]
    public void ClampBackdropColor_ClampsExtremeBlackAndPureWhiteIntoBalancedRange()
    {
        Color clampedBlack = MusicDominantColorExtractor.ClampBackdropColor(Color.FromArgb(255, 2, 2, 2));
        Color clampedWhite = MusicDominantColorExtractor.ClampBackdropColor(Color.FromArgb(255, 254, 254, 254));

        Assert.InRange(clampedBlack.R, (byte)45, (byte)70);
        Assert.InRange(clampedWhite.R, (byte)195, (byte)220);
    }

    [Fact]
    public void EnsureReadableAccentColor_AdaptsLightnessForLightAndDarkSurfaces()
    {
        Color paleYellow = Color.FromArgb(255, 250, 245, 190);
        Color darkNavy = Color.FromArgb(255, 15, 25, 65);

        Color onLight = MusicDominantColorExtractor.EnsureReadableAccentColor(paleYellow, isLightSurface: true);
        Color onDark = MusicDominantColorExtractor.EnsureReadableAccentColor(darkNavy, isLightSurface: false);

        double lightLum = (0.2126 * onLight.R + 0.7152 * onLight.G + 0.0722 * onLight.B) / 255.0;
        double darkLum = (0.2126 * onDark.R + 0.7152 * onDark.G + 0.0722 * onDark.B) / 255.0;

        Assert.True(lightLum < 0.55, $"Accent on light surface must darken for contrast (got lum={lightLum:F2}).");
        Assert.True(darkLum > 0.45, $"Accent on dark surface must brighten for contrast (got lum={darkLum:F2}).");
    }

    [Fact]
    public void CreateBlurredBmpBytes_ProducesValid32BppBmpAndSoftensSharpColorEdge()
    {
        const int width = 16;
        const int height = 16;
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                bool rightHalf = x >= width / 2;
                bgra[o] = rightHalf ? (byte)240 : (byte)10;     // B
                bgra[o + 1] = rightHalf ? (byte)180 : (byte)10; // G
                bgra[o + 2] = rightHalf ? (byte)60 : (byte)10;  // R
                bgra[o + 3] = 255;
            }
        }

        byte[]? bmp = MusicDominantColorExtractor.CreateBlurredBmpBytes(bgra, width, height, radius: 3, passes: 3);
        Assert.NotNull(bmp);
        Assert.Equal(54 + width * height * 4, bmp.Length);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);

        int rowMid = height / 2;
        int leftEdgeOffset = 54 + (rowMid * width + (width / 2 - 1)) * 4;
        int rightEdgeOffset = 54 + (rowMid * width + (width / 2)) * 4;
        byte blurredLeftB = bmp[leftEdgeOffset];
        byte blurredRightB = bmp[rightEdgeOffset];

        // Across the former 10 -> 240 step edge, box-blur passes blend both sides smoothly.
        Assert.InRange(blurredLeftB, (byte)70, (byte)170);
        Assert.InRange(blurredRightB, (byte)80, (byte)185);
        Assert.Equal(255, bmp[leftEdgeOffset + 3]);
    }
}
