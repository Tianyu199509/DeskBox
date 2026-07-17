using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class QuickLookPreviewServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildToggleMessage_UsesQuickLookPipeProtocol()
    {
        const string path = @"C:\Work\preview file.pdf";

        Assert.Equal(
            $"{QuickLookPreviewService.ToggleMessage}|{path}|",
            QuickLookPreviewService.BuildToggleMessage(path));
    }

    [Fact]
    public void IsPreviewablePath_AcceptsExistingFilesAndDirectories()
    {
        Directory.CreateDirectory(_root);
        string filePath = Path.Combine(_root, "preview.txt");
        File.WriteAllText(filePath, "preview");

        Assert.True(QuickLookPreviewService.IsPreviewablePath(_root));
        Assert.True(QuickLookPreviewService.IsPreviewablePath(filePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsPreviewablePath_RejectsMissingPaths(string? path)
    {
        Assert.False(QuickLookPreviewService.IsPreviewablePath(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
