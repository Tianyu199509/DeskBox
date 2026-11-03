using System.Text.RegularExpressions;

namespace DeskBox.Tests;

/// <summary>
/// Pins the file-operation dialog delegation contract: interactive delete and
/// move operations let the Windows Shell own confirmation and conflict dialogs
/// (issue #86), while the historical shortcut-loss fix keeps internal drags
/// from ever returning Move to the Shell.
/// </summary>
public sealed class ShellDialogDelegationContractTests
{
    [Fact]
    public void ShellDelete_NeverSuppressesConfirmationForInteractiveCallers()
    {
        string fileService = Read("src/DeskBox/Services/FileService.cs");

        // The old single-shot silent combination must stay gone.
        Assert.DoesNotContain(
            "FofAllowUndo | FofNoConfirmation",
            fileService,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ShellDeleteRecycleOnDelete | ShellDeleteNoConfirmation",
            fileService,
            StringComparison.Ordinal);

        // Confirmation suppression is allowed only inside the headless branch
        // of the shared delete helper; the interactive branch omits the flag.
        const string helperStart =
            "    private static IReadOnlySet<string> DeleteEntriesWithShell(";
        int helperIndex = fileService.IndexOf(
            helperStart,
            StringComparison.Ordinal);
        int helperEnd = fileService.IndexOf(
            "    public async Task RelocateDirectoryAsync(",
            helperIndex + helperStart.Length,
            StringComparison.Ordinal);
        Assert.True(helperIndex >= 0 && helperEnd > helperIndex,
            "DeleteEntriesWithShell helper was removed.");
        string helper = fileService[helperIndex..helperEnd];
        Assert.Contains("else", helper, StringComparison.Ordinal);
        Assert.Contains("ShellDeleteNoConfirmation", helper, StringComparison.Ordinal);
        Assert.Contains("ShellDeleteNoErrorUi", helper, StringComparison.Ordinal);
        Assert.Contains("ShellDeleteWantNukeWarning", helper, StringComparison.Ordinal);
        Assert.Matches(
            @"bool interactive = ownerHandle != IntPtr\.Zero;",
            helper);
    }

    [Fact]
    public void ShellMove_KeepsTheNameConflictDialogAvailable()
    {
        string shellTransfer = Read("src/DeskBox/Services/FileService.ShellTransfer.cs");

        // The modern engine's operation flags must skip the confirmation
        // suppression so a plan/execute race surfaces the native
        // Replace/Keep-both dialog instead of a silent overwrite. (The
        // legacy FoMove flag lists were retired with the SHFileOperation
        // bridge, 2026-10-13.)
        int flags = shellTransfer.IndexOf(
            "fileOperation.SetOperationFlags(",
            StringComparison.Ordinal);
        Assert.True(flags >= 0, "The modern engine no longer sets operation flags.");
        int flagsEnd = shellTransfer.IndexOf(");", flags, StringComparison.Ordinal);
        string flagList = shellTransfer[flags..flagsEnd];
        Assert.Contains(
            "ShellFileOperationNoConfirmMakeDirectory",
            flagList,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NoConfirmation",
            flagList,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PermanentDelete_DelegatesConfirmationToTheShell()
    {
        string fileService = Read("src/DeskBox/Services/FileService.cs");

        Assert.Contains(
            "DeleteEntryWithShell(normalizedPath, ownerHandle, allowUndo: false)",
            fileService,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveMultiSelection_UsesOneShellBatchCall()
    {
        string viewModel = Read("src/DeskBox/ViewModels/WidgetViewModel.Operations.cs");
        string fileService = Read("src/DeskBox/Services/FileService.cs");
        string shellTransfer = Read("src/DeskBox/Services/FileService.ShellTransfer.cs");

        Assert.Contains(
            "DeleteEntriesWithShellAsync",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExecuteShellDeleteOnStaThread(",
            fileService,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileOperation.DeleteItem(item, IntPtr.Zero)",
            shellTransfer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetDelete_PassesTheOwnerHandleInsteadOfSelfMadeDialogs()
    {
        string surface = Read(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string viewModel = Read("src/DeskBox/ViewModels/WidgetViewModel.Operations.cs");

        Assert.Contains("ownerHandle: _hostWindowHandle", surface, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConfirmPermanentDeleteAsync",
            surface,
            StringComparison.Ordinal);
        Assert.Contains("IntPtr ownerHandle = default", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalDrives_NeverReturnMoveToTheShellDataObject()
    {
        // Regression guard for the historical shortcut-loss incident and
        // external OLE non-optimized move contract: DeskBox performs the move
        // itself and must never ask the Shell or drop source to clean the
        // source path a second time.
        string surface = Read(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");

        Assert.Matches(
            @"if \(isDeskBoxFileDrag\)\s*\{\s*return DataPackageOperation\.None;\s*\}\s*return canReportCopy\s*\?\s*DataPackageOperation\.Copy\s*:\s*DataPackageOperation\.None;",
            surface);
        Assert.Equal(
            Windows.ApplicationModel.DataTransfer.DataPackageOperation.None,
            DeskBox.Controls.WidgetContents.FileSurfaceContent.ResolveSafeDropCompletionOperation(
                Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move,
                isDeskBoxFileDrag: true,
                requestedMoveCount: 1,
                completedMoveCount: 1,
                canReportCopy: true));
        Assert.Equal(
            Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            DeskBox.Controls.WidgetContents.FileSurfaceContent.ResolveSafeDropCompletionOperation(
                Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move,
                isDeskBoxFileDrag: false,
                requestedMoveCount: 1,
                completedMoveCount: 1,
                canReportCopy: true));
    }

    [Fact]
    public void ModernShellEngine_KeepsUndoOwnershipInApp()
    {
        // Undo-ownership decision (2026-10-13): DeskBox owns undo through its
        // own receipts and history entries. Shell transfers must not add
        // Explorer undo records (FOF_ALLOWUNDO / FOFX_ADDUNDORECORD) for
        // moves and copies — a shell-side Ctrl+Z would race the in-app undo
        // and diverge state.
        string shellTransfer = Read("src/DeskBox/Services/FileService.ShellTransfer.cs");
        Assert.Contains(
            "Deliberately no FOF_ALLOWUNDO / FOFX_ADDUNDORECORD",
            shellTransfer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("0x20000000", shellTransfer, StringComparison.Ordinal);
        Assert.DoesNotContain("FofAllowUndo", shellTransfer, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowUndoFlag_StaysScopedToTheRecycleBinDeleteChannel()
    {
        string fileService = Read("src/DeskBox/Services/FileService.cs");

        // Definition + the single delete-channel usage; a third occurrence
        // means someone wired recycle-on-delete into another operation family.
        Assert.Equal(
            2,
            CountOrdinalOccurrences(
                fileService,
                "ShellDeleteRecycleOnDelete"));
        Assert.Equal(
            0,
            CountOrdinalOccurrences(
                fileService,
                "FofAllowUndo"));
    }

    private static int CountOrdinalOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
