#if DESKBOX_NATIVE_AOT
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    internal AotNativeDropHighlightProbe PrimeAotNativeFolderHighlight(
        string folderPath)
    {
        WidgetItem item = ViewModel.Items.Single(candidate =>
            candidate.IsFolder &&
            string.Equals(
                Path.GetFullPath(candidate.Path),
                Path.GetFullPath(folderPath),
                StringComparison.OrdinalIgnoreCase));
        ListViewBase activeView = GetActiveItemsView();
        activeView.UpdateLayout();
        DependencyObject container = activeView.ContainerFromItem(item) ??
            throw new InvalidOperationException(
                "The owned folder target does not have a realized container.");
        FileItemSurface surface =
            FindAotLocalFileDescendant<FileItemSurface>(container) ??
            throw new InvalidOperationException(
                "The owned folder target has no FileItemSurface.");
        Border border = surface.InteractiveBorder;
        AotNativeDropScreenBounds rootBounds =
            CaptureAotNativeDropScreenBounds(Root);
        AotNativeDropScreenBounds folderBounds =
            CaptureAotNativeDropScreenBounds(border);
        (int outsideX, int outsideY) = FindPointInsideRootOutsideFolder(
            rootBounds,
            folderBounds);

        SetFolderDropTarget(border);
        return new AotNativeDropHighlightProbe(
            folderPath,
            rootBounds,
            folderBounds,
            outsideX,
            outsideY,
            HasActiveChildDropTargetVisual,
            GetAotNativeFolderVisualState(border));
    }

    internal AotNativeDropHighlightState CaptureAotNativeFolderHighlightState(
        string folderPath)
    {
        WidgetItem? item = ViewModel.Items.FirstOrDefault(candidate =>
            candidate.IsFolder &&
            string.Equals(
                Path.GetFullPath(candidate.Path),
                Path.GetFullPath(folderPath),
                StringComparison.OrdinalIgnoreCase));
        ListViewBase activeView = GetActiveItemsView();
        DependencyObject? container = item is null
            ? null
            : activeView.ContainerFromItem(item);
        FileItemSurface? surface = container is null
            ? null
            : FindAotLocalFileDescendant<FileItemSurface>(container);
        return new AotNativeDropHighlightState(
            HasActiveChildDropTargetVisual,
            _folderDropTarget is not null,
            _stackMemberDropTarget is not null,
            GetAotNativeFolderVisualState(surface?.InteractiveBorder));
    }

    internal AotNativeDropProgressSnapshot CaptureAotNativeDropProgress()
    {
        Brush? background = ImportProgressCard.Background;
        return new AotNativeDropProgressSnapshot(
            IsImportBusy,
            ImportBusyElapsedMilliseconds,
            ImportProgressCard.Visibility == Visibility.Visible,
            ImportProgressCard.Visibility.ToString(),
            Canvas.GetZIndex(ImportProgressCard),
            ImportProgressCard.Translation.Z,
            background?.GetType().FullName ?? string.Empty,
            background is AcrylicBrush,
            ImportProgressBar.IsIndeterminate,
            ImportProgressBar.Value,
            ImportPercentText.Text,
            ImportTitleText.Text,
            ImportDescriptionText.Text);
    }

    private AotNativeDropScreenBounds CaptureAotNativeDropScreenBounds(
        FrameworkElement element)
    {
        if (_hostWindowHandle == IntPtr.Zero ||
            element.XamlRoot is null ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0 ||
            !Win32Helper.GetWindowRect(
                _hostWindowHandle,
                out Win32Helper.RECT windowBounds))
        {
            throw new InvalidOperationException(
                "The real File Widget screen bounds are unavailable.");
        }

        Windows.Foundation.Point topLeft = element.TransformToVisual(null)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        double scale = element.XamlRoot.RasterizationScale;
        int left = windowBounds.Left + (int)Math.Floor(topLeft.X * scale);
        int top = windowBounds.Top + (int)Math.Floor(topLeft.Y * scale);
        int right = windowBounds.Left + (int)Math.Ceiling(
            (topLeft.X + element.ActualWidth) * scale);
        int bottom = windowBounds.Top + (int)Math.Ceiling(
            (topLeft.Y + element.ActualHeight) * scale);
        return new AotNativeDropScreenBounds(left, top, right, bottom);
    }

    private static (int X, int Y) FindPointInsideRootOutsideFolder(
        AotNativeDropScreenBounds root,
        AotNativeDropScreenBounds folder)
    {
        (int X, int Y)[] candidates =
        [
            (root.Left + 2, root.Top + 2),
            (root.Right - 2, root.Top + 2),
            (root.Left + 2, root.Bottom - 2),
            (root.Right - 2, root.Bottom - 2),
            ((root.Left + root.Right) / 2, root.Bottom - 2)
        ];
        foreach ((int x, int y) in candidates)
        {
            if (root.Contains(x, y) && !folder.Contains(x, y))
            {
                return (x, y);
            }
        }

        throw new InvalidOperationException(
            "No point inside the File Widget but outside the owned folder target was found.");
    }

    private string GetAotNativeFolderVisualState(Border? border)
    {
        if (border is null)
        {
            return string.Empty;
        }

        // Every drop target now renders as the neutral hover surface, so the
        // probe reports the recorded target instead of a border the visual no
        // longer draws.
        return IsActiveChildDropTarget(border)
            ? "DropTarget"
            : "Normal";
    }

    private bool IsActiveChildDropTarget(Border border) =>
        ReferenceEquals(border, _folderDropTarget) ||
        ReferenceEquals(border, _stackMemberDropTarget) ||
        ReferenceEquals(border, _launchDropTarget);
}

internal sealed record AotNativeDropScreenBounds(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    internal bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}

internal sealed record AotNativeDropHighlightProbe(
    string FolderPath,
    AotNativeDropScreenBounds RootBounds,
    AotNativeDropScreenBounds FolderBounds,
    int OutsideScreenX,
    int OutsideScreenY,
    bool HighlightActiveBeforeNativeCallback,
    string FolderVisualStateBeforeNativeCallback);

internal sealed record AotNativeDropHighlightState(
    bool AnyChildHighlightActive,
    bool FolderHighlightActive,
    bool StackHighlightActive,
    string FolderVisualState);

internal sealed record AotNativeDropProgressSnapshot(
    bool IsImportBusy,
    long? BusyElapsedMilliseconds,
    bool CardVisible,
    string CardVisibility,
    int CanvasZIndex,
    float TranslationZ,
    string BackgroundType,
    bool BackgroundIsAcrylicBrush,
    bool ProgressIndeterminate,
    double ProgressValue,
    string PercentText,
    string TitleText,
    string DescriptionText);
#endif
