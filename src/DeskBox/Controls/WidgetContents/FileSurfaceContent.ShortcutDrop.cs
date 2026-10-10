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
        IEnumerable<string>? sourcePathsOverride = null)
    {
        DataPackageOperation supported =
            DeskBoxDragData.GetFileTransferOperations(dataView, allowedOperations);
        bool noOperationMetadata =
            supported == DataPackageOperation.None;
        string destination = destinationFolderPath ??
            ViewModel.CurrentFolderPath ??
            ViewModel.MappedFolderPath ??
            string.Empty;
        string[] sourcePaths = (sourcePathsOverride ?? GetPackagePaths(dataView))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool sameVolume = sourcePaths.Length == 0 ||
            (destination.Length > 0 &&
             FileDropIntentPolicy.AreAllOnSameVolume(sourcePaths, destination));
        string action = _settingsService.Settings.ManagedDropAction;
        bool followWindows = string.Equals(
            action,
            SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);

        return FileDropIntentPolicy.ResolveMappedTransfer(
            hasMappedFolder: !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath),
            forceCopy,
            controlDown: Win32Helper.IsKeyPressed(VirtualKey.Control),
            shiftDown: Win32Helper.IsKeyPressed(VirtualKey.Shift),
            defaultMove: string.Equals(
                action,
                SettingsService.ManagedDropActionMove,
                StringComparison.Ordinal),
            canCopy: noOperationMetadata ||
                supported.HasFlag(DataPackageOperation.Copy),
            // A Link-only source never authorized relocation: treating Link
            // as move capability would let a managed move delete a file the
            // drag source did not sanction for deletion.
            canMove: noOperationMetadata ||
                supported.HasFlag(DataPackageOperation.Move),
            altDown: Win32Helper.IsKeyPressed(VirtualKey.Menu),
            followWindows,
            sameVolume,
            // The source may not advertise Link even though an extracted
            // filesystem path is sufficient for DeskBox to create a shortcut.
            canLink: true);
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
        // Shortcut creation needs a concrete folder to write into. Without
        // one the shortcut leg would create nothing and stay silent, so an
        // unmapped widget keeps the settings-backed import instead.
        return (ViewModel.CurrentFolderPath ?? ViewModel.MappedFolderPath) is
            { Length: > 0 };
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

    private static string GetShortcutDisplayName(string sourcePath)
    {
        string trimmed = sourcePath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string name = Path.GetFileNameWithoutExtension(trimmed);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(trimmed);
        }

        return string.IsNullOrWhiteSpace(name)
            ? "Shortcut.lnk"
            : name + " - Shortcut.lnk";
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
                ShortcutHelper.CreateShellNamespaceShortcut(
                    linkPath,
                    item.ParsingName,
                    T("Widget.CreateShortcut"));
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
            await ViewModel.RefreshFromConfigAsync();
        }

        return created;
    }

    private async Task<IReadOnlyList<string>> CreateShortcutFilesAsync(
        IReadOnlyList<DroppedFilePath> droppedFiles,
        string destinationFolderPath,
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
            if (!File.Exists(source) && !Directory.Exists(source))
            {
                continue;
            }

            string linkPath = FileService.GetAvailablePath(
                Path.Combine(destination, GetShortcutDisplayName(source)),
                reserved);
            if (Directory.Exists(source))
            {
                ShortcutHelper.CreateOrUpdateFolderShortcut(
                    linkPath,
                    source,
                    T("Widget.CreateShortcut"));
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
            return [];
        }

        IReadOnlyList<string> created = await CreateShortcutFilesAsync(
            droppedFiles,
            destinationFolderPath,
            cancellationToken);
        if (created.Count > 0 &&
            !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            await ViewModel.RefreshFromConfigAsync();
        }

        return created;
    }
}
