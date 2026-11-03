using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed class CustomWidgetRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public long CreatedAtUtcTicks { get; set; } = DateTimeOffset.UtcNow.UtcTicks;
    public long UpdatedAtUtcTicks { get; set; }
    public bool IsDeleted { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public long EffectiveUpdatedAtUtcTicks =>
        UpdatedAtUtcTicks > 0 ? UpdatedAtUtcTicks : CreatedAtUtcTicks;

    [JsonIgnore]
    public DateTimeOffset CreatedAt
    {
        get => CreatedAtLocal;
        set => CreatedAtUtcTicks = value.UtcTicks;
    }

    [JsonIgnore]
    public DateTimeOffset UpdatedAtUtc =>
        new(EffectiveUpdatedAtUtcTicks, TimeSpan.Zero);

    [JsonIgnore]
    public DateTimeOffset CreatedAtLocal =>
        new DateTimeOffset(CreatedAtUtcTicks, TimeSpan.Zero).ToLocalTime();

    [JsonIgnore]
    public bool IsToday => CreatedAtLocal.Date == DateTimeOffset.Now.Date;

    [JsonIgnore]
    public string TimeText => IsToday
        ? CreatedAtLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
        : CreatedAtLocal.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string DateText => CreatedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string GetField(string key)
    {
        return key switch
        {
            "id" => Id,
            "createdAt" => CreatedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            "timeText" => TimeText,
            "dateText" => DateText,
            "isToday" => IsToday ? "true" : "false",
            _ => Fields.TryGetValue(key, out string? val) ? val : string.Empty
        };
    }
}

public sealed class CustomWidgetStorePayload
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> State { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<CustomWidgetRecord>> Collections { get; set; } =
        new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(CustomWidgetStorePayload))]
internal sealed partial class CustomWidgetStoreJsonContext : JsonSerializerContext;

/// <summary>
/// Manages isolated local storage for a single Custom Widget instance under
/// <c>%LOCALAPPDATA%\DeskBox\data\custom-widgets\{widgetId}\</c>:
/// <list type="bullet">
/// <item><c>manifest.json</c>: Declarative UI &amp; logic definition (watched for hot-reload).</item>
/// <item><c>store.json</c>: Resilient JSON store for settings, state, and collections.</item>
/// <item><c>attachments\</c>: Managed file copies when drop mode is <c>copy</c>.</item>
/// </list>
/// </summary>
public sealed class CustomWidgetDataStore
{
    public const string SubdirectoryName = "custom-widgets";

    private static readonly ConcurrentDictionary<string, CustomWidgetDataStore> WidgetStores =
        new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _widgetId;
    private readonly string _widgetDirectory;
    private readonly string _manifestPath;
    private readonly string _storePath;
    private readonly string _attachmentsDirectory;
    private CustomWidgetStorePayload? _cached;
    private long _lastLocalSaveUtcTicks;

    public CustomWidgetDataStore(string widgetId, string? rootDataDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        _widgetId = widgetId;
        string baseDir = string.IsNullOrWhiteSpace(rootDataDirectory)
            ? DeskBoxDataPathService.Current.DataDirectory
            : rootDataDirectory;
        _widgetDirectory = Path.Combine(baseDir, SubdirectoryName, GetSafeWidgetFileName(widgetId));
        _manifestPath = Path.Combine(_widgetDirectory, "manifest.json");
        _storePath = Path.Combine(_widgetDirectory, "store.json");
        _attachmentsDirectory = Path.Combine(_widgetDirectory, "attachments");
        Directory.CreateDirectory(_widgetDirectory);
    }

    public string WidgetId => _widgetId;
    public string WidgetDirectory => _widgetDirectory;
    public string ManifestPath => _manifestPath;
    public string ManifestFilePath => _manifestPath;
    public string StorePath => _storePath;
    public string StoreFilePath => _storePath;
    public string AttachmentsDirectory => _attachmentsDirectory;
    public long LastLocalSaveUtcTicks => Interlocked.Read(ref _lastLocalSaveUtcTicks);

    public event EventHandler? Changed;
    internal event Action<object?, bool>? StoreMutated;

    public static CustomWidgetDataStore ForWidget(string widgetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        return WidgetStores.GetOrAdd(widgetId, static id => new CustomWidgetDataStore(id));
    }

    internal static void ReleaseWidgetIfUnused(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId) ||
            !WidgetStores.TryGetValue(widgetId, out CustomWidgetDataStore? store))
        {
            return;
        }

        if (store.Changed is null && store.StoreMutated is null)
        {
            WidgetStores.TryRemove(new KeyValuePair<string, CustomWidgetDataStore>(widgetId, store));
            if (store._gate.Wait(0))
            {
                try
                {
                    store._cached = null;
                }
                finally
                {
                    store._gate.Release();
                }
            }
        }
    }

    internal static int TrimInMemoryPayloads()
    {
        int trimmed = 0;
        foreach (CustomWidgetDataStore store in WidgetStores.Values)
        {
            if (!store._gate.Wait(0))
            {
                continue;
            }

            try
            {
                if (store._cached is not null)
                {
                    store._cached = null;
                    trimmed++;
                }
            }
            finally
            {
                store._gate.Release();
            }
        }

        return trimmed;
    }

    public static string GetWidgetsRootDirectory()
    {
        string path = Path.Combine(DeskBoxDataPathService.Current.DataDirectory, SubdirectoryName);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<string> ReadOrCreateManifestJsonAsync(string defaultTemplateId = "work-logger")
    {
        await _gate.WaitAsync();
        try
        {
            if (File.Exists(_manifestPath))
            {
                string existing = await File.ReadAllTextAsync(_manifestPath);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    if (CustomWidgetManifestMetadataStamper.ShouldAllowAutomaticOfficialUpgrade(existing))
                    {
                        if (TryResolveUpgradedOfficialPresetId(existing) is { } upgradedPresetId)
                        {
                            string upgraded = CustomWidgetManifestMetadataStamper.StampProvenance(
                                CustomWidgetTemplates.ResolveEffectiveTemplateJson(upgradedPresetId),
                                origin: CustomWidgetOriginKinds.Official,
                                isCustomized: false);
                            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_manifestPath, upgraded);
                            return upgraded;
                        }

                        if (CustomWidgetRemoteCatalogService.Shared.TryResolveNewerOfficialManifestJson(
                                existing,
                                out string? remoteOtaJson) &&
                            !string.IsNullOrWhiteSpace(remoteOtaJson))
                        {
                            string stampedRemote = CustomWidgetManifestMetadataStamper.StampProvenance(
                                remoteOtaJson,
                                origin: CustomWidgetOriginKinds.Official,
                                isCustomized: false);
                            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_manifestPath, stampedRemote);
                            return stampedRemote;
                        }
                    }

                    return existing;
                }
            }

            string templateJson = CustomWidgetManifestMetadataStamper.StampProvenance(
                CustomWidgetTemplates.ResolveEffectiveTemplateJson(defaultTemplateId),
                origin: CustomWidgetOriginKinds.Official,
                isCustomized: false);
            Directory.CreateDirectory(_widgetDirectory);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_manifestPath, templateJson);
            return templateJson;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? TryResolveUpgradedOfficialPresetId(string rawJson)
    {
        if (rawJson.Contains("\"id\": \"deskbox.sys-pulse\"", StringComparison.Ordinal) &&
            (rawJson.Contains("\"version\": \"1.0.0\"", StringComparison.Ordinal) ||
             rawJson.Contains("\"version\": \"2.0.0\"", StringComparison.Ordinal) ||
             rawJson.Contains("\"version\": \"3.0.0\"", StringComparison.Ordinal)))
        {
            return CustomWidgetTemplates.SystemPulseTemplateId;
        }

        if (rawJson.Contains("\"id\": \"deskbox.pomodoro\"", StringComparison.Ordinal) &&
            (rawJson.Contains("\"version\": \"2.0.0\"", StringComparison.Ordinal) ||
             rawJson.Contains("\"version\": \"3.0.0\"", StringComparison.Ordinal) ||
             rawJson.Contains("\"version\": \"3.1.0\"", StringComparison.Ordinal) ||
             rawJson.Contains("\"version\": \"3.2.0\"", StringComparison.Ordinal) ||
             rawJson.Contains("\"version\": \"4.0.0\"", StringComparison.Ordinal)))
        {
            return CustomWidgetTemplates.PomodoroTemplateId;
        }

        if (rawJson.Contains("\"id\": \"deskbox.countdown\"", StringComparison.Ordinal) &&
            (rawJson.Contains("\"version\": \"1.0.0\"", StringComparison.Ordinal) ||
             rawJson.Contains("\"version\": \"2.0.0\"", StringComparison.Ordinal)))
        {
            return CustomWidgetTemplates.CountdownTemplateId;
        }

        if (rawJson.Contains("\"id\": \"deskbox.ai-pulse\"", StringComparison.Ordinal) &&
            (rawJson.Contains("\"version\": \"1.0.0\"", StringComparison.Ordinal) ||
             rawJson.Contains("\"version\": \"2.0.0\"", StringComparison.Ordinal)))
        {
            return CustomWidgetTemplates.AiPulseTemplateId;
        }

        return null;
    }

    public async Task WriteManifestJsonAsync(string manifestJson, object? origin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_widgetDirectory);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_manifestPath, manifestJson);
        }
        finally
        {
            _gate.Release();
        }

        StoreMutated?.Invoke(origin, true);
    }

    public async Task<CustomWidgetStorePayload> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return Clone(await EnsureCachedNoLockAsync());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CustomWidgetStorePayload> EnsureCachedNoLockAsync()
    {
        _cached ??= await ResilientJsonStore.LoadAsync(
            _storePath,
            json => Normalize(JsonSerializer.Deserialize(
                json,
                CustomWidgetStoreJsonContext.Default.CustomWidgetStorePayload)),
            static () => new CustomWidgetStorePayload(),
            nameof(CustomWidgetDataStore));
        return _cached;
    }

    public async Task<CustomWidgetStorePayload> ReloadFromDiskAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _cached = await ResilientJsonStore.LoadAsync(
                _storePath,
                json => Normalize(JsonSerializer.Deserialize(
                    json,
                    CustomWidgetStoreJsonContext.Default.CustomWidgetStorePayload)),
                static () => new CustomWidgetStorePayload(),
                nameof(CustomWidgetDataStore));
            return Clone(_cached);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CustomWidgetStorePayload payload, object? origin = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        CustomWidgetStorePayload normalized = Normalize(payload);
        await _gate.WaitAsync();
        try
        {
            Interlocked.Exchange(ref _lastLocalSaveUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            await ResilientJsonStore.SaveAsync(
                _storePath,
                JsonSerializer.Serialize(
                    normalized,
                    CustomWidgetStoreJsonContext.Default.CustomWidgetStorePayload));
            _cached = Clone(normalized);
            Interlocked.Exchange(ref _lastLocalSaveUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        StoreMutated?.Invoke(origin, false);
    }

    public async Task ClearAllDataAsync(object? origin = null)
    {
        await _gate.WaitAsync();
        try
        {
            Interlocked.Exchange(ref _lastLocalSaveUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            _ = await EnsureCachedNoLockAsync();
            var fresh = new CustomWidgetStorePayload();
            if (_cached is not null)
            {
                foreach ((string k, string v) in _cached.Settings)
                {
                    fresh.Settings[k] = v;
                }
            }

            await ResilientJsonStore.SaveAsync(
                _storePath,
                JsonSerializer.Serialize(
                    fresh,
                    CustomWidgetStoreJsonContext.Default.CustomWidgetStorePayload));
            _cached = Clone(fresh);
            Interlocked.Exchange(ref _lastLocalSaveUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

            if (Directory.Exists(_attachmentsDirectory))
            {
                try
                {
                    CustomWidgetStorageFileOps.DeleteDirectoryRecursive(_attachmentsDirectory);
                }
                catch (Exception ex)
                {
                    App.Log($"[CustomWidgetStore] Attachment cleanup failed for {_widgetId}: {ex.Message}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        StoreMutated?.Invoke(origin, false);
    }

    public async Task<(string StoredPath, string FullPath, string FileName, string Mode)> ResolveDroppedFileWithPortablePathAsync(
        string sourcePath,
        string requestedMode)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        string fileName = Path.GetFileName(
            fullSourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string mode = string.Equals(requestedMode, "copy", StringComparison.OrdinalIgnoreCase)
            ? "copy"
            : "link";

        if (mode == "copy" && File.Exists(fullSourcePath))
        {
            Directory.CreateDirectory(_attachmentsDirectory);
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            string targetPath = Path.Combine(_attachmentsDirectory, fileName);
            int counter = 2;
            while (File.Exists(targetPath))
            {
                targetPath = Path.Combine(_attachmentsDirectory, $"{baseName} ({counter}){ext}");
                counter++;
            }

            await Task.Run(() => CustomWidgetStorageFileOps.CopyFileWithoutOverwrite(fullSourcePath, targetPath));
            string actualFileName = Path.GetFileName(targetPath);
            string portablePath = $"attachments/{actualFileName}";
            return (portablePath, targetPath, actualFileName, "copy");
        }

        return (fullSourcePath, fullSourcePath, fileName, "link");
    }

    public async Task<(string ResolvedPath, string FileName, string Mode)> ResolveDroppedFileAsync(
        string sourcePath,
        string requestedMode)
    {
        (string storedPath, _, string fileName, string mode) =
            await ResolveDroppedFileWithPortablePathAsync(sourcePath, requestedMode);
        return (storedPath, fileName, mode);
    }

    /// <summary>
    /// Resolves either a portable widget-relative attachment path (<c>attachments/foo.pdf</c>)
    /// or a legacy/rebased absolute path into a safe runtime filesystem path.
    /// </summary>
    public string ResolveRuntimePath(string? storedOrRawPath)
    {
        if (string.IsNullOrWhiteSpace(storedOrRawPath))
        {
            return string.Empty;
        }

        string trimmed = Environment.ExpandEnvironmentVariables(storedOrRawPath.Trim());
        if (trimmed.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith(@"attachments\", StringComparison.OrdinalIgnoreCase))
        {
            string relSubPath = trimmed["attachments/".Length..]
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(_attachmentsDirectory, relSubPath));
            string attachmentsRoot = Path.GetFullPath(_attachmentsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(attachmentsRoot, StringComparison.OrdinalIgnoreCase)
                ? candidate
                : string.Empty;
        }

        if (!Path.IsPathRooted(trimmed))
        {
            string candidate = Path.GetFullPath(Path.Combine(_widgetDirectory, trimmed));
            string widgetRoot = Path.GetFullPath(_widgetDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (candidate.StartsWith(widgetRoot, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        if (File.Exists(trimmed) || Directory.Exists(trimmed))
        {
            return trimmed;
        }

        // Cross-machine fallback for legacy absolute paths containing \widgets\<id>\attachments\<name>
        string normalized = trimmed.Replace('\\', '/');
        const string marker = "/attachments/";
        int markerIndex = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0 && markerIndex + marker.Length < normalized.Length)
        {
            string fileTail = normalized[(markerIndex + marker.Length)..];
            if (!fileTail.Contains("..", StringComparison.Ordinal))
            {
                string localFallback = Path.GetFullPath(
                    Path.Combine(_attachmentsDirectory, fileTail.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(localFallback))
                {
                    return localFallback;
                }
            }
        }

        return trimmed;
    }

    public bool IsInsideManagedAttachments(string? resolvedFullPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedFullPath))
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(resolvedFullPath);
            string root = Path.GetFullPath(_attachmentsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string FormatCollectionAsMarkdown(
        string widgetTitle,
        string collectionName,
        IReadOnlyList<CustomWidgetRecord> records)
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(string.IsNullOrWhiteSpace(widgetTitle) ? collectionName : widgetTitle);
        sb.AppendLine();

        if (records.Count == 0)
        {
            sb.Append('_').Append(Localized.T("CustomWidget.Store.EmptyExportMarkdown")).AppendLine("_");
            return sb.ToString();
        }

        foreach (IGrouping<string, CustomWidgetRecord> dateGroup in records
                     .GroupBy(r => r.DateText)
                     .OrderByDescending(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("## ").AppendLine(dateGroup.Key);
            foreach (CustomWidgetRecord item in dateGroup)
            {
                string time = item.CreatedAtLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
                string tag = item.GetField("tag");
                string text = item.GetField("text");
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = item.GetField("title");
                }
                string fileName = item.GetField("fileName");
                string filePath = item.GetField("filePath");

                sb.Append("- `").Append(time).Append('`');
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    sb.Append(" **[").Append(tag).Append("]**");
                }
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append(' ').Append(text);
                }
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    if (!string.IsNullOrWhiteSpace(filePath))
                    {
                        sb.Append(" — 📎 [").Append(fileName).Append("](").Append(filePath).Append(')');
                    }
                    else
                    {
                        sb.Append(" — 📎 ").Append(fileName);
                    }
                }
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    internal static void DeleteWidgetStorage(string widgetId) =>
        DeleteWidgetDirectory(widgetId);

    internal static void DeleteWidgetDirectory(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return;
        }

        WidgetStores.TryRemove(widgetId, out _);
        string dir = Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            SubdirectoryName,
            GetSafeWidgetFileName(widgetId));
        if (Directory.Exists(dir))
        {
            try
            {
                CustomWidgetStorageFileOps.DeleteDirectoryRecursive(dir);
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidgetStore] Failed to delete widget directory '{dir}': {ex.Message}");
            }
        }
    }

    private static string GetSafeWidgetFileName(string widgetId)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(widgetId.Length);
        foreach (char ch in widgetId)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }
        return builder.Length == 0 ? "default" : builder.ToString();
    }

    private static CustomWidgetStorePayload Normalize(CustomWidgetStorePayload? payload)
    {
        if (payload is null)
        {
            return new CustomWidgetStorePayload();
        }

        var normalized = new CustomWidgetStorePayload
        {
            SchemaVersion = Math.Max(1, payload.SchemaVersion),
            Settings = new Dictionary<string, string>(
                payload.Settings ?? [],
                StringComparer.Ordinal),
            State = new Dictionary<string, string>(
                payload.State ?? [],
                StringComparer.Ordinal),
            Collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal)
        };

        if (payload.Collections is not null)
        {
            foreach ((string key, List<CustomWidgetRecord>? list) in payload.Collections)
            {
                if (string.IsNullOrWhiteSpace(key) || list is null)
                {
                    continue;
                }

                var clonedList = new List<CustomWidgetRecord>(list.Count);
                foreach (CustomWidgetRecord record in list)
                {
                    if (record is null) continue;
                    long createdTicks = record.CreatedAtUtcTicks > 0
                        ? record.CreatedAtUtcTicks
                        : DateTimeOffset.UtcNow.UtcTicks;
                    long updatedTicks = record.UpdatedAtUtcTicks > 0
                        ? record.UpdatedAtUtcTicks
                        : createdTicks;
                    clonedList.Add(new CustomWidgetRecord
                    {
                        Id = string.IsNullOrWhiteSpace(record.Id)
                            ? Guid.NewGuid().ToString("N")[..12]
                            : record.Id,
                        CreatedAtUtcTicks = createdTicks,
                        UpdatedAtUtcTicks = updatedTicks,
                        IsDeleted = record.IsDeleted,
                        Fields = new Dictionary<string, string>(
                            record.Fields ?? [],
                            StringComparer.Ordinal)
                    });
                }
                normalized.Collections[key] = clonedList;
            }
        }

        return normalized;
    }

    private static CustomWidgetStorePayload Clone(CustomWidgetStorePayload source) =>
        Normalize(source);

    private static readonly object WatcherRegistryGate = new();
    private static readonly Dictionary<string, SharedWidgetsDirectoryWatcher> SharedWatchers =
        new(StringComparer.OrdinalIgnoreCase);

    internal IDisposable RegisterManifestWatcher(Action<FileSystemEventArgs> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Directory.CreateDirectory(_widgetDirectory);
        string parentDir = Path.GetFullPath(
            Path.GetDirectoryName(_widgetDirectory) ?? _widgetDirectory);
        string folderName = Path.GetFileName(
            _widgetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        lock (WatcherRegistryGate)
        {
            if (!SharedWatchers.TryGetValue(parentDir, out SharedWidgetsDirectoryWatcher? shared))
            {
                shared = new SharedWidgetsDirectoryWatcher(parentDir);
                SharedWatchers[parentDir] = shared;
            }

            return shared.Subscribe(folderName, callback, () =>
            {
                lock (WatcherRegistryGate)
                {
                    if (shared.IsEmpty &&
                        SharedWatchers.TryGetValue(parentDir, out SharedWidgetsDirectoryWatcher? current) &&
                        ReferenceEquals(current, shared))
                    {
                        SharedWatchers.Remove(parentDir);
                        shared.Dispose();
                    }
                }
            });
        }
    }

    private sealed class SharedWidgetsDirectoryWatcher : IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, List<Action<FileSystemEventArgs>>> _listenersByFolder =
            new(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher? _watcher;

        public SharedWidgetsDirectoryWatcher(string parentDirectory)
        {
            try
            {
                Directory.CreateDirectory(parentDirectory);
                _watcher = new FileSystemWatcher(parentDirectory, "*.json")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += OnFileEvent;
                _watcher.Created += OnFileEvent;
                _watcher.Renamed += OnFileEvent;
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidget] Failed to start shared manifest watcher for '{parentDirectory}': {ex.Message}");
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_gate)
                {
                    return _listenersByFolder.Count == 0;
                }
            }
        }

        public IDisposable Subscribe(
            string folderName,
            Action<FileSystemEventArgs> callback,
            Action onPossiblyEmpty)
        {
            lock (_gate)
            {
                if (!_listenersByFolder.TryGetValue(folderName, out List<Action<FileSystemEventArgs>>? list))
                {
                    list = [];
                    _listenersByFolder[folderName] = list;
                }

                list.Add(callback);
            }

            return new Subscription(this, folderName, callback, onPossiblyEmpty);
        }

        private void Unsubscribe(
            string folderName,
            Action<FileSystemEventArgs> callback,
            Action onPossiblyEmpty)
        {
            lock (_gate)
            {
                if (_listenersByFolder.TryGetValue(folderName, out List<Action<FileSystemEventArgs>>? list))
                {
                    list.Remove(callback);
                    if (list.Count == 0)
                    {
                        _listenersByFolder.Remove(folderName);
                    }
                }
            }

            onPossiblyEmpty();
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            string? dir = Path.GetDirectoryName(e.FullPath);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            string folderName = Path.GetFileName(dir);
            Action<FileSystemEventArgs>[]? targets = null;
            lock (_gate)
            {
                if (_listenersByFolder.TryGetValue(folderName, out List<Action<FileSystemEventArgs>>? list) &&
                    list.Count > 0)
                {
                    targets = list.ToArray();
                }
            }

            if (targets is null)
            {
                return;
            }

            foreach (Action<FileSystemEventArgs> target in targets)
            {
                try
                {
                    target(e);
                }
                catch (Exception ex)
                {
                    App.Log($"[CustomWidget] Shared manifest watcher callback failed: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            FileSystemWatcher? watcher;
            lock (_gate)
            {
                watcher = _watcher;
                _watcher = null;
                _listenersByFolder.Clear();
            }

            if (watcher is not null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnFileEvent;
                watcher.Created -= OnFileEvent;
                watcher.Renamed -= OnFileEvent;
                watcher.Dispose();
            }
        }

        private sealed class Subscription(
            SharedWidgetsDirectoryWatcher owner,
            string folderName,
            Action<FileSystemEventArgs> callback,
            Action onPossiblyEmpty) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.Unsubscribe(folderName, callback, onPossiblyEmpty);
                }
            }
        }
    }
}
