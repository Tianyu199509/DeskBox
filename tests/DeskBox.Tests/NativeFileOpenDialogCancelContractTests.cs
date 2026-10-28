using DeskBox.Platform;

namespace DeskBox.Tests;

/// <summary>
/// IFileDialog::Show reports a plain user cancel as
/// HRESULT_FROM_WIN32(ERROR_CANCELLED) = 0x800704C7. The constant was once
/// mistyped as 0x800704C5 (1221, ERROR_REQUEST_OUT_OF_SEQUENCE), which
/// turned every user cancel into a logged failure; this pin keeps the
/// look-alike code from ever reading as a cancel again.
/// </summary>
public sealed class NativeFileOpenDialogCancelContractTests
{
    [Fact]
    public void IsUserCancelled_ClassifiesOnlyErrorCancelled()
    {
        const int errorCancelled = unchecked((int)0x800704C7);
        const int requestOutOfSequence = unchecked((int)0x800704C5);

        Assert.True(NativeFileOpenDialog.IsUserCancelled(errorCancelled));
        Assert.False(NativeFileOpenDialog.IsUserCancelled(requestOutOfSequence));
        Assert.False(NativeFileOpenDialog.IsUserCancelled(0));
        Assert.False(NativeFileOpenDialog.IsUserCancelled(unchecked((int)0x8000FFFF)));
    }
}
