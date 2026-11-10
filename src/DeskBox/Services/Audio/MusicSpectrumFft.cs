using System;

namespace DeskBox.Services.Audio;

/// <summary>
/// Zero-allocation 1024-point Hann-windowed Radix-2 FFT analyzer and perceptual
/// frequency-band mapper for the Music widget spectrum visualizer, with automatic
/// organic fallback synthesis when shared-mode loopback is silent or unavailable
/// (e.g. WASAPI Exclusive / ASIO output).
/// </summary>
internal sealed class MusicSpectrumFft
{
    public const int FftSize = 1024;
    public const int HalfFftSize = FftSize / 2;
    public const int BandCount = 24;
    public const int MiniBandCount = 4;

    private const float MinFrequencyHz = 45.0f;
    private const float MaxFrequencyHz = 15000.0f;
    private const float MinDb = -46.0f;
    private const float MaxDb = -3.0f;
    private const float SilenceRmsThreshold = 0.00035f;
    private const float SilenceFallbackDelaySeconds = 1.0f;
    private const float UnavailableFallbackDelaySeconds = 0.2f;

    private static readonly float[] s_hannWindow = CreateHannWindow();
    private static readonly int[] s_bitReversal = CreateBitReversalTable();
    private static readonly (float[] Cos, float[] Sin) s_twiddles = CreateTwiddleTables();
    private static readonly (float LowHz, float HighHz, float TiltGain)[] s_bandSpecs = CreateBandSpecs();

    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imag = new float[FftSize];
    private readonly float[] _magnitudes = new float[HalfFftSize];
    private readonly float[] _rawBands = new float[BandCount];
    private readonly float[] _proceduralBands = new float[BandCount];

    private float _agcPeak = 0.35f;
    private float _silentSeconds;
    private float _fallbackBlend;
    private double _proceduralTimeSeconds;

    public bool IsUsingProceduralFallback => _fallbackBlend >= 0.5f;

    public float FallbackBlend => _fallbackBlend;

    public void Reset()
    {
        Array.Clear(_rawBands);
        Array.Clear(_proceduralBands);
        _agcPeak = 0.35f;
        _silentSeconds = 0.0f;
        _fallbackBlend = 0.0f;
    }

    /// <summary>
    /// Computes 24 perceptual logarithmic frequency bands in [0..1] from the latest
    /// 1024 mono PCM samples. Automatically crossfades to an organic breathing wave
    /// when <paramref name="isPlaying"/> is true but capture is unavailable or silent
    /// for over 1 second (such as WASAPI Exclusive / ASIO playback).
    /// </summary>
    public void ComputeBands(
        ReadOnlySpan<float> pcmSamples,
        int sampleRate,
        bool isPlaying,
        bool captureAvailable,
        float deltaTimeSeconds,
        Span<float> outputBands24)
    {
        if (outputBands24.Length < BandCount)
        {
            throw new ArgumentException($"Output span must have at least {BandCount} elements.", nameof(outputBands24));
        }

        float dt = Math.Clamp(deltaTimeSeconds, 0.001f, 0.25f);
        _proceduralTimeSeconds += dt;

        if (!isPlaying)
        {
            _silentSeconds = 0.0f;
            _fallbackBlend = MoveTowards(_fallbackBlend, 0.0f, dt * 6.0f);
            outputBands24[..BandCount].Clear();
            return;
        }

        bool hasValidSamples = captureAvailable &&
            pcmSamples.Length >= FftSize &&
            sampleRate >= 8000;

        float rms = 0.0f;
        if (hasValidSamples)
        {
            rms = ComputeRmsAndLoadWindowed(pcmSamples[..FftSize]);
        }

        bool isSilent = !hasValidSamples || rms < SilenceRmsThreshold;
        if (isSilent)
        {
            _silentSeconds = Math.Min(10.0f, _silentSeconds + dt);
            Array.Clear(_rawBands);
        }
        else
        {
            _silentSeconds = 0.0f;
            ExecuteInPlaceFft();
            ExtractPerceptualBands(sampleRate);
        }

        float triggerThreshold = captureAvailable
            ? SilenceFallbackDelaySeconds
            : UnavailableFallbackDelaySeconds;
        bool shouldUseFallback = _silentSeconds >= triggerThreshold;
        float targetBlend = shouldUseFallback ? 1.0f : 0.0f;
        float blendRate = shouldUseFallback ? (1.0f / 0.45f) : (1.0f / 0.12f);
        _fallbackBlend = MoveTowards(_fallbackBlend, targetBlend, dt * blendRate);

        if (_fallbackBlend > 0.001f)
        {
            GenerateProceduralBands(_proceduralTimeSeconds, _proceduralBands);
        }

        for (int i = 0; i < BandCount; i++)
        {
            float realVal = _rawBands[i];
            float procVal = _fallbackBlend > 0.001f ? _proceduralBands[i] : 0.0f;
            outputBands24[i] = Math.Clamp(
                realVal + ((procVal - realVal) * _fallbackBlend),
                0.0f,
                1.0f);
        }
    }

    /// <summary>
    /// Maps the 24 perceptual bands (0 = bass .. 23 = air) onto <paramref name="barCount"/>
    /// bars arranged as a center-weighted acoustic waveform (bass &amp; vocal core near the
    /// center, harmonics &amp; air radiating outward on both sides with natural stereo-like
    /// odd/even alternation).
    /// </summary>
    public static void MapSymmetricWaveBars(
        ReadOnlySpan<float> bands24,
        Span<float> targetBars)
    {
        int count = targetBars.Length;
        if (count == 0)
        {
            return;
        }

        if (bands24.Length < BandCount)
        {
            targetBars.Clear();
            return;
        }

        float center = (count - 1) * 0.5f;
        float halfSpan = Math.Max(1.0f, center);

        for (int i = 0; i < count; i++)
        {
            float signedNorm = (i - center) / halfSpan; // -1 .. +1
            float distNorm = MathF.Abs(signedNorm);     // 0 at center .. 1 at outer tips

            // Spread low-to-high bands outward from center, alternating even/odd bands
            // between left and right halves so the two sides feel organically alive
            // rather than a rigid mirror clone.
            float pairIndex = distNorm * ((BandCount * 0.5f) - 1.0f);
            int pairFloor = Math.Clamp((int)MathF.Floor(pairIndex), 0, (BandCount / 2) - 1);
            int pairCeil = Math.Clamp(pairFloor + 1, 0, (BandCount / 2) - 1);
            float frac = pairIndex - pairFloor;

            int sideOffset = signedNorm < 0.0f ? 0 : 1;
            int bandA = Math.Clamp((pairFloor * 2) + sideOffset, 0, BandCount - 1);
            int bandB = Math.Clamp((pairCeil * 2) + sideOffset, 0, BandCount - 1);
            float sample = bands24[bandA] + ((bands24[bandB] - bands24[bandA]) * frac);

            // Subtle bell-curve spatial taper so outer tips frame the vinyl cleanly.
            float envelope = 0.56f + (0.44f * MathF.Cos(distNorm * MathF.PI * 0.5f));
            targetBars[i] = Math.Clamp(sample * envelope, 0.0f, 1.0f);
        }
    }

    /// <summary>
    /// Maps the 24 perceptual bands (0 = bass .. 23 = air) left-to-right across
    /// <paramref name="targetBars"/> for classic graphic-equalizer and LED-matrix modes.
    /// </summary>
    public static void MapLinearEqualizerBars(
        ReadOnlySpan<float> bands24,
        Span<float> targetBars)
    {
        int count = targetBars.Length;
        if (count == 0)
        {
            return;
        }

        if (bands24.Length < BandCount)
        {
            targetBars.Clear();
            return;
        }

        if (count == 1)
        {
            targetBars[0] = Math.Clamp(bands24[0], 0.0f, 1.0f);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            float bandPos = t * (BandCount - 1);
            int low = Math.Clamp((int)MathF.Floor(bandPos), 0, BandCount - 1);
            int high = Math.Clamp(low + 1, 0, BandCount - 1);
            float frac = bandPos - low;
            float sample = bands24[low] + ((bands24[high] - bands24[low]) * frac);

            // Gentle presence lift through vocal/harmonic midrange with soft edge polish.
            float contour = 0.88f + (0.14f * MathF.Sin(t * MathF.PI));
            targetBars[i] = Math.Clamp(sample * contour, 0.0f, 1.0f);
        }
    }

    /// <summary>
    /// Maps the 24 perceptual bands seamlessly around a 360-degree closed halo ring
    /// (<paramref name="targetBars"/>[0] at 12 o'clock clockwise back to 12 o'clock)
    /// with zero seam discontinuity at the wrap boundary.
    /// </summary>
    public static void MapRadialHaloBars(
        ReadOnlySpan<float> bands24,
        Span<float> targetBars)
    {
        int count = targetBars.Length;
        if (count == 0)
        {
            return;
        }

        if (bands24.Length < BandCount)
        {
            targetBars.Clear();
            return;
        }

        int halfCount = Math.Max(1, count / 2);
        for (int i = 0; i < count; i++)
        {
            // Fold circular index [0..count) into [0..1] from 6 o'clock (bass core) up to
            // 12 o'clock (air/harmonics) symmetrically on both right and left arcs so both
            // 12 o'clock (index 0) and 6 o'clock (index count/2) are C0-continuous.
            float angleNorm = i / (float)count; // 0 .. <1
            float mirroredArc = 1.0f - MathF.Abs((angleNorm * 2.0f) - 1.0f); // 0 at 12h -> 1 at 6h -> 0 at 12h
            float bassToAir = 1.0f - mirroredArc; // 0 (bass at bottom) .. 1 (air at top)

            float pairPos = bassToAir * ((BandCount * 0.5f) - 1.0f);
            int pairFloor = Math.Clamp((int)MathF.Floor(pairPos), 0, (BandCount / 2) - 1);
            int pairCeil = Math.Clamp(pairFloor + 1, 0, (BandCount / 2) - 1);
            float frac = pairPos - pairFloor;

            // Alternate odd/even bands softly between right and left hemispheres while
            // blending to symmetric shared values at the 12h & 6h poles so the seam is invisible.
            float poleBlend = MathF.Sin(mirroredArc * MathF.PI); // 0 at poles, 1 at 3h/9h equators
            bool isLeftHemisphere = i > halfCount;

            int evenA = Math.Clamp(pairFloor * 2, 0, BandCount - 1);
            int evenB = Math.Clamp(pairCeil * 2, 0, BandCount - 1);
            int oddA = Math.Clamp((pairFloor * 2) + 1, 0, BandCount - 1);
            int oddB = Math.Clamp((pairCeil * 2) + 1, 0, BandCount - 1);

            float evenSample = bands24[evenA] + ((bands24[evenB] - bands24[evenA]) * frac);
            float oddSample = bands24[oddA] + ((bands24[oddB] - bands24[oddA]) * frac);
            float avgSample = (evenSample + oddSample) * 0.5f;
            float sideSample = isLeftHemisphere ? oddSample : evenSample;
            float sample = avgSample + ((sideSample - avgSample) * poleBlend * 0.75f);

            float haloEnvelope = 0.72f + (0.28f * mirroredArc);
            targetBars[i] = Math.Clamp(sample * haloEnvelope, 0.0f, 1.0f);
        }
    }

    /// <summary>
    /// Maps the 24 perceptual bands into <paramref name="targetBars"/> (typically 4 bars:
    /// Bass, Low-Mid, Vocal/Presence, Treble/Air) for compact equalizer badges.
    /// </summary>
    public static void MapMiniBadgeBars(
        ReadOnlySpan<float> bands24,
        Span<float> targetBars)
    {
        int count = targetBars.Length;
        if (count == 0)
        {
            return;
        }

        if (bands24.Length < BandCount)
        {
            targetBars.Clear();
            return;
        }

        for (int b = 0; b < count; b++)
        {
            int start = (b * BandCount) / count;
            int end = Math.Max(start + 1, ((b + 1) * BandCount) / count);
            float maxVal = 0.0f;
            float sumVal = 0.0f;
            for (int i = start; i < end && i < BandCount; i++)
            {
                float v = bands24[i];
                if (v > maxVal)
                {
                    maxVal = v;
                }

                sumVal += v;
            }

            float avgVal = sumVal / Math.Max(1, end - start);
            targetBars[b] = Math.Clamp((maxVal * 0.68f) + (avgVal * 0.32f), 0.0f, 1.0f);
        }
    }

    private float ComputeRmsAndLoadWindowed(ReadOnlySpan<float> samples)
    {
        double sumSquares = 0.0;
        for (int i = 0; i < FftSize; i++)
        {
            float s = samples[i];
            if (!float.IsFinite(s))
            {
                s = 0.0f;
            }

            sumSquares += (double)s * s;
        }

        for (int i = 0; i < FftSize; i++)
        {
            int srcIndex = s_bitReversal[i];
            float s = samples[srcIndex];
            if (!float.IsFinite(s))
            {
                s = 0.0f;
            }

            _real[i] = s * s_hannWindow[srcIndex];
            _imag[i] = 0.0f;
        }

        return (float)Math.Sqrt(sumSquares / FftSize);
    }

    private void ExecuteInPlaceFft()
    {
        float[] real = _real;
        float[] imag = _imag;
        float[] cosTable = s_twiddles.Cos;
        float[] sinTable = s_twiddles.Sin;

        for (int len = 2; len <= FftSize; len <<= 1)
        {
            int half = len >> 1;
            int step = FftSize / len;
            for (int i = 0; i < FftSize; i += len)
            {
                int twiddleIndex = 0;
                for (int k = 0; k < half; k++)
                {
                    int u = i + k;
                    int v = u + half;

                    float cos = cosTable[twiddleIndex];
                    float sin = sinTable[twiddleIndex];
                    twiddleIndex += step;

                    float vr = real[v];
                    float vi = imag[v];
                    float tr = (vr * cos) - (vi * sin);
                    float ti = (vr * sin) + (vi * cos);

                    real[v] = real[u] - tr;
                    imag[v] = imag[u] - ti;
                    real[u] += tr;
                    imag[u] += ti;
                }
            }
        }

        const float normScale = 4.0f / FftSize;
        _magnitudes[0] = 0.0f;
        for (int bin = 1; bin < HalfFftSize; bin++)
        {
            float r = real[bin];
            float im = imag[bin];
            _magnitudes[bin] = MathF.Sqrt((r * r) + (im * im)) * normScale;
        }
    }

    private void ExtractPerceptualBands(int sampleRate)
    {
        float binHz = (float)sampleRate / FftSize;
        int maxBin = HalfFftSize - 1;
        float frameMaxWeighted = 0.0f;

        for (int b = 0; b < BandCount; b++)
        {
            (float lowHz, float highHz, float tiltGain) = s_bandSpecs[b];
            float binLow = lowHz / binHz;
            float binHigh = highHz / binHz;

            float bandMag;
            if (binHigh - binLow < 1.15f)
            {
                float binCenter = Math.Clamp((binLow + binHigh) * 0.5f, 1.0f, maxBin);
                int b0 = (int)MathF.Floor(binCenter);
                int b1 = Math.Min(maxBin, b0 + 1);
                float frac = binCenter - b0;
                bandMag = _magnitudes[b0] + ((_magnitudes[b1] - _magnitudes[b0]) * frac);
            }
            else
            {
                int startBin = Math.Clamp((int)MathF.Floor(binLow), 1, maxBin);
                int endBin = Math.Clamp((int)MathF.Ceiling(binHigh), startBin, maxBin);
                float peak = 0.0f;
                float sum = 0.0f;
                int count = 0;
                for (int bin = startBin; bin <= endBin; bin++)
                {
                    float m = _magnitudes[bin];
                    if (m > peak)
                    {
                        peak = m;
                    }

                    sum += m;
                    count++;
                }

                float avg = count > 0 ? (sum / count) : 0.0f;
                bandMag = (peak * 0.68f) + (avg * 0.32f);
            }

            float weighted = bandMag * tiltGain;
            _rawBands[b] = weighted;
            if (weighted > frameMaxWeighted)
            {
                frameMaxWeighted = weighted;
            }
        }

        // Gentle bounded AGC so quiet background listening (~10-20% volume) stays expressive
        // without over-amplifying low-level noise floors.
        if (frameMaxWeighted > _agcPeak)
        {
            _agcPeak += (frameMaxWeighted - _agcPeak) * 0.24f;
        }
        else
        {
            _agcPeak += (Math.Max(0.18f, frameMaxWeighted) - _agcPeak) * 0.025f;
        }

        float agcGain = Math.Clamp(0.52f / Math.Max(0.12f, _agcPeak), 0.75f, 3.0f);
        const float invDbSpan = 1.0f / (MaxDb - MinDb);

        for (int b = 0; b < BandCount; b++)
        {
            float scaled = _rawBands[b] * agcGain;
            float db = 20.0f * MathF.Log10(scaled + 1e-6f);
            float norm = Math.Clamp((db - MinDb) * invDbSpan, 0.0f, 1.0f);
            _rawBands[b] = MathF.Pow(norm, 1.22f);
        }
    }

    private static void GenerateProceduralBands(double timeSeconds, Span<float> target)
    {
        float t = (float)(timeSeconds % 3600.0);
        float breath = 0.72f + (0.28f * MathF.Sin(t * 1.45f));

        for (int i = 0; i < BandCount; i++)
        {
            float freqRatio = i / (float)(BandCount - 1);
            float w1 = (MathF.Sin((t * 3.1f) + (i * 0.48f)) * 0.5f) + 0.5f;
            float w2 = (MathF.Sin((t * 4.7f) - (i * 0.33f) + 1.1f) * 0.5f) + 0.5f;
            float w3 = (MathF.Sin((t * 1.9f) + (i * 0.77f) + 2.4f) * 0.5f) + 0.5f;
            float combined = (w1 * 0.45f) + (w2 * 0.35f) + (w3 * 0.20f);

            // Warm musical contour: slightly richer in low-mids/vocals, airy taper at top end.
            float contour = 1.0f - (0.34f * freqRatio);
            target[i] = Math.Clamp((0.12f + (0.42f * combined * contour)) * breath, 0.08f, 0.56f);
        }
    }

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        if (MathF.Abs(target - current) <= maxDelta)
        {
            return target;
        }

        return current + (MathF.Sign(target - current) * maxDelta);
    }

    private static float[] CreateHannWindow()
    {
        var window = new float[FftSize];
        float scale = (2.0f * MathF.PI) / (FftSize - 1);
        for (int i = 0; i < FftSize; i++)
        {
            window[i] = 0.5f * (1.0f - MathF.Cos(scale * i));
        }

        return window;
    }

    private static int[] CreateBitReversalTable()
    {
        var table = new int[FftSize];
        int bits = 0;
        for (int temp = FftSize; temp > 1; temp >>= 1)
        {
            bits++;
        }

        for (int i = 0; i < FftSize; i++)
        {
            int reversed = 0;
            int value = i;
            for (int b = 0; b < bits; b++)
            {
                reversed = (reversed << 1) | (value & 1);
                value >>= 1;
            }

            table[i] = reversed;
        }

        return table;
    }

    private static (float[] Cos, float[] Sin) CreateTwiddleTables()
    {
        var cos = new float[HalfFftSize];
        var sin = new float[HalfFftSize];
        for (int i = 0; i < HalfFftSize; i++)
        {
            double angle = (-2.0 * Math.PI * i) / FftSize;
            cos[i] = (float)Math.Cos(angle);
            sin[i] = (float)Math.Sin(angle);
        }

        return (cos, sin);
    }

    private static (float LowHz, float HighHz, float TiltGain)[] CreateBandSpecs()
    {
        var specs = new (float LowHz, float HighHz, float TiltGain)[BandCount];
        float ratio = MaxFrequencyHz / MinFrequencyHz;
        for (int b = 0; b < BandCount; b++)
        {
            float low = MinFrequencyHz * MathF.Pow(ratio, b / (float)BandCount);
            float high = MinFrequencyHz * MathF.Pow(ratio, (b + 1) / (float)BandCount);
            float center = MathF.Sqrt(low * high);
            float tilt = Math.Clamp(MathF.Pow(center / 180.0f, 0.34f), 0.78f, 2.85f);
            specs[b] = (low, high, tilt);
        }

        return specs;
    }
}
