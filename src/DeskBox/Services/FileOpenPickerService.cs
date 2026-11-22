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
            // Isolate modal IFileOpenDialog::Show on a dedicated STA thread
            // (matching FolderPickerService) so third-party shell extensions
            // never re-enter the WinUI 3 Mouse-in-Pointer UI thread.
            IReadOnlyList<string> paths = await NativeFileOpenDialog.RunOnDedicatedStaAsync(
                ownerHwnd,
                staOwnerHwnd => NativeFileOpenDialog.ShowPickFiles(staOwnerHwnd, suggestedFolder),
                NativeFileOpenDialog.DedicatedStaDialogKind.File)
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

        var picker = BuildPicker(ownerHwnd, fileTypeFilters, suggestedStartLocation);
        PickFileResult? file = await picker.PickSingleFileAsync();
        return string.IsNullOrWhiteSpace(file?.Path) ? null : file.Path;
    }

    public static async Task<IReadOnlyList<string>> PickMultipleFilesAsync(
        IntPtr ownerHwnd,
        IReadOnlyList<string> fileTypeFilters,
        PickerLocationId suggestedStartLocation)
    {
        ValidateOwnerWindowHandle(ownerHwnd);

        var picker = BuildPicker(ownerHwnd, fileTypeFilters, suggestedStartLocation);
        IReadOnlyList<PickFileResult> files =
            await picker.PickMultipleFilesAsync();
        return files
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    private static FileOpenPicker BuildPicker(
        IntPtr ownerHwnd,
        IReadOnlyList<string>? fileTypeFilters,
        PickerLocationId? suggestedStartLocation)
    {
        var ownerWindowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
            ownerHwnd);
        var picker = new FileOpenPicker(ownerWindowId)
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        if (suggestedStartLocation is { } startLocation)
        {
            picker.SuggestedStartLocation = startLocation;
        }

        if (fileTypeFilters is not null)
        {
            foreach (string filter in fileTypeFilters)
            {
                picker.FileTypeFilter.Add(filter);
            }
        }

        return picker;
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
