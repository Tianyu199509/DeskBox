namespace DeskBox.Tests;

public sealed class AotStage4D2ContractTests
{
    [Fact]
    public void UnusedFileOperationHelper_IsRemoved()
    {
        string helperPath = TestPaths.FromRepository(
            "src/DeskBox/Helpers/FileOperationHelper.cs");

        Assert.False(
            File.Exists(helperPath),
            "The unreferenced IFileOperation helper must not remain AOT-reachable source.");
    }

    [Fact]
    public void ProductFileOperations_RemainOwnedByFileService()
    {
        string fileService = ReadRepositoryFile("src/DeskBox/Services/FileService.cs");

        Assert.Contains("ExecuteShellMovePlanAsync(", fileService, StringComparison.Ordinal);
        Assert.Contains("DeleteEntryWithShell(", fileService, StringComparison.Ordinal);
        Assert.Contains("MoveEntriesWithShellProgress(", fileService, StringComparison.Ordinal);
        Assert.Contains("SHFileOperation(ref", fileService, StringComparison.Ordinal);

        string projectDirectory = TestPaths.FromRepository("src/DeskBox");
        string separator = Path.DirectorySeparatorChar.ToString();
        string[] remainingReferences = Directory.EnumerateFiles(
                projectDirectory,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(
                "FileOperationHelper",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(projectDirectory, path))
            .ToArray();

        Assert.Empty(remainingReferences);
    }

    [Fact]
    public void AotAudit_RequiresTheStage4D2DeadInteropToStayRemoved()
    {
        string script = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 58", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", script, StringComparison.Ordinal);
        Assert.Contains("stage4D2RemovedSourceFiles", script, StringComparison.Ordinal);
        Assert.Contains("stage4D2UnexpectedExistingSourceFiles", script, StringComparison.Ordinal);
        Assert.Contains("stage4D2FileOperationWarningMessages", script, StringComparison.Ordinal);
        Assert.Contains("src\\DeskBox\\Helpers\\FileOperationHelper.cs", script, StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4D-2 removed source files are present",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4D-2 dead IFileOperation warnings remain",
            script,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
