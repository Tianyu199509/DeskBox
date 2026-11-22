using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    private sealed class ManagedMediaPlayerSlot
    {
        private int _sourceGeneration;
        private int _isDisposed;

        public required MediaPlayerElement Element { get; init; }
        public required MediaPlayer Player { get; init; }
        public string AppliedSource { get; set; } = "\0";
        public string AppliedPoster { get; set; } = "\0";
        public int SourceGeneration => Volatile.Read(ref _sourceGeneration);
        public int IncrementSourceGeneration() => Interlocked.Increment(ref _sourceGeneration);
        public int PosterGeneration { get; set; }
        public MediaSource? AppliedMediaSource { get; set; }
        public Windows.Storage.Streams.IRandomAccessStream? AppliedMediaStream { get; set; }
        public Windows.Foundation.TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? MediaFailedHandler { get; set; }
        public bool WasPlayingBeforeSuspend { get; set; }
        public bool IsDisposed
        {
            get => Volatile.Read(ref _isDisposed) != 0;
            set => Volatile.Write(ref _isDisposed, value ? 1 : 0);
        }
    }

    private readonly List<ManagedMediaPlayerSlot> _activeMediaPlayers = [];
    private bool _isMediaPlaybackSuspended;

    private static void ApplyMediaSlotSuspension(ManagedMediaPlayerSlot slot, bool suspended)
    {
        if (slot.IsDisposed)
        {
            return;
        }

        try
        {
            if (suspended)
            {
                bool isPlaying = slot.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
                slot.WasPlayingBeforeSuspend = isPlaying;
                if (isPlaying)
                {
                    slot.Player.Pause();
                }
            }
            else if (slot.WasPlayingBeforeSuspend)
            {
                slot.WasPlayingBeforeSuspend = false;
                slot.Player.Play();
            }
        }
        catch
        {
        }
    }

    internal void SetMediaPlaybackSuspended(bool suspended)
    {
        if (_isMediaPlaybackSuspended == suspended)
        {
            return;
        }

        _isMediaPlaybackSuspended = suspended;
        foreach (ManagedMediaPlayerSlot slot in _activeMediaPlayers)
        {
            ApplyMediaSlotSuspension(slot, suspended);
        }

        foreach (OpenPopoverEntry entry in _openPopoversById.Values)
        {
            foreach (ManagedMediaPlayerSlot slot in entry.MediaPlayers)
            {
                ApplyMediaSlotSuspension(slot, suspended);
            }
        }
    }

    private static void DisposeMediaPlayerSlot(ManagedMediaPlayerSlot slot)
    {
        if (slot.IsDisposed)
        {
            return;
        }

        slot.IsDisposed = true;
        slot.IncrementSourceGeneration();
        slot.PosterGeneration++;
        if (slot.MediaFailedHandler is { } failedHandler)
        {
            slot.MediaFailedHandler = null;
            try { slot.Player.MediaFailed -= failedHandler; } catch { }
        }
        try { slot.Player.Pause(); } catch { }
        try { slot.Element.SetMediaPlayer(null); } catch { }
        try { slot.Element.Source = null; } catch { }
        try { slot.Player.Dispose(); } catch { }
        try { slot.AppliedMediaSource?.Dispose(); } catch { }
        slot.AppliedMediaSource = null;
        try { slot.AppliedMediaStream?.Dispose(); } catch { }
        slot.AppliedMediaStream = null;
    }

    private void ForgetMediaPlayerElement(MediaPlayerElement mpe)
    {
        for (int i = _activeMediaPlayers.Count - 1; i >= 0; i--)
        {
            ManagedMediaPlayerSlot slot = _activeMediaPlayers[i];
            if (ReferenceEquals(slot.Element, mpe))
            {
                _activeMediaPlayers.RemoveAt(i);
                DisposeMediaPlayerSlot(slot);
            }
        }

        foreach (OpenPopoverEntry entry in _openPopoversById.Values)
        {
            for (int i = entry.MediaPlayers.Count - 1; i >= 0; i--)
            {
                ManagedMediaPlayerSlot slot = entry.MediaPlayers[i];
                if (ReferenceEquals(slot.Element, mpe))
                {
                    entry.MediaPlayers.RemoveAt(i);
                    DisposeMediaPlayerSlot(slot);
                }
            }
        }
    }

    private void ReleaseMediaPlayers()
    {
        if (_activeMediaPlayers.Count == 0)
        {
            return;
        }

        foreach (ManagedMediaPlayerSlot slot in _activeMediaPlayers)
        {
            DisposeMediaPlayerSlot(slot);
        }

        _activeMediaPlayers.Clear();
    }

    private Uri? ResolveAuthorizedMediaUri(string? rawCandidate)
    {
        if (string.IsNullOrWhiteSpace(rawCandidate))
        {
            return null;
        }

        string trimmed = rawCandidate.Trim();
        string localPath = _viewModel.ResolveAuthorizedImagePath(trimmed);
        if (!string.IsNullOrWhiteSpace(localPath) && System.IO.File.Exists(localPath))
        {
            return Uri.TryCreate(System.IO.Path.GetFullPath(localPath), UriKind.Absolute, out Uri? fileUri)
                ? fileUri
                : null;
        }

        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (_viewModel.IsStudioGalleryThumbnailPreview || _viewModel.IsUntrustedStagePreview)
            {
                return null;
            }

            return CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(trimmed, out Uri? httpsUri, out _)
                ? httpsUri
                : null;
        }

        return null;
    }

    private FrameworkElement BuildMediaPlayer(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawSrc = spec.Src ?? spec.FilePath ?? spec.Value ?? string.Empty;
        string rawPoster = spec.Poster ?? string.Empty;
        double cornerRadius = Math.Clamp(spec.CornerRadius ?? RadiusMedium, 0, 64);

        static Stretch ResolveMediaStretch(string? rawStretch, CustomWidgetEvaluationContext evalCtx)
        {
            string interpolated = CustomWidgetExpressionEvaluator.Interpolate(rawStretch ?? "uniform", evalCtx);
            ReadOnlySpan<char> resolved = interpolated.AsSpan().Trim();
            if (resolved.Equals("cover", StringComparison.OrdinalIgnoreCase) ||
                resolved.Equals("uniformtofill", StringComparison.OrdinalIgnoreCase))
            {
                return Stretch.UniformToFill;
            }

            if (resolved.Equals("fill", StringComparison.OrdinalIgnoreCase))
            {
                return Stretch.Fill;
            }

            if (resolved.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return Stretch.None;
            }

            return Stretch.Uniform;
        }

        static bool ResolveMediaBool(string? expr, bool fallback, CustomWidgetEvaluationContext evalCtx)
        {
            if (string.IsNullOrWhiteSpace(expr))
            {
                return fallback;
            }

            string trimmed = expr.Trim();
            if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) || trimmed == "1")
            {
                return true;
            }
            if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) || trimmed == "0")
            {
                return false;
            }

            string interpolated = CustomWidgetExpressionEvaluator.Interpolate(trimmed, evalCtx).Trim();
            if (string.Equals(interpolated, "true", StringComparison.OrdinalIgnoreCase) || interpolated == "1")
            {
                return true;
            }
            if (string.Equals(interpolated, "false", StringComparison.OrdinalIgnoreCase) || interpolated == "0")
            {
                return false;
            }

            return CustomWidgetExpressionEvaluator.EvaluateCondition(trimmed, evalCtx);
        }

        Stretch stretch = ResolveMediaStretch(spec.Stretch, ctx);

        static double ResolveMediaVolume(string? rawVol, CustomWidgetEvaluationContext evalCtx, double fallback = 0.8)
        {
            if (string.IsNullOrWhiteSpace(rawVol))
            {
                return fallback;
            }
            if (CustomWidgetExpressionEvaluator.EvaluateDouble(rawVol, evalCtx) is double v)
            {
                return v > 1.0 ? Math.Clamp(v / 100.0, 0.0, 1.0) : Math.Clamp(v, 0.0, 1.0);
            }
            return fallback;
        }

        bool initialMuted = ResolveMediaBool(spec.MutedExpression, spec.Muted, ctx);
        bool initialLoop = ResolveMediaBool(spec.LoopExpression, spec.Loop, ctx);
        bool initialShowTransport = ResolveMediaBool(spec.ShowTransportExpression, spec.ShowTransportControls, ctx);
        bool initialAutoPlay = ResolveMediaBool(spec.AutoPlayExpression, spec.AutoPlay, ctx);

        var fallbackIcon = CreateInlineIconElement("\uE714", 24, ResolveForegroundBrush("muted"));
        fallbackIcon.HorizontalAlignment = HorizontalAlignment.Center;
        fallbackIcon.VerticalAlignment = VerticalAlignment.Center;
        var fallbackStage = new Grid
        {
            Background = ResolveCardBackground("subtle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        fallbackStage.Children.Add(fallbackIcon);

        var stage = new Grid
        {
            MinHeight = spec.MinHeight ?? 96,
            Height = spec.Height ?? 168
        };
        stage.Children.Add(fallbackStage);

        MediaPlayer? createdPlayer = null;
        MediaPlayerElement? createdElement = null;
        try
        {
            createdPlayer = new MediaPlayer
            {
                IsMuted = initialMuted,
                IsLoopingEnabled = initialLoop,
                Volume = ResolveMediaVolume(spec.Volume, ctx)
            };

            createdElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = initialShowTransport,
                AutoPlay = initialAutoPlay && !_isMediaPlaybackSuspended,
                Stretch = stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            if (createdElement.TransportControls is { } tc)
            {
                tc.IsCompact = true;
                tc.IsZoomButtonVisible = false;
                tc.IsPlaybackRateButtonVisible = false;
            }

            createdElement.SetMediaPlayer(createdPlayer);
        }
        catch (Exception)
        {
            try
            {
                createdPlayer?.Dispose();
            }
            catch
            {
            }

            createdPlayer = null;
            createdElement = null;
        }

        if (createdPlayer is { } player && createdElement is { } mediaElement)
        {
            stage.Children.Add(mediaElement);

            var slot = new ManagedMediaPlayerSlot
            {
                Element = mediaElement,
                Player = player
            };
            if (_currentPopoverBuildOwner is { } popoverOwner)
            {
                popoverOwner.MediaPlayers.Add(slot);
            }
            else
            {
                _activeMediaPlayers.Add(slot);
            }

            void ClearMediaSlot()
            {
                try
                {
                    mediaElement.Source = null;
                }
                catch
                {
                }
                MediaSource? mediaSource = slot.AppliedMediaSource;
                slot.AppliedMediaSource = null;
                if (mediaSource is not null)
                {
                    try
                    {
                        mediaSource.Dispose();
                    }
                    catch
                    {
                    }
                }
                Windows.Storage.Streams.IRandomAccessStream? mediaStream = slot.AppliedMediaStream;
                slot.AppliedMediaStream = null;
                if (mediaStream is not null)
                {
                    try
                    {
                        mediaStream.Dispose();
                    }
                    catch
                    {
                    }
                }
                try
                {
                    mediaElement.Visibility = Visibility.Collapsed;
                    fallbackStage.Visibility = Visibility.Visible;
                }
                catch
                {
                }
            }

            // Capture DispatcherQueue on the UI thread before registering the MTA
            // MediaFailed callback. Accessing mediaElement.DispatcherQueue inside the
            // Media Foundation callback thread throws RPC_E_WRONG_THREAD.
            Microsoft.UI.Dispatching.DispatcherQueue? mediaDispatcherQueue = mediaElement.DispatcherQueue;

            // On Windows 10 (missing HEVC/AV1/VP9/WebM codecs or Schannel TLS 1.3
            // stream failures), MediaPlayer reports decode/network failures
            // asynchronously via MediaFailed. Revert to fallbackStage so the widget
            // does not get stuck showing a blank black surface.
            Windows.Foundation.TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> onMediaFailed =
                (_, _) =>
                {
                    try
                    {
                        if (slot.IsDisposed)
                        {
                            return;
                        }

                        int failedGeneration = slot.SourceGeneration;
                        _ = mediaDispatcherQueue?.TryEnqueue(() =>
                        {
                            if (!slot.IsDisposed && slot.SourceGeneration == failedGeneration)
                            {
                                ClearMediaSlot();
                            }
                        });
                    }
                    catch
                    {
                    }
                };
            slot.MediaFailedHandler = onMediaFailed;
            player.MediaFailed += onMediaFailed;

            async Task ApplyPosterRemoteHttpsAsync(Uri posterUri, int generation)
            {
                try
                {
                    string hostKey = $"host:{posterUri.DnsSafeHost.ToLowerInvariant()}";
                    bool permitted = await _viewModel.EnsureWidgetPermissionGrantedAsync(
                        CustomWidgetPermissionKind.PublicNetwork,
                        "media.poster",
                        posterUri.AbsoluteUri,
                        isReadOnlyPublicGet: false);
                    if (!permitted ||
                        !await _viewModel.EnsureCommunityExternalTargetApprovedAsync(
                            "media.poster",
                            hostKey,
                            posterUri.AbsoluteUri,
                            respectDenialCooldown: true))
                    {
                        if (!slot.IsDisposed && generation == slot.PosterGeneration)
                        {
                            mediaElement.PosterSource = null;
                        }
                        return;
                    }

                    byte[]? imageBytes = await _viewModel.FetchValidatedRemoteImageBytesAsync(posterUri);
                    if (slot.IsDisposed || generation != slot.PosterGeneration || imageBytes is not { Length: > 0 })
                    {
                        if (!slot.IsDisposed && generation == slot.PosterGeneration)
                        {
                            mediaElement.PosterSource = null;
                        }
                        return;
                    }

                    using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await System.IO.WindowsRuntimeStreamExtensions.AsStreamForWrite(ras).WriteAsync(imageBytes);
                    ras.Seek(0);

                    var bitmap = new BitmapImage { DecodePixelWidth = 960 };
                    await bitmap.SetSourceAsync(ras);
                    if (!slot.IsDisposed && generation == slot.PosterGeneration)
                    {
                        mediaElement.PosterSource = bitmap;
                    }
                }
                catch
                {
                    if (!slot.IsDisposed && generation == slot.PosterGeneration)
                    {
                        try { mediaElement.PosterSource = null; } catch { }
                    }
                }
            }

            void ApplyPoster(string candidate)
            {
                string trimmed = candidate.Trim();
                if (string.Equals(slot.AppliedPoster, trimmed, StringComparison.Ordinal))
                {
                    return;
                }
                slot.AppliedPoster = trimmed;
                int generation = ++slot.PosterGeneration;

                Uri? posterUri = ResolveAuthorizedMediaUri(trimmed);
                if (posterUri is null)
                {
                    try
                    {
                        mediaElement.PosterSource = null;
                    }
                    catch
                    {
                    }
                    return;
                }

                if (string.Equals(posterUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    _ = ApplyPosterRemoteHttpsAsync(posterUri, generation);
                    return;
                }

                try
                {
                    if (posterUri.IsAbsoluteUri && posterUri.IsFile)
                    {
                        var posterBitmap = new BitmapImage { DecodePixelWidth = 960 };
                        mediaElement.PosterSource = posterBitmap;
                        WidgetImageStreamLoader.Fill(posterBitmap, posterUri.LocalPath, ok =>
                        {
                            if (!ok && !slot.IsDisposed && generation == slot.PosterGeneration)
                            {
                                try { mediaElement.PosterSource = null; } catch { }
                            }
                        });
                    }
                    else
                    {
                        mediaElement.PosterSource = new BitmapImage(posterUri);
                    }
                }
                catch
                {
                }
            }

            void CommitVerifiedMediaUri(Uri mediaUri, CustomWidgetEvaluationContext evalCtx)
            {
                Windows.Storage.Streams.IRandomAccessStream? nextStream = null;
                try
                {
                    mediaElement.Visibility = Visibility.Visible;
                    fallbackStage.Visibility = Visibility.Collapsed;
                    MediaSource nextSource = DeskBox.Platform.ClipboardStorageFileOps.CreateMediaSourceFromUri(mediaUri, out nextStream);
                    MediaSource? previousSource = slot.AppliedMediaSource;
                    Windows.Storage.Streams.IRandomAccessStream? previousStream = slot.AppliedMediaStream;
                    slot.AppliedMediaSource = nextSource;
                    slot.AppliedMediaStream = nextStream;
                    mediaElement.Source = nextSource;
                    previousSource?.Dispose();
                    previousStream?.Dispose();
                    if (ResolveMediaBool(spec.AutoPlayExpression, spec.AutoPlay, evalCtx) && !_isMediaPlaybackSuspended)
                    {
                        player.Play();
                    }
                }
                catch
                {
                    try { nextStream?.Dispose(); } catch { }
                    ClearMediaSlot();
                }
            }

            async Task AuthorizeAndCommitRemoteMediaAsync(Uri mediaUri, int generation, CustomWidgetEvaluationContext evalCtx)
            {
                try
                {
                    string hostKey = $"host:{mediaUri.DnsSafeHost.ToLowerInvariant()}";
                    bool permitted = await _viewModel.EnsureWidgetPermissionGrantedAsync(
                        CustomWidgetPermissionKind.PublicNetwork,
                        "media.play",
                        mediaUri.AbsoluteUri,
                        isReadOnlyPublicGet: false);
                    if (!permitted ||
                        !await _viewModel.EnsureCommunityExternalTargetApprovedAsync(
                            "media.play",
                            hostKey,
                            mediaUri.AbsoluteUri,
                            respectDenialCooldown: true))
                    {
                        if (!slot.IsDisposed && generation == slot.SourceGeneration)
                        {
                            ClearMediaSlot();
                        }
                        return;
                    }

                    if (slot.IsDisposed || generation != slot.SourceGeneration)
                    {
                        return;
                    }

                    CommitVerifiedMediaUri(mediaUri, evalCtx);
                }
                catch
                {
                    if (!slot.IsDisposed && generation == slot.SourceGeneration)
                    {
                        ClearMediaSlot();
                    }
                }
            }

            void ApplyMediaSource(string candidate, CustomWidgetEvaluationContext evalCtx)
            {
                string trimmed = candidate.Trim();
                if (string.Equals(slot.AppliedSource, trimmed, StringComparison.Ordinal))
                {
                    return;
                }
                slot.AppliedSource = trimmed;
                int generation = slot.IncrementSourceGeneration();

                Uri? mediaUri = ResolveAuthorizedMediaUri(trimmed);
                if (mediaUri is null)
                {
                    ClearMediaSlot();
                    return;
                }

                if (string.Equals(mediaUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    ClearMediaSlot();
                    _ = AuthorizeAndCommitRemoteMediaAsync(mediaUri, generation, evalCtx);
                    return;
                }

                CommitVerifiedMediaUri(mediaUri, evalCtx);
            }

            if (!string.IsNullOrWhiteSpace(rawPoster))
            {
                ApplyPoster(CustomWidgetExpressionEvaluator.Interpolate(rawPoster, ctx));
            }
            ApplyMediaSource(CustomWidgetExpressionEvaluator.Interpolate(rawSrc, ctx), ctx);

            RegisterRetainedPatcher(liveCtx =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(rawPoster) && ContainsDynamicToken(rawPoster))
                    {
                        ApplyPoster(CustomWidgetExpressionEvaluator.Interpolate(rawPoster, liveCtx));
                    }
                    ApplyMediaSource(CustomWidgetExpressionEvaluator.Interpolate(rawSrc, liveCtx), liveCtx);
                    if (ContainsDynamicToken(spec.Stretch))
                    {
                        mediaElement.Stretch = ResolveMediaStretch(spec.Stretch, liveCtx);
                    }
                    player.IsMuted = ResolveMediaBool(spec.MutedExpression, spec.Muted, liveCtx);
                    player.IsLoopingEnabled = ResolveMediaBool(spec.LoopExpression, spec.Loop, liveCtx);
                    mediaElement.AreTransportControlsEnabled = ResolveMediaBool(spec.ShowTransportExpression, spec.ShowTransportControls, liveCtx);
                    if (!string.IsNullOrWhiteSpace(spec.Volume))
                    {
                        player.Volume = ResolveMediaVolume(spec.Volume, liveCtx, player.Volume);
                    }
                }
                catch (Exception)
                {
                    mediaElement.Visibility = Visibility.Collapsed;
                    fallbackStage.Visibility = Visibility.Visible;
                }
            });
        }
        else
        {
            fallbackStage.Visibility = Visibility.Visible;
        }

        var container = new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            BorderThickness = new Thickness(1),
            BorderBrush = DividerBrush,
            Clip = new RectangleGeometry(),
            Child = stage
        };
        container.SizeChanged += (_, e) =>
        {
            if (container.Clip is RectangleGeometry clipRect)
            {
                clipRect.Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
            }
        };
        MarkInteractiveElement(container);

        return ApplyContainerVisualOverrides(container, spec, ctx);
    }
}
