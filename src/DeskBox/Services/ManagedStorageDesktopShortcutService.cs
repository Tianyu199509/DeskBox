using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Maintains a user-owned desktop entry for the managed storage root. The
/// folder shortcut deliberately has no icon or executable dependency on
/// DeskBox, so uninstalling the application does not break the entry.
/// </summary>
public sealed class ManagedStorageDesktopShortcutService
{
    internal const string ShortcutFileName = "DeskBox Files.lnk";
    internal const string ShortcutDescription = "DeskBox managed storage";
    private const int MaxNumberedShortcutCandidates = 99;

    private readonly SettingsService _settingsService;
    private readonly string _desktopDirectory;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public ManagedStorageDesktopShortcutService(SettingsService settingsService)
        : this(
            settingsService,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
    {
    }

    internal ManagedStorageDesktopShortcutService(
        SettingsService settingsService,
        string desktopDirectory)
    {
        _settingsService = settingsService;
        _desktopDirectory = Path.GetFullPath(desktopDirectory);
    }

    /// <summary>
    /// Creates, updates, or removes the managed-storage desktop shortcut to
    /// match the current setting. <paramref name="previousRootPath"/> allows a
    /// verified DeskBox shortcut to follow a successful storage migration.
    /// </summary>
    public async Task SyncAsync(string? previousRootPath = null)
    {
        await _syncGate.WaitAsync();
        try
        {
            AppSettings settings = _settingsService.Settings;
            string currentRootPath = SettingsService.NormalizeManagedStorageRootPath(
                settings.DefaultManagedStorageRootPath);
            string? normalizedPreviousRootPath = TryNormalizePath(previousRootPath);
            string? storedShortcutPath = GetSafeStoredShortcutPath(
                settings.ManagedStorageDesktopShortcutPath);

            if (!settings.ManagedStorageDesktopShortcutEnabled)
            {
                if (storedShortcutPath is not null &&
                    TryDeleteOwnedShortcut(
                        storedShortcutPath,
                        currentRootPath,
                        normalizedPreviousRootPath))
                {
                    await StoreShortcutPathAsync(string.Empty);
                }

                return;
            }

            if (!ShouldMaintainShortcut(settings, currentRootPath, storedShortcutPath))
            {
                return;
            }

            string? shortcutPath = storedShortcutPath;
            if (shortcutPath is not null && File.Exists(shortcutPath) &&
                !IsOwnedShortcut(shortcutPath, currentRootPath, normalizedPreviousRootPath))
            {
                // The saved filename has been replaced by something that no
                // longer belongs to DeskBox. Leave it untouched and pick a new
                // collision-free name.
                shortcutPath = null;
            }

            shortcutPath ??= FindOwnedShortcut(currentRootPath, normalizedPreviousRootPath);
            shortcutPath ??= GetAvailableShortcutPath(_desktopDirectory);

            Directory.CreateDirectory(currentRootPath);
            ShortcutHelper.CreateOrUpdateFolderShortcut(
                shortcutPath,
                currentRootPath,
                ShortcutDescription);

            if (!string.Equals(
                    settings.ManagedStorageDesktopShortcutPath,
                    shortcutPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                await StoreShortcutPathAsync(shortcutPath);
            }

            App.Log(
                $"[ManagedStorageShortcut] Ready path='{shortcutPath}' " +
                $"target='{currentRootPath}'");
        }
        catch (Exception ex)
        {
            // A redirected/offline desktop must not block app startup or a
            // storage-path migration. The uninstaller provides a second
            // opportunity to create the entry.
            App.Log($"[ManagedStorageShortcut] Sync failed: {ex}");
        }
        finally
        {
            _syncGate.Release();
        }
    }

    internal static string GetAvailableShortcutPath(string desktopDirectory)
    {
        string normalizedDesktopDirectory = Path.GetFullPath(desktopDirectory);
        string candidate = Path.Combine(normalizedDesktopDirectory, ShortcutFileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        string baseName = Path.GetFileNameWithoutExtension(ShortcutFileName);
        for (int number = 2; number <= MaxNumberedShortcutCandidates; number++)
        {
            candidate = Path.Combine(
                normalizedDesktopDirectory,
                $"{baseName} ({number}).lnk");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(
            normalizedDesktopDirectory,
            $"{baseName} ({Guid.NewGuid():N}).lnk");
    }

    private static bool ShouldMaintainShortcut(
        AppSettings settings,
        string currentRootPath,
        string? storedShortcutPath)
    {
        if (storedShortcutPath is not null && File.Exists(storedShortcutPath))
        {
            return true;
        }

        if (Directory.Exists(currentRootPath))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(currentRootPath).Any())
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        return settings.Widgets.Any(widget =>
            widget.WidgetKind == WidgetKind.File &&
            widget.FollowsDefaultStoragePath &&
            !settings.DeletedWidgetIds.Contains(widget.Id, StringComparer.Ordinal));
    }

    private string? FindOwnedShortcut(
        string currentRootPath,
        string? previousRootPath)
    {
        string baseName = Path.GetFileNameWithoutExtension(ShortcutFileName);
        for (int number = 1; number <= MaxNumberedShortcutCandidates; number++)
        {
            string fileName = number == 1
                ? ShortcutFileName
                : $"{baseName} ({number}).lnk";
            string candidate = Path.Combine(_desktopDirectory, fileName);
            if (IsOwnedShortcut(candidate, currentRootPath, previousRootPath))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? GetSafeStoredShortcutPath(string? shortcutPath)
    {
        string? normalizedPath = TryNormalizePath(shortcutPath);
        if (normalizedPath is null ||
            !string.Equals(
                Path.GetDirectoryName(normalizedPath),
                _desktopDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(normalizedPath).Equals(
                ".lnk",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalizedPath;
    }

    private static bool TryDeleteOwnedShortcut(
        string shortcutPath,
        string currentRootPath,
        string? previousRootPath)
    {
        if (!File.Exists(shortcutPath))
        {
            return true;
        }

        if (!IsOwnedShortcut(shortcutPath, currentRootPath, previousRootPath))
        {
            App.Log(
                $"[ManagedStorageShortcut] Preserved unowned shortcut '{shortcutPath}'");
            return false;
        }

        File.Delete(shortcutPath);
        App.Log($"[ManagedStorageShortcut] Removed path='{shortcutPath}'");
        return true;
    }

    private static bool IsOwnedShortcut(
        string shortcutPath,
        string currentRootPath,
        string? previousRootPath)
    {
        ShortcutInfo? metadata = ShortcutHelper.ReadStoredMetadata(shortcutPath);
        if (metadata is null ||
            !string.Equals(
                metadata.Description,
                ShortcutDescription,
                StringComparison.Ordinal))
        {
            return false;
        }

        string? targetPath = TryNormalizePath(metadata.TargetPath);
        return targetPath is not null &&
               (PathsEqual(targetPath, currentRootPath) ||
                (previousRootPath is not null && PathsEqual(targetPath, previousRootPath)));
    }

    private async Task StoreShortcutPathAsync(string shortcutPath)
    {
        _settingsService.Settings.ManagedStorageDesktopShortcutPath = shortcutPath;
        await _settingsService.SaveAsync(notifySubscribers: false);
    }

    private static bool PathsEqual(string leftPath, string rightPath)
    {
        string? normalizedLeft = TryNormalizePath(leftPath);
        string? normalizedRight = TryNormalizePath(rightPath);
        return normalizedLeft is not null &&
               normalizedRight is not null &&
               string.Equals(
                   normalizedLeft.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   normalizedRight.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return null;
        }
    }
}
