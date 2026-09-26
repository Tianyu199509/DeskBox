using DeskBox.Views;

namespace DeskBox.Tests;

public sealed class NativeDropVisualContractTests
{
    [Theory]
    [InlineData("复制到“{0}”", "复制到%1")]
    [InlineData("Move to \"{0}\"", "Move to %1")]
    [InlineData("Nach „{0}“ verschieben", "Nach %1 verschieben")]
    [InlineData("「{0}」に移動", "%1に移動")]
    public void LocalizedFolderCaption_BecomesAShellInsertTemplate(
        string localized,
        string expected)
    {
        Assert.Equal(
            expected,
            ContentWidgetWindow.ToShellDropDescriptionMessage(localized));
    }

    [Fact]
    public void ExternalFileDrag_UsesTheShellImageAndDropDescriptionPath()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string window = ReadRepositoryFile(
            "src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs");
        string target = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropTarget.cs");
        string imageManager = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropImageManager.cs");
        string description = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropDescriptionWriter.cs");

        Assert.Contains(
            "e.DragUIOverride.IsContentVisible = false;",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "e.DragUI.SetContentFromDataPackage();",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateNativeFileDropDescription",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Microsoft.UI.Content.DesktopChildSiteBridge",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.EnumChildWindows(",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "target.Register();",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "_nativeFileDropTargets[targetWindow] = target;",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "SuppressesNativeShellDragVisual: false",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDropEffectPolicy.Copy => \"Widget.CopyToFolder\"",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDropEffectPolicy.Move => \"Widget.MoveToFolder\"",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "return new NativeDropDescriptionText(message, targetName);",
            window,
            StringComparison.Ordinal);

        Assert.Contains(
            "NativeDropImageManager.TryCreate()",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dropImageManager?.DragEnter(",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dropImageManager?.DragOver(point, effect);",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dropImageManager?.DragLeave();",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dropImageManager?.Drop(",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDropDescriptionWriter.TryApply(",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeDropDescriptionWriter.TryClear(dataObject)",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateShellVisual(point, effect);",
            target,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScheduleNativeFileDropFallback(",
            window,
            StringComparison.Ordinal);

        Assert.Contains(
            "4657278A-411B-11D2-839A-00C04FD918D0",
            imageManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "4657278B-411B-11D2-839A-00C04FD918D0",
            imageManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "CoCreateInstance(",
            imageManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "delegate* unmanaged[Stdcall]",
            imageManager,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetVtableEntry(ShowVtableSlot)",
            imageManager,
            StringComparison.Ordinal);

        Assert.Contains(
            "RegisterClipboardFormatW(\"DropDescription\")",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            "RegisterClipboardFormatW(\"DragWindow\")",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            "DdwmUpdateWindow = WmUser + 3",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            "release: true",
            description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ContentFileAssociations_UseTheNativeVisualAndFallbackImport()
    {
        string window = ReadRepositoryFile(
            "src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs");
        string interaction = ReadRepositoryFile(
            "src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs");
        string quickCapture = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs");
        string todo = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DragDrop.cs");
        string todoAdapter = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContentAdapter.cs");

        Assert.Contains(
            "QuickCaptureWidgetContentAdapter => true",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "TodoWidgetContentAdapter => true",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Widget.Compact.QuickCaptureDropHint",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Widget.Compact.TodoDropHint",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindNativeDropDataContext<QuickCaptureItemViewModel>",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindNativeDropDataContext<TodoItemViewModel>",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "case QuickCaptureWidgetContentAdapter quickCapture:",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "case TodoWidgetContentAdapter todo:",
            window,
            StringComparison.Ordinal);

        foreach (string source in new[] { quickCapture, todo })
        {
            Assert.Contains(
                "e.DragUIOverride.IsContentVisible = false;",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "e.DragUIOverride.IsGlyphVisible = false;",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "e.DragUIOverride.IsCaptionVisible = false;",
                source,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "e.DragUIOverride.IsContentVisible = isInternalFileDrag;",
            interaction,
            StringComparison.Ordinal);
        Assert.Contains(
            "e.DragUIOverride.IsGlyphVisible = isInternalFileDrag;",
            interaction,
            StringComparison.Ordinal);
        Assert.Contains(
            "e.DragUIOverride.IsCaptionVisible = isInternalFileDrag;",
            interaction,
            StringComparison.Ordinal);

        Assert.Contains(
            "ImportNativeDroppedFilesAsync(",
            quickCapture,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImportNativeDroppedFilesAsync(",
            todo,
            StringComparison.Ordinal);
        Assert.Contains(
            "todoContent.ImportNativeDroppedFilesAsync(files, targetItem)",
            todoAdapter,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExplorerFiles_CanTargetFoldersAndStacksThroughTheNativeBridge()
    {
        string window = ReadRepositoryFile(
            "src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string visuals = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs");

        Assert.Contains(
            "FindNativeDropDataContext<WidgetItem>",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "NormalizeNativeFileDropItemTarget(",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Widget.Stack.DragCaption.Import",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "_nativeFileDropItemTarget is WidgetStackItem stack",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "Name.Length: > 0",
            window,
            StringComparison.Ordinal);

        Assert.Contains(
            "WidgetItem? targetItem = null",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "targetItem is WidgetStackItem stack",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "targetItem is { IsFolder: true, Path.Length: > 0 } folder",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImportNativeDroppedFilesIntoFolderAsync(",
            visuals,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImportNativeDroppedFilesIntoStackAsync(",
            visuals,
            StringComparison.Ordinal);
        Assert.Contains(
            "TransferItemsWithResultAsync(",
            visuals,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.AddItemsToStack(targetStackKey, importedItems)",
            visuals,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeShellVisual_DoesNotIntroduceBuiltInComRcws()
    {
        string combined = string.Join(
            Environment.NewLine,
            ReadRepositoryFile("src/DeskBox/Helpers/NativeDropImageManager.cs"),
            ReadRepositoryFile("src/DeskBox/Helpers/NativeDropDescriptionWriter.cs"),
            ReadRepositoryFile("src/DeskBox/Helpers/NativeDropComDataReader.cs"));

        Assert.DoesNotContain(
            "Marshal.GetObjectForIUnknown",
            combined,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.ReleaseComObject",
            combined,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[ComImport",
            combined,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
