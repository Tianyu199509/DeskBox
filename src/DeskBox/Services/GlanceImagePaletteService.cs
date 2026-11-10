using Windows.Graphics.Imaging;
using Windows.UI;

namespace DeskBox.Services;

internal readonly record struct GlanceImagePalette(Color Primary, Color Secondary);

/// <summary>
/// Extracts a compact two-color palette from Glance images. Images are decoded
/// to a tiny sample and cached by path/write stamp so rotation does no repeated
/// full-resolution work.
/// </summary>
internal sealed class GlanceImagePaletteService
{
    private const uint SampleEdge = 40;
    private const int MaxCacheEntries = 32;

    public static GlanceImagePaletteService Shared { get; } = new();

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
            GlanceImagePalette? palette = await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var fileStream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var stream = fileStream.AsRandomAccessStream();
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
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
                    ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return ExtractPalette(provider.DetachPixelData());
            }, cancellationToken).ConfigureAwait(false);
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
            App.LogVerbose($"[GlanceImagePalette] Failed to sample '{path}': {ex.Message}");
            return null;
        }
    }

    internal static GlanceImagePalette? ExtractPalette(ReadOnlySpan<byte> bgraPixels)
    {
        if (bgraPixels.Length < 4)
        {
            return null;
        }

        bool hasAnySample = false;
        (double R, double G, double B, double Weight) first = default;
        for (int i = 0; i <= bgraPixels.Length - 4; i += 4)
        {
            if (!TryReadWeightedSample(bgraPixels, i, out var sample))
            {
                continue;
            }

            if (!hasAnySample || sample.Weight > first.Weight)
            {
                first = sample;
                hasAnySample = true;
            }
        }

        if (!hasAnySample)
        {
            return null;
        }

        (double R, double G, double B, double Weight) second = first;
        double bestSecondScore = -1;
        for (int i = 0; i <= bgraPixels.Length - 4; i += 4)
        {
            if (!TryReadWeightedSample(bgraPixels, i, out var sample))
            {
                continue;
            }

            double score = ColorDistanceSquared(sample, first) * sample.Weight;
            if (score > bestSecondScore)
            {
                bestSecondScore = score;
                second = sample;
            }
        }

        if (ColorDistanceSquared(first, second) < 64)
        {
            return new GlanceImagePalette(ToColor(first), ToColor(first));
        }

        for (int iteration = 0; iteration < 6; iteration++)
        {
            var firstAccumulator = new ColorAccumulator();
            var secondAccumulator = new ColorAccumulator();
            for (int i = 0; i <= bgraPixels.Length - 4; i += 4)
            {
                if (!TryReadWeightedSample(bgraPixels, i, out var sample))
                {
                    continue;
                }

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
        for (int i = 0; i <= bgraPixels.Length - 4; i += 4)
        {
            if (!TryReadWeightedSample(bgraPixels, i, out var sample))
            {
                continue;
            }

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

    private static bool TryReadWeightedSample(
        ReadOnlySpan<byte> bgraPixels,
        int index,
        out (double R, double G, double B, double Weight) sample)
    {
        double alpha = bgraPixels[index + 3] / 255.0;
        if (alpha < 0.10)
        {
            sample = default;
            return false;
        }

        double red = Math.Clamp(bgraPixels[index + 2] / alpha, 0, 255);
        double green = Math.Clamp(bgraPixels[index + 1] / alpha, 0, 255);
        double blue = Math.Clamp(bgraPixels[index] / alpha, 0, 255);
        double maximum = Math.Max(red, Math.Max(green, blue));
        double minimum = Math.Min(red, Math.Min(green, blue));
        double saturation = (maximum - minimum) / 255.0;
        double luminance = ((red * 0.2126) + (green * 0.7152) + (blue * 0.0722)) / 255.0;
        double midtoneWeight = 0.72 + ((1.0 - Math.Abs(luminance - 0.52)) * 0.28);
        double weight = alpha * (0.48 + (saturation * 0.92)) * midtoneWeight;
        sample = (red, green, blue, weight);
        return true;
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
