using System.Diagnostics;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

public enum FolderPickOutcome
{
    Picked,
    Cancelled,
    Failed,
}

public readonly record struct FolderPickResult(
    string? Folder,
    FolderPickOutcome Outcome,
    int HResult);

public static class FolderPickerService
{
    /// <summary>
    /// The folder-pick entrance. This deliberately shows the native common
    /// item dialog instead of the WinAppSDK picker: the WinAppSDK
    /// implementation runs IFileOpenDialog::Show on a background MTA thread
    /// and folds every failure — including the dialog never appearing —
    /// into a silent null, which surfaced to users as "clicking does
    /// nothing" on some machines (intermittently, elevated sessions
    /// included). The native dialog shows on the caller's STA thread and
    /// reports failures as exceptions, so every outcome is logged with its
    /// HRESULT and real failures can surface visibly.
    /// </summary>
    public static FolderPickResult PickFolderWithOutcome(
        IntPtr ownerHwnd,
        string? ownerLabel = null)
    {
        ValidateOwnerWindowHandle(ownerHwnd);

        string owner = ownerLabel ?? "unlabeled";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string? folder = NativeFileOpenDialog.ShowPickFolder(ownerHwnd);
            stopwatch.Stop();
            App.Log($"[FolderPicker] outcome={(folder is null ? "cancel" : "picked")} " +
                $"owner={owner} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new FolderPickResult(
                folder,
                folder is null ? FolderPickOutcome.Cancelled : FolderPickOutcome.Picked,
                0);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            App.Log($"[FolderPicker] outcome=failed hr=0x{ex.HResult:X8} " +
                $"owner={owner} elapsedMs={stopwatch.ElapsedMilliseconds} ex={ex.Message}");
            return new FolderPickResult(null, FolderPickOutcome.Failed, ex.HResult);
        }
    }

    public static Task<string?> PickFolderAsync(IntPtr ownerHwnd, string? ownerLabel = null)
    {
        return Task.FromResult(PickFolderWithOutcome(ownerHwnd, ownerLabel).Folder);
    }

    internal static void ValidateOwnerWindowHandle(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero || !Win32Helper.IsWindow(ownerHwnd))
        {
            throw new ArgumentException(
                "FolderPicker requires a valid owner window handle.",
                nameof(ownerHwnd));
        }
    }
}
