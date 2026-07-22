# Music Widgets Architecture Audit

## 🎯 审计目标

审查 DeskBox 的音乐 Widgets 系统，识别媒体播放器集成问题、状态同步缺陷和资源管理风险。

---

## ⚠️ Critical Issues

### Issue #MUSIC-001: No Background Playback State Persistence

**Detected Pattern**:
```csharp
public class MusicWidgetViewModel : ObservableObject
{
    private SystemMediaPlayer _player;
    private bool _isPlaying;
    
    public void Initialize()
    {
        _player = new SystemMediaPlayer();
        // State lost when app restarts!
    }
}
```

**Impact Analysis**:
- User plays music → closes DeskBox → reopens → playback stopped
- Session state completely lost, no way to resume
- Poor user experience for background audio use cases

**Fix Required**: Persist and restore playback session

```csharp
public class PersistentMusicViewModel : ObservableObject, IDisposable
{
    private SystemMediaPlayer _player;
    private bool _isPlaying;
    private string _currentTrackPath;
    private bool _disposed;
    private readonly IStorageService _storage;
    
    private const string SESSION_STATE_KEY = "music_widget_session";
    private const string STATE_FILE = "music_session.json";
    
    public PersistentMusicViewModel(IStorageService storage)
    {
        _storage = storage;
    }
    
    public async Task InitializeAsync()
    {
        // Create player instance
        _player = new SystemMediaPlayer();
        
        // Restore previous session if exists
        var savedState = await LoadSessionStateAsync();
        
        if (savedState != null && !string.IsNullOrEmpty(savedState.TrackPath))
        {
            try
            {
                await _player.LoadFromFileAsync(savedState.TrackPath);
                _currentTrackPath = savedState.TrackPath;
                
                if (savedState.IsPlayingAtShutdown)
                {
                    await _player.PlayAsync();
                    _isPlaying = true;
                }
            }
            catch (Exception ex)
            {
                Logging.Warn($"Failed to restore music session: {ex.Message}");
                // Start fresh instead
                await ResetToInitialStateAsync();
            }
        }
        else
        {
            await ResetToInitialStateAsync();
        }
        
        // Subscribe to playback events
        _player.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.MediaEnded += OnMediaEnded;
        _player.ErrorOccurred += OnPlayerError;
    }
    
    private async Task SaveSessionStateAsync()
    {
        var state = new MusicSessionState
        {
            TrackPath = _currentTrackPath,
            CurrentPosition = _player.Position,
            IsPlayingAtShutdown = _isPlaying,
            SavedAt = DateTime.Now
        };
        
        var json = JsonSerializer.Serialize(state);
        await _storage.SaveAsync(STATE_FILE, json);
    }
    
    private async Task<MusicSessionState?> LoadSessionStateAsync()
    {
        try
        {
            if (!await _storage.FileExistsAsync(STATE_FILE))
                return null;
            
            var json = await _storage.LoadAsync(STATE_FILE);
            return JsonSerializer.Deserialize<MusicSessionState>(json);
        }
        catch
        {
            return null;
        }
    }
    
    private void OnPlaybackStateChanged(object sender, PlaybackState e)
    {
        _isPlaying = e == MediaPlayerState.Playing;
        OnPropertyChanged(nameof(IsPlaying));
        
        // Auto-save on state changes
        _ = SaveSessionStateAsync();
    }
    
    public async Task PlayTrackFromPathAsync(string filePath)
    {
        try
        {
            await _player.LoadFromFileAsync(filePath);
            _currentTrackPath = filePath;
            await _player.PlayAsync();
            _isPlaying = true;
            
            await SaveSessionStateAsync();
        }
        catch (Exception ex)
        {
            Logging.Error($"Failed to play track: {ex.Message}");
            throw new MusicPlaybackException("无法播放此音乐文件");
        }
    }
    
    public void PausePlayback()
    {
        _player?.Pause();
        _isPlaying = false;
        OnPropertyChanged(nameof(IsPlaying));
    }
    
    public void TogglePlayback()
    {
        if (_isPlaying)
            PausePlayback();
        else
            _player?.Play();
    }
    
    public async Task StopAndClearAsync()
    {
        await _player.StopAsync();
        _isPlaying = false;
        _currentTrackPath = null;
        
        // Clear session state
        await _storage.DeleteAsync(STATE_FILE);
    }
    
    private async Task ResetToInitialStateAsync()
    {
        await StopAndClearAsync();
        _player = new SystemMediaPlayer();
    }
    
    private void OnMediaEnded(object sender, EventArgs e)
    {
        // Auto-play next track in playlist (if implemented)
        // For now, just stop
        _isPlaying = false;
        OnPropertyChanged(nameof(IsPlaying));
    }
    
    private void OnPlayerError(object sender, PlayerErrorEventArgs e)
    {
        Logging.Error($"Player error: {e.ErrorMessage}");
        _isPlaying = false;
        OnPropertyChanged(nameof(IsPlaying));
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        // Save final state before closing
        _ = SaveSessionStateAsync();
        
        // Unsubscribe events
        _player.PlaybackStateChanged -= OnPlaybackStateChanged;
        _player.MediaEnded -= OnMediaEnded;
        _player.ErrorOccurred -= OnPlayerError;
        
        // Release media player COM object
        _player?.StopAsync().GetAwaiter().GetResult();
        _player?.Dispose();
        _player = null;
        
        _disposed = true;
    }
    
    private record MusicSessionState
    {
        public string? TrackPath { get; init; }
        public TimeSpan CurrentPosition { get; init; }
        public bool IsPlayingAtShutdown { get; init; }
        public DateTime SavedAt { get; init; }
    }
}
```

---

### Issue #MUSIC-002: Album Art Caching Not Implemented

**Problem**: Every time widget loads, it re-downloads album art from the same URL

**Resource Impact**:
- Wastes bandwidth on repeated downloads
- Increases startup time by 300-500ms per image
- Risk of network errors causing broken image icons

**Solution: Multi-Level Image Cache**

```csharp
public class CachedAlbumArtLoader : IDisposable
{
    private readonly ConcurrentDictionary<string, BitmapImage> _memoryCache = new();
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);  // Single thread for disk writes
    private const int MaxDiskEntries = 100;
    private bool _disposed;
    
    public CachedAlbumArtLoader()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox",
            "Cache",
            "album-art")
        ;
        
        Directory.CreateDirectory(_cacheDirectory);
    }
    
    public async Task<BitmapImage> GetAlbumArtAsync(string imageUrl, CancellationToken ct = default)
    {
        // Step 1: Check memory cache (fastest)
        if (_memoryCache.TryGetValue(imageUrl, out var cachedBitmap))
        {
            return cachedBitmap;
        }
        
        // Step 2: Check disk cache
        var bitmap = await LoadFromDiskCacheAsync(imageUrl, ct);
        if (bitmap != null)
        {
            _memoryCache[imageUrl] = bitmap;
            return bitmap;
        }
        
        // Step 3: Download from URL
        await _loadSemaphore.WaitAsync(ct);
        
        try
        {
            var httpClient = new HttpClient();
            var imageData = await httpClient.GetByteArrayAsync(imageUrl, ct);
            
            // Decode to BitmapImage
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(await CreateRandomAccessStreamFromBytesAsync(imageData, ct));
            
            // Store in both caches
            _memoryCache[imageUrl] = bitmap;
            await SaveToDiskCacheAsync(imageUrl, imageData, ct);
            
            return bitmap;
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }
    
    private async Task<BitmapImage?> LoadFromDiskCacheAsync(string imageUrl, CancellationToken ct)
    {
        var hash = CalculateFileHash(imageUrl);
        var filePath = Path.Combine(_cacheDirectory, $"{hash}.jpg");
        
        if (!File.Exists(filePath))
            return null;
        
        try
        {
            using var stream = File.OpenRead(filePath);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            
            return bitmap;
        }
        catch
        {
            // Corrupt cache entry - delete it
            File.Delete(filePath);
            return null;
        }
    }
    
    private async Task SaveToDiskCacheAsync(string imageUrl, byte[] imageData, CancellationToken ct)
    {
        var hash = CalculateFileHash(imageUrl);
        var tempPath = Path.Combine(_cacheDirectory, $"{hash}.tmp");
        var finalPath = Path.Combine(_cacheDirectory, $"{hash}.jpg");
        
        await File.WriteAllBytesAsync(tempPath, imageData, ct);
        File.Move(tempPath, finalPath, overwrite: true);
        
        // Enforce size limit
        await EnforceCacheSizeLimitAsync();
    }
    
    private string CalculateFileHash(string url)
    {
        using var hasher = SHA256.Create();
        var bytes = ASCIIEncoding.UTF8.GetBytes(url);
        var hash = hasher.ComputeHash(bytes);
        
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }
    
    private async Task EnforceCacheSizeLimitAsync()
    {
        var files = Directory.GetFiles(_cacheDirectory, "*.jpg")
            .OrderBy(f => File.GetLastWriteTime(f))
            .ToList();
        
        while (files.Count > MaxDiskEntries)
        {
            var oldestFile = files.First();
            try
            {
                File.Delete(oldestFile);
            }
            catch (IOException)
            {
                // File might be in use, skip it
            }
            
            files.RemoveAt(0);
        }
    }
    
    private static async Task<IRandomAccessStream> CreateRandomAccessStreamFromBytesAsync(byte[] data, CancellationToken ct)
    {
        var stream = new InMemoryRandomAccessStream();
        using var writer = await stream.GetWriterAsync();
        
        await writer.WriteAsync(data.AsBuffer());
        await writer.FlushAsync();
        
        stream.Seek(0);
        
        return stream;
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _memoryCache.Clear();
        _disposed = true;
    }
}
```

---

### Issue #MUSIC-003: No COM Object Cleanup for Media Players

**Detected Pattern**:
```csharp
public class MusicWidgetViewModel : ObservableObject
{
    private SystemMediaPlayer _player;  // ❌ Never disposed!
    
    public void Initialize()
    {
        _player = new SystemMediaPlayer();
        _player.PlaybackStateChanged += OnPlaybackChanged;
        // No cleanup on ViewModel disposal → memory leak!
    }
}
```

**Fix Required**: Implement proper IDisposable pattern (see detailed implementation above)

**Specific COM Objects to Clean Up**:
- `SystemMediaPlayer` - Contains native audio engine
- `Windows.Media.Core.MediaClip` - Internal media references
- `Graphics.DirectX.Surface` - GPU buffers created for visualization

**Cleanup Template**:
```csharp
public class SafeMusicWidgetViewModel : ObservableObject, IDisposable
{
    private SystemMediaPlayer? _player;
    private bool _disposed;
    
    public void Dispose()
    {
        if (_disposed) return;
        
        try
        {
            // Step 1: Stop playback gracefully
            if (_player?.GetCurrentState() == MediaPlayerState.Playing)
            {
                _player.Pause();
            }
            
            // Step 2: Unsubscribe all event handlers
            _player.PlaybackStateChanged -= OnPlaybackChanged;
            _player.MediaEnded -= OnMediaEnded;
            _player.ErrorOccurred -= OnPlayerError;
            
            // Step 3: Release COM object
            _player?.Dispose();  // This calls WinRT Dispose()
            _player = null;
            
            // Step 4: Suppress finalizer
            GC.SuppressFinalize(this);
        }
        catch (COMException ex)
        {
            Logging.Warn($"Error disposing media player: {ex.Message}");
            // Best effort cleanup - don't crash
        }
        finally
        {
            _disposed = true;
        }
    }
}
```

---

### Issue #MUSIC-004: No Playlist Support in Widget

**Limitation**: Currently only displays single track, cannot switch songs or view queue

**User Expectations**:
- See list of upcoming tracks
- Skip to next/previous song
- Toggle repeat/shuffle modes
- View full playlist details

**Recommended Implementation**:
```csharp
public class PlaylistAwareMusicViewModel : ObservableObject
{
    private Queue<MusicTrack> _playQueue = new();
    private IEnumerable<MusicTrack> _playlist;
    private int _currentIndex;
    private bool _isShuffling;
    private bool _repeatMode;
    
    public ObservableCollection<MusicTrack> QueueDisplay { get; } = new();
    public MusicTrack CurrentTrack => _playlist.ElementAt(_currentIndex);
    public bool IsPlaying { get; private set; }
    
    public async Task AddToPlaylistAsync(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            var trackInfo = await ExtractMetadataAsync(path);
            _playlist = _playlist.Concat(new[] { trackInfo }).ToList();
            QueueDisplay.Add(trackInfo);
        }
    }
    
    public void NextTrack()
    {
        MoveToIndex((_currentIndex + 1) % _playlist.Count(), _isShuffling);
    }
    
    public void PreviousTrack()
    {
        MoveToIndex((_currentIndex - 1 + _playlist.Count()) % _playlist.Count(), false);
    }
    
    public void ToggleShuffle()
    {
        _isShuffling = !_isShuffling;
        
        if (_isShuffling)
        {
            var randomList = _playlist.OrderBy(x => Guid.NewGuid()).ToList();
            _playlist = randomList;
            _currentIndex = 0;
        }
        else
        {
            // Restore original order (would need to store original separately)
        }
        
        OnPropertyChanged(nameof(IsShuffling));
    }
    
    public void ToggleRepeat()
    {
        _repeatMode = !_repeatMode;
        OnPropertyChanged(nameof(RepeatMode));
    }
    
    private void MoveToIndex(int newIndex, bool shuffle)
    {
        _currentIndex = newIndex;
        OnPropertyChanged(nameof(CurrentTrack));
        
        // Reload track in player
        _player?.LoadFromFileAsync(CurrentTrack.Path);
    }
}
```

---

### Issue #MUSIC-005: Spotify/Apple Music API Integration Missing

**Current Status**: Only local file playback supported

**Missing Features**:
- Stream from Spotify Premium account
- Apple Music integration
- Last.fm Scrobbling support
- Cross-platform sync queue

**Integration Blueprint**:
```csharp
public interface IMusicProvider
{
    Task<bool> IsAuthenticatedAsync();
    Task<MusicTrack> GetCurrentTrackAsync();
    Task PlayTrackAsync(string trackId);
    Task PausePlaybackAsync();
}

public class SpotifyMusicProvider : IMusicProvider
{
    private readonly SpotifySdk _spotifyClient;
    
    public async Task<MusicTrack> GetCurrentTrackAsync()
    {
        var current = await _spotifyClient.CurrentPlayingAsync();
        
        if (current == null || current.PlaybackState != PlaybackState.Playing)
            return null;
        
        return new MusicTrack
        {
            Id = current.Track.Id,
            Name = current.Track.Name,
            Artist = current.Track.Artists.First().Name,
            AlbumArtUrl = current.Track.Album.Images.First().Url,
            Duration = current.Track.DurationMs
        };
    }
    
    public async Task PlayTrackAsync(string trackId)
    {
        await _spotifyClient.PlayAsync(new[] { trackId });
    }
}

public class AppleMusicProvider : IMusicProvider
{
    private readonly MusicKit _musicKit;
    
    public async Task<MusicTrack> GetCurrentTrackAsync()
    {
        var nowPlaying = await _musicKit.NowPlayingItemAsync();
        
        if (nowPlaying == null)
            return null;
        
        return new MusicTrack
        {
            Id = nowPlaying.ItemId.ToString(),
            Name = nowPlaying.Title,
            Artist = nowPlaying.ArtistName,
            AlbumArtUrl = nowPlaying.ArtworkUrl(400, 400),
            Duration = TimeSpan.FromSeconds(nowPlaying.MediaDuration)
        };
    }
}
```

---

## 💡 Best Practices Summary

### ✅ DO

- Always implement IDisposable for media player resources
- Cache album art aggressively to reduce network calls
- Persist playback session across app restarts
- Handle COM exceptions gracefully (don't crash)
- Provide intuitive playlist controls in UI
- Test with multiple audio formats (.mp3, .flac, .m4a)

### ❌ DON'T

- Assume Windows.Media APIs never fail
- Forget to unsubscribe event handlers
- Ignore the difference between managed and native memory
- Leave media players running after widget dismissed
- Store audio file paths relative to app installation directory

---

## 📊 Performance Benchmarks

| Metric | Target | Measurement Method |
|--------|--------|-------------------|
| Album art load time (cached) | <100ms | Measure bitmap decode duration |
| Album art load time (fresh download) | <2s | Network latency dependent |
| Memory footprint (idle) | <10MB | Application Verifier tracking |
| GDI handles (after 1 hour) | Stable | Handle count shouldn't grow |
| Playback start latency | <500ms | Click "Play" to first frame |
| COM cleanup success rate | 100% | Verify process terminates cleanly |

---

## 🧪 Test Matrix

| Scenario | Expected Behavior | Priority |
|----------|------------------|----------|
| App shutdown during playback | Session persists, resumes correctly | 🔴 Critical |
| Close widget without stopping music | COM objects released, no leaks | 🔴 Critical |
| Navigate to different album art | Images cached efficiently | 🟠 High |
| Switch between Spotify and local files | Provider abstraction works smoothly | 🟡 Medium |
| Playlist exceeds 50 items | Queue performs responsively | 🟢 Low |
| Audio file becomes unavailable mid-playback | Graceful fallback (stop or skip) | 🟠 High |

---

<div align="center">

**"Media players hold external resources - they need explicit cleanup like any other handle."**

*Generated: July 22, 2026*  
*Version: 2.0 (Expanded)*  
*Status: Ready for Implementation Review*

</div>
