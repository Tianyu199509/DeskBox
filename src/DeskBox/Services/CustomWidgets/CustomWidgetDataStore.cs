using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed class CustomWidgetRecord
{
    private readonly Dictionary<string, JsonElement> _fieldsJson = new(StringComparer.Ordinal);
    private readonly CustomWidgetStructuredStateView _fieldsView;
    private string? _id;

    public CustomWidgetRecord()
    {
        _fieldsView = new CustomWidgetStructuredStateView(_fieldsJson);
    }

    public string Id
    {
        get => _id ??= Guid.NewGuid().ToString("N")[..12];
        set => _id = value;
    }

    public long CreatedAtUtcTicks { get; set; } = DateTimeOffset.UtcNow.UtcTicks;
    public long UpdatedAtUtcTicks { get; set; }
    public bool IsDeleted { get; set; }

    internal static CustomWidgetRecord CreateProjected(
        string id,
        params ReadOnlySpan<(string Key, string? Value)> fields)
    {
        var record = new CustomWidgetRecord
        {
            _id = id
        };
        record._fieldsJson.EnsureCapacity(fields.Length);
        foreach ((string key, string? value) in fields)
        {
            record._fieldsJson[key] = CustomWidgetScriptEngine.CreateStringJsonElement(value);
        }

        return record;
    }

    [JsonIgnore]
    public Dictionary<string, JsonElement> FieldsJson
    {
        get => _fieldsJson;
        init
        {
            _fieldsJson.Clear();
            if (value is null)
            {
                return;
            }

            foreach ((string k, JsonElement el) in value)
            {
                if (el.ValueKind != JsonValueKind.Undefined)
                {
                    _fieldsJson[k] = el.Clone();
                }
            }
        }
    }

    public CustomWidgetStructuredStateView Fields
    {
        get => _fieldsView;
        set => _fieldsView.ReplaceAllFrom(value);
    }

    public CustomWidgetRecord Clone()
    {
        var copy = new CustomWidgetRecord
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N")[..12] : Id,
            CreatedAtUtcTicks = CreatedAtUtcTicks > 0 ? CreatedAtUtcTicks : DateTimeOffset.UtcNow.UtcTicks,
            UpdatedAtUtcTicks = UpdatedAtUtcTicks > 0 ? UpdatedAtUtcTicks : (CreatedAtUtcTicks > 0 ? CreatedAtUtcTicks : DateTimeOffset.UtcNow.UtcTicks),
            IsDeleted = IsDeleted
        };
        foreach ((string k, JsonElement el) in _fieldsJson)
        {
            if (el.ValueKind != JsonValueKind.Undefined)
            {
                copy._fieldsJson[k] = el.Clone();
            }
        }
        return copy;
    }

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
            _ => _fieldsJson.TryGetValue(key, out JsonElement direct)
                ? CustomWidgetScriptEngine.JsonElementToDisplayString(direct)
                : (_fieldsJson.Count > 0 && (key.Contains('.') || key.Contains('['))
                    ? CustomWidgetScriptEngine.ResolveComputedToken(key, _fieldsJson)
                    : string.Empty)
        };
    }
}

[JsonConverter(typeof(CustomWidgetStorePayloadJsonConverter))]
public sealed class CustomWidgetStorePayload
{
    private readonly Dictionary<string, JsonElement> _stateJson = new(StringComparer.Ordinal);
    private readonly CustomWidgetStructuredStateView _stateView;

    public CustomWidgetStorePayload()
    {
        _stateView = new CustomWidgetStructuredStateView(_stateJson);
    }

    public int SchemaVersion { get; set; } = 1;
    [JsonIgnore]
    public bool LoadedFromDisk { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);

    public CustomWidgetStructuredStateView State
    {
        get => _stateView;
        set => _stateView.ReplaceAllFrom(value);
    }

    [JsonIgnore]
    public Dictionary<string, JsonElement> StateJson
    {
        get => _stateJson;
        init
        {
            _stateJson.Clear();
            if (value is null)
            {
                return;
            }

            foreach ((string k, JsonElement el) in value)
            {
                if (el.ValueKind != JsonValueKind.Undefined)
                {
                    _stateJson[k] = el.Clone();
                }
            }
        }
    }

    public Dictionary<string, List<CustomWidgetRecord>> Collections { get; set; } =
        new(StringComparer.Ordinal);
}

public sealed class CustomWidgetStorePayloadJsonConverter : JsonConverter<CustomWidgetStorePayload>
{
    public override CustomWidgetStorePayload? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        return CustomWidgetDataStore.ParseStorePayloadElement(doc.RootElement);
    }

    public override void Write(
        Utf8JsonWriter writer,
        CustomWidgetStorePayload value,
        JsonSerializerOptions options)
    {
        CustomWidgetDataStore.WriteStorePayloadJson(writer, value);
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(CustomWidgetStorePayload))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(double))]
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
    public const string ManifestFileName = "manifest.json";
    public const string LogicScriptFileName = "logic.js";
    public const string LogicJsFileName = LogicScriptFileName;
    public const string SidecarLogicFileName = LogicScriptFileName;
    public const string LocalesDirectoryName = CustomWidgetLocaleCatalog.DirectoryName;
    public const int MaxLogicJsBytes = 128 * 1024;
    public const int MaxSingleLocaleBytes = CustomWidgetLocaleCatalog.MaxSingleLocaleBytes;
    public const int MaxLocaleCatalogBytes = CustomWidgetLocaleCatalog.MaxTotalCatalogBytes;

    private static readonly ConcurrentDictionary<string, CustomWidgetDataStore> WidgetStores =
        new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _widgetId;
    private readonly bool _useSharedSaveRelay;
    private readonly string _widgetDirectory;
    private readonly string _manifestPath;
    private readonly string _logicJsPath;
    private readonly string _localesDirectory;
    private readonly string _storePath;
    private readonly string _attachmentsDirectory;
    private CustomWidgetStorePayload? _cached;

    /// <summary>
    /// Companion-process single-writer relay (Settings / Studio hosts): when set,
    /// shared desktop widget <c>store.json</c> saves and resets forward to the
    /// authoritative main process over IPC instead of racing direct file writes.
    /// Isolated stores with an explicit <c>rootDataDirectory</c> always write locally.
    /// </summary>
    public static Func<string, string, bool, Task>? SaveRelay { get; set; }

    internal Func<string, string, bool, Task>? SaveRelayOverride { get; init; }

    private Func<string, string, bool, Task>? EffectiveSaveRelay =>
        SaveRelayOverride ?? (_useSharedSaveRelay ? SaveRelay : null);

    public CustomWidgetDataStore(string widgetId, string? rootDataDirectory = null, bool createDirectory = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        _widgetId = widgetId;
        _useSharedSaveRelay = string.IsNullOrWhiteSpace(rootDataDirectory);
        string baseDir = _useSharedSaveRelay
            ? DeskBoxDataPathService.Current.DataDirectory
            : rootDataDirectory!;
        string rootCustomWidgetsDir = Path.GetFullPath(Path.Combine(baseDir, SubdirectoryName));
        string candidateDir = Path.GetFullPath(Path.Combine(rootCustomWidgetsDir, GetSafeWidgetFileName(widgetId)));
        if (!IsUnderRootDirectory(candidateDir, rootCustomWidgetsDir))
        {
            throw new InvalidOperationException(
                $"Resolved widget directory '{candidateDir}' escapes root '{rootCustomWidgetsDir}'.");
        }

        _widgetDirectory = candidateDir;
        _manifestPath = Path.Combine(_widgetDirectory, "manifest.json");
        _logicJsPath = Path.Combine(_widgetDirectory, "logic.js");
        _localesDirectory = Path.Combine(_widgetDirectory, LocalesDirectoryName);
        _storePath = Path.Combine(_widgetDirectory, "store.json");
        _attachmentsDirectory = Path.Combine(_widgetDirectory, "attachments");
        if (createDirectory)
        {
            Directory.CreateDirectory(_widgetDirectory);
        }
    }

    public string WidgetId => _widgetId;
    public string WidgetDirectory => _widgetDirectory;
    public string ManifestPath => _manifestPath;
    public string ManifestFilePath => _manifestPath;
    public string LogicJsPath => _logicJsPath;
    public string LogicScriptPath => _logicJsPath;
    public string LocalesDirectoryPath => _localesDirectory;
    public string StorePath => _storePath;
    public string StoreFilePath => _storePath;
    public string AttachmentsDirectory => _attachmentsDirectory;

    internal event Action<object?, bool>? StoreMutated;
    public static event Action<string, string>? StoreChanged;

    public static bool HasExistingWidgetDirectory(string widgetId, string? rootDataDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return false;
        }

        string baseDir = string.IsNullOrWhiteSpace(rootDataDirectory)
            ? DeskBoxDataPathService.Current.DataDirectory
            : rootDataDirectory;
        string rootCustomWidgetsDir = Path.GetFullPath(Path.Combine(baseDir, SubdirectoryName));
        string dir = Path.GetFullPath(Path.Combine(rootCustomWidgetsDir, GetSafeWidgetFileName(widgetId)));
        return IsUnderRootDirectory(dir, rootCustomWidgetsDir) && Directory.Exists(dir);
    }

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

        if (store.StoreMutated is null)
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

    public async Task<string?> TryReadExistingManifestJsonAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return File.Exists(_manifestPath)
                ? await File.ReadAllTextAsync(_manifestPath)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ReadOrCreateManifestJsonAsync(
        string defaultTemplateId = CustomWidgetTemplates.SystemPulseTemplateId,
        CustomWidgetRemoteCatalogService? catalogOverride = null,
        bool createIfMissing = true)
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
                        if (TryResolveNewerBuiltInBundle(existing, out CustomWidgetTemplateBundle? newerBuiltIn) &&
                            newerBuiltIn is not null)
                        {
                            string stampedBuiltIn = CustomWidgetManifestMetadataStamper.StampProvenance(
                                newerBuiltIn.ManifestJson,
                                origin: CustomWidgetOriginKinds.Official,
                                isCustomized: false);
                            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_manifestPath, stampedBuiltIn);
                            CustomWidgetLocaleCatalog.WriteOrDeleteDirectoryAtomically(newerBuiltIn.Locales, _localesDirectory);
                            WriteOrDeleteSidecarNoLock(_logicJsPath, newerBuiltIn.LogicJs, MaxLogicJsBytes, "logic.js");
                            CustomWidgetTrustStore.Shared.RecordExecutableSurfaceAfterWrite(
                                WidgetId,
                                stampedBuiltIn,
                                newerBuiltIn.LogicJs,
                                trustedLocalAuthoring: false,
                                preserveTrustedAuthoringOnExternalWrite: true);
                            existing = stampedBuiltIn;
                        }

                        CustomWidgetRemoteCatalogService effectiveCatalog =
                            catalogOverride ?? CustomWidgetRemoteCatalogService.Shared;
                        if (effectiveCatalog.TryResolveNewerOfficialBundle(
                                existing,
                                out CustomWidgetTemplateBundle? remoteOtaBundle) &&
                            remoteOtaBundle is not null &&
                            !string.IsNullOrWhiteSpace(remoteOtaBundle.ManifestJson))
                        {
                            string localizedOtaJson = CustomWidgetTemplates.ApplyCultureSpecificOfficialTemplateDefaults(
                                remoteOtaBundle.Id,
                                remoteOtaBundle.ManifestJson);
                            string stampedRemote = CustomWidgetManifestMetadataStamper.StampProvenance(
                                localizedOtaJson,
                                origin: CustomWidgetOriginKinds.Official,
                                isCustomized: false);
                            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_manifestPath, stampedRemote);
                            CustomWidgetLocaleCatalog.WriteOrDeleteDirectoryAtomically(remoteOtaBundle.Locales, _localesDirectory);
                            WriteOrDeleteSidecarNoLock(_logicJsPath, remoteOtaBundle.LogicJs, MaxLogicJsBytes, "logic.js");

                            // Signed Official OTA preserves official baseline trust while revoking
                            // any explicit privileged overrides (e.g. CommandExecution) if surface changed.
                            CustomWidgetTrustStore.Shared.RecordExecutableSurfaceAfterWrite(
                                WidgetId,
                                stampedRemote,
                                remoteOtaBundle.LogicJs,
                                trustedLocalAuthoring: false,
                                preserveTrustedAuthoringOnExternalWrite: true);

                            return stampedRemote;
                        }
                    }

                    return existing;
                }
            }

            if (!createIfMissing)
            {
                return string.Empty;
            }

            CustomWidgetTemplateBundle bundle =
                catalogOverride is not null &&
                catalogOverride.TryGetRemoteTemplateBundle(defaultTemplateId, out CustomWidgetTemplateBundle? overrideBundle) &&
                overrideBundle is not null
                    ? overrideBundle with
                    {
                        ManifestJson = CustomWidgetTemplates.ApplyCultureSpecificOfficialTemplateDefaults(
                            defaultTemplateId,
                            overrideBundle.ManifestJson)
                    }
                    : CustomWidgetTemplates.ResolveEffectiveTemplateBundle(defaultTemplateId);
            string templateJson = CustomWidgetManifestMetadataStamper.StampProvenance(
                bundle.ManifestJson,
                origin: CustomWidgetOriginKinds.Official,
                isCustomized: false);
            Directory.CreateDirectory(_widgetDirectory);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_manifestPath, templateJson);
            if (bundle.Locales is { IsEmpty: false } && !Directory.Exists(_localesDirectory))
            {
                bundle.Locales.WriteToDirectoryAtomically(_localesDirectory);
            }
            if (!string.IsNullOrWhiteSpace(bundle.LogicJs) && !File.Exists(_logicJsPath))
            {
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_logicJsPath, bundle.LogicJs);
            }
            return templateJson;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryResolveNewerBuiltInBundle(
        string existingManifestJson,
        out CustomWidgetTemplateBundle? bundle)
    {
        bundle = null;
        try
        {
            using JsonDocument existingDoc = JsonDocument.Parse(
                existingManifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (existingDoc.RootElement.ValueKind != JsonValueKind.Object ||
                !existingDoc.RootElement.TryGetProperty("id", out JsonElement idEl) ||
                idEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string rawId = idEl.GetString() ?? string.Empty;
            if (!rawId.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? normId = CustomWidgetTemplates.TryNormalizeKnownTemplateId(rawId);
            if (normId is null)
            {
                return false;
            }

            string existingVerText = existingDoc.RootElement.TryGetProperty("version", out JsonElement verEl) &&
                                     verEl.ValueKind == JsonValueKind.String
                ? verEl.GetString() ?? "0.0.0"
                : "0.0.0";

            CustomWidgetTemplateBundle candidate = CustomWidgetTemplates.GetTemplateBundle(normId);
            using JsonDocument builtInDoc = JsonDocument.Parse(candidate.ManifestJson);
            string builtInVerText = builtInDoc.RootElement.TryGetProperty("version", out JsonElement bVerEl) &&
                                    bVerEl.ValueKind == JsonValueKind.String
                ? bVerEl.GetString() ?? "0.0.0"
                : "0.0.0";

            if (Version.TryParse(NormalizeSemVer(existingVerText), out Version? existingVer) &&
                Version.TryParse(NormalizeSemVer(builtInVerText), out Version? builtInVer) &&
                builtInVer > existingVer)
            {
                bundle = candidate;
                return true;
            }
        }
        catch
        {
            // Ignore malformed existing manifest; caller keeps existing text untouched.
        }

        return false;

        static string NormalizeSemVer(string v)
        {
            string s = v.Trim().TrimStart('v', 'V');
            int dash = s.IndexOf('-');
            if (dash > 0)
            {
                s = s[..dash];
            }
            return s.Contains('.') ? s : s + ".0";
        }
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
        StoreChanged?.Invoke(_manifestPath, _widgetId);
    }

    public async Task<string?> ReadLogicJsOrNullAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return ReadBoundedTextFileOrNullNoLock(_logicJsPath, MaxLogicJsBytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ReadLogicJsOrNull()
    {
        _gate.Wait();
        try
        {
            return ReadBoundedTextFileOrNullNoLock(_logicJsPath, MaxLogicJsBytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CustomWidgetLocaleCatalog> ReadLocaleCatalogAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return CustomWidgetLocaleCatalog.LoadFromDirectory(_localesDirectory);
        }
        finally
        {
            _gate.Release();
        }
    }

    public CustomWidgetLocaleCatalog ReadLocaleCatalog()
    {
        _gate.Wait();
        try
        {
            return CustomWidgetLocaleCatalog.LoadFromDirectory(_localesDirectory);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CustomWidgetLocaleCatalog> ReadLazyRuntimeLocalesAsync(string? activeCultureName = null)
    {
        await _gate.WaitAsync();
        try
        {
            return CustomWidgetLocaleCatalog.LoadLazyForRuntimeCulture(_localesDirectory, activeCultureName);
        }
        finally
        {
            _gate.Release();
        }
    }

    public CustomWidgetLocaleCatalog ReadLazyRuntimeLocales(string? activeCultureName = null)
    {
        _gate.Wait();
        try
        {
            return CustomWidgetLocaleCatalog.LoadLazyForRuntimeCulture(_localesDirectory, activeCultureName);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteWorkspaceBundleAsync(
        string manifestJson,
        string? logicJs = null,
        bool updateLogicJs = true,
        CustomWidgetLocaleCatalog? locales = null,
        bool updateLocales = true,
        object? origin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_widgetDirectory);
            if (updateLogicJs)
            {
                WriteOrDeleteSidecarNoLock(_logicJsPath, logicJs, MaxLogicJsBytes, "logic.js");
            }

            if (updateLocales)
            {
                if (locales is null || locales.IsEmpty)
                {
                    if (Directory.Exists(_localesDirectory))
                    {
                        CustomWidgetStorageFileOps.DeleteDirectoryRecursive(_localesDirectory);
                    }
                }
                else
                {
                    locales.WriteToDirectoryAtomically(_localesDirectory);
                }
            }

            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_manifestPath, manifestJson);
        }
        finally
        {
            _gate.Release();
        }

        StoreMutated?.Invoke(origin, true);
        StoreChanged?.Invoke(_manifestPath, _widgetId);
    }

    private static string? ReadBoundedTextFileOrNullNoLock(string filePath, int maxBytes)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            long length = stream.Length;
            if (length <= 0 || length > maxBytes)
            {
                return null;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string content = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }

    private void WriteOrDeleteSidecarNoLock(
        string filePath,
        string? content,
        int maxBytes,
        string fileLabel)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            CustomWidgetStorageFileOps.DeleteFileIfExists(filePath);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(content);
        if (byteCount > maxBytes)
        {
            throw new InvalidOperationException(
                $"Custom widget '{fileLabel}' exceeds maximum allowed size ({maxBytes / 1024} KB).");
        }

        Directory.CreateDirectory(_widgetDirectory);
        CustomWidgetStorageFileOps.ReplaceTextFileAtomically(filePath, content);
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
        if (_cached is not null && EffectiveSaveRelay is not null)
        {
            string currentDiskHash = ReadCurrentStoreContentHash();
            if (!string.Equals(
                    currentDiskHash,
                    Volatile.Read(ref _lastSavedStoreContentHash),
                    StringComparison.Ordinal))
            {
                _cached = null;
            }
        }

        if (_cached is null)
        {
            string loadedHash = string.Empty;
            _cached = await ResilientJsonStore.LoadUtf8Async(
                _storePath,
                utf8Json =>
                {
                    loadedHash = ComputeUtf8ContentHash(utf8Json.Span);
                    CustomWidgetStorePayload normalized = Normalize(JsonSerializer.Deserialize(
                        utf8Json.Span,
                        CustomWidgetStoreJsonContext.Default.CustomWidgetStorePayload));
                    normalized.LoadedFromDisk = true;
                    return normalized;
                },
                static () => new CustomWidgetStorePayload(),
                nameof(CustomWidgetDataStore));
            Volatile.Write(ref _lastSavedStoreContentHash, loadedHash);
        }

        return _cached;
    }

    public async Task<CustomWidgetStorePayload> ReloadFromDiskAsync()
    {
        await _gate.WaitAsync();
        try
        {
            string loadedHash = string.Empty;
            _cached = await ResilientJsonStore.LoadUtf8Async(
                _storePath,
                utf8Json =>
                {
                    loadedHash = ComputeUtf8ContentHash(utf8Json.Span);
                    CustomWidgetStorePayload normalized = Normalize(JsonSerializer.Deserialize(
                        utf8Json.Span,
                        CustomWidgetStoreJsonContext.Default.CustomWidgetStorePayload));
                    normalized.LoadedFromDisk = true;
                    return normalized;
                },
                static () => new CustomWidgetStorePayload(),
                nameof(CustomWidgetDataStore));
            Volatile.Write(ref _lastSavedStoreContentHash, loadedHash);
            return Clone(_cached);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string _lastSavedStoreContentHash = string.Empty;

    public string LastSavedStoreContentHash => Volatile.Read(ref _lastSavedStoreContentHash);

    public string ReadCurrentStoreContentHash()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return string.Empty;
            }

            using var stream = new FileStream(
                _storePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            long length = stream.Length;
            if (length < 0 || length > 32 * 1024 * 1024)
            {
                return string.Empty;
            }

            Span<byte> bomProbe = stackalloc byte[3];
            int probeRead = stream.Read(bomProbe);
            if (!(probeRead == 3 && bomProbe[0] == 0xEF && bomProbe[1] == 0xBB && bomProbe[2] == 0xBF))
            {
                stream.Position = 0;
            }

            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(stream, digest);
            return Convert.ToHexStringLower(digest);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ComputeUtf8ContentHash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return Convert.ToHexStringLower(digest);
    }

    public bool IsInsideWidgetDirectory(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            string fullCandidate = Path.GetFullPath(candidatePath);
            string widgetRoot = Path.GetFullPath(_widgetDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullCandidate.StartsWith(widgetRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task SaveAsync(CustomWidgetStorePayload payload, object? origin = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        CustomWidgetStorePayload normalized = Normalize(payload);
        if (EffectiveSaveRelay is { } relay && _widgetId.Length > 0)
        {
            await RelaySaveOrClearAsync(relay, normalized, clearAttachments: false, origin);
            return;
        }

        await _gate.WaitAsync();
        try
        {
            byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(
                normalized,
                CustomWidgetStoreJsonContext.Default.CustomWidgetStorePayload);
            string nextHash = ComputeUtf8ContentHash(utf8Bytes);
            string previousHash = Volatile.Read(ref _lastSavedStoreContentHash);
            Volatile.Write(ref _lastSavedStoreContentHash, nextHash);
            try
            {
                await ResilientJsonStore.SaveAsync(_storePath, utf8Bytes);
            }
            catch
            {
                Volatile.Write(ref _lastSavedStoreContentHash, previousHash);
                throw;
            }

            normalized.LoadedFromDisk = true;
            _cached = Clone(normalized);
        }
        finally
        {
            _gate.Release();
        }

        StoreMutated?.Invoke(origin, false);
        StoreChanged?.Invoke(_storePath, _widgetId);
    }

    public async Task ClearAllDataAsync(object? origin = null)
    {
        if (EffectiveSaveRelay is { } relay && _widgetId.Length > 0)
        {
            CustomWidgetStorePayload fresh;
            await _gate.WaitAsync();
            try
            {
                _ = await EnsureCachedNoLockAsync();
                fresh = new CustomWidgetStorePayload();
                if (_cached is not null)
                {
                    foreach ((string k, string v) in _cached.Settings)
                    {
                        fresh.Settings[k] = v;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            await RelaySaveOrClearAsync(relay, fresh, clearAttachments: true, origin);
            return;
        }

        await _gate.WaitAsync();
        try
        {
            _ = await EnsureCachedNoLockAsync();
            var fresh = new CustomWidgetStorePayload();
            if (_cached is not null)
            {
                foreach ((string k, string v) in _cached.Settings)
                {
                    fresh.Settings[k] = v;
                }
            }

            byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(
                fresh,
                CustomWidgetStoreJsonContext.Default.CustomWidgetStorePayload);
            string nextHash = ComputeUtf8ContentHash(utf8Bytes);
            string previousHash = Volatile.Read(ref _lastSavedStoreContentHash);
            Volatile.Write(ref _lastSavedStoreContentHash, nextHash);
            try
            {
                await ResilientJsonStore.SaveAsync(_storePath, utf8Bytes);
            }
            catch
            {
                Volatile.Write(ref _lastSavedStoreContentHash, previousHash);
                throw;
            }

            _cached = Clone(fresh);

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

        StoreMutated?.Invoke(origin, false);
        StoreChanged?.Invoke(_storePath, _widgetId);
    }

    private async Task RelaySaveOrClearAsync(
        Func<string, string, bool, Task> relay,
        CustomWidgetStorePayload normalized,
        bool clearAttachments,
        object? origin)
    {
        CustomWidgetStorePayload? previousCache;
        string previousHash;
        CustomWidgetStorePayload optimisticCache = Clone(normalized);
        var bufferWriter = new ArrayBufferWriter<byte>(512);
        using (var jsonWriter = new Utf8JsonWriter(bufferWriter, new JsonWriterOptions { Indented = true }))
        {
            WriteStorePayloadJson(jsonWriter, normalized);
        }

        ReadOnlySpan<byte> utf8Span = bufferWriter.WrittenSpan;
        string nextHash = ComputeUtf8ContentHash(utf8Span);
        string jsonPayload = Encoding.UTF8.GetString(utf8Span);

        await _gate.WaitAsync();
        try
        {
            previousCache = _cached;
            previousHash = Volatile.Read(ref _lastSavedStoreContentHash);
            _cached = optimisticCache;
            Volatile.Write(ref _lastSavedStoreContentHash, nextHash);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await relay(_widgetId, jsonPayload, clearAttachments);
        }
        catch
        {
            await _gate.WaitAsync();
            try
            {
                if (ReferenceEquals(_cached, optimisticCache))
                {
                    _cached = previousCache;
                    Volatile.Write(ref _lastSavedStoreContentHash, previousHash);
                }
            }
            finally
            {
                _gate.Release();
            }

            throw;
        }

        await _gate.WaitAsync();
        try
        {
            if (ReferenceEquals(_cached, optimisticCache))
            {
                string diskHash = ReadCurrentStoreContentHash();
                if (!string.IsNullOrEmpty(diskHash))
                {
                    Volatile.Write(ref _lastSavedStoreContentHash, diskHash);
                }
                else
                {
                    // In-memory mock relay did not write disk; align hash with empty disk.
                    Volatile.Write(ref _lastSavedStoreContentHash, string.Empty);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        StoreMutated?.Invoke(origin, false);
        StoreChanged?.Invoke(_storePath, _widgetId);
    }

    internal static string ComposeRelayPayload(string widgetId, string storePayloadJson, bool clearAttachments) =>
        $"{widgetId}|{(clearAttachments ? '1' : '0')}\n{storePayloadJson}";

    internal static async Task<(bool Ok, string? Payload, string? Error)> ApplyRelayedStorePayloadAsync(string? wirePayload)
    {
        if (string.IsNullOrEmpty(wirePayload))
        {
            return (false, null, "empty payload");
        }

        int newline = wirePayload.IndexOf('\n');
        if (newline <= 0 || newline == wirePayload.Length - 1)
        {
            return (false, null, "malformed payload");
        }

        ReadOnlySpan<char> header = wirePayload.AsSpan(0, newline);
        int pipe = header.LastIndexOf('|');
        if (pipe <= 0)
        {
            return (false, null, "malformed header");
        }

        string widgetId = header[..pipe].ToString();
        bool clearAttachments = header[(pipe + 1)..] is "1";

        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return (false, null, "malformed widgetId");
        }

        CustomWidgetStorePayload? parsed;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(wirePayload[(newline + 1)..]);
            parsed = ParseStorePayloadElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return (false, null, "malformed store json");
        }

        if (parsed is null)
        {
            return (false, null, "null store payload");
        }

        CustomWidgetDataStore store = ForWidget(widgetId);
        if (clearAttachments)
        {
            await store.ClearAllDataAsync();
        }

        await store.SaveAsync(parsed);
        return (true, null, null);
    }

    public async Task<(CustomWidgetManifest? Manifest, CustomWidgetStorePayload Payload, DateTimeOffset ModifiedAt)> LoadReadOnlySearchSnapshotAsync(
        string? activeCultureName = null)
    {
        await _gate.WaitAsync();
        try
        {
            CustomWidgetManifest? manifest = null;
            DateTime latestUtc = DateTime.MinValue;
            if (File.Exists(_manifestPath))
            {
                var mfInfo = new FileInfo(_manifestPath);
                if (mfInfo.LastWriteTimeUtc > latestUtc)
                {
                    latestUtc = mfInfo.LastWriteTimeUtc;
                }

                string rawJson = await File.ReadAllTextAsync(_manifestPath).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(rawJson))
                {
                    CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(rawJson);
                    manifest = parsed.Manifest;
                    if (manifest is not null && Directory.Exists(_localesDirectory))
                    {
                        CustomWidgetLocaleCatalog locales =
                            CustomWidgetLocaleCatalog.LoadLazyForRuntimeCulture(_localesDirectory, activeCultureName);
                        if (locales.HasAnyLocale)
                        {
                            manifest = manifest.WithLocales(locales);
                        }
                    }
                }
            }

            CustomWidgetStorePayload payload;
            if (File.Exists(_storePath))
            {
                var stInfo = new FileInfo(_storePath);
                if (stInfo.LastWriteTimeUtc > latestUtc)
                {
                    latestUtc = stInfo.LastWriteTimeUtc;
                }

                payload = Clone(await EnsureCachedNoLockAsync().ConfigureAwait(false));
            }
            else
            {
                payload = new CustomWidgetStorePayload();
            }

            DateTimeOffset modifiedAt = latestUtc > DateTime.MinValue
                ? new DateTimeOffset(DateTime.SpecifyKind(latestUtc, DateTimeKind.Utc))
                : DateTimeOffset.UtcNow;
            return (manifest, payload, modifiedAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(string StoredPath, string FullPath, string FileName, string Mode)> ResolveDroppedFileWithPortablePathAsync(
        string sourcePath,
        string requestedMode)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        string fileName = Path.GetFileName(
            fullSourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        bool isRestrictedReparse =
            DeskBox.Platform.StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(fullSourcePath) ||
            DeskBox.Platform.StorageMigrationNativeMethods.IsAppExecutionAliasReparsePoint(fullSourcePath);
        string mode = !isRestrictedReparse &&
                      (string.Equals(requestedMode, "copy", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(requestedMode, "auto", StringComparison.OrdinalIgnoreCase))
            ? "copy"
            : "link";

        if (mode == "copy" && File.Exists(fullSourcePath))
        {
            Directory.CreateDirectory(_attachmentsDirectory);
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            string targetPath = Path.Combine(_attachmentsDirectory, fileName);
            int counter = 2;
            while (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                targetPath = Path.Combine(_attachmentsDirectory, $"{baseName} ({counter}){ext}");
                counter++;
            }

            await Task.Run(() => CustomWidgetStorageFileOps.CopyFileWithoutOverwrite(fullSourcePath, targetPath));
            string actualFileName = Path.GetFileName(targetPath);
            string portablePath = $"attachments/{actualFileName}";
            return (portablePath, targetPath, actualFileName, "copy");
        }

        if (mode == "copy" && Directory.Exists(fullSourcePath))
        {
            Directory.CreateDirectory(_attachmentsDirectory);
            string folderBase = string.IsNullOrWhiteSpace(fileName) ? "Folder" : fileName;
            string targetDir = Path.Combine(_attachmentsDirectory, folderBase);
            int counter = 2;
            while (Directory.Exists(targetDir) || File.Exists(targetDir))
            {
                targetDir = Path.Combine(_attachmentsDirectory, $"{folderBase} ({counter})");
                counter++;
            }

            await Task.Run(() => CopyDirectoryWithoutReparse(fullSourcePath, targetDir));
            string actualDirName = Path.GetFileName(targetDir);
            string portablePath = $"attachments/{actualDirName}";
            return (portablePath, targetDir, actualDirName, "copy");
        }

        return (fullSourcePath, fullSourcePath, fileName, "link");
    }

    private static void CopyDirectoryWithoutReparse(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string filePath in Directory.EnumerateFiles(sourceDir))
        {
            FileAttributes attrs = File.GetAttributes(filePath);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string targetFile = Path.Combine(destDir, Path.GetFileName(filePath));
            CustomWidgetStorageFileOps.CopyFileWithoutOverwrite(filePath, targetFile);
        }

        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            FileAttributes attrs = File.GetAttributes(subDir);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string targetSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectoryWithoutReparse(subDir, targetSubDir);
        }
    }

    /// <summary>
    /// Resolves either a portable widget-relative attachment path (<c>attachments/foo.pdf</c>)
    /// or an existing local filesystem path into a safe runtime filesystem path.
    /// </summary>
    public string ResolveRuntimePath(string? storedOrRawPath)
    {
        if (string.IsNullOrWhiteSpace(storedOrRawPath) ||
            CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri(storedOrRawPath))
        {
            return string.Empty;
        }

        string trimmed = Environment.ExpandEnvironmentVariables(storedOrRawPath.Trim());
        if (CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri(trimmed))
        {
            return string.Empty;
        }
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
        string rootCustomWidgetsDir = Path.GetFullPath(Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            SubdirectoryName));
        string dir = Path.GetFullPath(Path.Combine(
            rootCustomWidgetsDir,
            GetSafeWidgetFileName(widgetId)));
        if (!IsUnderRootDirectory(dir, rootCustomWidgetsDir))
        {
            return;
        }

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

    private static bool IsUnderRootDirectory(string candidateFullPath, string rootFullPath)
    {
        string prefix = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootFullPath
            : rootFullPath + Path.DirectorySeparatorChar;
        return candidateFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetSafeWidgetFileName(string widgetId)
    {
        const int MaxFileNameLength = 96;
        string raw = (widgetId ?? string.Empty).Trim();
        var builder = new StringBuilder(Math.Min(raw.Length, MaxFileNameLength + 8));
        foreach (char ch in raw)
        {
            builder.Append((char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-') ? ch : '_');
        }

        string sanitized = builder.ToString().Trim('.', ' ');
        if (sanitized.Length > MaxFileNameLength)
        {
            sanitized = sanitized[..MaxFileNameLength].Trim('.', ' ');
        }

        if (sanitized.Length == 0 ||
            sanitized is "." or ".." ||
            IsWindowsReservedDeviceFileName(sanitized))
        {
            return "invalid_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
        }

        return sanitized;
    }

    private static bool IsWindowsReservedDeviceFileName(string fileName)
    {
        int dotIndex = fileName.IndexOf('.');
        ReadOnlySpan<char> stem = (dotIndex >= 0 ? fileName.AsSpan(0, dotIndex) : fileName.AsSpan()).Trim();
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '0' and <= '9')
        {
            return true;
        }

        return false;
    }

    internal static CustomWidgetStorePayload ParseStorePayloadElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new CustomWidgetStorePayload();
        }

        var payload = new CustomWidgetStorePayload
        {
            SchemaVersion = root.TryGetProperty("schemaVersion", out JsonElement svEl) &&
                            svEl.ValueKind == JsonValueKind.Number &&
                            svEl.TryGetInt32(out int sv)
                ? Math.Max(1, sv)
                : 1
        };

        if (root.TryGetProperty("settings", out JsonElement settingsEl) &&
            settingsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in settingsEl.EnumerateObject())
            {
                payload.Settings[prop.Name] = CustomWidgetScriptEngine.JsonElementToDisplayString(prop.Value);
            }
        }

        if (root.TryGetProperty("state", out JsonElement stateEl) &&
            stateEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in stateEl.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                {
                    continue;
                }

                payload.StateJson[prop.Name] = prop.Value.Clone();
            }
        }

        if (root.TryGetProperty("collections", out JsonElement colsEl) &&
            colsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty colProp in colsEl.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(colProp.Name) || colProp.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var list = new List<CustomWidgetRecord>();
                foreach (JsonElement recEl in colProp.Value.EnumerateArray())
                {
                    if (recEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string id = recEl.TryGetProperty("id", out JsonElement idEl) &&
                                idEl.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(idEl.GetString())
                        ? idEl.GetString()!
                        : Guid.NewGuid().ToString("N")[..12];

                    long createdTicks = recEl.TryGetProperty("createdAtUtcTicks", out JsonElement cEl) &&
                                        cEl.ValueKind == JsonValueKind.Number &&
                                        cEl.TryGetInt64(out long ct) &&
                                        ct > 0
                        ? ct
                        : DateTimeOffset.UtcNow.UtcTicks;

                    long updatedTicks = recEl.TryGetProperty("updatedAtUtcTicks", out JsonElement uEl) &&
                                        uEl.ValueKind == JsonValueKind.Number &&
                                        uEl.TryGetInt64(out long ut) &&
                                        ut > 0
                        ? ut
                        : createdTicks;

                    bool isDeleted = recEl.TryGetProperty("isDeleted", out JsonElement dEl) &&
                                     dEl.ValueKind == JsonValueKind.True;

                    var fieldsJson = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    if (recEl.TryGetProperty("fields", out JsonElement fieldsEl) &&
                        fieldsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty fp in fieldsEl.EnumerateObject())
                        {
                            if (fp.Value.ValueKind != JsonValueKind.Undefined)
                            {
                                fieldsJson[fp.Name] = fp.Value.Clone();
                            }
                        }
                    }

                    list.Add(new CustomWidgetRecord
                    {
                        Id = id,
                        CreatedAtUtcTicks = createdTicks,
                        UpdatedAtUtcTicks = updatedTicks,
                        IsDeleted = isDeleted,
                        FieldsJson = fieldsJson
                    });
                }

                payload.Collections[colProp.Name] = list;
            }
        }

        return Normalize(payload);
    }

    internal static void WriteStorePayloadJson(Utf8JsonWriter writer, CustomWidgetStorePayload? payload)
    {
        if (payload is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", Math.Max(1, payload.SchemaVersion));

        writer.WritePropertyName("settings");
        writer.WriteStartObject();
        foreach ((string k, string v) in payload.Settings)
        {
            writer.WriteString(k, v ?? string.Empty);
        }
        writer.WriteEndObject();

        writer.WritePropertyName("state");
        writer.WriteStartObject();
        foreach ((string k, JsonElement el) in payload.StateJson)
        {
            if (el.ValueKind != JsonValueKind.Undefined)
            {
                writer.WritePropertyName(k);
                el.WriteTo(writer);
            }
        }
        writer.WriteEndObject();

        writer.WritePropertyName("collections");
        writer.WriteStartObject();
        foreach ((string colKey, List<CustomWidgetRecord> list) in payload.Collections)
        {
            writer.WritePropertyName(colKey);
            writer.WriteStartArray();
            foreach (CustomWidgetRecord record in list)
            {
                writer.WriteStartObject();
                writer.WriteString("id", record.Id);
                writer.WriteNumber("createdAtUtcTicks", record.CreatedAtUtcTicks);
                writer.WriteNumber("updatedAtUtcTicks", record.UpdatedAtUtcTicks);
                writer.WriteBoolean("isDeleted", record.IsDeleted);
                writer.WritePropertyName("fields");
                writer.WriteStartObject();
                foreach ((string fk, JsonElement fv) in record.FieldsJson)
                {
                    if (fv.ValueKind != JsonValueKind.Undefined)
                    {
                        writer.WritePropertyName(fk);
                        fv.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
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
            LoadedFromDisk = payload.LoadedFromDisk,
            Settings = new Dictionary<string, string>(
                payload.Settings ?? [],
                StringComparer.Ordinal),
            Collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal)
        };

        if (payload.StateJson is not null)
        {
            foreach ((string k, JsonElement el) in payload.StateJson)
            {
                if (el.ValueKind != JsonValueKind.Undefined)
                {
                    normalized.StateJson[k] = el.Clone();
                }
            }
        }

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
                    clonedList.Add(record.Clone());
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
                _watcher = new FileSystemWatcher(parentDirectory, "*.*")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += OnFileEvent;
                _watcher.Created += OnFileEvent;
                _watcher.Deleted += OnFileEvent;
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
            string fileName = Path.GetFileName(e.FullPath);
            if (string.IsNullOrEmpty(fileName) || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? dir = Path.GetDirectoryName(e.FullPath);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            string dirName = Path.GetFileName(dir);
            string folderName;
            if (string.Equals(dirName, LocalesDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string? widgetDir = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(widgetDir))
                {
                    return;
                }

                folderName = Path.GetFileName(widgetDir);
            }
            else
            {
                if (!string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fileName, "store.json", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fileName, "logic.js", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                folderName = dirName;
            }
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
                watcher.Deleted -= OnFileEvent;
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
