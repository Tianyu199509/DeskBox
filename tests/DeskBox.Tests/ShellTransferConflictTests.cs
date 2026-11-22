using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ShellTransferConflictTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(ShellTransferConflictTests), Guid.NewGuid().ToString("N"));

    private string FileAt(string relative, string contents)
    {
        string path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void InteractivePlan_PreservesExistingAndInBatchNameCollisions_BackgroundKeepsBoth()
    {
        string first = FileAt("source-a/report.txt", "first");
        string second = FileAt("source-b/report.txt", "second");
        string target = FileAt("target/report.txt", "original");
        string folder = Path.GetDirectoryName(target)!;

        var ask = OrganizerService.CreateTransferPlans(folder, [first, second], FileService.FileTransferConflictPolicy.AskUser);
        Assert.Equal(2, ask.Count);
        Assert.All(ask, plan => Assert.Equal(target, plan.DestinationPath));
        var keep = OrganizerService.CreateTransferPlans(folder, [first, second]);
        Assert.Equal(Path.Combine(folder, "report (2).txt"), keep[0].DestinationPath);
        Assert.Equal(Path.Combine(folder, "report (3).txt"), keep[1].DestinationPath);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Empty(OrganizerService.CreateTransferPlans(folder, [target], FileService.FileTransferConflictPolicy.AskUser));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skip_IsAccountedForWithoutInventingASuccessFromSameSizedFiles(bool move)
    {
        string source = FileAt("source/a.txt", "aaaa");
        string target = FileAt("target/a.txt", "bbbb");
        FileService.TransferOperation[] operations = [new(source, target)];
        var sink = new FileService.ShellFileOperationProgressSink(operations, CancellationToken.None, null, move);
        sink.CapturePreExistingDestinationState();
        sink.RecordTransferResult(source, target, 0x00270005);
        var outcome = sink.BuildCompletedResults();

        Assert.Empty(outcome.Results);
        Assert.Equal(0, outcome.UnaccountedOperationCount);
        Assert.Equal([source], sink.SkippedSourcePaths);
        Assert.Empty(FileService.ReconcileShellTransferResults(operations, outcome.Results, move));
        Assert.Equal("aaaa", File.ReadAllText(source));
        Assert.Equal("bbbb", File.ReadAllText(target));
    }

    [Fact]
    public void MissingReceipt_RemainsUnaccountedEvenWhenDestinationLooksComplete()
    {
        string source = FileAt("source/a.txt", "same");
        string target = FileAt("target/a.txt", "same");
        FileService.TransferOperation[] operations = [new(source, target)];
        var sink = new FileService.ShellFileOperationProgressSink(operations, CancellationToken.None, null, false);
        Assert.Equal(1, sink.BuildCompletedResults().UnaccountedOperationCount);
        Assert.Empty(FileService.ReconcileShellTransferResults(operations, [], false));
    }

    [Fact]
    public void KeepBoth_UsesRealizedPathAndDoesNotClaimAnOverwrite()
    {
        string source = FileAt("source/a.txt", "new");
        string target = FileAt("target/a.txt", "old");
        string renamed = Path.Combine(Path.GetDirectoryName(target)!, "a (2).txt");
        FileService.TransferOperation[] operations = [new(source, target)];
        var sink = new FileService.ShellFileOperationProgressSink(operations, CancellationToken.None, null, true);
        sink.CapturePreExistingDestinationState();
        File.Move(source, renamed);
        sink.RecordTransferResult(source, renamed, 0);

        var result = Assert.Single(FileService.ReconcileShellTransferResults(operations, sink.BuildCompletedResults().Results, true));
        Assert.Equal(renamed, result.DestinationPath);
        Assert.False(result.HadOverwrite);
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Equal("new", File.ReadAllText(result.DestinationPath));
    }

    [Fact]
    public void Replace_RecordsOverwriteAndDisablesUnsafeUndo()
    {
        string source = FileAt("source/a.txt", "new");
        string target = FileAt("target/a.txt", "old");
        FileService.TransferOperation[] operations = [new(source, target)];
        var sink = new FileService.ShellFileOperationProgressSink(operations, CancellationToken.None, null, true);
        sink.CapturePreExistingDestinationState();
        File.Move(source, target, overwrite: true);
        sink.RecordTransferResult(source, target, 0);
        var result = Assert.Single(sink.BuildCompletedResults().Results);
        Assert.True(result.HadOverwrite);
        var undo = OrganizerService.ExpandResultsForSafeFolderMergeUndo([result], new(StringComparer.OrdinalIgnoreCase));
        Assert.False(undo.AllMergesUndoable);
        Assert.Empty(undo.UndoableResults);
    }

    [Fact]
    public void TwoSourcesWithSameName_ReplacementWithinBatchAlsoDisablesUndo()
    {
        string first = FileAt("source-a/a.txt", "first");
        string second = FileAt("source-b/a.txt", "second");
        string folder = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        string target = Path.Combine(folder, "a.txt");
        FileService.TransferOperation[] operations = [new(first, target), new(second, target)];
        var sink = new FileService.ShellFileOperationProgressSink(operations, CancellationToken.None, null, true);
        sink.CapturePreExistingDestinationState();
        File.Move(first, target);
        sink.RecordTransferResult(first, target, 0);
        File.Move(second, target, overwrite: true);
        sink.RecordTransferResult(second, target, 0);
        var result = sink.BuildCompletedResults();
        Assert.Equal(2, result.Results.Count);
        Assert.True(result.Results[1].HadOverwrite);
        Assert.False(OrganizerService.ExpandResultsForSafeFolderMergeUndo(result.Results, new()).AllMergesUndoable);
    }

    [Fact]
    public void FolderMove_WithSkippedLeafPreservesSourceAndReportsOnlyMovedLeaf()
    {
        string moved = FileAt("source/folder/moved.txt", "move");
        string skipped = FileAt("source/folder/skipped.txt", "keep");
        string existing = FileAt("target/folder/skipped.txt", "old!");
        string sourceFolder = Path.GetDirectoryName(moved)!;
        string targetFolder = Path.GetDirectoryName(existing)!;
        var sink = new FileService.ShellFileOperationProgressSink([new(sourceFolder, targetFolder, true)], CancellationToken.None, null, true);
        sink.CapturePreExistingDestinationState();
        string movedTarget = Path.Combine(targetFolder, "moved.txt");
        File.Move(moved, movedTarget);
        sink.RecordTransferResult(moved, movedTarget, 0);
        sink.RecordTransferResult(skipped, existing, 0x00270005);
        var outcome = sink.BuildCompletedResults();
        Assert.False(outcome.HasIncompleteDirectoryOperation);
        Assert.Equal(0, outcome.UnaccountedOperationCount);
        Assert.Equal(moved, Assert.Single(outcome.Results).SourcePath);
        Assert.True(File.Exists(skipped));
        Assert.Equal("old!", File.ReadAllText(existing));
    }

    [Fact]
    public void ShellUserWait_PausesTimeoutAndResumeRestoresHangDetection()
    {
        long now = 0;
        var watchdog = new StaOperationWatchdog(TimeSpan.FromSeconds(45), () => now);
        var sink = new FileService.ShellFileOperationProgressSink([], CancellationToken.None, null, false, watchdog);
        sink.PauseTimer();
        now = 3_600_000;
        Assert.False(watchdog.ShouldAbandon(now, null, out _));
        sink.ResumeTimer();
        now += 44_999;
        Assert.False(watchdog.ShouldAbandon(now, null, out _));
        now++;
        Assert.True(watchdog.ShouldAbandon(now, null, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
