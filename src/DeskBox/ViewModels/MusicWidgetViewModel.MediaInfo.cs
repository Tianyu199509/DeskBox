using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
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
            Title = string.Empty;
            Artist = string.Empty;
            Album = string.Empty;
            _sourceAppUserModelId = string.Empty;
            SourceDisplayName = string.Empty;
            PlaybackState = MusicPlaybackState.Unknown;
            Position = TimeSpan.Zero;
        _lastSyncedPosition = TimeSpan.Zero;
        _lastPositionSyncAt = DateTimeOffset.UtcNow;
            Duration = TimeSpan.Zero;
            ThumbnailImage = null;
            _lastCoverSignature = null;
            _coverRetrySignature = null;
            _coverRetryCount = 0;
            SetArtworkColor(AccentColorHelper.DefaultAccentColor, hasArtworkColor: false);
            CanPlay = false;
            CanPause = false;
            CanGoPrevious = false;
            CanGoNext = false;
            CanSeek = false;
            CanChangeShuffle = false;
            CanChangeRepeat = false;
            PlaybackMode = MusicPlaybackMode.Normal;
            RaiseDisplayPropertiesChanged();
            return;
        }

        _preferredSessionId ??= info.SessionId;
        Title = info.Title;
        Artist = info.Artist;
        Album = info.Album;
        _sourceAppUserModelId = info.SourceAppUserModelId;
        SourceDisplayName = info.SourceDisplayName;
        PlaybackState = info.PlaybackState;
        Position = info.Position;
        Duration = info.Duration;
        _lastSyncedPosition = info.Position;
        _lastPositionSyncAt = DateTimeOffset.UtcNow;
        CanPlay = info.CanPlay;
        CanPause = info.CanPause;
        CanGoPrevious = info.CanGoPrevious;
        CanGoNext = info.CanGoNext;
        CanSeek = info.CanSeek && info.Duration > TimeSpan.Zero;
        CanChangeShuffle = info.CanChangeShuffle;
        CanChangeRepeat = info.CanChangeRepeat;
        PlaybackMode = info.PlaybackMode;
        // Cover signature dedup: skip expensive cover reload when the song hasn't changed.
        // Timeline and playback events go through lightweight refresh paths, but
        // MediaPropertiesChanged can fire even when only metadata (not the thumbnail) changes.
        string coverSig = $"{info.SessionId}\u001E{info.Title}\u001E{info.Artist}\u001E{info.Album}";
        bool coverChanged = !string.Equals(_lastCoverSignature, coverSig, StringComparison.Ordinal);

        if (coverChanged)
        {
            int gen = ++_coverGeneration;
            var (cover, artworkColor) = await LoadThumbnailAndColorAsync(info);
            if (_isDisposed || gen != _coverGeneration) return;

            if (cover is not null)
            {
                ThumbnailImage = cover;
                SetArtworkColor(artworkColor ?? AccentColorHelper.DefaultAccentColor, artworkColor.HasValue);
                App.ScheduleLightMemoryCleanup();
                _lastCoverSignature = coverSig;
                _coverRetrySignature = null;
                _coverRetryCount = 0;
            }
            else
            {
                _lastCoverSignature = null;
                if (!ScheduleCoverRetry(coverSig, gen))
                {
                    ThumbnailImage = null;
                    SetArtworkColor(AccentColorHelper.DefaultAccentColor, hasArtworkColor: false);
                    App.ScheduleLightMemoryCleanup();
                }
            }
        }

        RaiseDisplayPropertiesChanged();
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
        bool didChangeColor = !_artworkColor.Equals(value);
        bool didChangeAvailability = _hasArtworkColor != hasArtworkColor;

        _artworkColor = value;
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
        if (UseArtworkBackdrop && _hasArtworkColor)
        {
            return _artworkColor;
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

    private void RaiseMusicAccentPropertiesChanged()
    {
        OnPropertyChanged(nameof(MusicAccentBrush));
        OnPropertyChanged(nameof(PlayPauseButtonBackgroundBrush));
    }

    /// <summary>
    /// Loads the thumbnail BitmapImage and extracts the dominant color in a single
    /// stream pass, avoiding the double OpenReadAsync that the previous separate
    /// methods incurred. Uses DecodePixelWidth to cap memory for large artwork.
    /// </summary>
    private const int CoverDecodePixelWidth = 192;
    private const int MaxCoverRetryCount = 3;

    private static async Task<(BitmapImage? image, Color? color)> LoadThumbnailAndColorAsync(MusicSessionInfo info)
    {
        if (info.Thumbnail is null)
        {
            return (null, null);
        }

        try
        {
            PerformanceLogger.RecordMusicCoverDecode();
            using var stream = await info.Thumbnail.OpenReadAsync();

            // Create a clone for BitmapDecoder so BitmapImage can still read the original stream.
            using var colorStream = stream.CloneStream();

            // Load BitmapImage with DecodePixelWidth to cap memory for high-res artwork.
            var image = new BitmapImage { DecodePixelWidth = CoverDecodePixelWidth };
            await image.SetSourceAsync(stream);

            // Extract dominant color from a tiny downscaled version.
            Color? color = await ExtractDominantColorAsync(colorStream);

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
            return false;
        }

        int retryNumber = ++_coverRetryCount;
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

        await RefreshAsync();
    }

    private static async Task<Color?> ExtractDominantColorAsync(IRandomAccessStream stream)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            uint targetWidth = Math.Max(1, Math.Min(32, decoder.PixelWidth));
            uint targetHeight = Math.Max(1, Math.Min(32, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] pixels = data.DetachPixelData();
            if (pixels.Length < 4)
            {
                return null;
            }

            double totalR = 0;
            double totalG = 0;
            double totalB = 0;
            double weightTotal = 0;

            for (int i = 0; i <= pixels.Length - 4; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                byte a = pixels[i + 3];
                if (a < 24)
                {
                    continue;
                }

                double saturationWeight = 0.55 + (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) / 255.0;
                double alphaWeight = a / 255.0;
                double weight = saturationWeight * alphaWeight;
                totalR += r * weight;
                totalG += g * weight;
                totalB += b * weight;
                weightTotal += weight;
            }

            if (weightTotal <= 0.01)
            {
                return null;
            }

            byte averageR = ClampByte(totalR / weightTotal);
            byte averageG = ClampByte(totalG / weightTotal);
            byte averageB = ClampByte(totalB / weightTotal);
            return Color.FromArgb(0xFF, averageR, averageG, averageB);
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Failed to sample thumbnail color: {ex.Message}");
            return null;
        }
    }
}
