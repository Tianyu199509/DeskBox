using System.Globalization;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services.Distribution;

internal enum ChannelMigrationOutcomeKind
{
    None = 0,
    MigratedFromLegacyStoreLocalCache = 1,
    MigratedFromDirectToStoreInitial = 2,
    MigratedFromDirectToStoreNewer = 3,
    ImportedFromStoreToDirect = 4,
    DeclinedByUser = 5,
    SkippedNewerSchema = 6,
}

internal sealed record ChannelMigrationOutcome(
    ChannelMigrationOutcomeKind Kind,
    bool OtherChannelInstalled = false,
    string? BackupDirectory = null,
    bool NotifiedPreUi = false)
{
    internal static ChannelMigrationOutcome None { get; } = new(ChannelMigrationOutcomeKind.None);
}

/// <summary>
/// Orchestrates primary-instance-only, bidirectional, non-destructive data
/// migration between the Direct installer edition (<c>%LOCALAPPDATA%\DeskBox</c>)
/// and the Microsoft Store edition (<c>%USERPROFILE%\DeskBox\.appdata</c>).
/// Runs strictly AFTER primary single-instance lock acquisition so a secondary
/// launch never freezes a stale snapshot or writes migration sentinels while
/// the other edition is still running.
/// </summary>
internal static class ChannelMigrationService
{
    internal const string DirectSyncWatermarkFileName = ".direct-sync-watermark.v1";
    internal const string StoreImportDeclinedMarkerFileName = ".store-import-declined.v1";
    private static readonly TimeSpan NewerSourceSkewTolerance = TimeSpan.FromSeconds(60);

    internal static ChannelMigrationOutcome RunStartupChannelMigration(
        DeskBoxDataPathService dataPaths,
        AppDistributionChannel currentChannel,
        bool isInteractiveLaunch,
        string? cultureName = null,
        Func<string, string, bool>? confirmPrompt = null,
        Func<bool>? otherChannelInstalledProbe = null,
        Func<string, string?>? physicalPathResolver = null,
        Action<string>? log = null,
        Action<string, string>? infoNotice = null,
        int maxSupportedSchemaVersion = SettingsMigrationPipeline.CurrentSchemaVersion)
    {
        if (dataPaths.IsDevelopmentRoot)
        {
            return ChannelMigrationOutcome.None;
        }

        string storeDurableRoot = currentChannel == AppDistributionChannel.MicrosoftStore
            ? dataPaths.RootPath
            : ChannelCoexistenceService.GetStoreDurableDataRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string primarySettingsPath = currentChannel == AppDistributionChannel.MicrosoftStore
            ? Path.Combine(dataPaths.RootPath, "data", "settings.json")
            : Path.Combine(storeDurableRoot, "data", "settings.json");
        string fallbackSettingsPath = currentChannel == AppDistributionChannel.MicrosoftStore
            ? Path.Combine(dataPaths.LegacyLocalAppDataRoot, "data", "settings.json")
            : Path.Combine(dataPaths.RootPath, "data", "settings.json");

        string? lazyEffectiveCulture = string.IsNullOrWhiteSpace(cultureName) ? null : cultureName;
        string ResolveEffectiveCulture() =>
            lazyEffectiveCulture ??= ResolveMigrationCulture(null, primarySettingsPath, fallbackSettingsPath);
        Func<string, string, bool> prompt = confirmPrompt ??
            ((title, message) => SingleInstanceAndActivationInterop.ShowPreUiConfirmation(
                title,
                message,
                yesNo: true,
                isRtl: CustomWidgetLocaleCatalog.IsRightToLeftCulture(ResolveEffectiveCulture())));
        Action<string, string> notice = infoNotice ??
            ((title, message) => SingleInstanceAndActivationInterop.ShowPreUiConfirmation(
                title,
                message,
                yesNo: false,
                isRtl: CustomWidgetLocaleCatalog.IsRightToLeftCulture(ResolveEffectiveCulture())));
        Func<string, string?> resolveFinalPath = physicalPathResolver ?? TryResolvePhysicalPath;

        return currentChannel == AppDistributionChannel.MicrosoftStore
            ? RunStoreStartupMigration(
                dataPaths.LegacyLocalAppDataRoot,
                dataPaths.RootPath,
                dataPaths.LegacyRecoveryRoot,
                dataPaths.RecoveryDirectory,
                isInteractiveLaunch,
                lazyEffectiveCulture,
                prompt,
                otherChannelInstalledProbe ?? (() => ChannelCoexistenceService.DetectOtherChannelInstalled(isCurrentMicrosoftStore: true).OtherChannelInstalled),
                resolveFinalPath,
                log,
                notice,
                maxSupportedSchemaVersion)
            : RunDirectStartupImportFromStore(
                dataPaths.RootPath,
                storeDurableRoot,
                isInteractiveLaunch,
                lazyEffectiveCulture,
                prompt,
                otherChannelInstalledProbe ?? (() => ChannelCoexistenceService.DetectOtherChannelInstalled(isCurrentMicrosoftStore: false).OtherChannelInstalled),
                log,
                notice,
                maxSupportedSchemaVersion);
    }

    internal static ChannelMigrationOutcome RunStoreStartupMigration(
        string directOrLegacyRoot,
        string storeDurableRoot,
        string legacyRecoveryRoot,
        string storeRecoveryRoot,
        bool isInteractiveLaunch,
        string? cultureName,
        Func<string, string, bool> confirmPrompt,
        Func<bool> otherChannelInstalledProbe,
        Func<string, string?> resolveFinalPath,
        Action<string>? log = null,
        Action<string, string>? infoNotice = null,
        int maxSupportedSchemaVersion = SettingsMigrationPipeline.CurrentSchemaVersion)
    {
        string legacyDataDir = Path.Combine(directOrLegacyRoot, "data");
        string storeDataDir = Path.Combine(storeDurableRoot, "data");
        string legacySettingsPath = Path.Combine(legacyDataDir, "settings.json");
        string storeSettingsPath = Path.Combine(storeDataDir, "settings.json");
        string inProgressMarker = Path.Combine(storeDurableRoot, StoreUserAssetMigrationFileOps.MigrationInProgressMarkerFileName);

        bool legacySettingsExists = File.Exists(legacySettingsPath);
        bool storeSettingsExists = File.Exists(storeSettingsPath);
        // Probe settings.json itself (not legacyDataDir): under Win10 MSIX bindflt
        // merge semantics, an empty LocalCache\Local\DeskBox\data directory resolves
        // to LocalCache even when settings.json falls through to Direct's real
        // %LOCALAPPDATA%\DeskBox\data\settings.json.
        bool sourceIsLegacyLocalCache = legacySettingsExists && IsLegacyStoreLocalCachePath(legacySettingsPath, resolveFinalPath);

        if (!storeSettingsExists || File.Exists(inProgressMarker))
        {
            if (legacySettingsExists &&
                !sourceIsLegacyLocalCache &&
                TryReadSettingsSchemaVersion(legacySettingsPath) is int initialDirectSchema &&
                initialDirectSchema > maxSupportedSchemaVersion)
            {
                log?.Invoke(
                    $"[ChannelMigration] Skipped Direct-to-Store initial migration: source schemaVersion={initialDirectSchema} " +
                    $"exceeds supported ceiling {maxSupportedSchemaVersion}.");
                bool notified = false;
                if (isInteractiveLaunch && infoNotice is not null)
                {
                    string noticeCulture = ResolveMigrationCulture(cultureName, storeSettingsPath, legacySettingsPath);
                    NotifyNewerSchemaSkipped(noticeCulture, AppDistributionChannel.Direct, infoNotice);
                    notified = true;
                }

                return new ChannelMigrationOutcome(
                    ChannelMigrationOutcomeKind.SkippedNewerSchema,
                    OtherChannelInstalled: otherChannelInstalledProbe(),
                    NotifiedPreUi: notified);
            }

            StoreUserAssetMigrationFileOps.EnsureStoreDurableRootInitialized(
                directOrLegacyRoot,
                storeDurableRoot,
                legacyRecoveryRoot,
                storeRecoveryRoot);

            if (!legacySettingsExists || !File.Exists(storeSettingsPath))
            {
                return ChannelMigrationOutcome.None;
            }

            DateTime? directStamp = TryGetProfileLastWriteUtc(legacyDataDir);
            if (directStamp is not null && !sourceIsLegacyLocalCache)
            {
                WriteDirectWatermarkUtc(storeDurableRoot, directStamp.Value);
            }

            if (sourceIsLegacyLocalCache)
            {
                log?.Invoke("[ChannelMigration] Migrated legacy Store LocalCache profile into durable .appdata root.");
                return new ChannelMigrationOutcome(ChannelMigrationOutcomeKind.MigratedFromLegacyStoreLocalCache);
            }

            bool directStillInstalled = otherChannelInstalledProbe();
            log?.Invoke($"[ChannelMigration] Migrated Direct profile into Store .appdata root (directInstalled={directStillInstalled}).");
            return new ChannelMigrationOutcome(
                ChannelMigrationOutcomeKind.MigratedFromDirectToStoreInitial,
                OtherChannelInstalled: directStillInstalled);
        }

        // Durable Store profile already exists: check if an unvirtualized Direct
        // profile was modified more recently than both the Store profile and our
        // last import/dismiss watermark.
        if (!legacySettingsExists || sourceIsLegacyLocalCache)
        {
            if (sourceIsLegacyLocalCache)
            {
                StoreUserAssetMigrationFileOps.TryRetireMigratedLegacyLocalCacheDataDirectory(
                    legacyDataDir,
                    storeSettingsPath);
            }

            return ChannelMigrationOutcome.None;
        }

        DateTime? latestDirectUtc = TryGetProfileLastWriteUtc(legacyDataDir);
        DateTime? latestStoreUtc = TryGetProfileLastWriteUtc(storeDataDir);
        if (latestDirectUtc is null || latestStoreUtc is null)
        {
            return ChannelMigrationOutcome.None;
        }

        DateTime? watermarkUtc = ReadDirectWatermarkUtc(storeDurableRoot);
        DateTime baselineUtc = watermarkUtc is not null && watermarkUtc.Value > latestStoreUtc.Value
            ? watermarkUtc.Value
            : latestStoreUtc.Value;

        if (latestDirectUtc.Value <= baselineUtc + NewerSourceSkewTolerance)
        {
            return ChannelMigrationOutcome.None;
        }

        if (TryReadSettingsSchemaVersion(legacySettingsPath) is int newerDirectSchema &&
            newerDirectSchema > maxSupportedSchemaVersion)
        {
            log?.Invoke(
                $"[ChannelMigration] Skipped Direct-to-Store re-import: source schemaVersion={newerDirectSchema} " +
                $"exceeds supported ceiling {maxSupportedSchemaVersion}.");
            return new ChannelMigrationOutcome(
                ChannelMigrationOutcomeKind.SkippedNewerSchema,
                OtherChannelInstalled: otherChannelInstalledProbe(),
                NotifiedPreUi: false);
        }

        if (!isInteractiveLaunch)
        {
            log?.Invoke("[ChannelMigration] Direct profile is newer than Store profile; deferring re-import prompt until interactive launch.");
            return ChannelMigrationOutcome.None;
        }

        string effectiveCulture = ResolveMigrationCulture(cultureName, storeSettingsPath, legacySettingsPath);
        string formattedTime = latestDirectUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        string title = LocalizationService.TranslateForCulture(effectiveCulture, "ChannelCoexistence.DirectNewerPrompt.Title");
        string bodyTemplate = LocalizationService.TranslateForCulture(effectiveCulture, "ChannelCoexistence.DirectNewerPrompt.Body");
        string body = string.Format(CultureInfo.CurrentCulture, bodyTemplate, formattedTime);

        if (!confirmPrompt(title, body))
        {
            WriteDirectWatermarkUtc(storeDurableRoot, latestDirectUtc.Value);
            log?.Invoke("[ChannelMigration] User declined re-importing newer Direct profile into Store.");
            return new ChannelMigrationOutcome(
                ChannelMigrationOutcomeKind.DeclinedByUser,
                OtherChannelInstalled: otherChannelInstalledProbe());
        }

        string backupDir = Path.Combine(
            storeRecoveryRoot,
            $"pre-channel-migration-{DateTime.Now:yyyyMMdd-HHmmss}");
        try
        {
            StoreUserAssetMigrationFileOps.EnsureHiddenDirectory(storeRecoveryRoot);
            bool backupOk = StoreUserAssetMigrationFileOps.CopyDirectoryNonDestructive(
                storeDataDir,
                backupDir,
                overwriteExisting: true);
            if (!backupOk)
            {
                log?.Invoke("[ChannelMigration] Pre-migration Store backup was incomplete; aborting overwrite.");
                return ChannelMigrationOutcome.None;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ChannelMigration] Pre-migration Store backup failed; aborting overwrite: {ex.Message}");
            return ChannelMigrationOutcome.None;
        }

        bool copiedCleanly = StoreUserAssetMigrationFileOps.CopyDirectoryNonDestructive(
            legacyDataDir,
            storeDataDir,
            overwriteExisting: true);
        if (copiedCleanly)
        {
            RemoveStaleSplitStoreFilesWhenSourcePredatesSplit(legacyDataDir, storeDataDir);
        }

        StoreUserAssetMigrationFileOps.RebaseMigratedJsonStorePaths(legacyDataDir, storeDataDir);
        if (copiedCleanly)
        {
            WriteDirectWatermarkUtc(storeDurableRoot, latestDirectUtc.Value);
        }

        log?.Invoke(
            $"[ChannelMigration] Re-imported newer Direct profile into Store (clean={copiedCleanly}, backup='{backupDir}').");
        return new ChannelMigrationOutcome(
            ChannelMigrationOutcomeKind.MigratedFromDirectToStoreNewer,
            OtherChannelInstalled: otherChannelInstalledProbe(),
            BackupDirectory: backupDir);
    }

    internal const string StoreImportDeferredMarkerFileName = ".store-import-deferred.v1";

    internal static ChannelMigrationOutcome RunDirectStartupImportFromStore(
        string directRoot,
        string storeDurableRoot,
        bool isInteractiveLaunch,
        string? cultureName,
        Func<string, string, bool> confirmPrompt,
        Func<bool> otherChannelInstalledProbe,
        Action<string>? log = null,
        Action<string, string>? infoNotice = null,
        int maxSupportedSchemaVersion = SettingsMigrationPipeline.CurrentSchemaVersion)
    {
        if (string.IsNullOrWhiteSpace(directRoot) || string.IsNullOrWhiteSpace(storeDurableRoot))
        {
            return ChannelMigrationOutcome.None;
        }

        string directDataDir = Path.Combine(directRoot, "data");
        string storeDataDir = Path.Combine(storeDurableRoot, "data");
        string directSettingsPath = Path.Combine(directDataDir, "settings.json");
        string storeSettingsPath = Path.Combine(storeDataDir, "settings.json");
        string declinedMarker = Path.Combine(directRoot, StoreImportDeclinedMarkerFileName);
        string deferredMarker = Path.Combine(directRoot, StoreImportDeferredMarkerFileName);
        bool hadDeferredPrompt = File.Exists(deferredMarker);

        if ((File.Exists(directSettingsPath) && !hadDeferredPrompt) ||
            !File.Exists(storeSettingsPath) ||
            File.Exists(declinedMarker))
        {
            return ChannelMigrationOutcome.None;
        }

        if (TryReadSettingsSchemaVersion(storeSettingsPath) is int storeSchema &&
            storeSchema > maxSupportedSchemaVersion)
        {
            log?.Invoke(
                $"[ChannelMigration] Skipped Store-to-Direct import: source schemaVersion={storeSchema} " +
                $"exceeds supported ceiling {maxSupportedSchemaVersion}.");
            bool notified = false;
            if (isInteractiveLaunch && infoNotice is not null)
            {
                string noticeCulture = ResolveMigrationCulture(cultureName, storeSettingsPath, directSettingsPath);
                NotifyNewerSchemaSkipped(noticeCulture, AppDistributionChannel.MicrosoftStore, infoNotice);
                notified = true;
                StoreUserAssetMigrationFileOps.TryDeleteFileQuietly(deferredMarker);
            }

            return new ChannelMigrationOutcome(
                ChannelMigrationOutcomeKind.SkippedNewerSchema,
                OtherChannelInstalled: otherChannelInstalledProbe(),
                NotifiedPreUi: notified);
        }

        if (!isInteractiveLaunch)
        {
            try
            {
                Directory.CreateDirectory(directRoot);
                File.WriteAllText(deferredMarker, "v1\n", Encoding.UTF8);
            }
            catch
            {
            }

            log?.Invoke("[ChannelMigration] Store profile detected on empty Direct launch; deferring import prompt until interactive launch.");
            return ChannelMigrationOutcome.None;
        }

        string effectiveCulture = ResolveMigrationCulture(cultureName, storeSettingsPath, directSettingsPath);
        string title = LocalizationService.TranslateForCulture(effectiveCulture, "ChannelCoexistence.StoreImportPrompt.Title");
        string body = LocalizationService.TranslateForCulture(effectiveCulture, "ChannelCoexistence.StoreImportPrompt.Body");
        if (!confirmPrompt(title, body))
        {
            try
            {
                Directory.CreateDirectory(directRoot);
                File.WriteAllText(declinedMarker, "v1\n", Encoding.UTF8);
                StoreUserAssetMigrationFileOps.TryDeleteFileQuietly(deferredMarker);
            }
            catch
            {
            }

            log?.Invoke("[ChannelMigration] User declined importing Store profile into Direct edition.");
            return new ChannelMigrationOutcome(
                ChannelMigrationOutcomeKind.DeclinedByUser,
                OtherChannelInstalled: otherChannelInstalledProbe());
        }

        bool copiedCleanly = StoreUserAssetMigrationFileOps.CopyDirectoryNonDestructive(
            storeDataDir,
            directDataDir,
            overwriteExisting: hadDeferredPrompt);
        StoreUserAssetMigrationFileOps.RebaseMigratedJsonStorePaths(storeDataDir, directDataDir);
        StoreUserAssetMigrationFileOps.TryDeleteFileQuietly(deferredMarker);

        log?.Invoke($"[ChannelMigration] Imported Store profile into Direct edition (clean={copiedCleanly}).");
        return new ChannelMigrationOutcome(
            ChannelMigrationOutcomeKind.ImportedFromStoreToDirect,
            OtherChannelInstalled: otherChannelInstalledProbe());
    }

    internal static int? TryReadSettingsSchemaVersion(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settingsPath, Encoding.UTF8));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if ((root.TryGetProperty("schemaVersion", out JsonElement el) ||
                 root.TryGetProperty("SchemaVersion", out el)) &&
                el.ValueKind == JsonValueKind.Number &&
                el.TryGetInt32(out int version))
            {
                return version;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ResolveCultureOrDefault(string? cultureName) =>
        string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolvePersistedOrAmbientCultureName()
            : cultureName;

    private static string ResolveMigrationCulture(
        string? explicitCulture,
        string primarySettingsPath,
        string fallbackSettingsPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitCulture))
        {
            return explicitCulture;
        }

        return LocalizationService.TryReadPersistedCultureNameFromFile(primarySettingsPath)
               ?? LocalizationService.TryReadPersistedCultureNameFromFile(fallbackSettingsPath)
               ?? LocalizationService.ResolvePersistedOrAmbientCultureName();
    }

    private static void RemoveStaleSplitStoreFilesWhenSourcePredatesSplit(
        string sourceDataDir,
        string targetDataDir)
    {
        // When importing an older profile that predates widget-layout.json or
        // desktop-organization-history.json, remove any stale split files in the
        // destination so they do not shadow the imported settings.json keys.
        foreach (string splitFileName in new[] { "widget-layout.json", "desktop-organization-history.json" })
        {
            string sourcePath = Path.Combine(sourceDataDir, splitFileName);
            if (!File.Exists(sourcePath))
            {
                string staleTargetPath = Path.Combine(targetDataDir, splitFileName);
                StoreUserAssetMigrationFileOps.TryDeleteFileQuietly(staleTargetPath);
                StoreUserAssetMigrationFileOps.TryDeleteFileQuietly(staleTargetPath + ".bak");
            }
        }
    }

    private static void NotifyNewerSchemaSkipped(
        string? cultureName,
        AppDistributionChannel sourceChannel,
        Action<string, string> infoNotice)
    {
        string effectiveCulture = ResolveCultureOrDefault(cultureName);
        string sourceLabel = LocalizationService.TranslateForCulture(
            effectiveCulture,
            sourceChannel == AppDistributionChannel.MicrosoftStore
                ? "ChannelCoexistence.ChannelName.Store"
                : "ChannelCoexistence.ChannelName.Direct");
        string title = LocalizationService.TranslateForCulture(effectiveCulture, "ChannelCoexistence.NewerSchemaSkipped.Title");
        string bodyTemplate = LocalizationService.TranslateForCulture(effectiveCulture, "ChannelCoexistence.NewerSchemaSkipped.Body");
        string body = string.Format(CultureInfo.CurrentCulture, bodyTemplate, sourceLabel);
        infoNotice(title, body);
    }

    internal static bool IsLegacyStoreLocalCachePath(string directoryPath, Func<string, string?> resolveFinalPath)
    {
        string? finalPath = resolveFinalPath(directoryPath);
        if (string.IsNullOrWhiteSpace(finalPath))
        {
            return false;
        }

        return finalPath.Contains(@"\LocalCache\", StringComparison.OrdinalIgnoreCase) &&
               finalPath.Contains(@"\Packages\", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryResolvePhysicalPath(string path) =>
        Win32Helper.TryGetFinalPath(path, out string physical) && !string.IsNullOrWhiteSpace(physical)
            ? physical
            : null;

    private static DateTime? TryGetProfileLastWriteUtc(string dataDir)
    {
        DateTime? latest = null;
        foreach (string fileName in new[] { "settings.json", "widget-layout.json" })
        {
            string filePath = Path.Combine(dataDir, fileName);
            try
            {
                if (File.Exists(filePath))
                {
                    DateTime utc = File.GetLastWriteTimeUtc(filePath);
                    if (latest is null || utc > latest.Value)
                    {
                        latest = utc;
                    }
                }
            }
            catch
            {
            }
        }

        return latest;
    }

    private static DateTime? ReadDirectWatermarkUtc(string storeDurableRoot)
    {
        string markerPath = Path.Combine(storeDurableRoot, DirectSyncWatermarkFileName);
        try
        {
            if (File.Exists(markerPath) &&
                long.TryParse(File.ReadAllText(markerPath, Encoding.UTF8).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) &&
                ticks > 0)
            {
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }
        catch
        {
        }

        return null;
    }

    private static void WriteDirectWatermarkUtc(string storeDurableRoot, DateTime utcStamp)
    {
        string markerPath = Path.Combine(storeDurableRoot, DirectSyncWatermarkFileName);
        try
        {
            StoreUserAssetMigrationFileOps.EnsureHiddenDirectory(storeDurableRoot);
            File.WriteAllText(markerPath, utcStamp.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
        }
        catch
        {
        }
    }
}
