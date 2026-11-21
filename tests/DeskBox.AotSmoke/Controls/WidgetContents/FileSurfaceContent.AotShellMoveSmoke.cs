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
    internal async Task<AotShellMoveMenuInvocationSnapshot>
        InvokeAotShellMoveBackToDesktopAsync(
            IReadOnlyCollection<string> selectedNames,
            bool expectMultiSelection)
    {
        string[] requestedNames = selectedNames.ToArray();
        if (requestedNames.Length == 0 ||
            expectMultiSelection != (requestedNames.Length > 1))
        {
            throw new InvalidOperationException(
                "The Shell move menu probe received an invalid selection shape.");
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
        string moveText = T("Widget.MoveBackToDesktop");
        MenuFlyoutItem moveItem = AotContextMenuProbe.FindItem(
            flyout,
            moveText);
        int moveIndex = AotContextMenuProbe.IndexOf(flyout, moveItem);
        var menuItems = AotContextMenuProbe.Snapshot(flyout, moveItem)
            .Select(entry => new AotShellMoveMenuItemSnapshot(
                entry.Index,
                entry.ItemType,
                entry.Text,
                entry.IsEnabled,
                entry.IsTarget))
            .ToArray();

        if (!moveItem.IsEnabled ||
            moveIndex < 0 ||
            _hostWindowHandle == IntPtr.Zero ||
            activeView.SelectedItems.Count != requestedNames.Length)
        {
            throw new InvalidOperationException(
                "The product Shell move menu was not enabled for the exact owned selection and host HWND.");
        }

        var feedbackCompletion =
            new TaskCompletionSource<WidgetFeedbackRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFeedbackRequested(
            object? sender,
            WidgetFeedbackRequestedEventArgs args)
        {
            // The move-back menu owns "file-move-desktop"; a watchdog-
            // abandoned transfer (the fixture's Late mode) surfaces through
            // the standard action-error route instead. Either completes the
            // probe — which one arrived is itself evidence.
            if (string.Equals(
                    args.Request.DeduplicationKey,
                    "file-move-desktop",
                    StringComparison.Ordinal) ||
                string.Equals(
                    args.Request.DeduplicationKey,
                    "file-action-error",
                    StringComparison.Ordinal))
            {
                feedbackCompletion.TrySetResult(args.Request);
            }
        }

        FeedbackRequested += OnFeedbackRequested;
        try
        {
            IInvokeProvider invokeProvider =
                AotContextMenuProbe.InvokeProvider(moveItem);
            invokeProvider.Invoke();
            Task completed = await Task.WhenAny(
                feedbackCompletion.Task,
                Task.Delay(TimeSpan.FromSeconds(20)));
            if (!ReferenceEquals(completed, feedbackCompletion.Task))
            {
                throw new TimeoutException(
                    "The product Shell move menu action did not complete with feedback.");
            }

            WidgetFeedbackRequest feedback = await feedbackCompletion.Task;
            return new AotShellMoveMenuInvocationSnapshot(
                expectMultiSelection,
                requestedNames,
                selectedItems.Select(item => item.Path).ToArray(),
                _hostWindowHandle.ToInt64(),
                AotContextMenuProbe.Count(flyout),
                moveIndex,
                moveItem.Text,
                moveItem.IsEnabled,
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

internal sealed record AotShellMoveMenuInvocationSnapshot(
    bool MultiSelection,
    IReadOnlyList<string> SelectedNames,
    IReadOnlyList<string> SelectedPaths,
    long HostWindowHandle,
    int MenuItemCount,
    int MoveIndex,
    string MoveText,
    bool MoveEnabled,
    bool AutomationInvoked,
    string FeedbackKey,
    string FeedbackSeverity,
    string FeedbackMessage,
    IReadOnlyList<AotShellMoveMenuItemSnapshot> Items);

internal sealed record AotShellMoveMenuItemSnapshot(
    int Index,
    string ItemType,
    string Text,
    bool IsEnabled,
    bool IsMove);
#endif
