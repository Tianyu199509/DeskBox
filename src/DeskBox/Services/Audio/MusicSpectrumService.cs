using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using DeskBox.Platform;

namespace DeskBox.Services.Audio;

/// <summary>
/// Shared ref-counted WASAPI Loopback audio capture service for real-time Music widget
/// spectrum analysis. Uses direct unmanaged COM vtable calls so it works identically in
/// Debug JIT and Retail Native AOT without dynamic COM marshaling or per-frame allocations.
/// Automatically stops capture and exits its worker thread when no visible playing widget
/// is subscribed (0% idle CPU/GPU overhead).
/// </summary>
internal sealed unsafe class MusicSpectrumService
{
    public static MusicSpectrumService Shared { get; } = new();

    private const int RingCapacity = 2048;
    private const int RingMask = RingCapacity - 1;
    private const int MaxMatchedMeters = 8;

    private const uint ClsctxAll = 23;
    private const uint AudclntStreamFlagsLoopback = 0x00020000;
    private const uint AudclntBufferFlagsSilent = 0x00000002;
    private const long LoopbackBufferDurationHns = 500_000; // 50 ms
    private const int CapturePollIntervalMs = 16;
    private const long SessionMeterRefreshIntervalMs = 700;

    private static readonly Guid s_clsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid s_iidMmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid s_iidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid s_iidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid s_iidAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid s_iidAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid s_iidAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private static readonly Guid s_subFormatIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid s_subFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");

    private readonly object _lifecycleGate = new();
    private readonly object _bufferGate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly float[] _ringBuffer = new float[RingCapacity];

    private int _ringWriteIndex;
    private int _ringAvailableCount;
    private volatile int _sampleRate = 48000;
    private volatile bool _captureAvailable;
    private ManualResetEvent? _stopSignal;
    private Thread? _captureThread;
    private int _captureGeneration;

    private MusicSpectrumService()
    {
    }

    public IDisposable AcquireSubscription(string? sourceAppUserModelId, string? sourceDisplayName)
    {
        var subscription = new Subscription(this, sourceAppUserModelId, sourceDisplayName);
        lock (_lifecycleGate)
        {
            _subscriptions.Add(subscription);
            EnsureCaptureThreadRunningLocked();
        }

        return subscription;
    }

    public void UpdateSubscriptionSource(
        IDisposable? token,
        string? sourceAppUserModelId,
        string? sourceDisplayName)
    {
        if (token is not Subscription sub)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            sub.UpdateSource(sourceAppUserModelId, sourceDisplayName);
        }
    }

    public bool ReadLatestSamples(
        Span<float> destination,
        out int sampleRate,
        out bool captureAvailable)
    {
        sampleRate = _sampleRate;
        captureAvailable = _captureAvailable;

        int needed = destination.Length;
        if (needed <= 0)
        {
            return false;
        }

        lock (_bufferGate)
        {
            if (_ringAvailableCount < needed)
            {
                destination.Clear();
                return false;
            }

            int start = (_ringWriteIndex - needed) & RingMask;
            int firstChunk = Math.Min(needed, RingCapacity - start);
            _ringBuffer.AsSpan(start, firstChunk).CopyTo(destination[..firstChunk]);
            if (firstChunk < needed)
            {
                _ringBuffer.AsSpan(0, needed - firstChunk).CopyTo(destination[firstChunk..]);
            }

            return true;
        }
    }

    private void ReleaseSubscription(Subscription subscription)
    {
        ManualResetEvent? signalToSet = null;
        lock (_lifecycleGate)
        {
            if (!_subscriptions.Remove(subscription) || _subscriptions.Count > 0)
            {
                return;
            }

            _captureGeneration++;
            signalToSet = _stopSignal;
            _stopSignal = null;
            _captureThread = null;
            _captureAvailable = false;
        }

        lock (_bufferGate)
        {
            Array.Clear(_ringBuffer);
            _ringWriteIndex = 0;
            _ringAvailableCount = 0;
        }

        try
        {
            signalToSet?.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void EnsureCaptureThreadRunningLocked()
    {
        if (_captureThread is { IsAlive: true })
        {
            return;
        }

        int generation = ++_captureGeneration;
        var stopSignal = new ManualResetEvent(initialState: false);
        _stopSignal = stopSignal;

        var thread = new Thread(() => RunCaptureLoop(generation, stopSignal))
        {
            IsBackground = true,
            Name = "DeskBox.MusicSpectrumCapture",
            Priority = ThreadPriority.Normal
        };
        _captureThread = thread;
        thread.Start();
    }

    private (string AppId, string DisplayName) GetActiveSourceHint()
    {
        lock (_lifecycleGate)
        {
            for (int i = _subscriptions.Count - 1; i >= 0; i--)
            {
                Subscription sub = _subscriptions[i];
                if (!string.IsNullOrWhiteSpace(sub.SourceAppUserModelId) ||
                    !string.IsNullOrWhiteSpace(sub.SourceDisplayName))
                {
                    return (sub.SourceAppUserModelId, sub.SourceDisplayName);
                }
            }
        }

        return (string.Empty, string.Empty);
    }

    private bool ShouldContinueCapture(int generation)
    {
        lock (_lifecycleGate)
        {
            return generation == _captureGeneration && _subscriptions.Count > 0;
        }
    }

    private void RunCaptureLoop(int generation, ManualResetEvent stopSignal)
    {
        int coInitHr = Ole32NativeMethods.CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
        bool shouldUninitCom = coInitHr is 0 or 1;

        try
        {
            while (ShouldContinueCapture(generation))
            {
                if (!TryRunSingleEndpointSession(generation, stopSignal, out bool rebindImmediately))
                {
                    if (!rebindImmediately)
                    {
                        _captureAvailable = false;
                        if (stopSignal.WaitOne(450))
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSpectrum] Capture loop exited: {ex.Message}");
        }
        finally
        {
            _captureAvailable = false;
            stopSignal.Dispose();
            if (shouldUninitCom)
            {
                Ole32NativeMethods.CoUninitialize();
            }
        }
    }

    private bool TryRunSingleEndpointSession(
        int generation,
        ManualResetEvent stopSignal,
        out bool rebindImmediately)
    {
        rebindImmediately = false;
        nint enumerator = 0;
        nint device = 0;
        nint audioClient = 0;
        nint captureClient = 0;
        bool audioClientStarted = false;
        WaveFormatEx* mixFormat = null;
        nint* matchedMeters = stackalloc nint[MaxMatchedMeters];
        int matchedMeterCount = 0;

        try
        {
            if (Ole32NativeMethods.CoCreateInstance(
                    in s_clsidMmDeviceEnumerator,
                    0,
                    ClsctxAll,
                    in s_iidMmDeviceEnumerator,
                    out enumerator) < 0 || enumerator == 0)
            {
                return false;
            }

            if (!TryGetDefaultRenderEndpoint(enumerator, out device))
            {
                return false;
            }

            _ = TryGetDeviceId(device, out string activeDeviceId);

            Guid iidClient = s_iidAudioClient;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Vtbl(device, 3))(
                device, &iidClient, ClsctxAll, 0, &audioClient) < 0 || audioClient == 0)
            {
                return false;
            }

            if (((delegate* unmanaged[Stdcall]<nint, WaveFormatEx**, int>)Vtbl(audioClient, 8))(
                audioClient, &mixFormat) < 0 || mixFormat == null)
            {
                return false;
            }

            if (!TryResolveSampleEncoding(mixFormat, out SampleEncoding encoding, out int channels, out int blockAlign, out int sampleRate))
            {
                return false;
            }

            int hrInit = ((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormatEx*, Guid*, int>)Vtbl(audioClient, 3))(
                audioClient,
                0 /* AUDCLNT_SHAREMODE_SHARED */,
                AudclntStreamFlagsLoopback,
                LoopbackBufferDurationHns,
                0,
                mixFormat,
                null);
            if (hrInit < 0)
            {
                return false;
            }

            Guid iidCapture = s_iidAudioCaptureClient;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(audioClient, 14))(
                audioClient, &iidCapture, &captureClient) < 0 || captureClient == 0)
            {
                return false;
            }

            if (((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(audioClient, 10))(audioClient) < 0)
            {
                return false;
            }

            audioClientStarted = true;
            _sampleRate = sampleRate;
            _captureAvailable = true;

            string? lastSourceAppId = null;
            string? lastSourceDisplayName = null;
            long lastMeterRefreshMs = -SessionMeterRefreshIntervalMs;
            Stopwatch clock = Stopwatch.StartNew();
            long lastTickMs = 0;

            while (ShouldContinueCapture(generation))
            {
                long nowMs = clock.ElapsedMilliseconds;
                (string sourceAppId, string sourceDisplayName) = GetActiveSourceHint();
                bool periodicDue = nowMs - lastMeterRefreshMs >= SessionMeterRefreshIntervalMs;
                bool sourceChanged = !string.Equals(sourceAppId, lastSourceAppId, StringComparison.Ordinal) ||
                    !string.Equals(sourceDisplayName, lastSourceDisplayName, StringComparison.Ordinal);

                if (periodicDue || sourceChanged)
                {
                    if (periodicDue &&
                        activeDeviceId.Length > 0 &&
                        HasDefaultRenderEndpointChanged(enumerator, activeDeviceId))
                    {
                        rebindImmediately = true;
                        return false;
                    }

                    ReleaseMeters(matchedMeters, ref matchedMeterCount);
                    RefreshMatchedSessionMeters(
                        device,
                        sourceAppId,
                        sourceDisplayName,
                        matchedMeters,
                        out matchedMeterCount);
                    lastSourceAppId = sourceAppId;
                    lastSourceDisplayName = sourceDisplayName;
                    lastMeterRefreshMs = nowMs;
                }

                bool playerActiveOnSharedMix = IsMatchedPlayerActive(matchedMeters, matchedMeterCount);

                uint packetFrames = 0;
                int hrPacket = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl(captureClient, 5))(
                    captureClient, &packetFrames);
                if (hrPacket < 0)
                {
                    return false;
                }

                int totalFramesThisTick = 0;
                while (packetFrames > 0)
                {
                    byte* dataPtr = null;
                    uint framesToRead = 0;
                    uint flags = 0;
                    ulong devPos = 0;
                    ulong qpcPos = 0;

                    int hrBuf = ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, ulong*, ulong*, int>)Vtbl(captureClient, 3))(
                        captureClient, &dataPtr, &framesToRead, &flags, &devPos, &qpcPos);
                    if (hrBuf < 0)
                    {
                        return false;
                    }

                    try
                    {
                        if (framesToRead > 0)
                        {
                            bool packetSilent = (flags & AudclntBufferFlagsSilent) != 0 ||
                                dataPtr == null ||
                                !playerActiveOnSharedMix;
                            if (packetSilent)
                            {
                                PushZeroSamples((int)framesToRead);
                            }
                            else
                            {
                                PushDecodedSamples(dataPtr, (int)framesToRead, encoding, channels, blockAlign);
                            }

                            totalFramesThisTick += (int)framesToRead;
                        }
                    }
                    finally
                    {
                        _ = ((delegate* unmanaged[Stdcall]<nint, uint, int>)Vtbl(captureClient, 4))(
                            captureClient, framesToRead);
                    }

                    hrPacket = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl(captureClient, 5))(
                        captureClient, &packetFrames);
                    if (hrPacket < 0)
                    {
                        return false;
                    }
                }

                if (totalFramesThisTick == 0)
                {
                    long elapsedMs = Math.Clamp(nowMs - lastTickMs, 8, 60);
                    int silentFrames = Math.Clamp((int)((sampleRate * elapsedMs) / 1000), 64, 1024);
                    PushZeroSamples(silentFrames);
                }

                lastTickMs = nowMs;
                if (stopSignal.WaitOne(CapturePollIntervalMs))
                {
                    break;
                }
            }

            return true;
        }
        finally
        {
            if (audioClientStarted && audioClient != 0)
            {
                _ = ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(audioClient, 11))(audioClient); // Stop()
            }

            ReleaseMeters(matchedMeters, ref matchedMeterCount);
            if (mixFormat != null)
            {
                Marshal.FreeCoTaskMem((nint)mixFormat);
            }

            ReleaseComPointer(ref captureClient);
            ReleaseComPointer(ref audioClient);
            ReleaseComPointer(ref device);
            ReleaseComPointer(ref enumerator);
        }
    }

    private static bool TryGetDefaultRenderEndpoint(nint enumerator, out nint device)
    {
        nint resolved = 0;
        // GetDefaultAudioEndpoint(eRender = 0, eMultimedia = 1), fallback to eConsole = 0
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Vtbl(enumerator, 4))(
            enumerator, 0, 1, &resolved);
        if (hr < 0 || resolved == 0)
        {
            hr = ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Vtbl(enumerator, 4))(
                enumerator, 0, 0, &resolved);
        }

        if (hr >= 0 && resolved != 0)
        {
            device = resolved;
            return true;
        }

        device = 0;
        return false;
    }

    private static bool TryGetDeviceId(nint device, out string deviceId)
    {
        nint idPtr = 0;
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(device, 5))(device, &idPtr);
            if (hr >= 0 && idPtr != 0)
            {
                deviceId = Marshal.PtrToStringUni(idPtr) ?? string.Empty;
                return deviceId.Length > 0;
            }
        }
        finally
        {
            if (idPtr != 0)
            {
                Marshal.FreeCoTaskMem(idPtr);
            }
        }

        deviceId = string.Empty;
        return false;
    }

    private static bool HasDefaultRenderEndpointChanged(nint enumerator, string activeDeviceId)
    {
        nint currentDefault = 0;
        try
        {
            if (!TryGetDefaultRenderEndpoint(enumerator, out currentDefault))
            {
                return false;
            }

            return TryGetDeviceId(currentDefault, out string currentDefaultId) &&
                   !string.Equals(activeDeviceId, currentDefaultId, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ReleaseComPointer(ref currentDefault);
        }
    }

    private void PushZeroSamples(int frameCount)
    {
        if (frameCount <= 0)
        {
            return;
        }

        int count = Math.Min(frameCount, RingCapacity);
        lock (_bufferGate)
        {
            for (int i = 0; i < count; i++)
            {
                _ringBuffer[_ringWriteIndex] = 0.0f;
                _ringWriteIndex = (_ringWriteIndex + 1) & RingMask;
            }

            _ringAvailableCount = Math.Min(RingCapacity, _ringAvailableCount + count);
        }
    }

    private void PushDecodedSamples(
        byte* dataPtr,
        int frameCount,
        SampleEncoding encoding,
        int channels,
        int blockAlign)
    {
        const int ChunkSize = 256;
        Span<float> scratch = stackalloc float[ChunkSize];

        int offset = 0;
        while (offset < frameCount)
        {
            int batch = Math.Min(ChunkSize, frameCount - offset);
            for (int i = 0; i < batch; i++)
            {
                byte* framePtr = dataPtr + ((offset + i) * blockAlign);
                scratch[i] = DecodeMonoFrame(framePtr, encoding, channels);
            }

            lock (_bufferGate)
            {
                for (int i = 0; i < batch; i++)
                {
                    _ringBuffer[_ringWriteIndex] = scratch[i];
                    _ringWriteIndex = (_ringWriteIndex + 1) & RingMask;
                }

                _ringAvailableCount = Math.Min(RingCapacity, _ringAvailableCount + batch);
            }

            offset += batch;
        }
    }

    private static float DecodeMonoFrame(byte* framePtr, SampleEncoding encoding, int channels)
    {
        int mixChannels = channels >= 2 ? 2 : 1;
        float sum = 0.0f;

        switch (encoding)
        {
            case SampleEncoding.Float32:
            {
                float* samples = (float*)framePtr;
                for (int c = 0; c < mixChannels; c++)
                {
                    float s = samples[c];
                    if (float.IsFinite(s))
                    {
                        sum += s;
                    }
                }

                break;
            }

            case SampleEncoding.Pcm16:
            {
                short* samples = (short*)framePtr;
                for (int c = 0; c < mixChannels; c++)
                {
                    sum += samples[c] * (1.0f / 32768.0f);
                }

                break;
            }

            case SampleEncoding.Pcm24:
            {
                for (int c = 0; c < mixChannels; c++)
                {
                    byte* p = framePtr + (c * 3);
                    int sample24 = (p[0] << 8) | (p[1] << 16) | ((sbyte)p[2] << 24);
                    sum += sample24 * (1.0f / 2147483648.0f);
                }

                break;
            }

            case SampleEncoding.Pcm32:
            {
                int* samples = (int*)framePtr;
                for (int c = 0; c < mixChannels; c++)
                {
                    sum += samples[c] * (1.0f / 2147483648.0f);
                }

                break;
            }
        }

        return mixChannels == 2 ? (sum * 0.5f) : sum;
    }

    private static bool TryResolveSampleEncoding(
        WaveFormatEx* format,
        out SampleEncoding encoding,
        out int channels,
        out int blockAlign,
        out int sampleRate)
    {
        encoding = SampleEncoding.Unsupported;
        channels = format->Channels;
        blockAlign = format->BlockAlign;
        sampleRate = (int)format->SamplesPerSec;

        if (channels <= 0 || blockAlign <= 0 || sampleRate < 8000 || sampleRate > 384000)
        {
            return false;
        }

        ushort tag = format->FormatTag;
        ushort bits = format->BitsPerSample;

        if (tag == 0x0003 && bits == 32)
        {
            encoding = SampleEncoding.Float32;
            return true;
        }

        if (tag == 0x0001)
        {
            encoding = bits switch
            {
                16 => SampleEncoding.Pcm16,
                24 => SampleEncoding.Pcm24,
                32 => SampleEncoding.Pcm32,
                _ => SampleEncoding.Unsupported
            };
            return encoding != SampleEncoding.Unsupported;
        }

        if (tag == 0xFFFE && format->ExtraSize >= 22)
        {
            Guid subFormat = ((WaveFormatExtensible*)format)->SubFormat;
            if (subFormat == s_subFormatIeeeFloat && bits == 32)
            {
                encoding = SampleEncoding.Float32;
                return true;
            }

            if (subFormat == s_subFormatPcm)
            {
                encoding = bits switch
                {
                    16 => SampleEncoding.Pcm16,
                    24 => SampleEncoding.Pcm24,
                    32 => SampleEncoding.Pcm32,
                    _ => SampleEncoding.Unsupported
                };
                return encoding != SampleEncoding.Unsupported;
            }
        }

        return false;
    }

    private static void RefreshMatchedSessionMeters(
        nint device,
        string sourceAppUserModelId,
        string sourceDisplayName,
        nint* matchedMeters,
        out int matchedCount)
    {
        matchedCount = 0;
        string normSourceApp = NormalizeMatchText(sourceAppUserModelId);
        string normSourceDisplay = NormalizeMatchText(sourceDisplayName);
        if (normSourceApp.Length < 3 && normSourceDisplay.Length < 3)
        {
            return;
        }

        nint sessionManager = 0;
        nint sessionEnumerator = 0;
        try
        {
            Guid iidMgr = s_iidAudioSessionManager2;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Vtbl(device, 3))(
                device, &iidMgr, ClsctxAll, 0, &sessionManager) < 0 || sessionManager == 0)
            {
                return;
            }

            // IAudioSessionManager2::GetSessionEnumerator is slot 5
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(sessionManager, 5))(
                sessionManager, &sessionEnumerator) < 0 || sessionEnumerator == 0)
            {
                return;
            }

            int count = 0;
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Vtbl(sessionEnumerator, 3))(
                sessionEnumerator, &count) < 0 || count <= 0)
            {
                return;
            }

            Guid iidControl2 = s_iidAudioSessionControl2;
            Guid iidMeter = s_iidAudioMeterInformation;

            for (int i = 0; i < count && matchedCount < MaxMatchedMeters; i++)
            {
                nint sessionControl = 0;
                nint sessionControl2 = 0;
                try
                {
                    if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vtbl(sessionEnumerator, 4))(
                        sessionEnumerator, i, &sessionControl) < 0 || sessionControl == 0)
                    {
                        continue;
                    }

                    if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(sessionControl, 0))(
                        sessionControl, &iidControl2, &sessionControl2) < 0 || sessionControl2 == 0)
                    {
                        continue;
                    }

                    // IsSystemSoundsSession (slot 15): S_OK (0) means system sounds
                    if (((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(sessionControl2, 15))(sessionControl2) == 0)
                    {
                        continue;
                    }

                    uint pid = 0;
                    _ = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl(sessionControl2, 14))(sessionControl2, &pid);
                    string displayName = ReadComCoTaskString(sessionControl2, slot: 4);
                    string sessionId = ReadComCoTaskString(sessionControl2, slot: 12);
                    string sessionInstanceId = ReadComCoTaskString(sessionControl2, slot: 13);
                    string processName = pid != 0 ? ProcessImageNameResolver.GetProcessName(pid) : string.Empty;

                    if (!IsMatchingSession(
                        normSourceApp,
                        normSourceDisplay,
                        NormalizeMatchText(processName),
                        NormalizeMatchText(displayName),
                        NormalizeMatchText(sessionId),
                        NormalizeMatchText(sessionInstanceId)))
                    {
                        continue;
                    }

                    nint meter = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(sessionControl, 0))(
                        sessionControl, &iidMeter, &meter) >= 0 && meter != 0)
                    {
                        matchedMeters[matchedCount++] = meter;
                    }
                }
                finally
                {
                    ReleaseComPointer(ref sessionControl2);
                    ReleaseComPointer(ref sessionControl);
                }
            }
        }
        finally
        {
            ReleaseComPointer(ref sessionEnumerator);
            ReleaseComPointer(ref sessionManager);
        }
    }

    private static bool IsMatchedPlayerActive(nint* matchedMeters, int matchedCount)
    {
        if (matchedCount <= 0)
        {
            // Target session was not uniquely identified on this endpoint (e.g. UWP brokered audio);
            // allow shared loopback samples through rather than muting the visualizer.
            return true;
        }

        for (int i = 0; i < matchedCount; i++)
        {
            nint meter = matchedMeters[i];
            if (meter == 0)
            {
                continue;
            }

            float peak = 0.0f;
            if (((delegate* unmanaged[Stdcall]<nint, float*, int>)Vtbl(meter, 3))(meter, &peak) >= 0 &&
                float.IsFinite(peak) &&
                peak >= 0.00015f)
            {
                return true;
            }
        }

        return false;
    }

    private static void ReleaseMeters(nint* matchedMeters, ref int matchedCount)
    {
        for (int i = 0; i < matchedCount; i++)
        {
            ReleaseComPointer(ref matchedMeters[i]);
        }

        matchedCount = 0;
    }

    private static string ReadComCoTaskString(nint comObj, int slot)
    {
        nint strPtr = 0;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(comObj, slot))(comObj, &strPtr) >= 0 &&
                strPtr != 0)
            {
                return Marshal.PtrToStringUni(strPtr) ?? string.Empty;
            }
        }
        finally
        {
            if (strPtr != 0)
            {
                Marshal.FreeCoTaskMem(strPtr);
            }
        }

        return string.Empty;
    }

    private static bool IsMatchingSession(
        string normSourceApp,
        string normSourceDisplay,
        string normProcessName,
        string normDisplayName,
        string normSessionId,
        string normSessionInstanceId)
    {
        return ContainsMeaningfulMatch(normSessionId, normSourceApp) ||
               ContainsMeaningfulMatch(normSessionInstanceId, normSourceApp) ||
               ContainsMeaningfulMatch(normDisplayName, normSourceDisplay) ||
               ContainsMeaningfulMatch(normProcessName, normSourceDisplay) ||
               ContainsMeaningfulMatch(normSourceApp, normProcessName) ||
               ContainsMeaningfulMatch(normSessionId, normSourceDisplay);
    }

    private static bool ContainsMeaningfulMatch(string haystack, string needle) =>
        needle.Length >= 3 &&
        haystack.Length >= 3 &&
        haystack.Contains(needle, StringComparison.Ordinal);

    private static string NormalizeMatchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int len = 0;
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[len++] = char.ToLowerInvariant(ch);
            }
        }

        return len == 0 ? string.Empty : new string(buffer[..len]);
    }

    private static nint Vtbl(nint comPtr, int slot) => (*(nint**)comPtr)[slot];

    private static void ReleaseComPointer(ref nint ptr)
    {
        nint current = ptr;
        if (current != 0)
        {
            ptr = 0;
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(current, 2))(current);
        }
    }

    private enum SampleEncoding
    {
        Unsupported,
        Float32,
        Pcm16,
        Pcm24,
        Pcm32
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort SamplesUnion;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    private sealed class Subscription : IDisposable
    {
        private readonly MusicSpectrumService _owner;
        private int _disposed;

        public Subscription(MusicSpectrumService owner, string? sourceAppUserModelId, string? sourceDisplayName)
        {
            _owner = owner;
            SourceAppUserModelId = sourceAppUserModelId ?? string.Empty;
            SourceDisplayName = sourceDisplayName ?? string.Empty;
        }

        public string SourceAppUserModelId { get; private set; }

        public string SourceDisplayName { get; private set; }

        public void UpdateSource(string? sourceAppUserModelId, string? sourceDisplayName)
        {
            SourceAppUserModelId = sourceAppUserModelId ?? string.Empty;
            SourceDisplayName = sourceDisplayName ?? string.Empty;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.ReleaseSubscription(this);
            }
        }
    }
}
