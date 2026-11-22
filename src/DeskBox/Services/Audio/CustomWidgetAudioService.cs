using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace DeskBox.Services;

/// <summary>
/// Pure-native WinRT audio engine for Custom Widgets (<c>audio.cue</c>, <c>audio.play</c>,
/// <c>audio.pause</c>, <c>audio.stop</c>, <c>audio.setVolume</c>, <c>audio.stopAll</c>).
/// Synthesizes zero-allocation-cached 44.1kHz PCM WAV cues in memory and manages multi-track
/// <see cref="MediaPlayer"/> channels (e.g. ambient white-noise mixers or radio streams).
/// </summary>
public sealed class CustomWidgetAudioService : IDisposable
{
    public static CustomWidgetAudioService Shared { get; } = new();

    public event Action<string>? AmbientOwnerPreempted;

    private const int SampleRate = 44100;
    private const int MaxConcurrentTracks = 8;

    private static readonly ConcurrentDictionary<string, byte[]> SynthesizedCueWavCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();
    private readonly Dictionary<string, TrackSlot> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CuePlayback> _transientCuePlayers = [];

    private sealed record CuePlayback(MediaPlayer Player, MediaSource Source, InMemoryRandomAccessStream Stream)
    {
        public void Dispose()
        {
            try
            {
                Player.Pause();
                Player.Source = null;
                Player.Dispose();
            }
            catch
            {
            }

            try
            {
                Source.Dispose();
                Stream.Dispose();
            }
            catch
            {
            }
        }
    }
    private bool _disposed;

    private sealed class TrackSlot : IDisposable
    {
        public required string Id { get; init; }
        public required string Source { get; set; }
        public string? OwnerId { get; set; }
        public double Volume { get; set; } = 0.8;
        public bool Loop { get; set; }
        public bool IsPlaying { get; set; }
        public MediaPlayer? Player { get; set; }
        public MediaSource? MediaSource { get; set; }
        public IRandomAccessStream? InMemoryStream { get; set; }
        public TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? MediaFailedHandler { get; set; }

        public void Dispose()
        {
            IsPlaying = false;
            try
            {
                if (Player is not null)
                {
                    if (MediaFailedHandler is not null)
                    {
                        try
                        {
                            Player.MediaFailed -= MediaFailedHandler;
                        }
                        catch
                        {
                        }
                        MediaFailedHandler = null;
                    }

                    Player.Pause();
                    Player.Source = null;
                    Player.Dispose();
                    Player = null;
                }
            }
            catch
            {
            }

            try
            {
                MediaSource?.Dispose();
                MediaSource = null;
            }
            catch
            {
            }

            try
            {
                InMemoryStream?.Dispose();
                InMemoryStream = null;
            }
            catch
            {
            }
        }
    }

    public string? LastPlayedCue { get; private set; }

    public int CuePlayCount { get; private set; }

    public IReadOnlyCollection<string> ActiveTrackIds
    {
        get
        {
            lock (_sync)
            {
                return _tracks.Values.Where(static t => t.IsPlaying).Select(static t => t.Id).ToArray();
            }
        }
    }

    public bool IsTrackPlaying(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return false;
        }

        lock (_sync)
        {
            return _tracks.TryGetValue(trackId.Trim(), out TrackSlot? slot) && slot.IsPlaying;
        }
    }

    public double GetTrackVolume(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return 0;
        }

        lock (_sync)
        {
            return _tracks.TryGetValue(trackId.Trim(), out TrackSlot? slot) ? slot.Volume : 0;
        }
    }

    public void PlayCue(string? cueName, double volume = 0.8)
    {
        string normalized = string.IsNullOrWhiteSpace(cueName) ? "complete" : cueName.Trim().ToLowerInvariant();
        double clampedVol = Math.Clamp(volume, 0.0, 1.0);

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            LastPlayedCue = normalized;
            CuePlayCount++;
        }

        byte[] wavBytes = SynthesizedCueWavCache.GetOrAdd(
            ResolveCanonicalCueKey(normalized),
            BuildSynthesizedCueWav);
        TryPlayWavBytesOneShot(wavBytes, clampedVol);
    }

    internal static string ResolveCanonicalCueKey(string cue) =>
        cue switch
        {
            "tick" or "click" => "tick",
            "pop" => "pop",
            "soft" or "gentle" => "soft",
            "alert" or "warning" or "error" => "alert",
            "bell" => "bell",
            _ => "complete"
        };

    internal static string ResolveCanonicalNoiseKind(string noise) =>
        noise switch
        {
            "brown" => "brown",
            "rain" => "rain",
            "pink" => "pink",
            _ => "white"
        };

    public bool PlayTrack(
        string? trackId,
        string? sourceUriOrPreset,
        double volume = 0.6,
        bool loop = false,
        string? ambientOwnerId = null) =>
        PlayTrackCore(
            trackId,
            sourceUriOrPreset,
            widgetDirectory: null,
            loop: loop,
            volume: volume,
            ambientOwnerId: ambientOwnerId);

    public bool PlayTrack(
        string? trackId,
        string? sourcePathOrUrl,
        string? widgetDirectory,
        bool loop = false,
        double volume = 0.8,
        string? ambientOwnerId = null) =>
        PlayTrackCore(
            trackId,
            sourcePathOrUrl,
            widgetDirectory: widgetDirectory,
            loop: loop,
            volume: volume,
            ambientOwnerId: ambientOwnerId);

    private bool PlayTrackCore(
        string? trackId,
        string? sourcePathOrUrl,
        string? widgetDirectory,
        bool loop,
        double volume,
        string? ambientOwnerId)
    {
        string id = string.IsNullOrWhiteSpace(trackId) ? "default" : trackId.Trim();
        double clampedVol = Math.Clamp(volume, 0.0, 1.0);
        string? normalizedOwnerId = string.IsNullOrWhiteSpace(ambientOwnerId) ? null : ambientOwnerId.Trim();
        List<string>? preemptedOwners = null;
        bool started = false;

        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            // Resume existing track if no new source was passed.
            if (string.IsNullOrWhiteSpace(sourcePathOrUrl))
            {
                if (!_tracks.TryGetValue(id, out TrackSlot? existing))
                {
                    return false;
                }

                if (normalizedOwnerId is not null)
                {
                    PreemptOtherAmbientOwnersCore(normalizedOwnerId, ref preemptedOwners);
                    existing.OwnerId = normalizedOwnerId;
                }

                if (existing.Player is null)
                {
                    existing.IsPlaying = false;
                    _tracks.Remove(id);
                    existing.Dispose();
                    started = false;
                }
                else
                {
                    try
                    {
                        existing.Volume = clampedVol;
                        existing.Loop = loop;
                        existing.Player.Volume = clampedVol;
                        existing.Player.IsLoopingEnabled = loop;
                        existing.Player.Play();
                        existing.IsPlaying = true;
                        started = true;
                    }
                    catch
                    {
                        existing.IsPlaying = false;
                        _tracks.Remove(id);
                        existing.Dispose();
                        started = false;
                    }
                }
            }
            else
            {
                if (normalizedOwnerId is not null)
                {
                    PreemptOtherAmbientOwnersCore(normalizedOwnerId, ref preemptedOwners);
                }

                string resolvedSource = sourcePathOrUrl.Trim();
                bool handledExisting = false;
                if (_tracks.TryGetValue(id, out TrackSlot? current))
                {
                    if (string.Equals(current.Source, resolvedSource, StringComparison.OrdinalIgnoreCase) &&
                        (current.Player is not null || resolvedSource.StartsWith("mirror:", StringComparison.OrdinalIgnoreCase)))
                    {
                        handledExisting = true;
                        if (normalizedOwnerId is not null)
                        {
                            current.OwnerId = normalizedOwnerId;
                        }

                        try
                        {
                            current.Volume = clampedVol;
                            current.Loop = loop;
                            if (current.Player is not null)
                            {
                                current.Player.Volume = clampedVol;
                                current.Player.IsLoopingEnabled = loop;
                                current.Player.Play();
                            }
                            current.IsPlaying = true;
                            started = true;
                        }
                        catch
                        {
                            current.IsPlaying = false;
                            _tracks.Remove(id);
                            current.Dispose();
                            started = false;
                        }
                    }
                    else
                    {
                        current.Dispose();
                        _tracks.Remove(id);
                    }
                }

                if (!handledExisting)
                {
                    var slot = new TrackSlot
                    {
                        Id = id,
                        Source = resolvedSource,
                        OwnerId = normalizedOwnerId,
                        Volume = clampedVol,
                        Loop = loop,
                        IsPlaying = false
                    };

                    if (TryAttachAndStartMediaPlayer(slot, resolvedSource, widgetDirectory))
                    {
                        if (_tracks.Count >= MaxConcurrentTracks)
                        {
                            string? oldestKey = _tracks.Keys.FirstOrDefault();
                            if (oldestKey is not null && _tracks.Remove(oldestKey, out TrackSlot? evicted))
                            {
                                evicted.Dispose();
                            }
                        }

                        _tracks[id] = slot;
                        started = true;
                    }
                    else
                    {
                        slot.IsPlaying = false;
                        slot.Dispose();
                        _tracks.Remove(id);
                        started = false;
                    }
                }
            }
        }

        NotifyAmbientOwnersPreempted(preemptedOwners);
        return started;
    }

    private void PreemptOtherAmbientOwnersCore(string newOwnerId, ref List<string>? preemptedOwners)
    {
        List<string>? keysToRemove = null;
        foreach ((string key, TrackSlot existingSlot) in _tracks)
        {
            string? existingOwner = existingSlot.OwnerId;
            if (string.IsNullOrWhiteSpace(existingOwner) ||
                string.Equals(existingOwner, newOwnerId, StringComparison.Ordinal))
            {
                continue;
            }

            if (existingSlot.IsPlaying)
            {
                preemptedOwners ??= [];
                if (!preemptedOwners.Contains(existingOwner, StringComparer.Ordinal))
                {
                    preemptedOwners.Add(existingOwner);
                }
            }

            keysToRemove ??= [];
            keysToRemove.Add(key);
        }

        if (keysToRemove is not null)
        {
            foreach (string key in keysToRemove)
            {
                if (_tracks.Remove(key, out TrackSlot? removed))
                {
                    removed.Dispose();
                }
            }
        }
    }

    private void NotifyAmbientOwnersPreempted(List<string>? preemptedOwners)
    {
        if (preemptedOwners is null || preemptedOwners.Count == 0)
        {
            return;
        }

        Action<string>? handlers = AmbientOwnerPreempted;
        if (handlers is null)
        {
            return;
        }

        foreach (string previousOwnerId in preemptedOwners)
        {
            try
            {
                handlers.Invoke(previousOwnerId);
            }
            catch
            {
            }
        }
    }

    public void StopTracksForOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        string normalizedOwner = ownerId.Trim();
        string ambientTrackId = ResolveAmbientTrackId(normalizedOwner);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            List<string>? keysToRemove = null;
            foreach ((string key, TrackSlot slot) in _tracks)
            {
                if (string.Equals(slot.OwnerId, normalizedOwner, StringComparison.Ordinal) ||
                    string.Equals(key, ambientTrackId, StringComparison.OrdinalIgnoreCase))
                {
                    keysToRemove ??= [];
                    keysToRemove.Add(key);
                }
            }

            if (keysToRemove is not null)
            {
                foreach (string key in keysToRemove)
                {
                    if (_tracks.Remove(key, out TrackSlot? removed))
                    {
                        removed.Dispose();
                    }
                }
            }
        }
    }

    public Task<bool> PlayAmbientAsync(string? ownerId, string? sourcePathOrUrl, double volume = 0.25)
    {
        string ambientTrackId = ResolveAmbientTrackId(ownerId);
        string? normalizedOwner = string.IsNullOrWhiteSpace(ownerId) ? null : ownerId.Trim();
        bool started = PlayTrack(
            ambientTrackId,
            sourcePathOrUrl,
            widgetDirectory: null,
            loop: true,
            volume: volume,
            ambientOwnerId: normalizedOwner);
        return Task.FromResult(started);
    }

    public void StopAmbient(string? ownerId)
    {
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            StopTracksForOwner(ownerId);
            return;
        }

        StopTrack(ResolveAmbientTrackId(ownerId));
    }

    private static string ResolveAmbientTrackId(string? ownerId) =>
        string.IsNullOrWhiteSpace(ownerId) ? "ambient:default" : $"ambient:{ownerId.Trim()}";

    public void PauseTrack(string? trackId)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(trackId))
            {
                foreach (TrackSlot slot in _tracks.Values)
                {
                    slot.IsPlaying = false;
                    try { slot.Player?.Pause(); } catch { }
                }
                return;
            }

            if (_tracks.TryGetValue(trackId.Trim(), out TrackSlot? target))
            {
                target.IsPlaying = false;
                try { target.Player?.Pause(); } catch { }
            }
        }
    }

    public void StopTrack(string? trackId)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(trackId))
            {
                StopAllCore();
                return;
            }

            if (_tracks.Remove(trackId.Trim(), out TrackSlot? target))
            {
                target.Dispose();
            }
        }
    }

    public void SetVolume(string? trackId, double volume)
    {
        double clamped = Math.Clamp(volume, 0.0, 1.0);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(trackId))
            {
                foreach (TrackSlot slot in _tracks.Values)
                {
                    slot.Volume = clamped;
                    try { if (slot.Player is not null) slot.Player.Volume = clamped; } catch { }
                }
                return;
            }

            if (_tracks.TryGetValue(trackId.Trim(), out TrackSlot? target))
            {
                target.Volume = clamped;
                try { if (target.Player is not null) target.Player.Volume = clamped; } catch { }
            }
        }
    }

    public void StopAll()
    {
        lock (_sync)
        {
            StopAllCore();
        }
    }

    public void SetPowerSuspended(bool suspended)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            foreach (TrackSlot slot in _tracks.Values)
            {
                if (!slot.IsPlaying || slot.Player is null)
                {
                    continue;
                }

                try
                {
                    if (suspended)
                    {
                        slot.Player.Pause();
                    }
                    else
                    {
                        slot.Player.Play();
                    }
                }
                catch
                {
                }
            }
        }
    }

    private void StopAllCore()
    {
        foreach (TrackSlot slot in _tracks.Values)
        {
            slot.Dispose();
        }
        _tracks.Clear();

        foreach (CuePlayback cue in _transientCuePlayers)
        {
            cue.Dispose();
        }
        _transientCuePlayers.Clear();

        if (_disposed)
        {
            try
            {
                DeskBox.Platform.CustomWidgetSystemControlInterop.StopPinnedWaveMemory();
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopAllCore();
        }
    }

    internal static Uri? TryResolveMediaUri(string rawSource, string? widgetDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawSource))
        {
            return null;
        }

        string trimmed = rawSource.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? absUri) &&
            (absUri.Scheme == Uri.UriSchemeHttp ||
             absUri.Scheme == Uri.UriSchemeHttps ||
             absUri.Scheme == Uri.UriSchemeFile ||
             string.Equals(absUri.Scheme, "ms-appx", StringComparison.OrdinalIgnoreCase)))
        {
            return absUri;
        }

        if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
        {
            return new Uri(Path.GetFullPath(trimmed));
        }

        if (!string.IsNullOrWhiteSpace(widgetDirectory))
        {
            try
            {
                string fullBase = Path.GetFullPath(widgetDirectory);
                string combined = Path.GetFullPath(Path.Combine(fullBase, trimmed));
                if (combined.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase) && File.Exists(combined))
                {
                    return new Uri(combined);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static InMemoryRandomAccessStream CreateWavStream(byte[] wav)
    {
        var ras = new InMemoryRandomAccessStream();
        Stream writer = ras.AsStreamForWrite();
        writer.Write(wav, 0, wav.Length);
        writer.Flush();
        ras.Seek(0);
        return ras;
    }

    private bool TryAttachAndStartMediaPlayer(TrackSlot slot, string rawSource, string? widgetDirectory)
    {
        IRandomAccessStream? memStream = null;
        MediaSource? mediaSource = null;
        MediaPlayer? player = null;

        try
        {
            if (rawSource.StartsWith("mirror:", StringComparison.OrdinalIgnoreCase))
            {
                slot.IsPlaying = true;
                return true;
            }

            if (rawSource.StartsWith("cue:", StringComparison.OrdinalIgnoreCase))
            {
                string cueKey = ResolveCanonicalCueKey(rawSource["cue:".Length..].Trim().ToLowerInvariant());
                byte[] wav = SynthesizedCueWavCache.GetOrAdd(cueKey, BuildSynthesizedCueWav);
                memStream = CreateWavStream(wav);
                mediaSource = MediaSource.CreateFromStream(memStream, "audio/wav");
            }
            else if (rawSource.StartsWith("noise:", StringComparison.OrdinalIgnoreCase))
            {
                string noiseKind = ResolveCanonicalNoiseKind(rawSource["noise:".Length..].Trim().ToLowerInvariant());
                byte[] wav = SynthesizedCueWavCache.GetOrAdd("noise:" + noiseKind, static k => BuildAmbientNoiseWav(k["noise:".Length..]));
                memStream = CreateWavStream(wav);
                mediaSource = MediaSource.CreateFromStream(memStream, "audio/wav");
            }
            else if (TryResolveMediaUri(rawSource, widgetDirectory) is { } uri)
            {
                mediaSource = DeskBox.Platform.ClipboardStorageFileOps.CreateMediaSourceFromUri(uri, out memStream);
            }

            if (mediaSource is null)
            {
                slot.IsPlaying = false;
                try { memStream?.Dispose(); } catch { }
                return false;
            }

            player = new MediaPlayer
            {
                AutoPlay = true,
                Volume = slot.Volume,
                IsLoopingEnabled = slot.Loop
            };

            TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failedHandler = (sender, _) =>
                OnTrackMediaFailed(slot, sender);
            slot.MediaFailedHandler = failedHandler;
            player.MediaFailed += failedHandler;
            player.Source = mediaSource;

            slot.InMemoryStream = memStream;
            slot.MediaSource = mediaSource;
            slot.Player = player;
            slot.IsPlaying = true;
            player.Play();
            return true;
        }
        catch
        {
            slot.IsPlaying = false;
            slot.Player = null;
            slot.MediaSource = null;
            slot.InMemoryStream = null;

            if (player is not null)
            {
                if (slot.MediaFailedHandler is not null)
                {
                    try { player.MediaFailed -= slot.MediaFailedHandler; } catch { }
                    slot.MediaFailedHandler = null;
                }

                try
                {
                    player.Source = null;
                    player.Dispose();
                }
                catch
                {
                }
            }

            try { mediaSource?.Dispose(); } catch { }
            try { memStream?.Dispose(); } catch { }
            return false;
        }
    }

    private void OnTrackMediaFailed(TrackSlot slot, MediaPlayer sender)
    {
        string? failedOwnerId = null;
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(slot.Player, sender))
            {
                return;
            }

            slot.IsPlaying = false;
            if (!string.IsNullOrWhiteSpace(slot.OwnerId))
            {
                failedOwnerId = slot.OwnerId;
            }

            if (_tracks.TryGetValue(slot.Id, out TrackSlot? current) && ReferenceEquals(current, slot))
            {
                _tracks.Remove(slot.Id);
            }

            slot.Dispose();
        }

        if (failedOwnerId is not null)
        {
            try
            {
                AmbientOwnerPreempted?.Invoke(failedOwnerId);
            }
            catch
            {
            }
        }
    }

    private void TryPlayWavBytesOneShot(byte[] wavBytes, double volume)
    {
        InMemoryRandomAccessStream? ras = null;
        MediaSource? mediaSource = null;
        MediaPlayer? player = null;
        CuePlayback? cuePlayback = null;

        try
        {
            ras = CreateWavStream(wavBytes);
            mediaSource = MediaSource.CreateFromStream(ras, "audio/wav");
            player = new MediaPlayer
            {
                AutoPlay = true,
                Volume = volume,
                Source = mediaSource
            };

            cuePlayback = new CuePlayback(player, mediaSource!, ras);
            lock (_sync)
            {
                if (_disposed)
                {
                    cuePlayback.Dispose();
                    return;
                }

                while (_transientCuePlayers.Count >= 4)
                {
                    CuePlayback oldest = _transientCuePlayers[0];
                    _transientCuePlayers.RemoveAt(0);
                    oldest.Dispose();
                }

                _transientCuePlayers.Add(cuePlayback);
            }

            CuePlayback capturedCue = cuePlayback;
            player.MediaEnded += (_, _) =>
            {
                lock (_sync)
                {
                    _transientCuePlayers.Remove(capturedCue);
                }
                capturedCue.Dispose();
            };
            player.MediaFailed += (_, _) =>
            {
                lock (_sync)
                {
                    _transientCuePlayers.Remove(capturedCue);
                }
                capturedCue.Dispose();
            };

            player.Play();
        }
        catch (COMException)
        {
            CleanupFailedCuePlayback(cuePlayback, player, mediaSource, ras);
            bool disposed;
            lock (_sync)
            {
                disposed = _disposed;
            }

            if (!disposed)
            {
                TryPlayWavBytesViaWinMm(wavBytes, volume);
            }
        }
        catch
        {
            CleanupFailedCuePlayback(cuePlayback, player, mediaSource, ras);
        }
    }

    private void CleanupFailedCuePlayback(
        CuePlayback? cuePlayback,
        MediaPlayer? player,
        MediaSource? mediaSource,
        InMemoryRandomAccessStream? ras)
    {
        if (cuePlayback is not null)
        {
            lock (_sync)
            {
                _transientCuePlayers.Remove(cuePlayback);
            }
            cuePlayback.Dispose();
            return;
        }

        try
        {
            if (player is not null)
            {
                player.Source = null;
                player.Dispose();
            }
        }
        catch
        {
        }

        try { mediaSource?.Dispose(); } catch { }
        try { ras?.Dispose(); } catch { }
    }

    private static void TryPlayWavBytesViaWinMm(byte[] wavBytes, double volume)
    {
        if (wavBytes.Length < 44 || volume <= 0.001)
        {
            return;
        }

        try
        {
            double clampedVolume = Math.Clamp(volume, 0.0, 1.0);
            byte[] pinnedWav = GC.AllocateArray<byte>(wavBytes.Length, pinned: true);
            System.Buffer.BlockCopy(wavBytes, 0, pinnedWav, 0, wavBytes.Length);

            if (clampedVolume < 0.999 && pinnedWav.Length >= 46)
            {
                Span<byte> pcm = pinnedWav.AsSpan(44);
                int sampleCount = pcm.Length / 2;
                for (int i = 0; i < sampleCount; i++)
                {
                    Span<byte> sampleSpan = pcm.Slice(i * 2, 2);
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(sampleSpan);
                    short scaled = (short)Math.Clamp(Math.Round(sample * clampedVolume), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(sampleSpan, scaled);
                }
            }

            DeskBox.Platform.CustomWidgetSystemControlInterop.PlayPinnedWaveMemoryAsync(pinnedWav);
        }
        catch
        {
        }
    }

    internal static byte[] BuildSynthesizedCueWav(string cue)
    {
        return cue switch
        {
            "tick" or "click" => RenderNotesWav([(1480.0, 0.0, 0.022, 0.35)], totalSec: 0.03),
            "pop" => RenderNotesWav([(520.0, 0.0, 0.055, 0.45), (780.0, 0.02, 0.05, 0.35)], totalSec: 0.085),
            "soft" or "gentle" => RenderNotesWav([(349.23, 0.0, 0.28, 0.32), (523.25, 0.03, 0.32, 0.28)], totalSec: 0.38),
            "alert" or "warning" or "error" => RenderNotesWav([(880.0, 0.0, 0.11, 0.42), (659.25, 0.12, 0.16, 0.42)], totalSec: 0.30),
            "bell" => RenderNotesWav([(880.0, 0.0, 0.42, 0.38), (1760.0, 0.0, 0.24, 0.16)], totalSec: 0.45),
            _ => RenderNotesWav(
                [
                    (523.25, 0.00, 0.14, 0.34),
                    (659.25, 0.08, 0.16, 0.34),
                    (783.99, 0.16, 0.26, 0.38)
                ],
                totalSec: 0.45)
        };
    }

    internal static byte[] BuildAmbientNoiseWav(string kind)
    {
        // Seamless 4-second synthesized ambient loop ("noise:white", "noise:pink", "noise:brown", "noise:rain").
        const int crossfadeSamples = 2048;
        int samples = SampleRate * 4;
        int totalGenerated = samples + crossfadeSamples;
        double[] rawSamples = ArrayPool<double>.Shared.Rent(totalGenerated);
        try
        {
            uint rng = 0x9E3779B9u;
            double brown = 0;
            double b0 = 0, b1 = 0, b2 = 0;
            double rainLp = 0, rainHp = 0, dropletEnv = 0;

            for (int i = 0; i < totalGenerated; i++)
            {
                rng ^= rng << 13;
                rng ^= rng >> 17;
                rng ^= rng << 5;
                double white = ((rng & 0xFFFF) / 32767.5) - 1.0;

                double val;
                if (kind == "brown")
                {
                    brown = (brown + (0.02 * white)) / 1.02;
                    val = brown * 3.2;
                }
                else if (kind == "rain")
                {
                    // Soft rainfall texture: band-shaped pink/velvet bed + gentle droplet micro-patter.
                    b0 = 0.995 * b0 + white * 0.11;
                    b1 = 0.940 * b1 + white * 0.28;
                    double pinkBed = (b0 + b1 + white * 0.14) * 0.14;
                    rainLp += 0.18 * (pinkBed - rainLp);
                    rainHp += 0.015 * (rainLp - rainHp);
                    double band = rainLp - rainHp;

                    if ((rng & 0x3FFu) == 0x155u)
                    {
                        dropletEnv = Math.Max(dropletEnv, 0.22 + (((rng >> 10) & 0xFF) / 255.0) * 0.28);
                    }
                    dropletEnv *= 0.988;
                    val = (band * 1.45) + (white * dropletEnv * 0.32);
                }
                else if (kind == "pink")
                {
                    b0 = 0.99765 * b0 + white * 0.0990460;
                    b1 = 0.96300 * b1 + white * 0.2965164;
                    b2 = 0.57000 * b2 + white * 1.0526913;
                    val = (b0 + b1 + b2 + white * 0.1848) * 0.16;
                }
                else
                {
                    val = white * 0.22;
                }

                rawSamples[i] = val;
            }

            byte[] wav = AllocatePcm16MonoWav(samples, out Span<byte> pcm);
            for (int i = 0; i < samples; i++)
            {
                double val = rawSamples[i];
                if (i < crossfadeSamples)
                {
                    double t = (double)i / crossfadeSamples;
                    val = (val * t) + (rawSamples[samples + i] * (1.0 - t));
                }

                short sample = (short)Math.Clamp(val * 28000.0, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(i * 2, 2), sample);
            }

            return wav;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rawSamples);
        }
    }

    private static byte[] RenderNotesWav(
        ReadOnlySpan<(double FreqHz, double StartSec, double DurationSec, double Amp)> notes,
        double totalSec)
    {
        int totalSamples = Math.Max(64, (int)(SampleRate * totalSec));
        byte[] wav = AllocatePcm16MonoWav(totalSamples, out Span<byte> pcm);

        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / SampleRate;
            double mix = 0.0;
            foreach ((double freq, double start, double dur, double amp) in notes)
            {
                double dt = t - start;
                if (dt < 0 || dt > dur)
                {
                    continue;
                }

                double attack = Math.Min(1.0, dt / 0.006);
                double decay = Math.Exp(-4.8 * (dt / Math.Max(0.015, dur)));
                double fundamental = Math.Sin(2.0 * Math.PI * freq * dt);
                double overtone = 0.22 * Math.Sin(4.0 * Math.PI * freq * dt);
                mix += (fundamental + overtone) * attack * decay * amp;
            }

            short sample = (short)Math.Clamp(mix * 30000.0, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(i * 2, 2), sample);
        }

        return wav;
    }

    private static byte[] AllocatePcm16MonoWav(int sampleCount, out Span<byte> pcmData)
    {
        int dataBytes = sampleCount * 2;
        byte[] buffer = new byte[44 + dataBytes];
        Span<byte> span = buffer.AsSpan();

        "RIFF"u8.CopyTo(span[0..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..8], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..12]);
        "fmt "u8.CopyTo(span[12..16]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..22], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..24], 1); // Mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..28], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..32], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..34], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..36], 16);
        "data"u8.CopyTo(span[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..44], dataBytes);

        pcmData = span[44..];
        return buffer;
    }
}
