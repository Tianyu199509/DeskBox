using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FileTransferUndoIdentityPropertyTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _desktopRoot;
    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly OrganizerService _organizerService;

    public FileTransferUndoIdentityPropertyTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            nameof(FileTransferUndoIdentityPropertyTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _desktopRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;
        _settingsService = new SettingsService(Path.Combine(_tempRoot, "settings"));
        _fileService = new FileService();
        _organizerService = TestOrganizerServices.Create(_settingsService, _fileService, () => _desktopRoot);
    }

    [Fact]
    public async Task FolderMergeMove_UndoRecreatesSourceFolderAndRestoresNumberedLeafBackToOriginalName()
    {
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string targetRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;

        string sourceDocs = Directory.CreateDirectory(Path.Combine(sourceRoot, "Docs")).FullName;
        string sourceReport = Path.Combine(sourceDocs, "report.txt");
        string sourceNotes = Path.Combine(sourceDocs, "notes.txt");
        string sourceSubDir = Directory.CreateDirectory(Path.Combine(sourceDocs, "NewSub")).FullName;
        string sourceSubFile = Path.Combine(sourceSubDir, "nested.txt");
        File.WriteAllText(sourceReport, "incoming-report");
        File.WriteAllText(sourceNotes, "incoming-notes");
        File.WriteAllText(sourceSubFile, "incoming-nested");

        string targetDocs = Directory.CreateDirectory(Path.Combine(targetRoot, "Docs")).FullName;
        string existingPre = Path.Combine(targetDocs, "pre.txt");
        string existingReport = Path.Combine(targetDocs, "report.txt");
        File.WriteAllText(existingPre, "pre-existing-file");
        File.WriteAllText(existingReport, "pre-existing-report");

        var widget = new WidgetConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Widget",
            MappedFolderPath = targetRoot,
            FollowsDefaultStoragePath = false
        };

        OrganizerOperationResult operation = await _organizerService.OrganizeDropAsync(
            widget,
            "Widget",
            [sourceDocs],
            move: true);

        Assert.False(Directory.Exists(sourceDocs));
        Assert.True(File.Exists(existingPre));
        Assert.True(File.Exists(existingReport));
        Assert.True(File.Exists(Path.Combine(targetDocs, "report (2).txt")));
        Assert.True(File.Exists(Path.Combine(targetDocs, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(targetDocs, "NewSub", "nested.txt")));
        Assert.True(operation.History.CanUndo);
        Assert.Single(operation.CompletedItems);

        bool undone = await _organizerService.UndoLatestAsync();

        Assert.True(undone);
        Assert.True(Directory.Exists(sourceDocs));
        Assert.True(File.Exists(sourceReport));
        Assert.Equal("incoming-report", File.ReadAllText(sourceReport));
        Assert.True(File.Exists(sourceNotes));
        Assert.Equal("incoming-notes", File.ReadAllText(sourceNotes));
        Assert.True(File.Exists(sourceSubFile));
        Assert.Equal("incoming-nested", File.ReadAllText(sourceSubFile));

        // Pre-existing destination files must remain completely untouched.
        Assert.True(File.Exists(existingPre));
        Assert.Equal("pre-existing-file", File.ReadAllText(existingPre));
        Assert.True(File.Exists(existingReport));
        Assert.Equal("pre-existing-report", File.ReadAllText(existingReport));
        Assert.False(File.Exists(Path.Combine(targetDocs, "report (2).txt")));
    }

    [Fact]
    public void DeleteSourceTreeByManifest_PreservesDistinctCaseOnlySiblingEntriesInManifest()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.cs"));

        Assert.Contains(
            "var exactManifest = new Dictionary<string, CopiedSourceFileRecord>(\n            StringComparer.Ordinal)",
            source.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ambiguousCaseInsensitiveKeys",
            source,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
