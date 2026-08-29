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

        // Confirmation suppression is allowed only inside the headless branch
        // of the shared delete helper; the interactive branch omits the flag.
        const string helperStart =
            "    private static IReadOnlySet<string> DeleteEntriesWithShell(";
        int helperIndex = fileService.IndexOf(
            helperStart,
            StringComparison.Ordinal);
        int helperEnd = fileService.IndexOf(
            "    private static void MoveEntriesWithShellProgress(",
            helperIndex + helperStart.Length,
            StringComparison.Ordinal);
        Assert.True(helperIndex >= 0 && helperEnd > helperIndex,
            "DeleteEntriesWithShell helper was removed.");
        string helper = fileService[helperIndex..helperEnd];
        Assert.Contains("if (!interactive)", helper, StringComparison.Ordinal);
        Assert.Contains("flags |= FofNoConfirmation;", helper, StringComparison.Ordinal);
        Assert.Matches(
            @"bool interactive = ownerHandle != IntPtr\.Zero;",
            helper);
    }

    [Fact]
    public void ShellMove_KeepsTheNameConflictDialogAvailable()
    {
        string fileService = Read("src/DeskBox/Services/FileService.cs");

        // Every FoMove flag list must skip FofNoConfirmation so a plan/execute
        // race surfaces the native Replace/Keep-both dialog instead of a
        // silent overwrite (Native AOT channel).
        foreach (Match move in Regex.Matches(
                     fileService,
                     @"Function = FoMove,.*?SHFileOperation",
                     RegexOptions.Singleline))
        {
            Assert.False(
                move.Value.Contains("FofNoConfirmation", StringComparison.Ordinal),
                "A FoMove operation suppresses the Shell conflict dialog again.");
        }
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

        Assert.Contains(
            "DeleteEntriesWithShellAsync",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "string.Join('\\0', existingPaths) + \"\\0\\0\"",
            fileService,
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
        // Regression guard for the historical shortcut-loss incident: DeskBox
        // performs the move itself and must never ask the Shell to clean the
        // source of an internal drag a second time.
        string surface = Read(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");

        Assert.Matches(
            @"return isDeskBoxFileDrag\s*\n\s*\?\s*DataPackageOperation\.None\s*\n\s*:\s*DataPackageOperation\.Move;",
            surface);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
