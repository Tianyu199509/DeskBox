using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Tests;

public sealed class DeskBoxDragDataTests
{
    [Fact]
    public async Task InternalMixedFileAndFolderDrop_PreservesEveryPath()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        string folderPath = Directory.CreateDirectory(
            Path.Combine(tempDirectory, "folder")).FullName;
        string filePath = Path.Combine(tempDirectory, "file.txt");
        File.WriteAllText(filePath, "content");

        try
        {
            var dataPackage = new DataPackage();
            dataPackage.Properties[DeskBoxDragData.SourcePathsProperty] =
                new[] { filePath, folderPath };

            IReadOnlyList<DroppedFilePath> internalFiles =
                DeskBoxDragData.GetInternalDroppedFiles(dataPackage.GetView());
            using DroppedFileBatch batch =
                await DeskBoxDragData.TryGetDroppedFilesAsync(dataPackage.GetView());

            Assert.Equal([filePath, folderPath], internalFiles.Select(file => file.Path));
            Assert.Equal([filePath, folderPath], batch.Files.Select(file => file.Path));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void TryCreateInternetShortcutFile_CreatesSafeUrlFileWithMotwAndRejectsNonHttpSchemes()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string? createdPath = DeskBoxDragData.TryCreateInternetShortcutFile(
                "https://example.com/docs/guide?id=1",
                "Guide Title",
                tempDirectory);
            Assert.NotNull(createdPath);
            Assert.True(File.Exists(createdPath));
            Assert.Equal(".url", Path.GetExtension(createdPath), ignoreCase: true);

            string contents = File.ReadAllText(createdPath);
            Assert.Contains("[InternetShortcut]", contents, StringComparison.Ordinal);
            Assert.Contains("URL=https://example.com/docs/guide?id=1", contents, StringComparison.Ordinal);

            Assert.Null(DeskBoxDragData.TryCreateInternetShortcutFile(
                "file:///C:/Windows/System32/cmd.exe",
                "bad",
                tempDirectory));
            Assert.Null(DeskBoxDragData.TryCreateInternetShortcutFile(
                "javascript:alert(1)",
                "bad",
                tempDirectory));
            Assert.Null(DeskBoxDragData.TryCreateInternetShortcutFile(
                "https://example.com/\r\nIconFile=\\\\evil\\share\\a.ico",
                "bad",
                tempDirectory));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
