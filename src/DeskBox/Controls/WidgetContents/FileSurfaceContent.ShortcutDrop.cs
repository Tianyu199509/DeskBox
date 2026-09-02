using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private FileDropIntent ResolveSurfaceDropIntent(
        DataPackageView dataView,
        bool forceCopy = false,
        string? destinationFolderPath = null,
        IEnumerable<string>? sourcePathsOverride = null)
    {
        DataPackageOperation requested = dataView.RequestedOperation;
        bool noPreference = requested == DataPackageOperation.None;
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
        // DeskBox's own file drags (same-widget reorder and cross-widget
        // moves) must keep their existing move semantics even though their
        // sources usually live outside the desktop. The desktop-based shortcut
        // default applies to external shell payloads only.
        bool isDeskBoxFileDrag = sourcePaths.Length > 0 &&
            string.Equals(
                TryGetString(
                    dataView.Properties,
                    DeskBoxDragData.InternalFileDragTokenProperty),
                DeskBoxDragData.InternalFileDragToken,
                StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(
                TryGetString(
                    dataView.Properties,
                    DeskBoxDragData.SourceWidgetIdProperty));

        return FileDropIntentPolicy.ResolveMappedTransfer(
            hasMappedFolder: !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath),
            forceCopy,
            controlDown: Win32Helper.IsKeyPressed(VirtualKey.Control),
            shiftDown: Win32Helper.IsKeyPressed(VirtualKey.Shift),
            defaultMove: string.Equals(
                action,
                SettingsService.ManagedDropActionMove,
                StringComparison.Ordinal),
            canCopy: noPreference || requested.HasFlag(DataPackageOperation.Copy),
            canMove: noPreference || requested.HasFlag(DataPackageOperation.Move) ||
                requested.HasFlag(DataPackageOperation.Link),
            altDown: Win32Helper.IsKeyPressed(VirtualKey.Menu),
            followWindows,
            sameVolume,
            // The source may not advertise Link even though an extracted
            // filesystem path is sufficient for DeskBox to create a shortcut.
            canLink: true,
            shortcutOutsideDesktop:
                !isDeskBoxFileDrag &&
                string.Equals(
                    action,
                    SettingsService.ManagedDropActionShortcutOutsideDesktop,
                    StringComparison.Ordinal),
            sourcesOnDesktop: ResolveSourcesOnDesktop(sourcePaths));
    }

    private static bool ResolveSourcesOnDesktop(
        IEnumerable<string> sourcePaths)
    {
        string[] paths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (paths.Length == 0)
        {
            return true;
        }

        (string userDesktop, string publicDesktop) =
            FileService.GetDesktopPaths();
        return FileDropIntentPolicy.AreAllUnderDirectories(
            paths,
            [userDesktop, publicDesktop]);
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
                DragDropPermissionService.CreateOrUpdateShortcut(
                    linkPath,
                    source,
                    string.Empty);
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
