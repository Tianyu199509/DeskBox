using System.Net;
using System.Security.Cryptography;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using ClipboardHistoryItem = DeskBox.Models.ClipboardHistoryItem;

namespace DeskBox.Services;

/// <summary>
/// Singleton system clipboard history manager and persistence coordinator
/// backing <see cref="WidgetKind.Clipboard"/>.
/// </summary>
public sealed class ClipboardHistoryService : IDisposable
{
    private const uint ThumbnailMaxPixelSize = 320;
    private const int MaxObservedApps = 24;
    private static readonly TimeSpan CaptureDebounceDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(350);

    private readonly ClipboardStore _store;
    private readonly SettingsService _settingsService;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _storeLock = new(1, 1);
    private readonly object _foregroundTrackingGate = new();
    private readonly HashSet<object> _activeForegroundTrackingViews = [];

    private ClipboardPlatformInterop.ListenerSession? _listenerSession;
    private CancellationTokenSource? _captureDebounceCts;
    private CancellationTokenSource? _saveDebounceCts;
    private ClipboardStoreDocument? _cachedDocument;
    private volatile IReadOnlyList<ClipboardHistoryItem> _itemsSnapshot = Array.Empty<ClipboardHistoryItem>();
    private volatile IReadOnlyList<ClipboardObservedApp> _observedAppsSnapshot = Array.Empty<ClipboardObservedApp>();
    private int _captureInFlight;
    private bool _isMonitoring;
    private bool _isPaused;
    private bool _isDisposed;

    public ClipboardHistoryService(
        ClipboardStore store,
        SettingsService settingsService,
        Action<string>? log = null)
    {
        _store = store;
        _settingsService = settingsService;
        _log = log;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public event EventHandler? HistoryChanged;

    public event EventHandler? PauseStateChanged;

    public event EventHandler? ItemsChanged
    {
        add => HistoryChanged += value;
        remove => HistoryChanged -= value;
    }

    public event EventHandler? MonitoringStateChanged
    {
        add => PauseStateChanged += value;
        remove => PauseStateChanged -= value;
    }

    public IReadOnlyList<ClipboardHistoryItem> Items => _itemsSnapshot;

    public IReadOnlyList<ClipboardObservedApp> ObservedApps => _observedAppsSnapshot;

    public bool IsMonitoring => _isMonitoring;

    public bool IsPaused => _isPaused;

    public string ImageDirectory => _store.ImageDirectory;

    public string ThumbnailDirectory => _store.ThumbnailDirectory;

    public string PayloadDirectory => _store.PayloadDirectory;

    public string ExportDirectory => _store.ExportDirectory;

    internal bool IsStoreLoadedInMemory => _cachedDocument is not null;

    private bool IsForegroundTrackingWanted
    {
        get
        {
            lock (_foregroundTrackingGate)
            {
                return !_isDisposed && _activeForegroundTrackingViews.Count > 0;
            }
        }
    }

    public void SetViewForegroundTracking(object viewToken, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(viewToken);

        bool shouldTrack;
        lock (_foregroundTrackingGate)
        {
            if (isActive && !_isDisposed)
            {
                _activeForegroundTrackingViews.Add(viewToken);
            }
            else
            {
                _activeForegroundTrackingViews.Remove(viewToken);
            }

            shouldTrack = !_isDisposed && _activeForegroundTrackingViews.Count > 0;
        }

        UiDispatch.RunOrDefer(() => _listenerSession?.SetForegroundTrackingEnabled(shouldTrack));
    }

    public void CaptureExternalForegroundWindow()
    {
        if (_isDisposed)
        {
            return;
        }

        UiDispatch.RunOrDefer(() => _listenerSession?.CaptureCurrentExternalForeground());
    }

    public void StartMonitoring()
    {
        if (_isDisposed || _isMonitoring)
        {
            return;
        }

        _isMonitoring = true;
        UiDispatch.RunOrDefer(() =>
        {
            if (_isDisposed || !_isMonitoring || _listenerSession is not null)
            {
                return;
            }

            try
            {
                _listenerSession = new ClipboardPlatformInterop.ListenerSession(OnNativeClipboardUpdate);
                if (IsForegroundTrackingWanted)
                {
                    _listenerSession.SetForegroundTrackingEnabled(true);
                }

                ClipboardStorageFileOps.CleanupOldFiles(_store.ExportDirectory, TimeSpan.FromDays(1));
                _log?.Invoke("[Clipboard] Started Win32 clipboard listener.");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Clipboard] Listener start failed: {ex.Message}");
            }
        });
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring && _listenerSession is null)
        {
            return;
        }

        _isMonitoring = false;
        CancelCaptureDebounce();
        UiDispatch.RunOrDefer(() =>
        {
            _listenerSession?.Dispose();
            _listenerSession = null;
            _log?.Invoke("[Clipboard] Stopped Win32 clipboard listener.");
        });
    }

    public void SetPaused(bool paused)
    {
        if (_isPaused == paused)
        {
            return;
        }

        _isPaused = paused;
        if (paused)
        {
            CancelCaptureDebounce();
        }

        PauseStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task EnsureLoadedAsync()
    {
        _ = await GetItemsAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClipboardHistoryItem>> GetItemsAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            if (PruneExpiredAndExcessItemsLocked(doc))
            {
                ScheduleSave();
            }

            RefreshSnapshotsLocked(doc);
            return _itemsSnapshot;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public string? GetAbsolutePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.Combine(_store.RootDirectory, normalized);
        return File.Exists(candidate) ? candidate : null;
    }

    public Task<string?> PrepareDragExportImageAsync(ClipboardHistoryItem item) =>
        PrepareExportImagePathAsync(item);

    public string? ResolveImagePath(ClipboardHistoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ImageFileName))
        {
            return null;
        }

        string path = Path.Combine(_store.ImageDirectory, item.ImageFileName);
        return File.Exists(path) ? path : null;
    }

    public string? ResolveThumbnailPath(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ThumbnailFileName))
        {
            string thumbPath = Path.Combine(_store.ThumbnailDirectory, item.ThumbnailFileName);
            if (File.Exists(thumbPath))
            {
                return thumbPath;
            }
        }

        return ResolveImagePath(item);
    }

    public async Task<string?> PrepareExportImagePathAsync(ClipboardHistoryItem item)
    {
        string? sourcePath = ResolveImagePath(item);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        Directory.CreateDirectory(_store.ExportDirectory);
        DateTime timestamp = item.CreatedAtUtcTicks > 0
            ? new DateTime(item.CreatedAtUtcTicks, DateTimeKind.Utc).ToLocalTime()
            : DateTime.Now;
        string exportPath = Path.Combine(
            _store.ExportDirectory,
            $"DeskBox_Clip_{timestamp:yyyyMMdd_HHmmss}_{item.Id[..Math.Min(6, item.Id.Length)]}.png");

        if (!File.Exists(exportPath))
        {
            await Task.Run(() => ClipboardStorageFileOps.CopyFileOverwrite(sourcePath, exportPath)).ConfigureAwait(false);
        }

        return exportPath;
    }

    /// <summary>
    /// Ingests a pre-built item directly (used by unit tests and drag-and-drop imports).
    /// </summary>
    public async Task<ClipboardHistoryItem?> IngestItemAsync(
        ClipboardHistoryItem candidate,
        ClipboardObservedApp? sourceApp = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            long nowTicks = DateTime.UtcNow.Ticks;

            if (sourceApp is not null && !string.IsNullOrWhiteSpace(sourceApp.AppPath))
            {
                RecordObservedAppLocked(doc, sourceApp, nowTicks);
            }

            ClipboardHistoryItem result;
            int existingIdx = doc.Items.FindIndex(x =>
                string.Equals(x.ContentHash, candidate.ContentHash, StringComparison.Ordinal));
            if (existingIdx >= 0)
            {
                ClipboardHistoryItem existing = doc.Items[existingIdx];
                existing.UpdatedAtUtcTicks = nowTicks;
                existing.UseCount = Math.Max(1, existing.UseCount + 1);
                if (!string.IsNullOrWhiteSpace(candidate.SourceAppName))
                {
                    existing.SourceAppName = candidate.SourceAppName;
                    existing.SourceAppPath = candidate.SourceAppPath;
                }

                doc.Items.RemoveAt(existingIdx);
                _ = await _store.ExternalizeItemPayloadsAsync(existing).ConfigureAwait(false);
                doc.Items.Insert(0, existing);
                result = existing;
            }
            else
            {
                if (candidate.CreatedAtUtcTicks <= 0)
                {
                    candidate.CreatedAtUtcTicks = nowTicks;
                }

                candidate.UpdatedAtUtcTicks = nowTicks;
                _ = await _store.ExternalizeItemPayloadsAsync(candidate).ConfigureAwait(false);
                doc.Items.Insert(0, candidate);
                result = candidate;
            }

            PruneExpiredAndExcessItemsLocked(doc);
            ScheduleSave();
            RaiseHistoryChanged();
            return result;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<bool> TogglePinAsync(string itemId)
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            ClipboardHistoryItem? item = doc.Items.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
            if (item is null)
            {
                return false;
            }

            item.IsPinned = !item.IsPinned;
            ScheduleSave();
            RaiseHistoryChanged();
            return item.IsPinned;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<bool> DeleteItemAsync(string itemId)
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            int removed = doc.Items.RemoveAll(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
            if (removed <= 0)
            {
                return false;
            }

            CleanupOrphanedMediaLocked(doc);
            await FlushSaveLockedAsync(doc).ConfigureAwait(false);
            RaiseHistoryChanged();
            return true;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<int> ClearUnpinnedAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            int removed = doc.Items.RemoveAll(x => !x.IsPinned);
            CleanupOrphanedMediaLocked(doc);
            await FlushSaveLockedAsync(doc).ConfigureAwait(false);
            RaiseHistoryChanged();
            return removed;
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public async Task<string> GetFullTextAsync(
        ClipboardHistoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!string.IsNullOrWhiteSpace(item.TextPayloadFileName))
        {
            string? external = await _store.ReadPayloadTextAsync(item.TextPayloadFileName, cancellationToken)
                .ConfigureAwait(false);
            if (external is not null)
            {
                return external;
            }
        }

        return item.TextContent ?? item.Summary ?? string.Empty;
    }

    public async Task<(string Text, bool WasTruncated)> GetTextPreviewAsync(
        ClipboardHistoryItem item,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (maxChars <= 0)
        {
            return (string.Empty, false);
        }

        if (!string.IsNullOrWhiteSpace(item.TextPayloadFileName))
        {
            (string? preview, bool truncated) = await _store
                .ReadPayloadTextPreviewAsync(item.TextPayloadFileName, maxChars, cancellationToken)
                .ConfigureAwait(false);
            if (preview is not null)
            {
                return (preview, truncated);
            }
        }

        string inline = item.TextContent ?? item.Summary ?? string.Empty;
        if (inline.Length <= maxChars)
        {
            return (inline, false);
        }

        return (ClipboardStore.TruncateSurrogateSafe(inline, maxChars), true);
    }

    public async Task<string?> GetHtmlPayloadAsync(
        ClipboardHistoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!string.IsNullOrWhiteSpace(item.HtmlPayloadFileName))
        {
            string? external = await _store.ReadPayloadTextAsync(item.HtmlPayloadFileName, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(external))
            {
                return external;
            }
        }

        return item.HtmlContent;
    }

    public async Task<string?> GetRtfPayloadAsync(
        ClipboardHistoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!string.IsNullOrWhiteSpace(item.RtfPayloadFileName))
        {
            string? external = await _store.ReadPayloadTextAsync(item.RtfPayloadFileName, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(external))
            {
                return external;
            }
        }

        return item.RtfContent;
    }

    public async Task<IReadOnlyList<ClipboardObservedApp>> GetObservedAppsSnapshotAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedDocument is not null)
            {
                return _observedAppsSnapshot;
            }
        }
        finally
        {
            _storeLock.Release();
        }

        ClipboardStorageStats stats = await ClipboardStore.CalculateDiskStorageStatsAsync(_store.RootDirectory)
            .ConfigureAwait(false);
        return stats.ObservedApps;
    }

    public async Task<ClipboardStorageStats> GetStorageStatsAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedDocument is not null)
            {
                int activeCount = _cachedDocument.Items.Count;
                int pinnedCount = _cachedDocument.Items.Count(x => x.IsPinned);
                (int imgCount, long imgBytes) = ClipboardStorageFileOps.MeasureDirectoryFiles(_store.ImageDirectory);
                (_, long thumbBytes) = ClipboardStorageFileOps.MeasureDirectoryFiles(_store.ThumbnailDirectory);
                (_, long payloadBytes) = ClipboardStorageFileOps.MeasureDirectoryFiles(_store.PayloadDirectory);
                var observed = _cachedDocument.ObservedApps
                    .OrderByDescending(a => a.LastSeenUtcTicks)
                    .Take(MaxObservedApps)
                    .ToList();
                return new ClipboardStorageStats(
                    activeCount,
                    pinnedCount,
                    imgCount,
                    imgBytes + thumbBytes + payloadBytes,
                    observed);
            }
        }
        finally
        {
            _storeLock.Release();
        }

        return await ClipboardStore.CalculateDiskStorageStatsAsync(_store.RootDirectory)
            .ConfigureAwait(false);
    }

    public async Task<bool> CopyItemToClipboardAsync(ClipboardHistoryItem item, bool forcePlainText = false, bool recordReuse = true)
    {
        ArgumentNullException.ThrowIfNull(item);

        bool plain = forcePlainText || _settingsService.Settings.Clipboard.ClipboardDefaultPastePlainText;
        DeskBoxClipboardWriteScope.MarkContentHash(item.ContentHash);

        string? preloadedText = null;
        string? preloadedHtml = null;
        string? preloadedRtf = null;
        if (item.Kind == ClipboardItemKind.Text)
        {
            preloadedText = await GetFullTextAsync(item).ConfigureAwait(false);
            if (!plain)
            {
                preloadedHtml = await GetHtmlPayloadAsync(item).ConfigureAwait(false);
                preloadedRtf = await GetRtfPayloadAsync(item).ConfigureAwait(false);
            }
        }

        bool copied = await UiDispatch.RunAsync(async () =>
        {
            try
            {
                switch (item.Kind)
                {
                    case ClipboardItemKind.Files:
                    {
                        IReadOnlyList<string> paths = item.FilePaths ?? [];
                        if (plain)
                        {
                            string joined = string.Join(Environment.NewLine, paths);
                            DeskBoxClipboardWriteScope.MarkText(joined);
                            var pkg = new DataPackage();
                            pkg.SetText(joined);
                            Clipboard.SetContent(pkg);
                            TryFlushClipboard();
                            return true;
                        }

                        string[] existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
                        if (existing.Length == 0)
                        {
                            return false;
                        }

                        DeskBoxClipboardWriteScope.MarkWrite(paths: existing);
                        return ShellClipboardHelper.TrySetFileDropList(existing, cut: false);
                    }

                    case ClipboardItemKind.Image:
                    {
                        string? imgPath = ResolveImagePath(item);
                        if (string.IsNullOrWhiteSpace(imgPath))
                        {
                            return false;
                        }

                        DeskBoxClipboardWriteScope.MarkWrite(hasImage: true);
                        StorageFile file = await StorageFile.GetFileFromPathAsync(imgPath);
                        var pkg = new DataPackage();
                        pkg.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));

                        string? exportPath = await PrepareExportImagePathAsync(item);
                        if (!string.IsNullOrWhiteSpace(exportPath) && File.Exists(exportPath))
                        {
                            StorageFile exportFile = await StorageFile.GetFileFromPathAsync(exportPath);
                            pkg.SetStorageItems([exportFile]);
                        }

                        Clipboard.SetContent(pkg);
                        TryFlushClipboard();
                        return true;
                    }

                    default:
                    {
                        string text = preloadedText ?? item.TextContent ?? item.Summary;
                        DeskBoxClipboardWriteScope.MarkText(text);
                        var pkg = new DataPackage();
                        pkg.SetText(text);
                        if (!plain)
                        {
                            if (!string.IsNullOrWhiteSpace(preloadedHtml))
                            {
                                pkg.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(preloadedHtml));
                            }

                            if (!string.IsNullOrWhiteSpace(preloadedRtf))
                            {
                                pkg.SetRtf(preloadedRtf);
                            }

                            if (item.SubKind == ClipboardTextSubKind.Url &&
                                Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri))
                            {
                                pkg.SetWebLink(uri);
                            }
                        }

                        Clipboard.SetContent(pkg);
                        TryFlushClipboard();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Clipboard] CopyItemToClipboard failed: {ex.Message}");
                return false;
            }
        }).ConfigureAwait(false);

        if (copied && recordReuse)
        {
            await RecordItemReuseAsync(item.Id).ConfigureAwait(false);
        }

        return copied;
    }

    public async Task<bool> CopyItemToClipboardAsync(string itemId, bool forcePlainText = false)
    {
        IReadOnlyList<ClipboardHistoryItem> items = _itemsSnapshot.Count > 0
            ? _itemsSnapshot
            : await GetItemsAsync().ConfigureAwait(false);
        ClipboardHistoryItem? item = items.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
        return item is not null && await CopyItemToClipboardAsync(item, forcePlainText).ConfigureAwait(false);
    }

    public async Task<(bool Copied, bool Pasted)> PasteItemToExternalWindowAsync(string itemId, bool forcePlainText = false)
    {
        IReadOnlyList<ClipboardHistoryItem> items = _itemsSnapshot.Count > 0
            ? _itemsSnapshot
            : await GetItemsAsync().ConfigureAwait(false);
        ClipboardHistoryItem? item = items.FirstOrDefault(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            return (false, false);
        }

        (bool copied, ClipboardPasteOutcome outcome) = await PasteItemToActiveWindowAsync(item, forcePlainText).ConfigureAwait(false);
        return (copied, copied && outcome == ClipboardPasteOutcome.Pasted);
    }

    internal async Task<(bool Copied, ClipboardPasteOutcome Outcome)> PasteItemToActiveWindowAsync(
        ClipboardHistoryItem item,
        bool forcePlainText = false)
    {
        bool copied = await CopyItemToClipboardAsync(item, forcePlainText, recordReuse: true).ConfigureAwait(false);
        if (!copied)
        {
            return (false, ClipboardPasteOutcome.CopiedFallback);
        }

        IntPtr fallbackHwnd = _listenerSession?.LastExternalForegroundWindow ?? IntPtr.Zero;
        await Task.Delay(18).ConfigureAwait(false);
        ClipboardPasteOutcome outcome = await Task.Run(() =>
            ClipboardPlatformInterop.TryPasteToActiveOrLastWindow(fallbackHwnd)).ConfigureAwait(false);
        return (true, outcome);
    }

    public async Task<int> ImportDroppedFilesAsync(IEnumerable<string> paths)
    {
        string[] valid = paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (valid.Length == 0)
        {
            return 0;
        }

        ClipboardHistoryItem item = BuildFilesHistoryItem(valid, sourceApp: null);
        await IngestItemAsync(item).ConfigureAwait(false);
        await CopyItemToClipboardAsync(item, forcePlainText: false, recordReuse: false).ConfigureAwait(false);
        return valid.Length;
    }

    public async Task<bool> ImportDroppedTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ClipboardHistoryItem? item = BuildTextHistoryItem(
            text,
            htmlContent: null,
            rtfContent: null,
            sourceApp: null,
            _settingsService.Settings.Clipboard);
        if (item is null)
        {
            return false;
        }

        await IngestItemAsync(item).ConfigureAwait(false);
        await CopyItemToClipboardAsync(item, forcePlainText: false, recordReuse: false).ConfigureAwait(false);
        return true;
    }

    private async Task RecordItemReuseAsync(string itemId)
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ClipboardStoreDocument doc = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            int idx = doc.Items.FindIndex(x => string.Equals(x.Id, itemId, StringComparison.Ordinal));
            if (idx < 0)
            {
                return;
            }

            ClipboardHistoryItem item = doc.Items[idx];
            item.UseCount = Math.Max(1, item.UseCount + 1);
            item.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;

            if (_settingsService.Settings.Clipboard.ClipboardMoveToTopOnReuse && idx > 0)
            {
                doc.Items.RemoveAt(idx);
                doc.Items.Insert(0, item);
                RaiseHistoryChanged();
            }

            ScheduleSave();
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private void OnNativeClipboardUpdate()
    {
        if (_isDisposed || !_isMonitoring || _isPaused)
        {
            return;
        }

        if (!FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Clipboard))
        {
            return;
        }

        CancelCaptureDebounce();
        var cts = new CancellationTokenSource();
        _captureDebounceCts = cts;
        _ = DebouncedCaptureAsync(cts.Token);
    }

    private async Task DebouncedCaptureAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(CaptureDebounceDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || _isDisposed || !_isMonitoring || _isPaused)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await UiDispatch.RunAsync(() => CaptureCurrentClipboardOnUiThreadAsync(token)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Clipboard] Capture failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _captureInFlight, 0);
        }
    }

    private async Task CaptureCurrentClipboardOnUiThreadAsync(CancellationToken token)
    {
        ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
        if (!slice.ClipboardCaptureText &&
            !slice.ClipboardCaptureImages &&
            !slice.ClipboardCaptureFiles)
        {
            return;
        }

        IntPtr fallbackHwnd = _listenerSession?.LastExternalForegroundWindow ?? IntPtr.Zero;
        ClipboardSourceAppInfo? rawSourceApp = ClipboardPlatformInterop.DetectSourceApp(fallbackHwnd);
        if (rawSourceApp is { } detectedApp && IsExcludedApp(detectedApp, slice.ClipboardExcludedApps))
        {
            return;
        }

        ClipboardSourceAppInfo? effectiveSourceApp = slice.ClipboardShowSourceApp ? rawSourceApp : null;
        ClipboardObservedApp? observedApp = rawSourceApp is { } obs
            ? new ClipboardObservedApp
            {
                AppPath = obs.ExecutablePath,
                AppName = obs.DisplayName,
                LastSeenUtcTicks = DateTime.UtcNow.Ticks
            }
            : null;

        long maxImageBytes = Math.Max(1L, slice.ClipboardMaxImageSizeMb) * 1024L * 1024L;
        long maxTextBytes = Math.Max(1L, slice.ClipboardMaxTextSizeMb) * 1024L * 1024L;

        bool ignoredByPrivacyOrCut = false;
        bool hasFilesFormat = false;
        bool hasNonWhitespaceText = false;
        string? unicodeText = null;
        bool unicodeTextExceededLimit = false;
        byte[]? rawPngBytes = null;
        int rawPngWidth = 0;
        int rawPngHeight = 0;
        bool hasDibFormat = false;
        string? htmlFragment = null;
        string? rtfText = null;

        bool snapshotOpened = await RunClipboardAttemptWithAsyncRetryAsync(
            () => ClipboardPlatformInterop.TryReadNativeSnapshot(
                slice.ClipboardIgnoreSystemPrivacyFormats,
                slice.ClipboardCaptureFiles,
                slice.ClipboardCaptureImages,
                maxImageBytes,
                slice.ClipboardCaptureText,
                maxTextBytes,
                out ignoredByPrivacyOrCut,
                out hasFilesFormat,
                out hasNonWhitespaceText,
                out unicodeText,
                out unicodeTextExceededLimit,
                out rawPngBytes,
                out rawPngWidth,
                out rawPngHeight,
                out hasDibFormat,
                out htmlFragment,
                out rtfText),
            token).ConfigureAwait(true);

        if (!snapshotOpened || ignoredByPrivacyOrCut || token.IsCancellationRequested)
        {
            return;
        }

        // Priority 1: File drop list (CF_HDROP)
        if (hasFilesFormat)
        {
            if (slice.ClipboardCaptureFiles &&
                ShellClipboardHelper.TryGetFileDropList(out string[] droppedFiles, out bool isCutDrop))
            {
                if (isCutDrop)
                {
                    return;
                }

                if (droppedFiles.Length > 0)
                {
                    if (DeskBoxClipboardWriteScope.ShouldIgnorePaths(droppedFiles))
                    {
                        return;
                    }

                    ClipboardHistoryItem filesItem = BuildFilesHistoryItem(droppedFiles, effectiveSourceApp);
                    if (!DeskBoxClipboardWriteScope.ShouldIgnoreContentHash(filesItem.ContentHash))
                    {
                        await IngestItemAsync(filesItem, observedApp).ConfigureAwait(false);
                    }
                }
            }

            return;
        }

        DataPackageView? dataView = null;
        if (!hasNonWhitespaceText || (slice.ClipboardCaptureText && unicodeText is null && !unicodeTextExceededLimit))
        {
            try
            {
                dataView = Clipboard.GetContent();
            }
            catch
            {
                // Continue with native snapshot payloads if WinRT DataPackageView fails.
            }
        }

        if (!hasNonWhitespaceText &&
            !unicodeTextExceededLimit &&
            dataView is not null &&
            SafeDataViewContains(dataView, StandardDataFormats.Text))
        {
            try
            {
                string? winrtText = await dataView.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(winrtText))
                {
                    hasNonWhitespaceText = true;
                    if (slice.ClipboardCaptureText)
                    {
                        unicodeText = winrtText;
                    }
                }
            }
            catch
            {
                // Ignore transient delayed-render failure and continue priority evaluation.
            }
        }

        bool hasWinRtBitmap = !hasNonWhitespaceText &&
                              dataView is not null &&
                              SafeDataViewContains(dataView, StandardDataFormats.Bitmap);
        bool hasAnyImagePayload = rawPngBytes is { Length: > 0 } || hasWinRtBitmap || hasDibFormat;

        ClipboardItemKind? priorityKind = ResolveCapturePriorityKind(
            hasFilesDrop: false,
            hasNonWhitespacePlainText: hasNonWhitespaceText,
            htmlFragment,
            rtfText,
            hasAnyImagePayload,
            out string? fallbackPlainTextFromRich);

        // Priority 2: Text / Link / Color / Rich Text (always beats composite spreadsheet/office bitmaps)
        if (priorityKind == ClipboardItemKind.Text)
        {
            if (!slice.ClipboardCaptureText || unicodeTextExceededLimit)
            {
                return;
            }

            string? text = !string.IsNullOrWhiteSpace(unicodeText)
                ? unicodeText
                : fallbackPlainTextFromRich;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            long utf8Bytes = Encoding.UTF8.GetByteCount(text);
            if (utf8Bytes > maxTextBytes || DeskBoxClipboardWriteScope.ShouldIgnoreText(text))
            {
                return;
            }

            ClipboardHistoryItem? textItem = BuildTextHistoryItem(
                text,
                htmlFragment,
                rtfText,
                effectiveSourceApp,
                slice);
            if (textItem is not null && !DeskBoxClipboardWriteScope.ShouldIgnoreContentHash(textItem.ContentHash))
            {
                await IngestItemAsync(textItem, observedApp).ConfigureAwait(false);
            }

            return;
        }

        // Priority 3: Image / Screenshot (only when no files and no text/rich-text payload exist)
        if (priorityKind == ClipboardItemKind.Image &&
            slice.ClipboardCaptureImages &&
            !DeskBoxClipboardWriteScope.ShouldIgnoreImage())
        {
            await TryCaptureAndPersistImageOnUiThreadAsync(
                rawPngBytes,
                rawPngWidth,
                rawPngHeight,
                hasDibFormat,
                hasWinRtBitmap ? dataView : null,
                maxImageBytes,
                effectiveSourceApp,
                observedApp,
                token).ConfigureAwait(false);
        }
    }

    internal static ClipboardItemKind? ResolveCapturePriorityKind(
        bool hasFilesDrop,
        bool hasNonWhitespacePlainText,
        string? htmlFragment,
        string? rtfText,
        bool hasImagePayload,
        out string? fallbackPlainTextFromRich)
    {
        fallbackPlainTextFromRich = null;

        if (hasFilesDrop)
        {
            return ClipboardItemKind.Files;
        }

        if (hasNonWhitespacePlainText)
        {
            return ClipboardItemKind.Text;
        }

        if (TryExtractVisiblePlainTextFromHtml(htmlFragment, out string? htmlPlain) &&
            !string.IsNullOrWhiteSpace(htmlPlain))
        {
            fallbackPlainTextFromRich = htmlPlain;
            return ClipboardItemKind.Text;
        }

        if (TryExtractVisiblePlainTextFromRtf(rtfText, out string? rtfPlain) &&
            !string.IsNullOrWhiteSpace(rtfPlain))
        {
            fallbackPlainTextFromRich = rtfPlain;
            return ClipboardItemKind.Text;
        }

        if (hasImagePayload)
        {
            return ClipboardItemKind.Image;
        }

        return null;
    }

    private static bool SafeDataViewContains(DataPackageView dataView, string formatId)
    {
        try
        {
            return dataView.Contains(formatId);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> RunClipboardAttemptWithAsyncRetryAsync(
        Func<bool> attemptOnUiThread,
        CancellationToken token)
    {
        if (attemptOnUiThread())
        {
            return true;
        }

        int retryCount = ClipboardPlatformInterop.OpenRetryDelaysMs.Length;
        for (int i = 0; i < retryCount; i++)
        {
            int delayMs = ClipboardPlatformInterop.OpenRetryDelaysMs[i];
            await Task.Delay(delayMs, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return false;
            }

            if (attemptOnUiThread())
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryExtractVisiblePlainTextFromHtml(string? htmlPayload, out string? plainText)
    {
        plainText = null;
        if (string.IsNullOrWhiteSpace(htmlPayload))
        {
            return false;
        }

        ReadOnlySpan<char> span = htmlPayload.AsSpan();
        int startFrag = span.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        if (startFrag >= 0)
        {
            span = span[(startFrag + "<!--StartFragment-->".Length)..];
            int endFrag = span.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
            if (endFrag >= 0)
            {
                span = span[..endFrag];
            }
        }
        else
        {
            int bodyStart = span.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (bodyStart >= 0)
            {
                int bodyCloseBracket = span[bodyStart..].IndexOf('>');
                if (bodyCloseBracket >= 0)
                {
                    span = span[(bodyStart + bodyCloseBracket + 1)..];
                    int bodyEnd = span.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                    if (bodyEnd >= 0)
                    {
                        span = span[..bodyEnd];
                    }
                }
            }
            else if (span.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
            {
                int firstTag = span.IndexOf('<');
                if (firstTag < 0)
                {
                    return false;
                }

                span = span[firstTag..];
            }
        }

        var sb = new StringBuilder(Math.Min(span.Length, 4096));
        int idx = 0;
        while (idx < span.Length)
        {
            if (span[idx] == '<')
            {
                int close = span[idx..].IndexOf('>');
                if (close < 0)
                {
                    break;
                }

                ReadOnlySpan<char> tag = span.Slice(idx + 1, close - 1).Trim();
                if (tag.StartsWith("script", StringComparison.OrdinalIgnoreCase))
                {
                    idx += close + 1;
                    int endScript = span[idx..].IndexOf("</script>", StringComparison.OrdinalIgnoreCase);
                    idx = endScript >= 0 ? idx + endScript + "</script>".Length : span.Length;
                    continue;
                }

                if (tag.StartsWith("style", StringComparison.OrdinalIgnoreCase))
                {
                    idx += close + 1;
                    int endStyle = span[idx..].IndexOf("</style>", StringComparison.OrdinalIgnoreCase);
                    idx = endStyle >= 0 ? idx + endStyle + "</style>".Length : span.Length;
                    continue;
                }

                if (tag.StartsWith("br", StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith("/p", StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith("/div", StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith("/tr", StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith("/li", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('\n');
                }
                else if (tag.StartsWith("/td", StringComparison.OrdinalIgnoreCase) ||
                         tag.StartsWith("/th", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('\t');
                }

                idx += close + 1;
                continue;
            }

            sb.Append(span[idx]);
            idx++;
        }

        if (sb.Length == 0)
        {
            return false;
        }

        string decoded = WebUtility.HtmlDecode(sb.ToString());
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return false;
        }

        plainText = decoded.Trim();
        return plainText.Length > 0;
    }

    internal static bool TryExtractVisiblePlainTextFromRtf(string? rtfPayload, out string? plainText)
    {
        plainText = null;
        if (string.IsNullOrWhiteSpace(rtfPayload))
        {
            return false;
        }

        ReadOnlySpan<char> span = rtfPayload.AsSpan().TrimStart();
        if (!span.StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sb = new StringBuilder(Math.Min(span.Length, 4096));
        int depth = 0;
        int skipGroupDepth = -1;
        int i = 0;

        while (i < span.Length)
        {
            char c = span[i];
            if (c == '{')
            {
                depth++;
                i++;
                if (skipGroupDepth < 0 && i < span.Length && span[i] == '\\')
                {
                    ReadOnlySpan<char> rest = span[(i + 1)..];
                    if (rest.StartsWith("*") ||
                        rest.StartsWith("fonttbl", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("colortbl", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("stylesheet", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("info", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("pict", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("object", StringComparison.OrdinalIgnoreCase))
                    {
                        skipGroupDepth = depth;
                    }
                }

                continue;
            }

            if (c == '}')
            {
                if (depth == skipGroupDepth)
                {
                    skipGroupDepth = -1;
                }

                depth = Math.Max(0, depth - 1);
                i++;
                continue;
            }

            if (skipGroupDepth >= 0)
            {
                i++;
                continue;
            }

            if (c == '\\')
            {
                i++;
                if (i >= span.Length)
                {
                    break;
                }

                char next = span[i];
                if (next is '\\' or '{' or '}')
                {
                    sb.Append(next);
                    i++;
                    continue;
                }

                if (next == '\'')
                {
                    // \'hh hex-encoded byte
                    i = Math.Min(span.Length, i + 3);
                    sb.Append('?');
                    continue;
                }

                if (!char.IsLetter(next))
                {
                    i++;
                    continue;
                }

                int wordStart = i;
                while (i < span.Length && char.IsLetter(span[i]))
                {
                    i++;
                }

                ReadOnlySpan<char> word = span[wordStart..i];
                int numStart = i;
                if (i < span.Length && (span[i] == '-' || char.IsDigit(span[i])))
                {
                    i++;
                    while (i < span.Length && char.IsDigit(span[i]))
                    {
                        i++;
                    }
                }

                if (i < span.Length && span[i] == ' ')
                {
                    i++;
                }

                if (word.Equals("par", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("line", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('\n');
                }
                else if (word.Equals("tab", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('\t');
                }
                else if (word.Equals("u", StringComparison.OrdinalIgnoreCase) &&
                         numStart < span.Length &&
                         int.TryParse(span[numStart..(i > 0 && span[i - 1] == ' ' ? i - 1 : i)], out int codeUnit))
                {
                    ushort u16 = unchecked((ushort)(short)codeUnit);
                    if (u16 != 0)
                    {
                        sb.Append((char)u16);
                    }

                    // Skip single ANSI fallback char if present (commonly '?').
                    if (i < span.Length && span[i] == '?')
                    {
                        i++;
                    }
                }

                continue;
            }

            if (c is not '\r' and not '\n')
            {
                sb.Append(c);
            }

            i++;
        }

        string extracted = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(extracted))
        {
            return false;
        }

        plainText = extracted;
        return true;
    }

    private static ClipboardHistoryItem BuildFilesHistoryItem(
        IReadOnlyList<string> paths,
        ClipboardSourceAppInfo? sourceApp)
    {
        string[] cleaned = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToArray();
        string normalizedForHash = string.Join(
            '\n',
            cleaned.Select(p => p.TrimEnd('\\', '/').ToUpperInvariant()).OrderBy(p => p, StringComparer.Ordinal));
        string hash = $"files:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedForHash)))}";

        string firstName = Path.GetFileName(cleaned[0].TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(firstName))
        {
            firstName = cleaned[0];
        }

        string summary = cleaned.Length == 1
            ? firstName
            : $"{firstName} (+{cleaned.Length - 1})";

        long totalBytes = 0;
        foreach (string p in cleaned)
        {
            try
            {
                if (File.Exists(p))
                {
                    totalBytes += new FileInfo(p).Length;
                }
            }
            catch
            {
                // Ignore inaccessible file metadata.
            }
        }

        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            ContentHash = hash,
            Summary = summary,
            TextContent = string.Join(Environment.NewLine, cleaned),
            FilePaths = cleaned.ToList(),
            ByteSize = totalBytes,
            SourceAppName = sourceApp?.DisplayName,
            SourceAppPath = sourceApp?.ExecutablePath
        };
    }

    private static ClipboardHistoryItem? BuildTextHistoryItem(
        string rawText,
        string? htmlContent,
        string? rtfContent,
        ClipboardSourceAppInfo? sourceApp,
        ClipboardSettingsSlice slice)
    {
        string trimmed = rawText.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        bool isSecret = ClipboardClassifier.IsHighConfidenceSecret(trimmed);
        if (isSecret &&
            string.Equals(
                slice.ClipboardSensitiveProtectionMode,
                ClipboardSettingsSlice.SecretProtectionSkip,
                StringComparison.Ordinal))
        {
            return null;
        }

        ClipboardClassifier.ClassifyText(
            trimmed,
            htmlContent,
            rtfContent,
            out ClipboardTextSubKind subKind,
            out string? colorHex);

        string normalizedKey = trimmed.Replace("\r\n", "\n", StringComparison.Ordinal);
        string contentHash = $"txt:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}";

        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            SubKind = subKind,
            ContentHash = contentHash,
            Summary = ClipboardClassifier.BuildSummary(trimmed),
            TextContent = rawText,
            HtmlContent = subKind == ClipboardTextSubKind.Html ? htmlContent : null,
            RtfContent = subKind is ClipboardTextSubKind.Html or ClipboardTextSubKind.Rtf ? rtfContent : null,
            ColorHex = colorHex,
            IsSensitive = isSecret,
            ByteSize = Encoding.UTF8.GetByteCount(rawText),
            SourceAppName = sourceApp?.DisplayName,
            SourceAppPath = sourceApp?.ExecutablePath
        };
    }

    private async Task TryCaptureAndPersistImageOnUiThreadAsync(
        byte[]? rawPngBytes,
        int rawPngWidth,
        int rawPngHeight,
        bool hasDibFormat,
        DataPackageView? dataViewWithBitmap,
        long maxImageBytes,
        ClipboardSourceAppInfo? effectiveSourceApp,
        ClipboardObservedApp? observedApp,
        CancellationToken token)
    {
        // Path 1: Native PNG buffer already captured without decoding.
        if (rawPngBytes is { Length: > 0 } && rawPngWidth > 0 && rawPngHeight > 0)
        {
            await PersistCapturedPngAndThumbnailAsync(
                rawPngBytes,
                rawPngWidth,
                rawPngHeight,
                preEncodedThumbBytes: rawPngWidth <= ThumbnailMaxPixelSize && rawPngHeight <= ThumbnailMaxPixelSize
                    ? rawPngBytes
                    : null,
                effectiveSourceApp,
                observedApp,
                token).ConfigureAwait(false);
            return;
        }

        // Path 2: WinRT StandardDataFormats.Bitmap (decoded ONCE for both full PNG and 320px thumbnail,
        // avoiding any 33 MB CF_DIB buffer copy when WinRT Bitmap is available).
        if (dataViewWithBitmap is not null)
        {
            try
            {
                IRandomAccessStreamReference bmpRef = await dataViewWithBitmap.GetBitmapAsync();
                using IRandomAccessStreamWithContentType stream = await bmpRef.OpenReadAsync();
                if (stream.Size > 0 && (long)stream.Size <= maxImageBytes * 4)
                {
                    var decoded = await DecodeOnceAndEncodeImageWithThumbnailAsync(stream, maxImageBytes, token)
                        .ConfigureAwait(true);
                    if (decoded.PngBytes is { Length: > 0 })
                    {
                        await PersistCapturedPngAndThumbnailAsync(
                            decoded.PngBytes,
                            decoded.Width,
                            decoded.Height,
                            decoded.ThumbBytes,
                            effectiveSourceApp,
                            observedApp,
                            token).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch
            {
                // Fall through to single-buffer CF_DIB fallback on UI thread.
            }
        }

        // Path 3: Single-buffer CF_DIB -> BMP fallback (WeChat / QQ / DingTalk / Snipaste),
        // decoded ONCE into SoftwareBitmap for both full PNG and 320px thumbnail.
        if (!hasDibFormat || token.IsCancellationRequested)
        {
            return;
        }

        byte[]? dibBmpBytes = null;
        bool dibOpened = await RunClipboardAttemptWithAsyncRetryAsync(
            () => ClipboardPlatformInterop.TryReadClipboardDibAsBmpOnce(maxImageBytes, out dibBmpBytes),
            token).ConfigureAwait(true);
        if (!dibOpened || dibBmpBytes is not { Length: > 0 } || token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            using var ms = new MemoryStream(dibBmpBytes, writable: false);
            using IRandomAccessStream winrtStream = ms.AsRandomAccessStream();
            var decoded = await DecodeOnceAndEncodeImageWithThumbnailAsync(winrtStream, maxImageBytes, token)
                .ConfigureAwait(false);
            if (decoded.PngBytes is { Length: > 0 })
            {
                await PersistCapturedPngAndThumbnailAsync(
                    decoded.PngBytes,
                    decoded.Width,
                    decoded.Height,
                    decoded.ThumbBytes,
                    effectiveSourceApp,
                    observedApp,
                    token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore malformed DIB buffers.
        }
    }

    private async Task PersistCapturedPngAndThumbnailAsync(
        byte[] pngBytes,
        int width,
        int height,
        byte[]? preEncodedThumbBytes,
        ClipboardSourceAppInfo? effectiveSourceApp,
        ClipboardObservedApp? observedApp,
        CancellationToken token)
    {
        if (pngBytes.Length == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        string sha256 = Convert.ToHexStringLower(SHA256.HashData(pngBytes));
        string contentHash = $"img:{sha256}";
        if (DeskBoxClipboardWriteScope.ShouldIgnoreContentHash(contentHash))
        {
            return;
        }

        string fileName = $"{sha256}.png";
        string imagePath = Path.Combine(_store.ImageDirectory, fileName);
        await ClipboardStorageFileOps.WriteBytesIfMissingAsync(imagePath, pngBytes, token).ConfigureAwait(false);

        string? thumbFileName;
        if (preEncodedThumbBytes is { Length: > 0 })
        {
            string thumbPath = Path.Combine(_store.ThumbnailDirectory, fileName);
            await ClipboardStorageFileOps.WriteBytesIfMissingAsync(thumbPath, preEncodedThumbBytes, token)
                .ConfigureAwait(false);
            thumbFileName = fileName;
        }
        else
        {
            thumbFileName = await EnsureThumbnailFromPngBytesAsync(pngBytes, width, height, fileName, token)
                .ConfigureAwait(false);
        }

        var imageItem = new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            ContentHash = contentHash,
            Summary = $"{width} × {height}",
            ImageFileName = fileName,
            ThumbnailFileName = thumbFileName ?? fileName,
            ImageWidth = width,
            ImageHeight = height,
            ByteSize = pngBytes.LongLength,
            SourceAppName = effectiveSourceApp?.DisplayName,
            SourceAppPath = effectiveSourceApp?.ExecutablePath
        };

        await IngestItemAsync(imageItem, observedApp).ConfigureAwait(false);
    }

    private static async Task<(byte[]? PngBytes, byte[]? ThumbBytes, int Width, int Height)> DecodeOnceAndEncodeImageWithThumbnailAsync(
        IRandomAccessStream inputStream,
        long maxImageBytes,
        CancellationToken token)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inputStream);
        uint pixelWidth = decoder.PixelWidth;
        uint pixelHeight = decoder.PixelHeight;
        if (pixelWidth == 0 || pixelHeight == 0)
        {
            return (null, null, 0, 0);
        }

        using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        byte[]? fullPngBytes = await EncodeSoftwareBitmapToPngBytesAsync(
            softwareBitmap,
            scaledWidth: 0,
            scaledHeight: 0,
            maxImageBytes,
            token).ConfigureAwait(false);
        if (fullPngBytes is not { Length: > 0 })
        {
            return (null, null, 0, 0);
        }

        if (pixelWidth <= ThumbnailMaxPixelSize && pixelHeight <= ThumbnailMaxPixelSize)
        {
            return (fullPngBytes, fullPngBytes, (int)pixelWidth, (int)pixelHeight);
        }

        ComputeScaledDimensions(pixelWidth, pixelHeight, ThumbnailMaxPixelSize, out uint thumbW, out uint thumbH);
        byte[]? thumbPngBytes = await EncodeSoftwareBitmapToPngBytesAsync(
            softwareBitmap,
            thumbW,
            thumbH,
            maxImageBytes,
            token).ConfigureAwait(false);

        return (fullPngBytes, thumbPngBytes, (int)pixelWidth, (int)pixelHeight);
    }

    private static async Task<byte[]?> EncodeSoftwareBitmapToPngBytesAsync(
        SoftwareBitmap softwareBitmap,
        uint scaledWidth,
        uint scaledHeight,
        long maxBytes,
        CancellationToken token)
    {
        using var outMemory = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outMemory);
        encoder.SetSoftwareBitmap(softwareBitmap);
        if (scaledWidth > 0 && scaledHeight > 0)
        {
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            encoder.BitmapTransform.ScaledWidth = scaledWidth;
            encoder.BitmapTransform.ScaledHeight = scaledHeight;
        }

        await encoder.FlushAsync();
        if (outMemory.Size == 0 || (long)outMemory.Size > maxBytes)
        {
            return null;
        }

        var bytes = new byte[(int)outMemory.Size];
        outMemory.Seek(0);
        using Stream netStream = outMemory.AsStreamForRead();
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = await netStream.ReadAsync(bytes.AsMemory(offset, bytes.Length - offset), token)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        return bytes;
    }

    private static void ComputeScaledDimensions(
        uint pixelWidth,
        uint pixelHeight,
        uint maxSide,
        out uint scaledWidth,
        out uint scaledHeight)
    {
        if (pixelWidth >= pixelHeight)
        {
            scaledWidth = Math.Min(pixelWidth, maxSide);
            scaledHeight = Math.Max(1, (uint)Math.Round(pixelHeight * (scaledWidth / (double)pixelWidth)));
        }
        else
        {
            scaledHeight = Math.Min(pixelHeight, maxSide);
            scaledWidth = Math.Max(1, (uint)Math.Round(pixelWidth * (scaledHeight / (double)pixelHeight)));
        }
    }

    private async Task<string?> EnsureThumbnailFromPngBytesAsync(
        byte[] pngBytes,
        int knownWidth,
        int knownHeight,
        string fileName,
        CancellationToken token)
    {
        string thumbPath = Path.Combine(_store.ThumbnailDirectory, fileName);
        if (File.Exists(thumbPath))
        {
            return fileName;
        }

        if (knownWidth > 0 &&
            knownHeight > 0 &&
            knownWidth <= ThumbnailMaxPixelSize &&
            knownHeight <= ThumbnailMaxPixelSize)
        {
            await ClipboardStorageFileOps.WriteBytesIfMissingAsync(thumbPath, pngBytes, token).ConfigureAwait(false);
            return fileName;
        }

        try
        {
            using var srcMem = new MemoryStream(pngBytes, writable: false);
            using IRandomAccessStream srcStream = srcMem.AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(srcStream);
            if (decoder.PixelWidth <= ThumbnailMaxPixelSize && decoder.PixelHeight <= ThumbnailMaxPixelSize)
            {
                await ClipboardStorageFileOps.WriteBytesIfMissingAsync(thumbPath, pngBytes, token).ConfigureAwait(false);
                return fileName;
            }

            ComputeScaledDimensions(decoder.PixelWidth, decoder.PixelHeight, ThumbnailMaxPixelSize, out uint thumbW, out uint thumbH);
            var transform = new BitmapTransform
            {
                InterpolationMode = BitmapInterpolationMode.Fant,
                ScaledWidth = thumbW,
                ScaledHeight = thumbH
            };

            using SoftwareBitmap thumbBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            byte[]? thumbBytes = await EncodeSoftwareBitmapToPngBytesAsync(
                thumbBitmap,
                scaledWidth: 0,
                scaledHeight: 0,
                maxBytes: long.MaxValue,
                token).ConfigureAwait(false);
            if (thumbBytes is not { Length: > 0 })
            {
                return null;
            }

            await ClipboardStorageFileOps.WriteBytesIfMissingAsync(thumbPath, thumbBytes, token).ConfigureAwait(false);
            return fileName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsExcludedApp(ClipboardSourceAppInfo sourceApp, IReadOnlyList<string>? excludedApps)
    {
        if (excludedApps is null || excludedApps.Count == 0)
        {
            return false;
        }

        string fullPath = sourceApp.ExecutablePath;
        string fileName = Path.GetFileName(fullPath);
        string stem = Path.GetFileNameWithoutExtension(fullPath);

        foreach (string rule in excludedApps)
        {
            if (string.IsNullOrWhiteSpace(rule))
            {
                continue;
            }

            string trimmed = rule.Trim();
            if (string.Equals(trimmed, fullPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, fileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RecordObservedAppLocked(ClipboardStoreDocument doc, ClipboardObservedApp app, long nowTicks)
    {
        int existingIdx = doc.ObservedApps.FindIndex(x =>
            string.Equals(x.AppPath, app.AppPath, StringComparison.OrdinalIgnoreCase));
        if (existingIdx >= 0)
        {
            ClipboardObservedApp existing = doc.ObservedApps[existingIdx];
            existing.AppName = app.AppName;
            existing.LastSeenUtcTicks = nowTicks;
            doc.ObservedApps.RemoveAt(existingIdx);
            doc.ObservedApps.Insert(0, existing);
        }
        else
        {
            app.LastSeenUtcTicks = nowTicks;
            doc.ObservedApps.Insert(0, app);
            if (doc.ObservedApps.Count > MaxObservedApps)
            {
                doc.ObservedApps.RemoveRange(MaxObservedApps, doc.ObservedApps.Count - MaxObservedApps);
            }
        }
    }

    private bool PruneExpiredAndExcessItemsLocked(ClipboardStoreDocument doc)
    {
        ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
        int retentionDays = slice.ClipboardRetentionDays;
        int maxItems = Math.Max(10, slice.ClipboardMaxItems);
        bool removedAny = false;

        if (retentionDays > 0)
        {
            long cutoffTicks = DateTime.UtcNow.AddDays(-retentionDays).Ticks;
            int expiredRemoved = doc.Items.RemoveAll(item =>
                !item.IsPinned &&
                item.UpdatedAtUtcTicks > 0 &&
                item.UpdatedAtUtcTicks < cutoffTicks);
            if (expiredRemoved > 0)
            {
                removedAny = true;
            }
        }

        int unpinnedCount = doc.Items.Count(x => !x.IsPinned);
        if (unpinnedCount > maxItems)
        {
            int toRemove = unpinnedCount - maxItems;
            for (int i = doc.Items.Count - 1; i >= 0 && toRemove > 0; i--)
            {
                if (!doc.Items[i].IsPinned)
                {
                    doc.Items.RemoveAt(i);
                    toRemove--;
                    removedAny = true;
                }
            }
        }

        if (removedAny)
        {
            CleanupOrphanedMediaLocked(doc);
        }

        return removedAny;
    }

    private void CleanupOrphanedMediaLocked(ClipboardStoreDocument doc)
    {
        var referencedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedThumbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedPayloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ClipboardHistoryItem item in doc.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.ImageFileName))
            {
                referencedImages.Add(item.ImageFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.ThumbnailFileName))
            {
                referencedThumbs.Add(item.ThumbnailFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.TextPayloadFileName))
            {
                referencedPayloads.Add(item.TextPayloadFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.HtmlPayloadFileName))
            {
                referencedPayloads.Add(item.HtmlPayloadFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.RtfPayloadFileName))
            {
                referencedPayloads.Add(item.RtfPayloadFileName);
            }
        }

        ClipboardStorageFileOps.DeleteOrphanedFiles(_store.ImageDirectory, referencedImages);
        ClipboardStorageFileOps.DeleteOrphanedFiles(_store.ThumbnailDirectory, referencedThumbs);
        ClipboardStorageFileOps.DeleteOrphanedFiles(_store.PayloadDirectory, referencedPayloads);
    }

    private async Task<ClipboardStoreDocument> EnsureLoadedLockedAsync()
    {
        if (_cachedDocument is null)
        {
            _cachedDocument = await _store.LoadAsync().ConfigureAwait(false);
            RefreshSnapshotsLocked(_cachedDocument);
        }

        return _cachedDocument;
    }

    private void RefreshSnapshotsLocked(ClipboardStoreDocument doc)
    {
        _itemsSnapshot = doc.Items.ToArray();
        _observedAppsSnapshot = doc.ObservedApps
            .OrderByDescending(a => a.LastSeenUtcTicks)
            .Take(MaxObservedApps)
            .ToArray();
    }

    private void ScheduleSave()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;
        _ = DebouncedSaveAsync(cts.Token);
    }

    private async Task DebouncedSaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounceDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || _isDisposed)
        {
            return;
        }

        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedDocument is not null)
            {
                await _store.SaveAsync(_cachedDocument).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Clipboard] Save failed: {ex.Message}");
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private async Task FlushSaveLockedAsync(ClipboardStoreDocument doc)
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        _saveDebounceCts = null;
        await _store.SaveAsync(doc).ConfigureAwait(false);
    }

    private void OnSettingsChanged()
    {
        if (_isDisposed || _cachedDocument is null)
        {
            return;
        }

        // Only raise HistoryChanged if tightened retention/max-items settings actually pruned items;
        // visual/appearance settings are handled directly by ClipboardWidgetViewModel.OnSettingsChanged
        // without triggering a duplicate 2,000-item RebuildVisibleItems() pass.
        _ = PruneOnSettingsChangedAsync();
    }

    private async Task PruneOnSettingsChangedAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || _cachedDocument is null)
            {
                return;
            }

            if (PruneExpiredAndExcessItemsLocked(_cachedDocument))
            {
                ScheduleSave();
                RaiseHistoryChanged();
            }
        }
        finally
        {
            _storeLock.Release();
        }
    }

    private void RaiseHistoryChanged()
    {
        if (_cachedDocument is not null)
        {
            RefreshSnapshotsLocked(_cachedDocument);
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelCaptureDebounce()
    {
        _captureDebounceCts?.Cancel();
        _captureDebounceCts?.Dispose();
        _captureDebounceCts = null;
    }

    private static void TryFlushClipboard()
    {
        try
        {
            Clipboard.Flush();
        }
        catch
        {
            // Clipboard content is already set; Flush can transiently fail if another app opens clipboard.
        }
    }

    public async Task FlushAsync()
    {
        await _storeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedDocument is not null)
            {
                await FlushSaveLockedAsync(_cachedDocument).ConfigureAwait(false);
            }
        }
        finally
        {
            _storeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        lock (_foregroundTrackingGate)
        {
            _activeForegroundTrackingViews.Clear();
        }

        _settingsService.SettingsChanged -= OnSettingsChanged;
        StopMonitoring();
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        _saveDebounceCts = null;
    }
}
