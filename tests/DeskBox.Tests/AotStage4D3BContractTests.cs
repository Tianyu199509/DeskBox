namespace DeskBox.Tests;

public sealed class AotStage4D3BContractTests
{
    [Fact]
    public void DropTargetComBoundary_UsesSourceGeneratedIUnknownCcw()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropTargetComInterop.cs");

        Assert.Contains("[GeneratedComInterface", source, StringComparison.Ordinal);
        Assert.Contains(
            "Options = ComInterfaceOptions.ManagedObjectWrapper",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Guid(\"00000122-0000-0000-C000-000000000046\")]",
            source,
            StringComparison.Ordinal);
        Assert.Contains("partial interface INativeDropTarget", source, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(source, "[PreserveSig]"));
        Assert.Contains("[GeneratedComClass]", source, StringComparison.Ordinal);
        Assert.Contains(
            "partial class NativeDropTargetComObject : INativeDropTarget",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DropTargetRegistration_UsesAnExplicitPointerAndBalancesItsLocalReference()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropTargetComInterop.cs");

        Assert.Contains("[LibraryImport(\"ole32.dll\")]", source, StringComparison.Ordinal);
        Assert.Contains(
            "RegisterDragDrop(nint hwnd, nint dropTarget)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("RevokeDragDrop(nint hwnd)", source, StringComparison.Ordinal);
        Assert.Contains(
            "ComInterfaceMarshaller<INativeDropTarget>.ConvertToUnmanaged",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ComInterfaceMarshaller<INativeDropTarget>.Free",
            source,
            StringComparison.Ordinal);
        Assert.Contains("finally", source, StringComparison.Ordinal);
        Assert.Contains("Marshal.ThrowExceptionForHR", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[DllImport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[ComImport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[ComVisible", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal.GetIUnknownForObject", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDropTarget_DelegatesRegistrationWithoutBuiltInComMarshalling()
    {
        string source = ReadRepositoryFile("src/DeskBox/Helpers/NativeDropTarget.cs");

        Assert.DoesNotContain("[ComImport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[ComVisible", source, StringComparison.Ordinal);
        Assert.DoesNotContain("interface IDropTarget", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RegisterDragDrop(IntPtr hwnd, IDropTarget dropTarget)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_comObject = new NativeDropTargetComObject(this)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDropTargetComInterop.Register(_hwnd, _comObject)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDropTargetComInterop.Revoke(_hwnd)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_RequiresTheStage4D3BRegistrationBoundary()
    {
        string script = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 59", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", script, StringComparison.Ordinal);
        Assert.Contains("stage4D3BSourceFiles", script, StringComparison.Ordinal);
        Assert.Contains("stage4D3BLegacyRegistrationPatterns", script, StringComparison.Ordinal);
        Assert.Contains("stage4D3BLegacyRegistrationSourceMatches", script, StringComparison.Ordinal);
        Assert.Contains("stage4D3BWarningMessages", script, StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4D-3B legacy drop-target registration patterns remain",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4D-3B drop-target boundary produced AOT warnings",
            script,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
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

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
