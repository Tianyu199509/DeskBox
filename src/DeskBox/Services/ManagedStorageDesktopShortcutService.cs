using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;

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
    /// Updates an existing managed-storage desktop shortcut without creating a
    /// replacement when the user has removed it. <paramref name="previousRootPath"/>
    /// allows a verified DeskBox shortcut to follow a successful storage migration.
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
                return;
            }

            if (!Directory.Exists(_desktopDirectory))
            {
                App.Log(
                    $"[ManagedStorageShortcut] Sync deferred because the desktop " +
                    $"directory is unavailable path='{_desktopDirectory}'");
                return;
            }

            string? shortcutPath = storedShortcutPath;
            if (shortcutPath is null ||
                !File.Exists(shortcutPath) ||
                !IsOwnedShortcut(shortcutPath, currentRootPath, normalizedPreviousRootPath))
            {
                shortcutPath = FindOwnedShortcut(
                    currentRootPath,
                    normalizedPreviousRootPath);
            }

            if (shortcutPath is null)
            {
                // A missing shortcut is an ordinary user deletion. Clear the
                // preference instead of resurrecting the desktop entry.
                await StoreShortcutStateAsync(enabled: false, string.Empty);
                App.Log(
                    "[ManagedStorageShortcut] Disabled because the previously " +
                    "maintained shortcut is no longer present.");
                return;
            }

            Directory.CreateDirectory(currentRootPath);
            ShortcutHelper.CreateOrUpdateFolderShortcut(
                shortcutPath,
                currentRootPath,
                ShortcutDescription);
            Win32Helper.NotifyShellDirectoryUpdated(_desktopDirectory);

            if (!string.Equals(
                    settings.ManagedStorageDesktopShortcutPath,
                    shortcutPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                await StoreShortcutStateAsync(enabled: true, shortcutPath);
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

    /// <summary>
    /// Creates the desktop shortcut only after an explicit user action.
    /// </summary>
    public async Task<bool> CreateAsync()
    {
        await _syncGate.WaitAsync();
        try
        {
            AppSettings settings = _settingsService.Settings;
            string currentRootPath = SettingsService.NormalizeManagedStorageRootPath(
                settings.DefaultManagedStorageRootPath);
            string? shortcutPath = GetSafeStoredShortcutPath(
                settings.ManagedStorageDesktopShortcutPath);

            if (shortcutPath is not null && File.Exists(shortcutPath) &&
                !IsOwnedShortcut(shortcutPath, currentRootPath, previousRootPath: null))
            {
                shortcutPath = null;
            }

            shortcutPath ??= FindOwnedShortcut(currentRootPath, previousRootPath: null);
            if (shortcutPath is null || Directory.Exists(shortcutPath))
            {
                shortcutPath = GetAvailableShortcutPath(_desktopDirectory);
            }

            Directory.CreateDirectory(currentRootPath);
            ShortcutHelper.CreateOrUpdateFolderShortcut(
                shortcutPath,
                currentRootPath,
                ShortcutDescription);
            Win32Helper.NotifyShellDirectoryUpdated(_desktopDirectory);
            await StoreShortcutStateAsync(enabled: true, shortcutPath);
            App.Log(
                $"[ManagedStorageShortcut] Created by user action path='{shortcutPath}' " +
                $"target='{currentRootPath}'");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[ManagedStorageShortcut] Explicit creation failed: {ex}");
            return false;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Removes the verified DeskBox shortcut after an explicit user action.
    /// </summary>
    public async Task<bool> RemoveAsync()
    {
        await _syncGate.WaitAsync();
        try
        {
            AppSettings settings = _settingsService.Settings;
            string currentRootPath = SettingsService.NormalizeManagedStorageRootPath(
                settings.DefaultManagedStorageRootPath);
            string? shortcutPath = GetSafeStoredShortcutPath(
                settings.ManagedStorageDesktopShortcutPath);

            if (shortcutPath is null ||
                !IsOwnedShortcut(shortcutPath, currentRootPath, previousRootPath: null))
            {
                shortcutPath = FindOwnedShortcut(
                    currentRootPath,
                    previousRootPath: null);
            }

            if (shortcutPath is not null &&
                !TryDeleteOwnedShortcut(
                    shortcutPath,
                    currentRootPath,
                    previousRootPath: null))
            {
                return false;
            }

            await StoreShortcutStateAsync(enabled: false, string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[ManagedStorageShortcut] Explicit removal failed: {ex}");
            return false;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Returns whether a verified shortcut currently exists on the desktop.
    /// The persisted preference is deliberately not treated as proof because
    /// the user can delete the external .lnk file in Explorer.
    /// </summary>
    public bool HasShortcut()
    {
        try
        {
            AppSettings settings = _settingsService.Settings;
            string currentRootPath = SettingsService.NormalizeManagedStorageRootPath(
                settings.DefaultManagedStorageRootPath);
            string? shortcutPath = GetSafeStoredShortcutPath(
                settings.ManagedStorageDesktopShortcutPath);
            return (shortcutPath is not null &&
                    IsOwnedShortcut(
                        shortcutPath,
                        currentRootPath,
                        previousRootPath: null)) ||
                   FindOwnedShortcut(
                       currentRootPath,
                       previousRootPath: null) is not null;
        }
        catch
        {
            return false;
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
        DesktopIconPositionInterop.NotifyShellItemDeleted(shortcutPath, wasDirectory: false);
        App.Log($"[ManagedStorageShortcut] Removed path='{shortcutPath}'");
        return true;
    }

    internal static bool IsManagedStorageShortcutFile(string? shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            !Path.GetExtension(shortcutPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(shortcutPath))
        {
            return false;
        }

        ShortcutInfo? metadata = ShortcutHelper.ReadStoredMetadata(shortcutPath);
        return metadata is not null &&
               string.Equals(metadata.Description, ShortcutDescription, StringComparison.Ordinal);
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

    private async Task StoreShortcutStateAsync(bool enabled, string shortcutPath)
    {
        _settingsService.Settings.ManagedStorageDesktopShortcutEnabled = enabled;
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
