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
    private static int s_pickInFlight;

    internal static bool IsPickInFlight => Volatile.Read(ref s_pickInFlight) != 0;

    /// <summary>
    /// The folder-pick entrance. This deliberately shows the native common
    /// item dialog on a dedicated background STA thread with a same-thread
    /// owner window instead of blocking the main XAML UI thread or using the
    /// WinAppSDK MTA picker:
    /// - WinAppSDK runs IFileOpenDialog::Show on an MTA worker and folds
    ///   failures into a silent null;
    /// - running Show directly on the main UI thread freezes all of DeskBox
    ///   whenever Shell/Quick Access/NAS/sleeping drives stall inside the
    ///   dialog and allows non-owner windows to re-enter nested modal loops.
    /// Isolating the dialog on its own STA thread keeps the UI responsive,
    /// avoids cross-thread HWND message coupling, enforces single-flight
    /// dialog activation, and logs every outcome with its HRESULT.
    /// </summary>
    public static Task<FolderPickResult> PickFolderWithOutcomeAsync(
        IntPtr ownerHwnd,
        string? ownerLabel = null)
    {
        return PickFolderWithOutcomeCoreAsync(
            ownerHwnd,
            ownerLabel,
            static referenceOwnerHwnd => NativeFileOpenDialog.RunOnDedicatedStaAsync(
                referenceOwnerHwnd,
                static staOwnerHwnd => NativeFileOpenDialog.ShowPickFolder(staOwnerHwnd)));
    }

    internal static async Task<FolderPickResult> PickFolderWithOutcomeCoreAsync(
        IntPtr ownerHwnd,
        string? ownerLabel,
        Func<IntPtr, Task<string?>> showPickerAsync)
    {
        ValidateOwnerWindowHandle(ownerHwnd);
        ArgumentNullException.ThrowIfNull(showPickerAsync);

        string owner = ownerLabel ?? "unlabeled";
        if (Interlocked.CompareExchange(ref s_pickInFlight, 1, 0) != 0)
        {
            _ = Task.Run(NativeFileOpenDialog.TryActivateActiveFolderDialog);
            App.Log($"[FolderPicker] outcome=busy owner={owner} elapsedMs=0");
            return new FolderPickResult(null, FolderPickOutcome.Cancelled, 0);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            string? folder = await showPickerAsync(ownerHwnd).ConfigureAwait(false);
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
        finally
        {
            Interlocked.Exchange(ref s_pickInFlight, 0);
        }
    }

    public static async Task<string?> PickFolderAsync(IntPtr ownerHwnd, string? ownerLabel = null)
    {
        FolderPickResult result = await PickFolderWithOutcomeAsync(ownerHwnd, ownerLabel);
        return result.Folder;
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
