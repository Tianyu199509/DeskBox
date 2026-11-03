using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private FileDropIntent ResolveSurfaceDropIntent(
        DataPackageView dataView,
        DataPackageOperation allowedOperations,
        bool forceCopy = false,
        string? destinationFolderPath = null,
        IEnumerable<string>? sourcePathsOverride = null,
        FileDropIntent? precomputedIntent = null)
    {
        if (precomputedIntent is { } cachedIntent)
        {
            return cachedIntent;
        }

        DataPackageOperation supported =
            DeskBoxDragData.GetFileTransferOperations(dataView, allowedOperations);
        bool noOperationMetadata =
            supported == DataPackageOperation.None;
        string destination = destinationFolderPath ??
            TransferIntentEngine.ResolveProspectiveManagedDestination(
                ViewModel.CurrentFolderPath,
                ViewModel.MappedFolderPath,
                _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        string[] sourcePaths = ResolveEffectiveDropSourcePaths(dataView, sourcePathsOverride);
        bool sameVolume = sourcePaths.Length == 0 ||
            (destination.Length > 0 &&
             PathOriginClassifier.AreAllOnSameVolume(
                 sourcePaths,
                 destination,
                 TimeSpan.FromMilliseconds(80)));
        string action = _settingsService.Settings.FileWidget.ManagedDropAction;
        bool followWindows = string.Equals(
            action,
            SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);
        bool isInternal = DeskBoxDragData.IsInternalFileDrag(dataView);
        SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources(
            sourcePaths,
            TimeSpan.FromMilliseconds(80));
        uint effects = Views.ContentWidgetWindow.ToNativeAllowedEffects(supported);

        return TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: isInternal ? TransferDomain.InternalDeskBox : TransferDomain.ExternalInbound,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: forceCopy,
            HasMappedFolder: !string.IsNullOrWhiteSpace(destination),
            ControlDown: Win32Helper.IsKeyPressed(VirtualKey.Control),
            ShiftDown: Win32Helper.IsKeyPressed(VirtualKey.Shift),
            AltDown: Win32Helper.IsKeyPressed(VirtualKey.Menu),
            AllowedEffects: effects,
            NoOperationMetadata: noOperationMetadata,
            DefaultMove: string.Equals(
                action,
                SettingsService.ManagedDropActionMove,
                StringComparison.Ordinal),
            FollowWindows: followWindows,
            SameVolume: sameVolume,
            CanLink: true,
            SourceOrigin: origin)).Intent;
    }

    private string[] ResolveEffectiveDropSourcePaths(
        DataPackageView dataView,
        IEnumerable<string>? sourcePathsOverride)
    {
        string[] explicitPaths = (sourcePathsOverride ?? GetPackagePaths(dataView))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (explicitPaths.Length > 0 ||
            DeskBoxDragData.IsInternalFileDrag(dataView) ||
            _externalDropPathHints.Length == 0)
        {
            return explicitPaths;
        }

        return _externalDropPathHints;
    }

    internal static bool? ResolveMoveWhenMapped(bool mapped, FileDropIntent intent)
    {
        // An explicit Copy/Move intent must survive even when the widget has
        // no managed folder yet (the import creates one on demand), so a Ctrl
        // (copy) gesture never runs the default move and deletes the source
        // against the user's intent. Only the unmodified Reference/Shortcut
        // default defers to the settings-backed decision; a mapped surface
        // always answers concretely.
        return intent switch
        {
            FileDropIntent.Move => true,
            FileDropIntent.Copy => false,
            _ => mapped ? false : null
        };
    }

    private bool HasShortcutDropDestination()
    {
        string destination = TransferIntentEngine.ResolveProspectiveManagedDestination(
            ViewModel.CurrentFolderPath,
            ViewModel.MappedFolderPath,
            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        return !string.IsNullOrWhiteSpace(destination);
    }

    private FileDropIntent? ResolveShortcutIntentOverride(
        FileDropIntent intent)
    {
        return intent == FileDropIntent.Shortcut && HasShortcutDropDestination()
            ? FileDropIntent.Shortcut
            : null;
    }

    private static DataPackageOperation ToDataPackageOperation(
        FileDropIntent intent)
    {
        return intent switch
        {
            FileDropIntent.Copy => DataPackageOperation.Copy,
            FileDropIntent.Move => DataPackageOperation.Move,
            FileDropIntent.Shortcut or FileDropIntent.Reference =>
                DataPackageOperation.Link,
            _ => DataPackageOperation.None
        };
    }

    private string FormatDropCaption(
        FileDropIntent intent,
        string targetName)
    {
        return intent switch
        {
            FileDropIntent.Shortcut =>
                $"{T("Widget.CreateShortcut")} \"{targetName}\"",
            FileDropIntent.Copy => _localizationService.Format(
                "Widget.CopyToFolder",
                targetName),
            FileDropIntent.Move => _localizationService.Format(
                "Widget.MoveToFolder",
                targetName),
            _ => string.Empty
        };
    }

    internal static string GetShortcutDisplayName(string sourcePath, bool isDirectory = false)
    {
        string trimmed = sourcePath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string extension = !isDirectory && ShortcutHelper.IsInternetShortcutPath(trimmed)
            ? ".url"
            : ".lnk";
        string name = isDirectory
            ? Path.GetFileName(trimmed)
            : Path.GetFileNameWithoutExtension(trimmed);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(trimmed);
        }

        return string.IsNullOrWhiteSpace(name)
            ? "Shortcut" + extension
            : name + " - Shortcut" + extension;
    }

    /// <summary>
    /// Materializes pasted virtual Shell namespace items (This PC, Recycle
    /// Bin, ...) as .lnk files in the widget's current folder. This is what
    /// Explorer does when such an item is pasted into a filesystem folder:
    /// there is no item body to move or copy, only a PIDL to store via
    /// IShellLink::SetIDList.
    /// </summary>
    private async Task<int> CreateShortcutsForPastedNamespaceItemsAsync(
        IReadOnlyList<ShellClipboardHelper.ShellNamespaceClipboardItem> items)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        string? destinationFolderPath =
            ViewModel.CurrentFolderPath ?? ViewModel.MappedFolderPath;
        if (string.IsNullOrWhiteSpace(destinationFolderPath))
        {
            // Shortcut creation needs a concrete folder to write into; without
            // one the paste would create nothing and stay silent.
            return 0;
        }

        string destination = Path.GetFullPath(destinationFolderPath);
        Directory.CreateDirectory(destination);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0;
        foreach (ShellClipboardHelper.ShellNamespaceClipboardItem item in items)
        {
            string itemName = FileService.SanitizeFileSystemName(item.DisplayName);
            if (string.IsNullOrWhiteSpace(itemName))
            {
                itemName = "Shortcut";
            }

            string linkPath = FileService.GetAvailablePath(
                Path.Combine(destination, $"{itemName} - Shortcut.lnk"),
                reserved);
            try
            {
                bool createdFromRawPidl = item.RawPidl is { Length: >= 4 } rawPidl &&
                    ShortcutHelper.TryCreateShortcutFromRawPidl(
                        linkPath,
                        rawPidl,
                        T("Widget.CreateShortcut"));
                if (!createdFromRawPidl)
                {
                    ShortcutHelper.CreateShellNamespaceShortcut(
                        linkPath,
                        item.ParsingName,
                        T("Widget.CreateShortcut"));
                }

                reserved.Add(linkPath);
                created++;
            }
            catch (Exception ex)
            {
                App.Log(
                    "[Paste] Namespace shortcut creation failed " +
                    $"target='{item.ParsingName}': {ex.Message}");
            }

            // Keep multi-item pastes responsive; the shortcut backends are
            // apartment-sensitive, so the COM work stays on this thread.
            await Task.Yield();
        }

        if (created > 0 &&
            !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            await ViewModel.RefreshFromConfigAsync(clearIconCacheBeforeHydration: false);
        }

        return created;
    }

    internal static async Task<IReadOnlyList<string>> CreateShortcutFilesAsync(
        IReadOnlyList<DroppedFilePath> droppedFiles,
        string destinationFolderPath,
        string shortcutDescription,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationFolderPath))
        {
            return [];
        }

        string destination = Path.GetFullPath(destinationFolderPath);
        Directory.CreateDirectory(destination);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = new List<string>();
        foreach (DroppedFilePath droppedFile in droppedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (droppedFile.ForceManagedCopy ||
                string.IsNullOrWhiteSpace(droppedFile.Path))
            {
                // A provider-owned temporary path can disappear as soon as
                // the drop completes. The caller imports those paths as copies.
                continue;
            }

            string source = Path.GetFullPath(droppedFile.Path);
            bool isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                continue;
            }

            string linkPath = FileService.GetAvailablePath(
                Path.Combine(destination, GetShortcutDisplayName(source, isDirectory)),
                reserved);
            reserved.Add(linkPath);
            try
            {
                if (isDirectory)
                {
                    ShortcutHelper.CreateOrUpdateFolderShortcut(
                        linkPath,
                        source,
                        shortcutDescription);
                }
                else if (ShortcutHelper.IsShortcutPath(source))
                {
                    DropStagingFileOps.CopyShortcutFilePreservingStreams(
                        source,
                        linkPath);
                    ShortcutHelper.InvalidateStoredMetadataCache(linkPath);
                }
                else
                {
                    // No icon override: the system renders the target file's own
                    // icon with the shortcut overlay, matching Explorer's native
                    // Alt-drag shortcut.
                    DragDropPermissionService.CreateOrUpdateShortcut(
                        linkPath,
                        source,
                        string.Empty,
                        iconPath: null);
                }

                created.Add(linkPath);
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[ShortcutDrop] Shortcut creation failed source='{source}': {ex.Message}");
            }
            // Keep long multi-file drops responsive without moving COM work to
            // an MTA thread (the C# shortcut backend is apartment-sensitive).
            await Task.Yield();
        }

        return created;
    }

    private async Task<IReadOnlyList<string>> CreateShortcutDropAsync(
        IReadOnlyList<DroppedFilePath> droppedFiles,
        string? destinationFolderPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationFolderPath))
        {
            destinationFolderPath = ViewModel.EnsureManagedFolderForDrop();
        }

        if (string.IsNullOrWhiteSpace(destinationFolderPath))
        {
            return [];
        }

        IReadOnlyList<string> created = await CreateShortcutFilesAsync(
            droppedFiles,
            destinationFolderPath,
            T("Widget.CreateShortcut"),
            cancellationToken);
        if (created.Count > 0 &&
            !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            await ViewModel.RefreshFromConfigAsync(clearIconCacheBeforeHydration: false);
        }

        return created;
    }
}
