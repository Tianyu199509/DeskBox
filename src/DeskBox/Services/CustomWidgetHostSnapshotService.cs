using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeskBox.Models;
using DeskBox.Platform;
using Windows.Storage.Streams;

namespace DeskBox.Services;

public sealed record CustomWidgetMusicSnapshot(
    bool HasSession,
    bool IsPlaying,
    string Status,
    string Title,
    string Artist,
    string Album,
    string SourceApp,
    string CoverPath,
    int PositionSeconds,
    int DurationSeconds,
    string PositionText,
    string DurationText,
    int ProgressPercent)
{
    public static readonly CustomWidgetMusicSnapshot Empty = new(
        HasSession: false,
        IsPlaying: false,
        Status: "idle",
        Title: string.Empty,
        Artist: string.Empty,
        Album: string.Empty,
        SourceApp: string.Empty,
        CoverPath: string.Empty,
        PositionSeconds: 0,
        DurationSeconds: 0,
        PositionText: "00:00",
        DurationText: "00:00",
        ProgressPercent: 0);

    public string ResolveToken(string path) => path switch
    {
        "hasSession" => HasSession ? "true" : "false",
        "isPlaying" => IsPlaying ? "true" : "false",
        "status" => Status,
        "title" => Title,
        "artist" => Artist,
        "album" => Album,
        "sourceApp" => SourceApp,
        "coverPath" => CoverPath,
        "positionSeconds" => PositionSeconds.ToString(CultureInfo.InvariantCulture),
        "durationSeconds" => DurationSeconds.ToString(CultureInfo.InvariantCulture),
        "positionText" => PositionText,
        "durationText" => DurationText,
        "progressPercent" => ProgressPercent.ToString(CultureInfo.InvariantCulture),
        _ => string.Empty
    };
}

public sealed record CustomWidgetTodoSnapshot(
    int TotalCount,
    int PendingCount,
    int CompletedCount,
    int TodayPendingCount,
    int TodayCompletedCount,
    int OverdueCount,
    int ImportantPendingCount,
    int CompletionPercent,
    string NextTaskTitle,
    string NextTaskDueText,
    string Summary,
    IReadOnlyList<CustomWidgetRecord> Records)
{
    public static readonly CustomWidgetTodoSnapshot Empty = new(
        TotalCount: 0,
        PendingCount: 0,
        CompletedCount: 0,
        TodayPendingCount: 0,
        TodayCompletedCount: 0,
        OverdueCount: 0,
        ImportantPendingCount: 0,
        CompletionPercent: 0,
        NextTaskTitle: string.Empty,
        NextTaskDueText: string.Empty,
        Summary: "0/0 (0%)",
        Records: []);

    public string ResolveToken(string path) => path switch
    {
        "totalCount" => TotalCount.ToString(CultureInfo.InvariantCulture),
        "pendingCount" => PendingCount.ToString(CultureInfo.InvariantCulture),
        "completedCount" => CompletedCount.ToString(CultureInfo.InvariantCulture),
        "todayPendingCount" => TodayPendingCount.ToString(CultureInfo.InvariantCulture),
        "todayCompletedCount" => TodayCompletedCount.ToString(CultureInfo.InvariantCulture),
        "overdueCount" => OverdueCount.ToString(CultureInfo.InvariantCulture),
        "importantPendingCount" => ImportantPendingCount.ToString(CultureInfo.InvariantCulture),
        "completionPercent" => CompletionPercent.ToString(CultureInfo.InvariantCulture),
        "nextTaskTitle" => NextTaskTitle,
        "nextTaskDueText" => NextTaskDueText,
        "summary" => Summary,
        _ => string.Empty
    };
}

/// <summary>
/// Controlled, read-only, privacy-safe snapshot bridge exposing DeskBox host state
/// (<c>$music.*</c> via Windows SMTC and <c>$todo.*</c> via <see cref="TodoWidgetStore"/>)
/// to Custom Widgets without coupling widget instances to live mutable ViewModels.
/// </summary>
public static class CustomWidgetHostSnapshotService
{
    private static readonly object s_musicGate = new();
    private static readonly List<EventHandler> s_musicSubscribers = [];
    private static MusicSessionService? s_sharedMusicSession;
    private static string s_cachedCoverSignature = string.Empty;
    private static string s_cachedCoverPath = string.Empty;

    public static IDisposable SubscribeMusicChanged(EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        MusicSessionService? toInit = null;
        lock (s_musicGate)
        {
            s_musicSubscribers.Add(handler);
            if (s_sharedMusicSession is null)
            {
                s_sharedMusicSession = new MusicSessionService();
                s_sharedMusicSession.SessionsChanged += OnSharedMusicEvent;
                s_sharedMusicSession.CurrentSessionChanged += OnSharedMusicEvent;
                s_sharedMusicSession.PlaybackInfoChanged += OnSharedMusicEvent;
                s_sharedMusicSession.MediaPropertiesChanged += OnSharedMusicEvent;
                s_sharedMusicSession.TimelinePropertiesChanged += OnSharedMusicEvent;
                toInit = s_sharedMusicSession;
            }
        }

        if (toInit is not null)
        {
            _ = InitializeMusicSafeAsync(toInit);
        }

        return new CallbackSubscription(() => UnsubscribeMusicChanged(handler));
    }

    private static void UnsubscribeMusicChanged(EventHandler handler)
    {
        MusicSessionService? toDispose = null;
        lock (s_musicGate)
        {
            s_musicSubscribers.Remove(handler);
            if (s_musicSubscribers.Count == 0 && s_sharedMusicSession is not null)
            {
                toDispose = s_sharedMusicSession;
                s_sharedMusicSession.SessionsChanged -= OnSharedMusicEvent;
                s_sharedMusicSession.CurrentSessionChanged -= OnSharedMusicEvent;
                s_sharedMusicSession.PlaybackInfoChanged -= OnSharedMusicEvent;
                s_sharedMusicSession.MediaPropertiesChanged -= OnSharedMusicEvent;
                s_sharedMusicSession.TimelinePropertiesChanged -= OnSharedMusicEvent;
                s_sharedMusicSession = null;
            }
        }

        try
        {
            toDispose?.Dispose();
        }
        catch
        {
        }
    }

    private static async Task InitializeMusicSafeAsync(MusicSessionService service)
    {
        try
        {
            await service.InitializeAsync().ConfigureAwait(false);
            OnSharedMusicEvent(service, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private static void OnSharedMusicEvent(object? sender, EventArgs e)
    {
        EventHandler[] snapshot;
        lock (s_musicGate)
        {
            if (s_musicSubscribers.Count == 0)
            {
                return;
            }

            snapshot = s_musicSubscribers.ToArray();
        }

        foreach (EventHandler handler in snapshot)
        {
            try
            {
                handler(sender, e);
            }
            catch
            {
            }
        }
    }

    public static async Task<CustomWidgetMusicSnapshot> CaptureMusicSnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        MusicSessionService? borrowed;
        bool ownsInstance = false;
        lock (s_musicGate)
        {
            borrowed = s_sharedMusicSession;
        }

        try
        {
            if (borrowed is null)
            {
                borrowed = new MusicSessionService();
                ownsInstance = true;
                await borrowed.InitializeAsync().ConfigureAwait(false);
            }

            MusicSessionInfo? info = await borrowed.GetCurrentSessionInfoAsync().ConfigureAwait(false);
            if (info is null ||
                (string.IsNullOrWhiteSpace(info.Title) &&
                 string.IsNullOrWhiteSpace(info.Artist) &&
                 info.PlaybackState is MusicPlaybackState.Unknown or MusicPlaybackState.Stopped))
            {
                return CustomWidgetMusicSnapshot.Empty;
            }

            bool isPlaying = info.PlaybackState == MusicPlaybackState.Playing;
            string status = info.PlaybackState switch
            {
                MusicPlaybackState.Playing => "playing",
                MusicPlaybackState.Paused => "paused",
                _ => "idle"
            };

            TimeSpan effectivePos = info.Position;
            if (isPlaying && info.PositionUpdatedUtc != default)
            {
                double rate = info.PlaybackRate > 0 ? info.PlaybackRate : 1.0;
                TimeSpan delta = DateTimeOffset.UtcNow - info.PositionUpdatedUtc;
                if (delta > TimeSpan.Zero && delta < TimeSpan.FromHours(6))
                {
                    effectivePos += TimeSpan.FromSeconds(delta.TotalSeconds * rate);
                }
            }

            TimeSpan duration = info.Duration > TimeSpan.Zero ? info.Duration : TimeSpan.Zero;
            if (duration > TimeSpan.Zero && effectivePos > duration)
            {
                effectivePos = duration;
            }
            if (effectivePos < TimeSpan.Zero)
            {
                effectivePos = TimeSpan.Zero;
            }

            int posSec = (int)Math.Round(effectivePos.TotalSeconds);
            int durSec = (int)Math.Round(duration.TotalSeconds);
            int progress = durSec > 0
                ? Math.Clamp((int)Math.Round(effectivePos.TotalSeconds * 100.0 / duration.TotalSeconds), 0, 100)
                : 0;

            string title = (info.Title ?? string.Empty).Trim();
            string artist = (info.Artist ?? string.Empty).Trim();
            string album = (info.Album ?? string.Empty).Trim();
            string sourceApp = (info.SourceDisplayName ?? string.Empty).Trim();
            string coverPath = await ResolveCoverPathSafeAsync(title, artist, album, info.Thumbnail).ConfigureAwait(false);

            return new CustomWidgetMusicSnapshot(
                HasSession: true,
                IsPlaying: isPlaying,
                Status: status,
                Title: title,
                Artist: artist,
                Album: album,
                SourceApp: sourceApp,
                CoverPath: coverPath,
                PositionSeconds: posSec,
                DurationSeconds: durSec,
                PositionText: FormatMediaClock(effectivePos),
                DurationText: FormatMediaClock(duration),
                ProgressPercent: progress);
        }
        catch
        {
            return CustomWidgetMusicSnapshot.Empty;
        }
        finally
        {
            if (ownsInstance && borrowed is not null)
            {
                try
                {
                    borrowed.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    public static IDisposable SubscribeTodoChanged(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        void OnStoreChanged(string _, string __) => handler();
        TodoWidgetStore.StoreChanged += OnStoreChanged;
        return new CallbackSubscription(() => TodoWidgetStore.StoreChanged -= OnStoreChanged);
    }

    public static async Task<CustomWidgetTodoSnapshot> CaptureTodoSnapshotAsync(
        SettingsService? settingsService = null,
        int maxItems = 20,
        string? widgetsDataRootOverride = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        int boundedMax = Math.Clamp(maxItems, 1, 50);
        string widgetsRoot = string.IsNullOrWhiteSpace(widgetsDataRootOverride)
            ? Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "widgets")
            : widgetsDataRootOverride;

        if (!Directory.Exists(widgetsRoot))
        {
            return CustomWidgetTodoSnapshot.Empty;
        }

        List<(string Id, string ListName)> candidateWidgets = ResolveActiveTodoWidgets(settingsService, widgetsRoot);
        if (candidateWidgets.Count == 0)
        {
            return CustomWidgetTodoSnapshot.Empty;
        }

        var allItems = new List<(TodoItem Item, string ListName)>();
        foreach ((string widgetId, string listName) in candidateWidgets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string storeFile = Path.Combine(widgetsRoot, widgetId, "todo.json");
                if (!File.Exists(storeFile) && !File.Exists(ResilientJsonStore.GetBackupPath(storeFile)))
                {
                    continue;
                }

                var store = new TodoWidgetStore(widgetsRoot, widgetId);
                TodoWidgetData data = await store.LoadReadOnlySnapshotAsync().ConfigureAwait(false);
                if (data.Items is { Count: > 0 })
                {
                    foreach (TodoItem item in data.Items)
                    {
                        if (!item.IsDeleted && !string.IsNullOrWhiteSpace(item.Text))
                        {
                            allItems.Add((item, listName));
                        }
                    }
                }
            }
            catch
            {
            }
        }

        if (allItems.Count == 0)
        {
            return CustomWidgetTodoSnapshot.Empty;
        }

        DateTimeOffset nowLocal = DateTimeOffset.Now;
        DateOnly today = DateOnly.FromDateTime(nowLocal.DateTime);
        allItems.RemoveAll(entry => TodoRecurrenceService.IsDeferredOccurrence(entry.Item, nowLocal.Date));
        if (allItems.Count == 0)
        {
            return CustomWidgetTodoSnapshot.Empty;
        }

        int totalCount = allItems.Count;
        int pendingCount = 0;
        int completedCount = 0;
        int todayPendingCount = 0;
        int todayCompletedCount = 0;
        int overdueCount = 0;
        int importantPendingCount = 0;

        foreach ((TodoItem item, _) in allItems)
        {
            bool isDueToday = item.DueDate.HasValue &&
                              DateOnly.FromDateTime(item.DueDate.Value.ToLocalTime().DateTime) == today;
            bool isCompletedToday = item.IsCompleted &&
                                    item.CompletedAt.HasValue &&
                                    DateOnly.FromDateTime(item.CompletedAt.Value.ToLocalTime().DateTime) == today;

            if (item.IsCompleted)
            {
                completedCount++;
                if (isDueToday || isCompletedToday)
                {
                    todayCompletedCount++;
                }
            }
            else
            {
                pendingCount++;
                if (item.IsImportant)
                {
                    importantPendingCount++;
                }

                if (isDueToday)
                {
                    todayPendingCount++;
                }

                if (IsItemOverdue(item, nowLocal, today))
                {
                    overdueCount++;
                }
            }
        }

        int completionPercent = totalCount > 0
            ? Math.Clamp((int)Math.Round(completedCount * 100.0 / totalCount), 0, 100)
            : 0;

        List<(TodoItem Item, string ListName)> ordered = allItems
            .OrderBy(static x => x.Item.IsCompleted ? 1 : 0)
            .ThenBy(x => !x.Item.IsCompleted && IsItemOverdue(x.Item, nowLocal, today) ? 0 : 1)
            .ThenBy(static x => !x.Item.IsCompleted && x.Item.IsImportant ? 0 : 1)
            .ThenBy(static x => x.Item.DueDate ?? DateTimeOffset.MaxValue)
            .ThenBy(static x => x.Item.SortOrder)
            .ToList();

        TodoItem? nextPending = ordered.FirstOrDefault(static x => !x.Item.IsCompleted).Item;
        string nextTaskTitle = nextPending is not null ? TruncateText(nextPending.Text.Trim(), 120) : string.Empty;
        string nextTaskDueText = nextPending is not null ? FormatDueText(nextPending) : string.Empty;
        string summary = $"{completedCount}/{totalCount} ({completionPercent}%)";

        var records = new List<CustomWidgetRecord>(Math.Min(boundedMax, ordered.Count));
        foreach ((TodoItem item, string listName) in ordered.Take(boundedMax))
        {
            bool overdue = !item.IsCompleted && IsItemOverdue(item, nowLocal, today);
            int totalSteps = item.Steps?.Count ?? 0;
            int doneSteps = totalSteps > 0 ? item.Steps!.Count(static s => s.IsCompleted) : 0;
            string stepsSummary = totalSteps > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{doneSteps}/{totalSteps}")
                : string.Empty;
            string cleanTitle = TruncateText(item.Text.Trim(), 160);
            string dueText = FormatDueText(item);
            string doneStr = item.IsCompleted ? "true" : "false";
            string importantStr = item.IsImportant ? "true" : "false";

            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = item.Id,
                ["title"] = cleanTitle,
                ["text"] = cleanTitle,
                ["done"] = doneStr,
                ["isCompleted"] = doneStr,
                ["important"] = importantStr,
                ["isImportant"] = importantStr,
                ["priority"] = item.IsImportant ? "high" : "normal",
                ["color"] = TodoItem.GetColorMarkerHex(item.ColorMarker),
                ["colorName"] = TodoItem.NormalizeColorMarker(item.ColorMarker) ?? string.Empty,
                ["dueDate"] = dueText,
                ["dueText"] = dueText,
                ["listName"] = listName,
                ["isOverdue"] = overdue ? "true" : "false",
                ["stepsSummary"] = stepsSummary
            };
            records.Add(new CustomWidgetRecord
            {
                Id = item.Id,
                Fields = fields
            });
        }

        return new CustomWidgetTodoSnapshot(
            TotalCount: totalCount,
            PendingCount: pendingCount,
            CompletedCount: completedCount,
            TodayPendingCount: todayPendingCount,
            TodayCompletedCount: todayCompletedCount,
            OverdueCount: overdueCount,
            ImportantPendingCount: importantPendingCount,
            CompletionPercent: completionPercent,
            NextTaskTitle: nextTaskTitle,
            NextTaskDueText: nextTaskDueText,
            Summary: summary,
            Records: records);
    }

    private static List<(string Id, string ListName)> ResolveActiveTodoWidgets(SettingsService? settingsService, string widgetsRoot)
    {
        var ids = new List<(string Id, string ListName)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (settingsService is not null)
        {
            try
            {
                IReadOnlyList<WidgetConfig> configured = settingsService.Settings.WidgetLayout.Widgets;
                foreach (WidgetConfig cfg in configured)
                {
                    if (cfg.WidgetKind == WidgetKind.Todo &&
                        !cfg.IsDisabled &&
                        !string.IsNullOrWhiteSpace(cfg.Id) &&
                        seen.Add(cfg.Id))
                    {
                        ids.Add((cfg.Id, cfg.Name?.Trim() ?? string.Empty));
                    }
                }

                return ids;
            }
            catch
            {
            }
        }

        try
        {
            foreach (string subDir in Directory.EnumerateDirectories(widgetsRoot))
            {
                string folderName = Path.GetFileName(subDir);
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    continue;
                }

                string todoPath = Path.Combine(subDir, "todo.json");
                if ((File.Exists(todoPath) || File.Exists(ResilientJsonStore.GetBackupPath(todoPath))) &&
                    seen.Add(folderName))
                {
                    ids.Add((folderName, string.Empty));
                }
            }
        }
        catch
        {
        }

        return ids;
    }

    private static bool IsItemOverdue(TodoItem item, DateTimeOffset nowLocal, DateOnly today)
    {
        if (item.IsCompleted || !item.DueDate.HasValue)
        {
            return false;
        }

        DateTimeOffset dueLocal = item.DueDate.Value.ToLocalTime();
        if (item.HasDueTime)
        {
            return dueLocal < nowLocal;
        }

        return DateOnly.FromDateTime(dueLocal.DateTime) < today;
    }

    private static string FormatDueText(TodoItem item)
    {
        if (!item.DueDate.HasValue)
        {
            return string.Empty;
        }

        DateTime local = item.DueDate.Value.ToLocalTime().DateTime;
        return item.HasDueTime
            ? local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatMediaClock(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return "00:00";
        }

        return span.TotalHours >= 1.0
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes:D2}:{span.Seconds:D2}");
    }

    private static string TruncateText(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    public static string GetSmtcCoverCacheDirectory() =>
        Path.Combine(DeskBoxDataPathService.Current.RootPath, "cache", "smtc-covers");

    internal static async Task<string> CacheSmtcCoverStreamAsync(
        IRandomAccessStreamReference thumbnail,
        string signature)
    {
        try
        {
            using IRandomAccessStreamWithContentType stream =
                await thumbnail.OpenReadAsync().AsTask().ConfigureAwait(false);
            ulong size = stream.Size;
            if (size == 0 || size > 2 * 1024 * 1024)
            {
                return string.Empty;
            }

            string cacheDir = GetSmtcCoverCacheDirectory();
            Directory.CreateDirectory(cacheDir);

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
            string shortHash = Convert.ToHexStringLower(hash[..6]);
            string ext = (stream.ContentType ?? string.Empty).Contains("png", StringComparison.OrdinalIgnoreCase)
                ? ".png"
                : ".jpg";
            string filePath = Path.Combine(cacheDir, $"smtc-cover-{shortHash}{ext}");
            string tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";

            try
            {
                using Stream netStream = stream.AsStreamForRead();
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await netStream.CopyToAsync(fs).ConfigureAwait(false);
                }

                CustomWidgetStorageFileOps.MoveFileOverwrite(tempPath, filePath);
            }
            finally
            {
                try { CustomWidgetStorageFileOps.DeleteFileIfExists(tempPath); } catch { }
            }

            PruneSmtcCoverCache(cacheDir, maxFiles: 32);
            return filePath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void PruneSmtcCoverCache(string cacheDir, int maxFiles)
    {
        try
        {
            List<FileInfo> files = new DirectoryInfo(cacheDir)
                .EnumerateFiles("smtc-cover-*")
                .Where(static f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .ToList();
            for (int i = maxFiles; i < files.Count; i++)
            {
                try { CustomWidgetStorageFileOps.DeleteFileIfExists(files[i].FullName); } catch { }
            }
        }
        catch
        {
        }
    }

    private static async Task<string> ResolveCoverPathSafeAsync(
        string title,
        string artist,
        string album,
        IRandomAccessStreamReference? thumbnail)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        string signature = $"{title}|{artist}|{album}";
        lock (s_musicGate)
        {
            if (string.Equals(signature, s_cachedCoverSignature, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(s_cachedCoverPath) &&
                File.Exists(s_cachedCoverPath))
            {
                return s_cachedCoverPath;
            }
        }

        if (thumbnail is null)
        {
            return string.Empty;
        }

        string filePath = await CacheSmtcCoverStreamAsync(thumbnail, signature).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            lock (s_musicGate)
            {
                s_cachedCoverSignature = signature;
                s_cachedCoverPath = filePath;
            }
        }

        return filePath;
    }

    private sealed class CallbackSubscription(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}
