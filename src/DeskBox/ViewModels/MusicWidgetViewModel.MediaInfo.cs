using System.Buffers.Binary;
using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.Services.Lyrics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace DeskBox.ViewModels;

public sealed partial class MusicWidgetViewModel
{
    private async Task ApplyInfoAsync(MusicSessionInfo? info)
    {
        if (ShouldDeferEmptyInfo(info))
        {
            ScheduleTransientEmptyInfoRetry();
            return;
        }

        ResetTransientEmptyInfoRetry();
        if (info is null)
        {
            _hasCurrentMediaInfo = false;
            Title = string.Empty;
            Artist = string.Empty;
            Album = string.Empty;
            _sourceAppUserModelId = string.Empty;
            SourceDisplayName = string.Empty;
            _timelineExtrapolator.Reset();
            PlaybackState = MusicPlaybackState.Unknown;
            UpdateProgressTimerState();
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            UpdateTimelineRowEligibility();
            ThumbnailImage = null;
            _lastCoverSignature = null;
            _lastCoverFingerprint = 0;
            _confirmedEmptyCoverSignature = null;
            _coverRetrySignature = null;
            _coverRetryCount = 0;
            _coverRetryInFlight = false;
            _lastTrackIdentitySignature = null;
            _previousTrackNativeDuration = TimeSpan.Zero;
            _currentTrackInitialNativeDuration = TimeSpan.Zero;
            _currentTrackDurationUntrusted = false;
            SetArtworkColor(AccentColorHelper.DefaultAccentColor, hasArtworkColor: false);
            CanPlay = false;
            CanPause = false;
            CanGoPrevious = false;
            CanGoNext = false;
            CanSeek = false;
            CanChangeShuffle = false;
            CanChangeRepeat = false;
            PlaybackMode = MusicPlaybackMode.Normal;
            HasSessionVolume = false;
            IsLyricsViewOpen = false;
            ClearLyricsState();
            RaiseDisplayPropertiesChanged();
            return;
        }

        _hasCurrentMediaInfo = true;
        Title = info.Title;
        Artist = info.Artist;
        Album = info.Album;
        _sourceAppUserModelId = info.SourceAppUserModelId;
        SourceDisplayName = info.SourceDisplayName;

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        string trackIdentitySig = $"{info.SourceAppUserModelId}\u001F{info.Title.Trim()}\u001F{info.Artist.Trim()}";
        bool trackIdentityChanged = !string.Equals(_lastTrackIdentitySignature, trackIdentitySig, StringComparison.Ordinal);
        if (trackIdentityChanged)
        {
            TimeSpan prevNativeDuration = _timelineExtrapolator.HasNativeDuration ? _duration : TimeSpan.Zero;
            _previousTrackNativeDuration = _lastTrackIdentitySignature is not null ? prevNativeDuration : TimeSpan.Zero;
            _lastTrackIdentitySignature = trackIdentitySig;
            _currentTrackInitialNativeDuration = info.Duration;
            _currentTrackDurationUntrusted = _previousTrackNativeDuration > TimeSpan.Zero &&
                info.Duration > TimeSpan.Zero &&
                Math.Abs((info.Duration - _previousTrackNativeDuration).TotalMilliseconds) < 50 &&
                !IsFreshTrackStartTimeline(info.Position, info.PositionUpdatedUtc, nowUtc);
            _hasRetriedLyricsWithUpdatedDuration = false;
        }
        else if (_currentTrackDurationUntrusted &&
                 info.Duration > TimeSpan.Zero &&
                 (Math.Abs((info.Duration - _currentTrackInitialNativeDuration).TotalMilliseconds) >= 500 ||
                  IsFreshTrackStartTimeline(info.Position, info.PositionUpdatedUtc, nowUtc)))
        {
            _currentTrackDurationUntrusted = false;
        }

        (TimeSpan extrapolatedPos, TimeSpan effectiveDur) = _timelineExtrapolator.ApplyFullSnapshot(
            info.SourceAppUserModelId,
            info.Title,
            info.Artist,
            info.PlaybackState,
            info.Position,
            info.Duration,
            info.PositionUpdatedUtc,
            info.PlaybackRate,
            nowUtc);

        PlaybackState = info.PlaybackState;
        Duration = effectiveDur;
        Position = ShouldExposeTimelinePosition() ? extrapolatedPos : TimeSpan.Zero;
        CanPlay = info.CanPlay;
        CanPause = info.CanPause;
        CanGoPrevious = info.CanGoPrevious;
        CanGoNext = info.CanGoNext;
        CanSeek = info.CanSeek && _timelineExtrapolator.HasNativeDuration;
        CanChangeShuffle = info.CanChangeShuffle;
        CanChangeRepeat = info.CanChangeRepeat;
        PlaybackMode = info.PlaybackMode;
        UpdateProgressTimerState();
        UpdateTimelineRowEligibility();

        ScheduleLyricsLookupForCurrentTrack();
        if (!trackIdentityChanged && _timelineExtrapolator.HasNativeDuration)
        {
            NotifyTimelineDurationUpdated(info.Duration, info.Position, info.PositionUpdatedUtc, nowUtc);
        }

        UpdateLyricActiveLineAndScheduleTimer();

        string coverSig = $"{info.SessionId}\u001E{info.Title}\u001E{info.Artist}\u001E{info.Album}";
        bool coverChanged = !string.Equals(_lastCoverSignature, coverSig, StringComparison.Ordinal);

        if (info.Thumbnail is null)
        {
            if (!string.Equals(_confirmedEmptyCoverSignature, coverSig, StringComparison.Ordinal) &&
                !(string.Equals(_lastCoverSignature, coverSig, StringComparison.Ordinal) && ThumbnailImage is not null) &&
                !(_coverRetryInFlight && string.Equals(_coverRetrySignature, coverSig, StringComparison.Ordinal)))
            {
                int gen = ++_coverGeneration;
                _lastCoverSignature = null;
                _lastCoverFingerprint = 0;
                if (!ScheduleCoverRetry(coverSig, gen))
                {
                    _confirmedEmptyCoverSignature = coverSig;
                    ThumbnailImage = null;
                    SetArtworkColor(AccentColorHelper.DefaultAccentColor, hasArtworkColor: false);
                }
            }
        }
        else
        {
            _confirmedEmptyCoverSignature = null;
            byte[]? thumbnailBytes = await ReadThumbnailBytesAsync(info.Thumbnail);
            if (_isDisposed)
            {
                return;
            }

            ulong fingerprint = ComputeThumbnailFingerprint(thumbnailBytes);
            bool shouldDecodeCover = coverChanged ||
                ThumbnailImage is null ||
                (fingerprint != 0 && fingerprint != _lastCoverFingerprint);

            if (shouldDecodeCover)
            {
                int gen = ++_coverGeneration;
                _coverRetryInFlight = false;
                var (cover, artworkColor) = await DecodeThumbnailAndColorAsync(thumbnailBytes);
                if (_isDisposed || gen != _coverGeneration)
                {
                    return;
                }

                if (cover is not null)
                {
                    ThumbnailImage = cover;
                    SetArtworkColor(artworkColor ?? AccentColorHelper.DefaultAccentColor, artworkColor.HasValue);
                    _lastCoverSignature = coverSig;
                    _lastCoverFingerprint = fingerprint;
                    _coverRetrySignature = null;
                    _coverRetryCount = 0;
                }
                else
                {
                    _lastCoverSignature = null;
                    _lastCoverFingerprint = 0;
                    if (!ScheduleCoverRetry(coverSig, gen))
                    {
                        _confirmedEmptyCoverSignature = coverSig;
                        ThumbnailImage = null;
                        SetArtworkColor(AccentColorHelper.DefaultAccentColor, hasArtworkColor: false);
                    }
                }
            }
        }

        RaiseDisplayPropertiesChanged();
    }

    private void CancelPendingLyricsLookup()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _lyricsFetchCts, null);
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cts.Dispose();
            }
        }
    }

    private void ClearLyricsState()
    {
        CancelPendingLyricsLookup();
        _lastLyricsTrackSignature = null;
        _lastLyricsQueryDuration = TimeSpan.Zero;
        _hasRetriedLyricsWithUpdatedDuration = false;
        ApplyResolvedLyrics(LrcDocument.Empty, isLoading: false);
    }

    private static bool IsFreshTrackStartTimeline(
        TimeSpan position,
        DateTimeOffset updatedUtc,
        DateTimeOffset nowUtc) =>
        position >= TimeSpan.Zero &&
        position < TimeSpan.FromSeconds(5) &&
        MusicTimelineExtrapolator.IsTimestampUsable(updatedUtc, nowUtc) &&
        (nowUtc - updatedUtc).TotalSeconds < 2.0;

    private void NotifyTimelineDurationUpdated(
        TimeSpan nativeDuration,
        TimeSpan rawPosition,
        DateTimeOffset updatedUtc,
        DateTimeOffset nowUtc)
    {
        if (nativeDuration <= TimeSpan.FromSeconds(5))
        {
            return;
        }

        if (_currentTrackDurationUntrusted)
        {
            if (Math.Abs((nativeDuration - _currentTrackInitialNativeDuration).TotalMilliseconds) >= 500 ||
                 IsFreshTrackStartTimeline(rawPosition, updatedUtc, nowUtc))
            {
                _currentTrackDurationUntrusted = false;
            }
            else
            {
                return;
            }
        }

        if (!_hasCurrentMediaInfo ||
            !ShowLyrics ||
            string.IsNullOrWhiteSpace(_title) ||
            _hasRetriedLyricsWithUpdatedDuration)
        {
            return;
        }

        bool queriedWithoutMatchingDuration =
            _lastLyricsQueryDuration <= TimeSpan.Zero ||
            Math.Abs((nativeDuration - _lastLyricsQueryDuration).TotalSeconds) > 5.0;
        if (!queriedWithoutMatchingDuration)
        {
            return;
        }

        if (_isLoadingLyrics || (!_currentLyrics.HasSyncedLines && !_currentLyrics.IsInstrumental))
        {
            _hasRetriedLyricsWithUpdatedDuration = true;
            _lyricsService.InvalidateNegativeCache(_title, _artist);
            _lastLyricsTrackSignature = null;
            ScheduleLyricsLookupForCurrentTrack();
        }
    }

    private void ScheduleLyricsLookupForCurrentTrack()
    {
        if (!_hasCurrentMediaInfo || !ShowLyrics || string.IsNullOrWhiteSpace(_title))
        {
            if (!ShowLyrics || string.IsNullOrWhiteSpace(_title))
            {
                CancelPendingLyricsLookup();
                _lastLyricsTrackSignature = null;
                _lastLyricsQueryDuration = TimeSpan.Zero;
                _hasRetriedLyricsWithUpdatedDuration = false;
                ApplyResolvedLyrics(LrcDocument.Empty, isLoading: false);
            }

            return;
        }

        string signature = $"{_title.Trim()}\u001F{_artist.Trim()}\u001F{_localLyricsFolder}";
        if (string.Equals(_lastLyricsTrackSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        _lastLyricsTrackSignature = signature;
        // Clear stale lyrics from the previous song immediately while marking lookup in progress.
        ApplyResolvedLyrics(LrcDocument.Empty, isLoading: true);

        CancelPendingLyricsLookup();
        var cts = new CancellationTokenSource();
        _lyricsFetchCts = cts;
        TimeSpan queryDuration = _timelineExtrapolator.HasNativeDuration && !_currentTrackDurationUntrusted
            ? Duration
            : TimeSpan.Zero;
        _lastLyricsQueryDuration = queryDuration;
        var query = new LyricsQuery(_title, _artist, _album, queryDuration);
        string localFolder = _localLyricsFolder;

        _ = ResolveTrackLyricsAsync(signature, query, localFolder, cts.Token);
    }

    private async Task ResolveTrackLyricsAsync(
        string signature,
        LyricsQuery query,
        string localFolder,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(LyricsLookupDebounceMs, cancellationToken);
            LrcDocument resolved = await _lyricsService
                .ResolveLyricsAsync(query, localFolder, cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || _isDisposed)
            {
                return;
            }

            if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isDisposed &&
                        !cancellationToken.IsCancellationRequested &&
                        string.Equals(_lastLyricsTrackSignature, signature, StringComparison.Ordinal))
                    {
                        ApplyResolvedLyrics(resolved, isLoading: false);
                    }
                });
                return;
            }

            if (string.Equals(_lastLyricsTrackSignature, signature, StringComparison.Ordinal))
            {
                ApplyResolvedLyrics(resolved, isLoading: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                App.Log($"[MusicWidget] Lyrics lookup failed: {ex.Message}");
                if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
                {
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        if (!_isDisposed && string.Equals(_lastLyricsTrackSignature, signature, StringComparison.Ordinal))
                        {
                            ApplyResolvedLyrics(LrcDocument.Empty, isLoading: false);
                        }
                    });
                }
                else if (string.Equals(_lastLyricsTrackSignature, signature, StringComparison.Ordinal))
                {
                    ApplyResolvedLyrics(LrcDocument.Empty, isLoading: false);
                }
            }
        }
    }

    private void ApplyResolvedLyrics(LrcDocument document, bool isLoading = false)
    {
        _isLoadingLyrics = isLoading;
        _currentLyrics = document;
        LyricLines.Clear();
        for (int i = 0; i < document.Lines.Count; i++)
        {
            LyricLines.Add(document.Lines[i]);
        }

        // Backfill duration for timeline-less players when LRCLIB or local LRC provides track length!
        if (!_timelineExtrapolator.HasNativeDuration && document.TrackDuration is { TotalSeconds: > 0 } lyricDuration)
        {
            (TimeSpan pos, TimeSpan dur) = _timelineExtrapolator.SetFallbackDuration(lyricDuration, DateTimeOffset.UtcNow);
            Duration = dur;
            if (!_isSeeking)
            {
                Position = pos;
            }
        }
        else if (!_timelineExtrapolator.HasNativeDuration && document.TrackDuration is null)
        {
            (TimeSpan pos, TimeSpan dur) = _timelineExtrapolator.SetFallbackDuration(TimeSpan.Zero, DateTimeOffset.UtcNow);
            Duration = dur;
            if (!_isSeeking)
            {
                Position = ShouldExposeTimelinePosition() ? pos : TimeSpan.Zero;
            }
        }

        OnPropertyChanged(nameof(LyricsListVisibility));
        OnPropertyChanged(nameof(LyricsPlaceholderVisibility));
        OnPropertyChanged(nameof(LyricsPlaceholderText));
        OnPropertyChanged(nameof(LyricsOffsetText));
        OnPropertyChanged(nameof(HasNonZeroLyricsOffset));
        OnPropertyChanged(nameof(CurrentLyricDurationSeconds));
        UpdateLyricActiveLineAndScheduleTimer();
        RefreshSubtitleState();
    }

    private void UpdateLyricActiveLineAndScheduleTimer()
    {
        _lyricTimer?.Stop();

        if (_isDisposed || !ShowLyrics || !_hasCurrentMediaInfo || !_currentLyrics.HasSyncedLines)
        {
            bool previewReset = _isLyricIntroPreview;
            _isLyricIntroPreview = false;
            ActiveLyricIndex = -1;
            CurrentLyricLine = string.Empty;
            CurrentLyricSecondaryLine = null;
            if (previewReset)
            {
                RefreshSubtitleState();
            }

            UpdateMusicTimerDiagnostics();
            return;
        }

        TimeSpan effectivePos = _timelineExtrapolator.GetPosition(DateTimeOffset.UtcNow);
        int activeIdx = _currentLyrics.FindActiveLineIndex(effectivePos);
        ActiveLyricIndex = activeIdx;

        bool nextIntroPreview;
        if (activeIdx >= 0 && activeIdx < _currentLyrics.Lines.Count)
        {
            LrcLine line = _currentLyrics.Lines[activeIdx];
            nextIntroPreview = false;
            bool previewChanged = _isLyricIntroPreview != nextIntroPreview;
            _isLyricIntroPreview = nextIntroPreview;
            CurrentLyricLine = line.PrimaryText;
            CurrentLyricSecondaryLine = line.SecondaryText;
            if (previewChanged)
            {
                RefreshSubtitleState();
            }
        }
        else
        {
            // During the track intro before the first vocal timestamp, preview line 0 at softer opacity.
            LrcLine openingLine = _currentLyrics.Lines[0];
            nextIntroPreview = true;
            bool previewChanged = _isLyricIntroPreview != nextIntroPreview;
            _isLyricIntroPreview = nextIntroPreview;
            CurrentLyricLine = openingLine.PrimaryText;
            CurrentLyricSecondaryLine = openingLine.SecondaryText;
            if (previewChanged)
            {
                RefreshSubtitleState();
            }
        }

        bool shouldScheduleNextWakeup =
            _lyricTimer is not null &&
            _isWindowVisible &&
            _isWindowRevealCompleted &&
            PlaybackState == MusicPlaybackState.Playing &&
            (!_isCompactCollapsed || ShowCapsuleLyrics);

        if (shouldScheduleNextWakeup)
        {
            TimeSpan? delay = _currentLyrics.GetNextTransitionDelay(effectivePos, _timelineExtrapolator.PlaybackRate);
            if (delay.HasValue)
            {
                _lyricTimer!.Interval = delay.Value;
                _lyricTimer.Start();
            }
        }

        UpdateMusicTimerDiagnostics();
    }

    private bool ShouldDeferEmptyInfo(MusicSessionInfo? info)
    {
        bool incomingInfoIsEmpty = info is null || string.IsNullOrWhiteSpace(info.Title);
        bool hasStableDisplay = !string.IsNullOrWhiteSpace(_title) || ThumbnailImage is not null;
        return incomingInfoIsEmpty &&
               hasStableDisplay &&
               _transientEmptyInfoRetryCount < MaxTransientEmptyInfoRetries;
    }

    private void ScheduleTransientEmptyInfoRetry()
    {
        if (_transientEmptyInfoRetryCount == 0)
        {
            ++_transientEmptyInfoGeneration;
        }

        int generation = _transientEmptyInfoGeneration;
        int retryNumber = ++_transientEmptyInfoRetryCount;
        _ = RetryTransientEmptyInfoAsync(generation, retryNumber);
    }

    private async Task RetryTransientEmptyInfoAsync(int generation, int retryNumber)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(80 + retryNumber * 60));
        if (_isDisposed || generation != _transientEmptyInfoGeneration)
        {
            return;
        }

        ScheduleFullRefresh();
    }

    private void ResetTransientEmptyInfoRetry()
    {
        _transientEmptyInfoRetryCount = 0;
        ++_transientEmptyInfoGeneration;
    }

    private void SetArtworkColor(Color value, bool hasArtworkColor)
    {
        Color clampedBackdrop = hasArtworkColor
            ? MusicDominantColorExtractor.ClampBackdropColor(value)
            : value;
        bool didChangeColor = !_artworkColor.Equals(value) || !_backdropColor.Equals(clampedBackdrop);
        bool didChangeAvailability = _hasArtworkColor != hasArtworkColor;

        _artworkColor = value;
        _backdropColor = clampedBackdrop;
        _hasArtworkColor = hasArtworkColor;

        if (didChangeColor)
        {
            OnPropertyChanged(nameof(ArtworkBackdropStartColor));
            OnPropertyChanged(nameof(ArtworkBackdropMidColor));
            OnPropertyChanged(nameof(ArtworkBackdropEndColor));
        }

        if (didChangeColor || didChangeAvailability)
        {
            RaiseMusicAccentPropertiesChanged();
        }
    }

    private Color GetMusicAccentColor()
    {
        if (App.Current.ThemeService?.UsesSystemAccentColor == false)
        {
            return App.Current.ThemeService.GetEffectiveAccentColor();
        }

        if (UseArtworkBackdrop && _hasArtworkColor)
        {
            return MusicDominantColorExtractor.EnsureReadableAccentColor(
                _artworkColor,
                TryIsLightApplicationTheme());
        }

        try
        {
            return App.Current.ThemeService?.GetEffectiveAccentColor()
                ?? AccentColorHelper.DefaultAccentColor;
        }
        catch
        {
            return AccentColorHelper.DefaultAccentColor;
        }
    }

    private static bool TryIsLightApplicationTheme()
    {
        try
        {
            return Application.Current?.RequestedTheme == ApplicationTheme.Light;
        }
        catch
        {
            return false;
        }
    }

    private void RaiseMusicAccentPropertiesChanged()
    {
        OnPropertyChanged(nameof(MusicAccentBrush));
        OnPropertyChanged(nameof(PlayPauseButtonBackgroundBrush));
    }

    private const int CoverDecodePixelWidth = 192;
    private const int MaxCoverDecodedDimension = 512;
    private const int DominantColorSampleMaxDimension = 32;
    private const int MaxCoverRetryCount = 3;
    private const ulong MaxCoverStreamBytes = 32UL * 1024 * 1024;

    private static async Task<byte[]?> ReadThumbnailBytesAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using IRandomAccessStreamWithContentType stream = await thumbnail.OpenReadAsync();
            ulong rawSize = stream.Size;
            if (rawSize == 0 || rawSize > MaxCoverStreamBytes)
            {
                return null;
            }

            byte[] bytes = new byte[(int)rawSize];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                uint loaded = await reader.LoadAsync((uint)bytes.Length);
                if (loaded == 0)
                {
                    reader.DetachStream();
                    return null;
                }

                if (loaded < bytes.Length)
                {
                    Array.Resize(ref bytes, (int)loaded);
                }

                reader.ReadBytes(bytes);
                reader.DetachStream();
            }

            return bytes;
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Failed to read thumbnail stream: {ex.Message}");
            return null;
        }
    }

    internal static ulong ComputeThumbnailFingerprint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0;
        }

        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        ulong hash = (fnvOffset ^ (ulong)bytes.Length) * fnvPrime;

        static ulong HashSlice(ulong current, ReadOnlySpan<byte> slice)
        {
            for (int i = 0; i < slice.Length; i++)
            {
                current ^= slice[i];
                current *= fnvPrime;
            }

            return current;
        }

        if (bytes.Length <= 192)
        {
            hash = HashSlice(hash, bytes);
        }
        else
        {
            hash = HashSlice(hash, bytes[..64]);
            int midStart = (bytes.Length / 2) - 32;
            hash = HashSlice(hash, bytes.Slice(midStart, 64));
            hash = HashSlice(hash, bytes[^64..]);
        }

        return hash == 0 ? 1UL : hash;
    }

    private static async Task<(BitmapImage? image, Color? color)> DecodeThumbnailAndColorAsync(byte[]? thumbnailBytes)
    {
        if (thumbnailBytes is null || thumbnailBytes.Length == 0)
        {
            return (null, null);
        }

        try
        {
            PerformanceLogger.RecordMusicCoverDecode();

            // Decode the compressed SMTC cover stream once on a background worker at <= 192px,
            // derive the 32x32 dominant-color sample directly from those decoded BGRA8 pixels,
            // and hand XAML an uncompressed 32bpp BMP payload so UI-thread BitmapImage setup
            // avoids running a second WIC JPEG/PNG/WebP decompression pass.
            (byte[]? bmpBytes, int decodedWidth, Color? color) = await Task.Run(
                () => DecodeScaledBmpAndColorCoreAsync(thumbnailBytes));
            if (bmpBytes is null || decodedWidth <= 0)
            {
                return (null, null);
            }

            using var uiStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(uiStream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bmpBytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            uiStream.Seek(0);
            var image = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = decodedWidth
            };
            await image.SetSourceAsync(uiStream);
            return (image, color);
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Failed to load thumbnail/color: {ex.Message}");
            return (null, null);
        }
    }

    private bool ScheduleCoverRetry(string coverSignature, int generation)
    {
        if (!string.Equals(_coverRetrySignature, coverSignature, StringComparison.Ordinal))
        {
            _coverRetrySignature = coverSignature;
            _coverRetryCount = 0;
        }

        if (_coverRetryCount >= MaxCoverRetryCount)
        {
            _coverRetryInFlight = false;
            return false;
        }

        int retryNumber = ++_coverRetryCount;
        _coverRetryInFlight = true;
        _ = RetryCoverAsync(coverSignature, generation, retryNumber);
        return true;
    }

    private async Task RetryCoverAsync(string coverSignature, int generation, int retryNumber)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250 * retryNumber));
        if (_isDisposed ||
            generation != _coverGeneration ||
            !string.Equals(_coverRetrySignature, coverSignature, StringComparison.Ordinal) ||
            string.Equals(_lastCoverSignature, coverSignature, StringComparison.Ordinal))
        {
            return;
        }

        _coverRetryInFlight = false;
        await RefreshAsync();
    }

    private static async Task<(byte[]? bmpBytes, int decodedWidth, Color? color)> DecodeScaledBmpAndColorCoreAsync(
        byte[] thumbnailBytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(thumbnailBytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            uint srcWidth = decoder.PixelWidth;
            uint srcHeight = decoder.PixelHeight;
            if (srcWidth == 0 || srcHeight == 0)
            {
                return (null, 0, null);
            }

            uint targetWidth = Math.Min((uint)CoverDecodePixelWidth, srcWidth);
            uint targetHeight = Math.Max(1u, (uint)Math.Round((double)srcHeight * targetWidth / srcWidth));
            if (targetHeight > MaxCoverDecodedDimension)
            {
                targetWidth = Math.Max(1u, (uint)Math.Round((double)targetWidth * MaxCoverDecodedDimension / targetHeight));
                targetHeight = MaxCoverDecodedDimension;
            }

            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            PixelDataProvider data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = data.DetachPixelData();
            int expectedBytes = checked((int)targetWidth * (int)targetHeight * 4);
            if (pixels.Length < expectedBytes)
            {
                return (null, 0, null);
            }

            byte[] sampled = SamplePremultipliedBgraGrid(
                pixels,
                (int)targetWidth,
                (int)targetHeight,
                DominantColorSampleMaxDimension);
            Color? color = sampled.Length < 4
                ? null
                : MusicDominantColorExtractor.ExtractDominantColor(sampled);

            byte[] bmpBytes = EncodeTopDownBgraBmp(pixels, (int)targetWidth, (int)targetHeight);
            return (bmpBytes, (int)targetWidth, color);
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Failed to decode thumbnail/color: {ex.Message}");
            return (null, 0, null);
        }
    }

    private static byte[] SamplePremultipliedBgraGrid(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int maxDimension)
    {
        int sampleWidth = Math.Max(1, Math.Min(maxDimension, width));
        int sampleHeight = Math.Max(1, Math.Min(maxDimension, height));
        if (sampleWidth == width && sampleHeight == height)
        {
            return bgra[..(width * height * 4)].ToArray();
        }

        byte[] sampled = new byte[sampleWidth * sampleHeight * 4];
        int dstOffset = 0;
        for (int sy = 0; sy < sampleHeight; sy++)
        {
            int srcY = (sy * height) / sampleHeight;
            int rowStart = srcY * width;
            for (int sx = 0; sx < sampleWidth; sx++)
            {
                int srcX = (sx * width) / sampleWidth;
                int srcOffset = (rowStart + srcX) * 4;
                sampled[dstOffset] = bgra[srcOffset];
                sampled[dstOffset + 1] = bgra[srcOffset + 1];
                sampled[dstOffset + 2] = bgra[srcOffset + 2];
                sampled[dstOffset + 3] = bgra[srcOffset + 3];
                dstOffset += 4;
            }
        }

        return sampled;
    }

    private static byte[] EncodeTopDownBgraBmp(ReadOnlySpan<byte> premultipliedBgra, int width, int height)
    {
        int pixelByteLength = checked(width * height * 4);
        ReadOnlySpan<byte> pixelSlice = premultipliedBgra[..pixelByteLength];

        bool hasNonOpaqueAlpha = false;
        for (int i = 3; i < pixelSlice.Length; i += 4)
        {
            if (pixelSlice[i] != 255)
            {
                hasNonOpaqueAlpha = true;
                break;
            }
        }

        if (!hasNonOpaqueAlpha)
        {
            const int v3HeaderSize = 54;
            byte[] bmp = new byte[v3HeaderSize + pixelByteLength];
            Span<byte> span = bmp;
            span[0] = (byte)'B';
            span[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(2, 4), bmp.Length);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(10, 4), v3HeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(14, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(18, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(22, 4), -height);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(26, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(28, 2), 32);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(34, 4), pixelByteLength);
            pixelSlice.CopyTo(span[v3HeaderSize..]);
            return bmp;
        }

        // Preserve straight alpha via BITMAPV4HEADER (BI_BITFIELDS + sRGB) when a cover has transparency.
        const int v4HeaderSize = 122;
        byte[] alphaBmp = new byte[v4HeaderSize + pixelByteLength];
        Span<byte> alphaSpan = alphaBmp;
        alphaSpan[0] = (byte)'B';
        alphaSpan[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(alphaSpan.Slice(2, 4), alphaBmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(alphaSpan.Slice(10, 4), v4HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(alphaSpan.Slice(14, 4), 108);
        BinaryPrimitives.WriteInt32LittleEndian(alphaSpan.Slice(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(alphaSpan.Slice(22, 4), -height);
        BinaryPrimitives.WriteUInt16LittleEndian(alphaSpan.Slice(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(alphaSpan.Slice(28, 2), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(alphaSpan.Slice(30, 4), 3u); // BI_BITFIELDS
        BinaryPrimitives.WriteInt32LittleEndian(alphaSpan.Slice(34, 4), pixelByteLength);
        BinaryPrimitives.WriteUInt32LittleEndian(alphaSpan.Slice(54, 4), 0x00FF0000u);
        BinaryPrimitives.WriteUInt32LittleEndian(alphaSpan.Slice(58, 4), 0x0000FF00u);
        BinaryPrimitives.WriteUInt32LittleEndian(alphaSpan.Slice(62, 4), 0x000000FFu);
        BinaryPrimitives.WriteUInt32LittleEndian(alphaSpan.Slice(66, 4), 0xFF000000u);
        BinaryPrimitives.WriteUInt32LittleEndian(alphaSpan.Slice(70, 4), 0x73524742u); // LCS_sRGB

        Span<byte> dstPixels = alphaSpan[v4HeaderSize..];
        for (int i = 0; i < pixelByteLength; i += 4)
        {
            byte a = pixelSlice[i + 3];
            if (a == 0)
            {
                dstPixels[i] = 0;
                dstPixels[i + 1] = 0;
                dstPixels[i + 2] = 0;
                dstPixels[i + 3] = 0;
            }
            else if (a == 255)
            {
                dstPixels[i] = pixelSlice[i];
                dstPixels[i + 1] = pixelSlice[i + 1];
                dstPixels[i + 2] = pixelSlice[i + 2];
                dstPixels[i + 3] = 255;
            }
            else
            {
                int half = a >> 1;
                dstPixels[i] = (byte)Math.Min(255, (pixelSlice[i] * 255 + half) / a);
                dstPixels[i + 1] = (byte)Math.Min(255, (pixelSlice[i + 1] * 255 + half) / a);
                dstPixels[i + 2] = (byte)Math.Min(255, (pixelSlice[i + 2] * 255 + half) / a);
                dstPixels[i + 3] = a;
            }
        }

        return alphaBmp;
    }
}
