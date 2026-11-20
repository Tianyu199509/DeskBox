#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    internal async Task<AotRecycleBinMenuInvocationSnapshot>
        InvokeAotRecycleBinMenuDeleteAsync(
            IReadOnlyCollection<string> selectedNames,
            bool expectMultiSelection)
    {
        string[] requestedNames = selectedNames.ToArray();
        if (requestedNames.Length == 0 ||
            expectMultiSelection != (requestedNames.Length > 1))
        {
            throw new InvalidOperationException(
                "The Recycle Bin menu probe received an invalid selection shape.");
        }

        WidgetItem[] selectedItems = requestedNames
            .Select(name => ViewModel.Items.Single(item =>
                item is not WidgetStackItem &&
                string.Equals(item.Name, name, StringComparison.Ordinal)))
            .ToArray();
        ListViewBase activeView = GetActiveItemsView();
        activeView.SelectedItems.Clear();
        foreach (WidgetItem item in selectedItems)
        {
            activeView.SelectedItems.Add(item);
        }
        UpdateSelectionCommandBar();
        RefreshItemSelectionVisuals();

        MenuFlyout flyout = expectMultiSelection
            ? CreateMultiSelectionFlyout()
            : CreateItemFlyout(selectedItems[0]);
        string deleteText = T("Widget.MoveToRecycleBin");
        MenuFlyoutItem deleteItem = AotContextMenuProbe.FindItem(
            flyout,
            deleteText);
        int deleteIndex = AotContextMenuProbe.IndexOf(flyout, deleteItem);
        var menuItems = AotContextMenuProbe.Snapshot(flyout, deleteItem)
            .Select(entry => new AotRecycleBinMenuItemSnapshot(
                entry.Index,
                entry.ItemType,
                entry.Text,
                entry.IsEnabled,
                entry.IsTarget))
            .ToArray();

        if (!deleteItem.IsEnabled ||
            deleteIndex < 0 ||
            activeView.SelectedItems.Count != requestedNames.Length)
        {
            throw new InvalidOperationException(
                "The product Recycle Bin menu was not enabled for the exact selection.");
        }

        var feedbackCompletion =
            new TaskCompletionSource<WidgetFeedbackRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFeedbackRequested(
            object? sender,
            WidgetFeedbackRequestedEventArgs args)
        {
            if (string.Equals(
                    args.Request.DeduplicationKey,
                    "file-delete",
                    StringComparison.Ordinal))
            {
                feedbackCompletion.TrySetResult(args.Request);
            }
        }

        FeedbackRequested += OnFeedbackRequested;
        try
        {
            IInvokeProvider invokeProvider =
                AotContextMenuProbe.InvokeProvider(deleteItem);
            invokeProvider.Invoke();
            Task completed = await Task.WhenAny(
                feedbackCompletion.Task,
                Task.Delay(TimeSpan.FromSeconds(20)));
            if (!ReferenceEquals(completed, feedbackCompletion.Task))
            {
                throw new TimeoutException(
                    "The product Recycle Bin menu action did not complete with feedback.");
            }

            WidgetFeedbackRequest feedback = await feedbackCompletion.Task;
            return new AotRecycleBinMenuInvocationSnapshot(
                expectMultiSelection,
                requestedNames,
                selectedItems.Select(item => item.Path).ToArray(),
                AotContextMenuProbe.Count(flyout),
                deleteIndex,
                deleteItem.Text,
                deleteItem.IsEnabled,
                AutomationInvoked: true,
                feedback.DeduplicationKey ?? string.Empty,
                feedback.Severity.ToString(),
                feedback.Message,
                menuItems);
        }
        finally
        {
            FeedbackRequested -= OnFeedbackRequested;
        }
    }
}

internal sealed record AotRecycleBinMenuInvocationSnapshot(
    bool MultiSelection,
    IReadOnlyList<string> SelectedNames,
    IReadOnlyList<string> SelectedPaths,
    int MenuItemCount,
    int DeleteIndex,
    string DeleteText,
    bool DeleteEnabled,
    bool AutomationInvoked,
    string FeedbackKey,
    string FeedbackSeverity,
    string FeedbackMessage,
    IReadOnlyList<AotRecycleBinMenuItemSnapshot> Items);

internal sealed record AotRecycleBinMenuItemSnapshot(
    int Index,
    string ItemType,
    string Text,
    bool IsEnabled,
    bool IsDelete);
#endif
