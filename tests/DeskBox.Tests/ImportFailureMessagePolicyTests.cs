using System.Runtime.InteropServices;
using DeskBox.Controls.WidgetContents;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Behavioral tests for the import-failure message policy: the coarse
/// transfer-error classification (shell HRESULT and raw Win32 forms) and the
/// exception → localized-key selection, including the held-open-source
/// wording wired for feedback 356.
/// </summary>
public sealed class ImportFailureMessagePolicyTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070020), FileService.FileTransferItemErrorKind.InUse)]
    [InlineData(unchecked((int)0x80070021), FileService.FileTransferItemErrorKind.InUse)]
    [InlineData(32, FileService.FileTransferItemErrorKind.InUse)]
    [InlineData(33, FileService.FileTransferItemErrorKind.InUse)]
    [InlineData(unchecked((int)0x80070005), FileService.FileTransferItemErrorKind.AccessDenied)]
    [InlineData(unchecked((int)0x80070070), FileService.FileTransferItemErrorKind.DiskFull)]
    [InlineData(unchecked((int)0x800700CE), FileService.FileTransferItemErrorKind.PathTooLong)]
    [InlineData(unchecked((int)0x80004005), FileService.FileTransferItemErrorKind.Unknown)]
    public void ClassifyTransferError_NormalizesShellAndRawWin32Forms(
        int hresult,
        FileService.FileTransferItemErrorKind expected)
    {
        var exception = new IOException("transfer failed", hresult);

        Assert.Equal(expected, FileService.ClassifyTransferError(exception));
    }

    [Fact]
    public void ClassifyTransferError_UnwrapsPartialFailureWrapper()
    {
        var wrapped = new FileService.FileTransferPartialFailureException(
            [],
            new IOException("locked", 32));

        Assert.Equal(
            FileService.FileTransferItemErrorKind.InUse,
            FileService.ClassifyTransferError(wrapped));
    }

    [Fact]
    public void SelectOverride_MapsMappedFolderUnavailable()
    {
        var exception = new OrganizerService.MappedFolderUnavailableException(
            @"Z:\storage\widget-a");

        var selection = ImportFailureMessagePolicy.SelectOverride(
            exception,
            requestedCount: 2,
            singleItemPath: null);

        Assert.NotNull(selection);
        Assert.Equal("Widget.Import.MappedFolderUnavailable", selection!.Value.Key);
        Assert.Empty(selection.Value.Args);
    }

    [Fact]
    public void SelectOverride_MapsDestinationOutsideMappedRoot()
    {
        var exception =
            new OrganizerService.DestinationOutsideMappedRootException(
                @"C:\outside",
                @"Z:\storage\widget-a");

        var selection = ImportFailureMessagePolicy.SelectOverride(
            exception,
            requestedCount: 1,
            singleItemPath: null);

        Assert.NotNull(selection);
        Assert.Equal(
            "Widget.Import.DestinationOutsideMappedRoot",
            selection!.Value.Key);
    }

    [Fact]
    public void SelectOverride_InUseSingleItem_UsesPathVariant()
    {
        var exception = new IOException("locked", 32);

        var selection = ImportFailureMessagePolicy.SelectOverride(
            exception,
            requestedCount: 1,
            singleItemPath: @"C:\Users\me\Desktop\app.lnk");

        Assert.NotNull(selection);
        Assert.Equal("Widget.Error.FileInUseWithPath", selection!.Value.Key);
        var argument = Assert.Single(selection.Value.Args);
        Assert.Equal(@"C:\Users\me\Desktop\app.lnk", argument);
    }

    [Fact]
    public void SelectOverride_InUseMultipleItems_UsesCountFreeVariant()
    {
        var exception = new IOException("locked", 32);

        var selection = ImportFailureMessagePolicy.SelectOverride(
            exception,
            requestedCount: 3,
            singleItemPath: null);

        Assert.NotNull(selection);
        Assert.Equal("Widget.Error.FileInUse", selection!.Value.Key);
        Assert.Empty(selection.Value.Args);
    }

    [Fact]
    public void SelectOverride_InUseWithZeroCompletedItems_StillExplains()
    {
        var exception = new FileService.FileTransferPartialFailureException(
            [],
            new IOException("locked", unchecked((int)0x80070020)));

        var selection = ImportFailureMessagePolicy.SelectOverride(
            exception,
            requestedCount: 1,
            singleItemPath: @"C:\Users\me\Desktop\app.lnk");

        Assert.NotNull(selection);
        Assert.Equal("Widget.Error.FileInUseWithPath", selection!.Value.Key);
    }

    [Fact]
    public void SelectOverride_PartialSuccessWithInUseInner_KeepsCountWording()
    {
        var exception = new FileService.FileTransferPartialFailureException(
            [new FileService.FileTransferResult(@"C:\src\a.txt", @"Z:\dst\a.txt")],
            new IOException("locked", 32));

        var selection = ImportFailureMessagePolicy.SelectOverride(
            exception,
            requestedCount: 2,
            singleItemPath: null);

        // Partial results surface the completed/requested counts; one
        // representative HRESULT cannot honestly label the remaining items.
        Assert.Null(selection);
    }

    [Fact]
    public void SelectOverride_UnknownCause_FallsBackToGenericWording()
    {
        var exception = new COMException(
            "strange failure",
            unchecked((int)0x80004005));

        var selection = ImportFailureMessagePolicy.SelectOverride(
            exception,
            requestedCount: 1,
            singleItemPath: @"C:\x\a.txt");

        Assert.Null(selection);
    }
}
