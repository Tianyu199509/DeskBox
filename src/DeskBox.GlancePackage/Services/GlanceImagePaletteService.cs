using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace DeskBox.Services;

internal readonly record struct GlanceImagePalette(Color Primary, Color Secondary);

/// <summary>
/// Package-owned copy of the built-in palette service. Extracts a compact two-color palette from Glance images. Images are decoded
/// to a tiny sample and cached by path/write stamp so rotation does no repeated
/// full-resolution work.
/// </summary>
internal sealed class GlanceImagePaletteService
{
    private const uint SampleEdge = 40;
    private const int MaxCacheEntries = 32;

    private sealed record CacheEntry(GlanceImagePalette Palette, long AccessOrder);

    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _accessOrder;

    public async Task<GlanceImagePalette?> GetPaletteAsync(
        string? path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        string cacheKey;
        try
        {
            var fileInfo = new FileInfo(path);
            cacheKey = $"{fileInfo.FullName}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return null;
        }

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(cacheKey, out CacheEntry? cached))
            {
                _cache[cacheKey] = cached with { AccessOrder = ++_accessOrder };
                return cached.Palette;
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            uint targetWidth = Math.Max(1, Math.Min(SampleEdge, decoder.PixelWidth));
            uint targetHeight = Math.Max(1, Math.Min(SampleEdge, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            PixelDataProvider provider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            cancellationToken.ThrowIfCancellationRequested();
            GlanceImagePalette? palette = ExtractPalette(provider.DetachPixelData());
            if (palette is null)
            {
                return null;
            }

            lock (_cacheGate)
            {
                _cache[cacheKey] = new CacheEntry(palette.Value, ++_accessOrder);
                if (_cache.Count > MaxCacheEntries)
                {
                    string oldestKey = _cache.MinBy(pair => pair.Value.AccessOrder).Key;
                    _cache.Remove(oldestKey);
                }
            }

            return palette;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DeskBox.GlancePackage.Services.PackageLogger.LogVerbose($"[GlanceImagePalette] Failed to sample '{path}': {ex.Message}");
            return null;
        }
    }

    internal static GlanceImagePalette? ExtractPalette(ReadOnlySpan<byte> bgraPixels)
    {
        if (bgraPixels.Length < 4)
        {
            return null;
        }

        var samples = new List<(double R, double G, double B, double Weight)>(bgraPixels.Length / 4);
        for (int i = 0; i <= bgraPixels.Length - 4; i += 4)
        {
            double alpha = bgraPixels[i + 3] / 255.0;
            if (alpha < 0.10)
            {
                continue;
            }

            double red = Math.Clamp(bgraPixels[i + 2] / alpha, 0, 255);
            double green = Math.Clamp(bgraPixels[i + 1] / alpha, 0, 255);
            double blue = Math.Clamp(bgraPixels[i] / alpha, 0, 255);
            double maximum = Math.Max(red, Math.Max(green, blue));
            double minimum = Math.Min(red, Math.Min(green, blue));
            double saturation = (maximum - minimum) / 255.0;
            double luminance = ((red * 0.2126) + (green * 0.7152) + (blue * 0.0722)) / 255.0;
            double midtoneWeight = 0.72 + ((1.0 - Math.Abs(luminance - 0.52)) * 0.28);
            double weight = alpha * (0.48 + (saturation * 0.92)) * midtoneWeight;
            samples.Add((red, green, blue, weight));
        }

        if (samples.Count == 0)
        {
            return null;
        }

        var first = samples.MaxBy(sample => sample.Weight);
        var second = samples.MaxBy(sample =>
            ColorDistanceSquared(sample, first) * sample.Weight);
        if (ColorDistanceSquared(first, second) < 64)
        {
            return new GlanceImagePalette(ToColor(first), ToColor(first));
        }

        for (int iteration = 0; iteration < 6; iteration++)
        {
            var firstAccumulator = new ColorAccumulator();
            var secondAccumulator = new ColorAccumulator();
            foreach (var sample in samples)
            {
                if (ColorDistanceSquared(sample, first) <= ColorDistanceSquared(sample, second))
                {
                    firstAccumulator.Add(sample);
                }
                else
                {
                    secondAccumulator.Add(sample);
                }
            }

            if (firstAccumulator.Weight > 0)
            {
                first = firstAccumulator.ToSample();
            }

            if (secondAccumulator.Weight > 0)
            {
                second = secondAccumulator.ToSample();
            }
        }

        double firstWeight = 0;
        double secondWeight = 0;
        foreach (var sample in samples)
        {
            if (ColorDistanceSquared(sample, first) <= ColorDistanceSquared(sample, second))
            {
                firstWeight += sample.Weight;
            }
            else
            {
                secondWeight += sample.Weight;
            }
        }

        return firstWeight >= secondWeight
            ? new GlanceImagePalette(ToColor(first), ToColor(second))
            : new GlanceImagePalette(ToColor(second), ToColor(first));
    }

    private static double ColorDistanceSquared(
        (double R, double G, double B, double Weight) first,
        (double R, double G, double B, double Weight) second)
    {
        double red = first.R - second.R;
        double green = first.G - second.G;
        double blue = first.B - second.B;
        return (red * red) + (green * green) + (blue * blue);
    }

    private static Color ToColor((double R, double G, double B, double Weight) sample) =>
        Color.FromArgb(
            0xFF,
            (byte)Math.Clamp(Math.Round(sample.R), 0, 255),
            (byte)Math.Clamp(Math.Round(sample.G), 0, 255),
            (byte)Math.Clamp(Math.Round(sample.B), 0, 255));

    private struct ColorAccumulator
    {
        public double Red;
        public double Green;
        public double Blue;
        public double Weight;

        public void Add((double R, double G, double B, double Weight) sample)
        {
            Red += sample.R * sample.Weight;
            Green += sample.G * sample.Weight;
            Blue += sample.B * sample.Weight;
            Weight += sample.Weight;
        }

        public readonly (double R, double G, double B, double Weight) ToSample() =>
            (Red / Weight, Green / Weight, Blue / Weight, Weight);
    }
}
