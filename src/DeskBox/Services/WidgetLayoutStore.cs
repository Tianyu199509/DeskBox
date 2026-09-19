using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Core.Persistence;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(
    typeof(WidgetLayoutDocument),
    TypeInfoPropertyName = "WidgetLayoutDocument")]
[JsonSerializable(
    typeof(WidgetLayoutSettingsSlice),
    TypeInfoPropertyName = "WidgetLayoutSettingsSlice")]
internal sealed partial class WidgetLayoutJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Serialized shape of widget-layout.json. The envelope carries its own
/// schema counter — independent of the settings schema, so future
/// device-domain migrations register against this file rather than the
/// settings pipeline — while the payload stays the typed slice so a new
/// member joins the file automatically.
/// </summary>
internal sealed class WidgetLayoutDocument
{
    public int SchemaVersion { get; set; } = WidgetLayoutStore.CurrentSchemaVersion;

    // required, not defaulted: a document without a `layout` key must fail
    // deserialization and take the quarantine path, never pass as a valid
    // empty layout that would strip the surviving settings keys.
    public required WidgetLayoutSettingsSlice Layout { get; set; }
}

/// <summary>
/// Outcome of <see cref="WidgetLayoutStore.LoadAsync"/>: the authoritative
/// slice to copy into AppSettings.WidgetLayout, plus the authority flag that
/// decides whether settings.json stops carrying the layout keys.
/// </summary>
public sealed class WidgetLayoutLoadResult
{
    public required WidgetLayoutSettingsSlice Data { get; init; }

    /// <inheritdoc cref="WidgetLayoutStore.IsAuthoritative"/>
    public required bool Authoritative { get; init; }

    /// <summary>
    /// File data needed structural fix-ups (null-filled collections, a stale
    /// schema stamp) — the caller should persist once it finishes adopting.
    /// </summary>
    public bool NeedsPersist { get; init; }
}

/// <summary>
/// Device-domain store for the widget layout (<see cref="WidgetLayoutSettingsSlice"/>):
/// widget configs, groups, per-topology layouts, deletion tombstones and
/// group-navigation defaults. This is device-layer data — machine-local and
/// deliberately outside every sync projection. Owns widget-layout.json beside
/// settings.json and adopts the legacy settings keys on first load,
/// fail-closed: settings.json keeps carrying those keys until this store
/// reports <see cref="IsAuthoritative"/>, so an interrupted migration loses
/// nothing and the next launch retries.
/// </summary>
public sealed class WidgetLayoutStore
{
    internal const int CurrentSchemaVersion = 1;

    private readonly string _layoutPath;
    private int _loadedSchemaVersion = CurrentSchemaVersion;

    public WidgetLayoutStore(string? layoutPath = null)
    {
        _layoutPath = layoutPath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "widget-layout.json");
    }

    /// <summary>
    /// True once widget-layout.json is the durable home of the layout — the
    /// file loaded this session, or the adoption write succeeded. While false
    /// the settings writer must keep emitting the layout keys into
    /// settings.json (on disk and on save) so the data survives to a retry.
    /// </summary>
    public bool IsAuthoritative { get; private set; }

    /// <summary>
    /// False when the loaded file was stamped by a schema NEWER than this
    /// build understands. The typed slice round-trips only the fields it
    /// knows, so writing it back would silently discard the newer fields —
    /// the store refuses to overwrite and the pristine file remains the
    /// authority for a future build that can read it.
    /// </summary>
    internal bool CanWrite => _loadedSchemaVersion <= CurrentSchemaVersion;

    /// <summary>The schema version stamped on the loaded file, for diagnostics.</summary>
    internal int LoadedSchemaVersion => _loadedSchemaVersion;

    /// <summary>
    /// settings.json wire names owned by this store — the facade keys that
    /// stop being written once the store is authoritative. Derived from the
    /// slice's own serialization so a new member joins the strip set
    /// automatically; the ownership contract tests pin the exact set.
    /// </summary>
    internal static IReadOnlySet<string> SettingsWireKeys { get; } =
        JsonSerializer.SerializeToNode(
                new WidgetLayoutSettingsSlice(),
                WidgetLayoutJsonContext.Default.WidgetLayoutSettingsSlice)!
            .AsObject()
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Loads the durable store. When widget-layout.json exists it wins — any
    /// legacy keys still inside settings.json are stale by definition. When
    /// it does not, the legacy seed is adopted and written BEFORE the caller
    /// may strip the settings keys, so the layout survives every crash window
    /// of the migration itself.
    /// </summary>
    public async Task<WidgetLayoutLoadResult> LoadAsync(WidgetLayoutSettingsSlice legacySeed)
    {
        ArgumentNullException.ThrowIfNull(legacySeed);

        // ResilientJsonStore owns the corruption protocol: an unreadable
        // primary is quarantined as .corrupt-<timestamp>, the .bak backup is
        // tried next and restored as primary, and only a total miss falls
        // through — the same protection settings.json gave these fields.
        ResilientJsonLoadResult<WidgetLayoutDocument> result =
            await ResilientJsonStore.LoadWithResultAsync(
                _layoutPath,
                static json =>
                {
                    // Fail closed on "parses but is not a layout document":
                    // `null`, `{}` or `{"layout":null}` must not be adopted
                    // as an authoritative empty layout — that would empty
                    // the desktop and strip the surviving settings keys on
                    // the next save. Throwing routes the file through the
                    // same quarantine/backup path as torn JSON.
                    WidgetLayoutDocument? document = JsonSerializer.Deserialize(
                        json,
                        WidgetLayoutJsonContext.Default.WidgetLayoutDocument);
                    return document?.Layout is { } layout
                        ? document
                        : throw new InvalidDataException(
                            "widget-layout.json is structurally empty.");
                },
                static () => new WidgetLayoutDocument { Layout = new WidgetLayoutSettingsSlice() },
                "WidgetLayout");

        if (result.Source is ResilientJsonLoadSource.Primary or ResilientJsonLoadSource.Backup)
        {
            WidgetLayoutSettingsSlice layout = result.Value.Layout ?? new WidgetLayoutSettingsSlice();
            _loadedSchemaVersion = result.Value.SchemaVersion;
            IsAuthoritative = true;
            return new WidgetLayoutLoadResult
            {
                Data = layout,
                Authoritative = true,
                // An older stamp is lifted to current on the next save; a
                // NEWER one is left alone entirely (see CanWrite).
                NeedsPersist = NormalizeStructure(layout) ||
                             result.Value.SchemaVersion < CurrentSchemaVersion
            };
        }

        if (result.Source is ResilientJsonLoadSource.DefaultAfterFailure)
        {
            // The primary was unreadable AND the backup could not recover; the
            // corrupt file was quarantined as .corrupt-<timestamp>. Writing a
            // fresh file now could stamp a stripped/empty slice over the only
            // forensic copy. Stay non-authoritative: the caller keeps routing
            // layout through the settings keys, and once the quarantine file
            // is removed the DefaultMissing path below adopts again.
            return new WidgetLayoutLoadResult
            {
                Data = legacySeed,
                Authoritative = false
            };
        }

        // DefaultMissing: no layout file — adopt the (already migrated and
        // normalized) legacy slice as the initial document.
        IsAuthoritative = await TrySaveSeedAsync(legacySeed);
        return new WidgetLayoutLoadResult
        {
            Data = legacySeed,
            Authoritative = IsAuthoritative
        };
    }

    /// <summary>
    /// Persists via <see cref="ResilientJsonStore"/> using the caller's
    /// temp-file writer. The writer is supplied by SettingsService so the
    /// slice serializes under the same lock that gives settings.json its
    /// coherent-snapshot contract — one save commits one consistent pair of
    /// files.
    /// </summary>
    public Task SaveAsync(Func<string, Task> writeTempFileAsync) =>
        ResilientJsonStore.SaveAsync(_layoutPath, writeTempFileAsync);

    /// <summary>
    /// Same load-bearing semantics as <c>SettingsService.SaveCheckedAsync</c>:
    /// a silent failure must never let a caller treat the layout file as
    /// durable when no commit happened.
    /// </summary>
    public async Task<bool> SaveCheckedAsync(Func<string, Task> writeTempFileAsync)
    {
        if (!CanWrite)
        {
            App.Log(
                $"[WidgetLayout] Refusing to overwrite a schema " +
                $"{_loadedSchemaVersion} file (this build understands " +
                $"{CurrentSchemaVersion}); leaving it pristine.");
            // Honest answer: nothing was committed, so the caller must not
            // treat the in-memory slice as durable.
            return false;
        }

        try
        {
            await SaveAsync(writeTempFileAsync);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log($"[WidgetLayout] Save failed: {ex}");
            return false;
        }
    }

    private Task<bool> TrySaveSeedAsync(WidgetLayoutSettingsSlice seed) =>
        SaveCheckedAsync(tempPath => WriteSeedTempFileAsync(tempPath, seed));

    private static async Task WriteSeedTempFileAsync(string tempPath, WidgetLayoutSettingsSlice seed)
    {
        await using var stream = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(
            stream,
            new WidgetLayoutDocument { Layout = seed },
            WidgetLayoutJsonContext.Default.WidgetLayoutDocument);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Structural fix-ups only: explicit JSON nulls on collection members are
    /// lifted back to the slice's non-null contract so downstream normalizers
    /// never see them. Semantic defaults (styles, tombstone pruning) stay in
    /// the SettingsService normalize passes, which run on the adopted data.
    /// </summary>
    private static bool NormalizeStructure(WidgetLayoutSettingsSlice layout)
    {
        bool changed = false;
        if (layout.FeatureWidgetEnabledStates is null)
        {
            layout.FeatureWidgetEnabledStates = [];
            changed = true;
        }
        if (layout.Widgets is null)
        {
            layout.Widgets = [];
            changed = true;
        }
        if (layout.WidgetGroups is null)
        {
            layout.WidgetGroups = [];
            changed = true;
        }
        if (layout.WidgetTopologyLayouts is null)
        {
            layout.WidgetTopologyLayouts = [];
            changed = true;
        }
        if (layout.DeletedWidgetIds is null)
        {
            layout.DeletedWidgetIds = [];
            changed = true;
        }
        return changed;
    }
}
