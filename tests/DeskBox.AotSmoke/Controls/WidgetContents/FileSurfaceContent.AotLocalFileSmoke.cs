#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Controls;
using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    internal async Task<AotLocalFileSurfaceSnapshot>
        WaitForAotLocalFileSurfaceAsync(
            string expectedFolderPath,
            IReadOnlyCollection<string> expectedNames,
            bool expectAtMappedRoot)
    {
        string[] expectedNamesInOrder = expectedNames.ToArray();
        AotLocalFileSurfaceSnapshot last = CaptureAotLocalFileSurface();
        for (int attempt = 0; attempt < 200; attempt++)
        {
            UpdateLayout();
            GetActiveItemsView().UpdateLayout();
            last = CaptureAotLocalFileSurface();
            string[] actualNames = last.Items
                .Select(item => item.Name)
                .ToArray();
            bool itemsInExpectedOrder = actualNames.SequenceEqual(
                expectedNamesInOrder,
                StringComparer.OrdinalIgnoreCase);
            bool navigationPresentationMatches = expectAtMappedRoot
                ? !last.NavigationBarVisible &&
                    last.NavigationBarVisibility == Visibility.Collapsed
                : last.NavigationBarVisible &&
                    last.NavigationBarVisibility == Visibility.Visible &&
                    !string.IsNullOrWhiteSpace(last.NavigationText);
            bool emptyPresentationMatches = expectedNamesInOrder.Length == 0
                ? last.EmptyStateVisible
                : !last.EmptyStateVisible;

            if (last.IsLoaded &&
                last.HasXamlRoot &&
                last.DataContextMatchesViewModel &&
                last.ActualWidth > 0 &&
                last.ActualHeight > 0 &&
                last.ViewModelInitialized &&
                IsPathEqual(
                    last.MappedFolderPath,
                    ViewModel.MappedFolderPath ?? string.Empty) &&
                IsPathEqual(last.CurrentFolderPath, expectedFolderPath) &&
                last.IsAtMappedRoot == expectAtMappedRoot &&
                last.ViewModelItemCount == expectedNamesInOrder.Length &&
                last.VisibleItemCount == expectedNamesInOrder.Length &&
                last.XamlItemCount == expectedNamesInOrder.Length &&
                last.RealizedContainerCount == expectedNamesInOrder.Length &&
                last.ProjectedItemCount == expectedNamesInOrder.Length &&
                itemsInExpectedOrder &&
                last.Items.All(item =>
                    item.ContainerRealized &&
                    item.DataContextMatches &&
                    item.NameProjected) &&
                navigationPresentationMatches &&
                emptyPresentationMatches &&
                last.ActiveViewVisible)
            {
                return last;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            $"The real local-file surface did not stabilize. Snapshot={last}");
    }

    private AotLocalFileSurfaceSnapshot CaptureAotLocalFileSurface()
    {
        ListViewBase activeView = GetActiveItemsView();
        var items = new List<AotLocalFileSurfaceItemSnapshot>();
        int realizedContainerCount = 0;
        int projectedItemCount = 0;
        foreach (object entry in activeView.Items)
        {
            if (entry is not WidgetItem item)
            {
                continue;
            }

            DependencyObject? container = activeView.ContainerFromItem(item);
            FileItemSurface? itemSurface = container is null
                ? null
                : FindAotLocalFileDescendant<FileItemSurface>(container);
            bool containerRealized = container is not null && itemSurface is not null;
            bool dataContextMatches = itemSurface is not null &&
                ReferenceEquals(itemSurface.DataContext, item);
            string projectedName = itemSurface?.ItemNameText.Text ?? string.Empty;
            bool nameProjected = string.Equals(
                projectedName,
                item.Name,
                StringComparison.Ordinal);
            if (containerRealized)
            {
                realizedContainerCount++;
            }
            if (containerRealized && dataContextMatches && nameProjected)
            {
                projectedItemCount++;
            }

            items.Add(new AotLocalFileSurfaceItemSnapshot(
                item.Name,
                item.Path,
                item.IsFolder,
                containerRealized,
                dataContextMatches,
                projectedName,
                nameProjected));
        }

        return new AotLocalFileSurfaceSnapshot(
            IsLoaded,
            XamlRoot is not null,
            ReferenceEquals(Root.DataContext, ViewModel),
            ActualWidth,
            ActualHeight,
            ViewModel.IsInitialized,
            ViewModel.MappedFolderPath ?? string.Empty,
            ViewModel.CurrentFolderPath ?? string.Empty,
            ViewModel.IsAtMappedRoot,
            ViewModel.CanNavigateUp,
            ViewModel.FolderNavigationVisibility,
            FolderNavigationBar.Visibility == Visibility.Visible,
            FolderNavigationBar.Visibility == Visibility.Visible
                ? FolderNavigationText.Text
                : string.Empty,
            ViewModel.Items.Count,
            ViewModel.VisibleItems.Count(),
            activeView.Items.Count,
            realizedContainerCount,
            projectedItemCount,
            EmptyState.Visibility == Visibility.Visible,
            activeView.Visibility == Visibility.Visible,
            ViewModel.ViewMode.ToString(),
            items);
    }

    private static T? FindAotLocalFileDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            T? nested = FindAotLocalFileDescendant<T>(
                VisualTreeHelper.GetChild(root, index));
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool IsPathEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record AotLocalFileSurfaceSnapshot(
    bool IsLoaded,
    bool HasXamlRoot,
    bool DataContextMatchesViewModel,
    double ActualWidth,
    double ActualHeight,
    bool ViewModelInitialized,
    string MappedFolderPath,
    string CurrentFolderPath,
    bool IsAtMappedRoot,
    bool CanNavigateUp,
    Visibility NavigationBarVisibility,
    bool NavigationBarVisible,
    string NavigationText,
    int ViewModelItemCount,
    int VisibleItemCount,
    int XamlItemCount,
    int RealizedContainerCount,
    int ProjectedItemCount,
    bool EmptyStateVisible,
    bool ActiveViewVisible,
    string ViewMode,
    IReadOnlyList<AotLocalFileSurfaceItemSnapshot> Items);

internal sealed record AotLocalFileSurfaceItemSnapshot(
    string Name,
    string Path,
    bool IsFolder,
    bool ContainerRealized,
    bool DataContextMatches,
    string ProjectedName,
    bool NameProjected);
#endif
