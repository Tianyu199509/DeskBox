using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.Windows.Storage.Pickers;

namespace DeskBox.Services;

public static class FileOpenPickerService
{
    private static int s_pickInFlight;

    /// <summary>
    /// The add-file entrance. This deliberately uses the native common item
    /// dialog instead of the WinAppSDK picker: the picker wrapper cannot
    /// disable the shell dialog's follow-shortcuts default, so picking a
    /// .lnk used to yield the target's path and the import moved the target
    /// entity itself (issue 458). The native dialog pins
    /// FOS_NODEREFERENCELINKS, so a selected shortcut is added as the .lnk.
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(
        IntPtr ownerHwnd,
        string? suggestedFolder = null)
    {
        ValidateOwnerWindowHandle(ownerHwnd);

        if (!string.IsNullOrWhiteSpace(suggestedFolder))
        {
            string normalizedFolder = Path.GetFullPath(suggestedFolder);
            if (!Directory.Exists(normalizedFolder))
            {
                throw new DirectoryNotFoundException(
                    $"The suggested file-picker folder does not exist: '{normalizedFolder}'.");
            }

            suggestedFolder = normalizedFolder;
        }

        if (Interlocked.CompareExchange(ref s_pickInFlight, 1, 0) != 0)
        {
            _ = Task.Run(NativeFileOpenDialog.TryActivateActiveFileDialog);
            App.Log("[FileOpenPicker] outcome=busy elapsedMs=0");
            return [];
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Host modal IFileOpenDialog::Show out-of-process in the single-STA
            // native proxy so Shell / Quick Access ThreadingModel=Single COM
            // components can never cross-thread marshal back to WinUI's Main STA
            // and cause AppHangB1 / Cross-thread deadlocks (#1267).
            IReadOnlyList<string> paths = await NativeFileOpenDialog.PickFilesOutOfProcessAsync(
                ownerHwnd,
                suggestedFolder)
                .ConfigureAwait(false);
            return paths
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            // elapsedMs separates an environment-killed dialog (a
            // cancel-looking failure at near-zero ms) from a human-paced one.
            App.Log($"[FileOpenPicker] Failed to pick files (elapsedMs={stopwatch.ElapsedMilliseconds}): {ex}");
            return [];
        }
        finally
        {
            Volatile.Write(ref s_pickInFlight, 0);
        }
    }

    public static async Task<string?> PickSingleFileAsync(
        IntPtr ownerHwnd,
        IReadOnlyList<string> fileTypeFilters,
        PickerLocationId suggestedStartLocation)
    {
        ValidateOwnerWindowHandle(ownerHwnd);
        IReadOnlyList<string> picked = await PickFilteredFilesOutOfProcessCoreAsync(
            ownerHwnd,
            fileTypeFilters,
            suggestedStartLocation,
            allowMultiSelect: false);
        return picked.Count > 0 ? picked[0] : null;
    }

    public static async Task<IReadOnlyList<string>> PickMultipleFilesAsync(
        IntPtr ownerHwnd,
        IReadOnlyList<string> fileTypeFilters,
        PickerLocationId suggestedStartLocation)
    {
        ValidateOwnerWindowHandle(ownerHwnd);
        return await PickFilteredFilesOutOfProcessCoreAsync(
            ownerHwnd,
            fileTypeFilters,
            suggestedStartLocation,
            allowMultiSelect: true);
    }

    private static async Task<IReadOnlyList<string>> PickFilteredFilesOutOfProcessCoreAsync(
        IntPtr ownerHwnd,
        IReadOnlyList<string>? fileTypeFilters,
        PickerLocationId suggestedStartLocation,
        bool allowMultiSelect)
    {
        if (Interlocked.CompareExchange(ref s_pickInFlight, 1, 0) != 0)
        {
            _ = Task.Run(NativeFileOpenDialog.TryActivateActiveFileDialog);
            App.Log("[FileOpenPicker] outcome=busy elapsedMs=0");
            return [];
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string? startFolder = ResolveSuggestedStartFolder(suggestedStartLocation);
            IReadOnlyList<string> paths = await NativeFileOpenDialog.PickFilesOutOfProcessAsync(
                ownerHwnd,
                startFolder,
                fileTypeFilters,
                allowMultiSelect: allowMultiSelect,
                noDereferenceLinks: false)
                .ConfigureAwait(false);
            return paths
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            App.Log($"[FileOpenPicker] Failed to pick filtered files (elapsedMs={stopwatch.ElapsedMilliseconds}): {ex}");
            return [];
        }
        finally
        {
            Volatile.Write(ref s_pickInFlight, 0);
        }
    }

    internal static string? ResolveSuggestedStartFolder(PickerLocationId suggestedStartLocation)
    {
        const string thisPcShellParsingName = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        string? candidate = suggestedStartLocation switch
        {
            PickerLocationId.ComputerFolder => thisPcShellParsingName,
            PickerLocationId.DocumentsLibrary => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            PickerLocationId.PicturesLibrary => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            PickerLocationId.MusicLibrary => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            PickerLocationId.VideosLibrary => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            PickerLocationId.Downloads => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads"),
            _ => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (string.Equals(candidate, thisPcShellParsingName, StringComparison.Ordinal))
        {
            return candidate;
        }

        return Directory.Exists(candidate) ? candidate : null;
    }

    internal static void ValidateOwnerWindowHandle(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero || !Win32Helper.IsWindow(ownerHwnd))
        {
            throw new ArgumentException(
                "FileOpenPicker requires a valid owner window handle.",
                nameof(ownerHwnd));
        }
    }
}
