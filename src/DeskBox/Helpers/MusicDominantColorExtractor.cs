using Windows.UI;

namespace DeskBox.Helpers;

/// <summary>
/// Extracts a vibrant dominant color from downscaled BGRA8 artwork pixels using
/// HSV hue bucketing and clamps lightness so progress indicators remain legible
/// against both light and dark widget surfaces.
/// </summary>
internal static class MusicDominantColorExtractor
{
    private const int HueBucketCount = 12;

    public static Color? ExtractDominantColor(ReadOnlySpan<byte> bgraPixels)
    {
        if (bgraPixels.Length < 4)
        {
            return null;
        }

        Span<double> bucketWeight = stackalloc double[HueBucketCount];
        Span<double> bucketR = stackalloc double[HueBucketCount];
        Span<double> bucketG = stackalloc double[HueBucketCount];
        Span<double> bucketB = stackalloc double[HueBucketCount];

        double fallbackR = 0;
        double fallbackG = 0;
        double fallbackB = 0;
        double fallbackWeight = 0;
        double chromaticWeightTotal = 0;

        for (int index = 0; index <= bgraPixels.Length - 4; index += 4)
        {
            byte b = bgraPixels[index];
            byte g = bgraPixels[index + 1];
            byte r = bgraPixels[index + 2];
            byte a = bgraPixels[index + 3];
            if (a < 28)
            {
                continue;
            }

            // BGRA8 input from BitmapDecoder uses premultiplied alpha; unpremultiply
            // partially transparent pixels so dark alpha fringes do not bias hue/value.
            if (a < 255)
            {
                r = (byte)Math.Min(255, (r * 255 + (a / 2)) / a);
                g = (byte)Math.Min(255, (g * 255 + (a / 2)) / a);
                b = (byte)Math.Min(255, (b * 255 + (a / 2)) / a);
            }

            double alphaFactor = a / 255.0;
            RgbToHsv(r, g, b, out double hue, out double saturation, out double value);

            double baseWeight = alphaFactor * (0.4 + saturation);
            fallbackR += r * baseWeight;
            fallbackG += g * baseWeight;
            fallbackB += b * baseWeight;
            fallbackWeight += baseWeight;

            // Chromatic mid-tones carry the album's visual identity; near-white and
            // near-black backgrounds are excluded from hue buckets unless no chromatic
            // pixels exist at all.
            if (saturation < 0.14 || value < 0.10 || value > 0.97)
            {
                continue;
            }

            double midtoneBias = 1.0 - Math.Min(0.55, Math.Abs(value - 0.58) * 0.85);
            double weight = alphaFactor * (0.30 + saturation * 1.65) * midtoneBias;
            int bucket = Math.Clamp((int)(hue / (360.0 / HueBucketCount)), 0, HueBucketCount - 1);

            bucketWeight[bucket] += weight;
            bucketR[bucket] += r * weight;
            bucketG[bucket] += g * weight;
            bucketB[bucket] += b * weight;
            chromaticWeightTotal += weight;
        }

        if (fallbackWeight <= 0.01)
        {
            return null;
        }

        if (chromaticWeightTotal >= fallbackWeight * 0.04)
        {
            int bestBucket = 0;
            double bestWeight = bucketWeight[0];
            for (int i = 1; i < HueBucketCount; i++)
            {
                if (bucketWeight[i] > bestWeight)
                {
                    bestWeight = bucketWeight[i];
                    bestBucket = i;
                }
            }

            if (bestWeight > 0.001)
            {
                return Color.FromArgb(
                    0xFF,
                    ClampByte(bucketR[bestBucket] / bestWeight),
                    ClampByte(bucketG[bestBucket] / bestWeight),
                    ClampByte(bucketB[bestBucket] / bestWeight));
            }
        }

        return Color.FromArgb(
            0xFF,
            ClampByte(fallbackR / fallbackWeight),
            ClampByte(fallbackG / fallbackWeight),
            ClampByte(fallbackB / fallbackWeight));
    }

    public static Color ClampBackdropColor(Color color)
    {
        RgbToHsl(color.R, color.G, color.B, out double h, out double s, out double l);
        double clampedL = Math.Clamp(l, 0.22, 0.82);
        double boostedS = s > 0.08 ? Math.Clamp(s * 1.12, 0.0, 0.90) : s;
        return HslToColor(h, boostedS, clampedL);
    }

    public static Color EnsureReadableAccentColor(Color color, bool isLightSurface)
    {
        RgbToHsl(color.R, color.G, color.B, out double h, out double s, out double l);
        double adjustedS = s > 0.08 ? Math.Max(0.32, Math.Min(0.92, s * 1.18)) : s;
        double adjustedL = isLightSurface
            ? Math.Clamp(l, 0.24, 0.46)
            : Math.Clamp(l, 0.62, 0.80);

        Color candidate = HslToColor(h, adjustedS, adjustedL);
        if (isLightSurface)
        {
            while (ComputePerceptualLuma(candidate) > 0.46 && adjustedL > 0.14)
            {
                adjustedL -= 0.03;
                candidate = HslToColor(h, adjustedS, adjustedL);
            }
        }
        else
        {
            while (ComputePerceptualLuma(candidate) < 0.50 && adjustedL < 0.88)
            {
                adjustedL += 0.03;
                candidate = HslToColor(h, adjustedS, adjustedL);
            }
        }

        return candidate;
    }

    private static double ComputePerceptualLuma(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static void RgbToHsv(byte rByte, byte gByte, byte bByte, out double h, out double s, out double v)
    {
        double r = rByte / 255.0;
        double g = gByte / 255.0;
        double b = bByte / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        v = max;
        s = max <= 0.0001 ? 0 : delta / max;
        if (delta <= 0.0001)
        {
            h = 0;
            return;
        }

        if (max == r)
        {
            h = 60.0 * (((g - b) / delta) % 6.0);
        }
        else if (max == g)
        {
            h = 60.0 * (((b - r) / delta) + 2.0);
        }
        else
        {
            h = 60.0 * (((r - g) / delta) + 4.0);
        }

        if (h < 0)
        {
            h += 360.0;
        }
    }

    private static void RgbToHsl(byte rByte, byte gByte, byte bByte, out double h, out double s, out double l)
    {
        double r = rByte / 255.0;
        double g = gByte / 255.0;
        double b = bByte / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        l = (max + min) * 0.5;
        if (delta <= 0.0001)
        {
            h = 0;
            s = 0;
            return;
        }

        s = delta / (1.0 - Math.Abs(2.0 * l - 1.0));
        if (max == r)
        {
            h = 60.0 * (((g - b) / delta) % 6.0);
        }
        else if (max == g)
        {
            h = 60.0 * (((b - r) / delta) + 2.0);
        }
        else
        {
            h = 60.0 * (((r - g) / delta) + 4.0);
        }

        if (h < 0)
        {
            h += 360.0;
        }
    }

    private static Color HslToColor(double h, double s, double l)
    {
        if (s <= 0.0001)
        {
            byte gray = ClampByte(l * 255.0);
            return Color.FromArgb(0xFF, gray, gray, gray);
        }

        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = l - c * 0.5;

        (double r1, double g1, double b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromArgb(
            0xFF,
            ClampByte((r1 + m) * 255.0),
            ClampByte((g1 + m) * 255.0),
            ClampByte((b1 + m) * 255.0));
    }

    /// <summary>
    /// Applies a multi-pass separable box blur (Gaussian approximation) to downscaled
    /// BGRA8 artwork pixels and encodes the result as an uncompressed top-down 32-bpp
    /// BMP byte array suitable for <c>BitmapImage.SetSourceAsync</c>.
    /// </summary>
    public static byte[]? CreateBlurredBmpBytes(
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height,
        int radius = 8,
        int passes = 3)
    {
        int pixelCount = width * height;
        if (width <= 0 || height <= 0 || bgraPixels.Length < pixelCount * 4)
        {
            return null;
        }

        byte[] src = new byte[pixelCount * 4];
        byte[] dst = new byte[pixelCount * 4];

        for (int i = 0; i < pixelCount * 4; i += 4)
        {
            byte b = bgraPixels[i];
            byte g = bgraPixels[i + 1];
            byte r = bgraPixels[i + 2];
            byte a = bgraPixels[i + 3];
            if (a < 255 && a > 0)
            {
                b = (byte)Math.Min(255, (b * 255 + (a / 2)) / a);
                g = (byte)Math.Min(255, (g * 255 + (a / 2)) / a);
                r = (byte)Math.Min(255, (r * 255 + (a / 2)) / a);
            }
            else if (a == 0)
            {
                b = 24;
                g = 26;
                r = 32;
            }

            src[i] = b;
            src[i + 1] = g;
            src[i + 2] = r;
            src[i + 3] = 255;
        }

        int effectiveRadius = Math.Clamp(radius, 1, Math.Max(1, Math.Min(width, height) / 2));
        int passCount = Math.Clamp(passes, 1, 4);
        for (int p = 0; p < passCount; p++)
        {
            BlurHorizontal(src, dst, width, height, effectiveRadius);
            BlurVertical(dst, src, width, height, effectiveRadius);
        }

        const int headerSize = 54;
        int imageBytes = pixelCount * 4;
        byte[] bmp = new byte[headerSize + imageBytes];
        int fileSize = bmp.Length;

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), fileSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), headerSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), -height);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 32);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), imageBytes);

        Buffer.BlockCopy(src, 0, bmp, headerSize, imageBytes);
        return bmp;
    }

    private static void BlurHorizontal(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int kernel = radius * 2 + 1;
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            int sumB = 0;
            int sumG = 0;
            int sumR = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int cx = Math.Clamp(k, 0, width - 1);
                int idx = rowOffset + cx * 4;
                sumB += src[idx];
                sumG += src[idx + 1];
                sumR += src[idx + 2];
            }

            for (int x = 0; x < width; x++)
            {
                int outIdx = rowOffset + x * 4;
                dst[outIdx] = (byte)((sumB + kernel / 2) / kernel);
                dst[outIdx + 1] = (byte)((sumG + kernel / 2) / kernel);
                dst[outIdx + 2] = (byte)((sumR + kernel / 2) / kernel);
                dst[outIdx + 3] = 255;

                int removeIdx = rowOffset + Math.Clamp(x - radius, 0, width - 1) * 4;
                int addIdx = rowOffset + Math.Clamp(x + radius + 1, 0, width - 1) * 4;
                sumB += src[addIdx] - src[removeIdx];
                sumG += src[addIdx + 1] - src[removeIdx + 1];
                sumR += src[addIdx + 2] - src[removeIdx + 2];
            }
        }
    }

    private static void BlurVertical(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int kernel = radius * 2 + 1;
        int stride = width * 4;
        for (int x = 0; x < width; x++)
        {
            int colOffset = x * 4;
            int sumB = 0;
            int sumG = 0;
            int sumR = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int cy = Math.Clamp(k, 0, height - 1);
                int idx = cy * stride + colOffset;
                sumB += src[idx];
                sumG += src[idx + 1];
                sumR += src[idx + 2];
            }

            for (int y = 0; y < height; y++)
            {
                int outIdx = y * stride + colOffset;
                dst[outIdx] = (byte)((sumB + kernel / 2) / kernel);
                dst[outIdx + 1] = (byte)((sumG + kernel / 2) / kernel);
                dst[outIdx + 2] = (byte)((sumR + kernel / 2) / kernel);
                dst[outIdx + 3] = 255;

                int removeIdx = Math.Clamp(y - radius, 0, height - 1) * stride + colOffset;
                int addIdx = Math.Clamp(y + radius + 1, 0, height - 1) * stride + colOffset;
                sumB += src[addIdx] - src[removeIdx];
                sumG += src[addIdx + 1] - src[removeIdx + 1];
                sumR += src[addIdx + 2] - src[removeIdx + 2];
            }
        }
    }

    private static byte ClampByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
