namespace DeskBox.Tests;

public sealed class AotStage4D3AContractTests
{
    [Fact]
    public void NativeDropTarget_DataReadSideDoesNotUseBuiltInComRcws()
    {
        string source = ReadRepositoryFile("src/DeskBox/Helpers/NativeDropTarget.cs");

        Assert.DoesNotContain("COMIDataObject", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal.GetObjectForIUnknown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal.ReleaseComObject", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(IStream)", source, StringComparison.Ordinal);
        Assert.Contains("NativeOleDataObject", source, StringComparison.Ordinal);
        Assert.Contains("NativeComStreamReader.CopyTo", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDropComDataReader_UsesExplicitBorrowedPointerSlots()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropComDataReader.cs");

        Assert.Contains("GetDataVtableSlot = 3", source, StringComparison.Ordinal);
        Assert.Contains("QueryGetDataVtableSlot = 5", source, StringComparison.Ordinal);
        Assert.Contains("SetDataVtableSlot = 7", source, StringComparison.Ordinal);
        Assert.Contains("ReadVtableSlot = 3", source, StringComparison.Ordinal);
        Assert.Contains("delegate* unmanaged[Stdcall]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal.GetObjectForIUnknown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal.GetIUnknownForObject", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal.ReleaseComObject", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDropDataReader_RemainsIndependentFromTheRegistrationCcw()
    {
        string readerSource = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropComDataReader.cs");
        string registrationSource = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropTargetComInterop.cs");

        Assert.DoesNotContain("INativeDropTarget", readerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterDragDrop", readerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeOleDataObject", registrationSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeComStreamReader", registrationSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_RequiresTheStage4D3ADataReaderBoundary()
    {
        string script = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 61", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", script, StringComparison.Ordinal);
        Assert.Contains("stage4D3ALegacyRcwPatterns", script, StringComparison.Ordinal);
        Assert.Contains("stage4D3ALegacyRcwSourceMatches", script, StringComparison.Ordinal);
        Assert.Contains("stage4D3ADataReaderWarningMessages", script, StringComparison.Ordinal);
        Assert.Contains("stage4D3AUnexpectedDropTargetWarningMessages", script, StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4D-3A legacy data-object RCW patterns remain",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4D-3A data reader produced AOT warnings",
            script,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
