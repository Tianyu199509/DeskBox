#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    internal async Task<AotFilePropertiesMenuInvocationSnapshot>
        InvokeAotFilePropertiesAsync(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            throw new InvalidOperationException(
                "The file Properties menu probe requires an exact owned target name.");
        }

        WidgetItem target = ViewModel.Items.Single(item =>
            item is not WidgetStackItem &&
            string.Equals(item.Name, targetName, StringComparison.Ordinal));
        ListViewBase activeView = GetActiveItemsView();
        activeView.SelectedItems.Clear();
        activeView.SelectedItems.Add(target);
        UpdateSelectionCommandBar();
        RefreshItemSelectionVisuals();

        MenuFlyout flyout = CreateItemFlyout(target);
        string propertiesText = T("Common.Properties");
        MenuFlyoutItem propertiesItem = AotContextMenuProbe.FindItem(
            flyout,
            propertiesText);
        int propertiesIndex = AotContextMenuProbe.IndexOf(flyout, propertiesItem);
        var menuItems = AotContextMenuProbe.Snapshot(flyout, propertiesItem)
            .Select(entry => new AotFilePropertiesMenuItemSnapshot(
                entry.Index,
                entry.ItemType,
                entry.Text,
                entry.IsEnabled,
                entry.IsTarget))
            .ToArray();

        if (!propertiesItem.IsEnabled ||
            propertiesIndex < 0 ||
            _hostWindowHandle == IntPtr.Zero ||
            activeView.SelectedItems.Count != 1 ||
            !ReferenceEquals(activeView.SelectedItems[0], target))
        {
            throw new InvalidOperationException(
                "The product Properties menu was not enabled for the exact owned item and host HWND.");
        }

        WidgetFeedbackRequest? feedback = null;
        void OnFeedbackRequested(
            object? sender,
            WidgetFeedbackRequestedEventArgs args)
        {
            if (string.Equals(
                    args.Request.DeduplicationKey,
                    "file-properties",
                    StringComparison.Ordinal))
            {
                feedback = args.Request;
            }
        }

        IReadOnlySet<long> baselineWindowHandles =
            AotFilePropertiesFixture.CaptureVisibleTopLevelWindowHandles();
        using var observationCancellation =
            new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Task<AotFilePropertiesDialogSnapshot> dialogTask = Task.Run(() =>
            AotFilePropertiesFixture.ObserveAndCloseOwnedDialogAsync(
                baselineWindowHandles,
                _hostWindowHandle,
                targetName,
                observationCancellation.Token));

        FeedbackRequested += OnFeedbackRequested;
        try
        {
            IInvokeProvider invokeProvider =
                AotContextMenuProbe.InvokeProvider(propertiesItem);
            invokeProvider.Invoke();
            AotFilePropertiesInvocationSnapshot invocation =
                await AotFilePropertiesFixture.WaitForInvocationResultAsync(
                    observationCancellation.Token);
            AotFilePropertiesDialogSnapshot dialog = await dialogTask;
            int remainingMatchingDialogs =
                AotFilePropertiesFixture.CountVisibleMatchingDialogs(targetName);

            return new AotFilePropertiesMenuInvocationSnapshot(
                target.Name,
                target.Path,
                _hostWindowHandle.ToInt64(),
                AotContextMenuProbe.Count(flyout),
                propertiesIndex,
                propertiesItem.Text,
                propertiesItem.IsEnabled,
                AutomationInvoked: true,
                feedback?.DeduplicationKey ?? string.Empty,
                feedback?.Severity.ToString() ?? string.Empty,
                feedback?.Message ?? string.Empty,
                remainingMatchingDialogs,
                invocation,
                dialog,
                menuItems);
        }
        catch
        {
            observationCancellation.Cancel();
            try
            {
                await dialogTask;
            }
            catch
            {
                // Preserve the original product/menu failure.
            }
            throw;
        }
        finally
        {
            FeedbackRequested -= OnFeedbackRequested;
        }
    }
}

internal sealed record AotFilePropertiesMenuInvocationSnapshot(
    string TargetName,
    string TargetPath,
    long HostWindowHandle,
    int MenuItemCount,
    int PropertiesIndex,
    string PropertiesText,
    bool PropertiesEnabled,
    bool AutomationInvoked,
    string FeedbackKey,
    string FeedbackSeverity,
    string FeedbackMessage,
    int RemainingMatchingDialogCount,
    AotFilePropertiesInvocationSnapshot Invocation,
    AotFilePropertiesDialogSnapshot Dialog,
    IReadOnlyList<AotFilePropertiesMenuItemSnapshot> Items);

internal sealed record AotFilePropertiesMenuItemSnapshot(
    int Index,
    string ItemType,
    string Text,
    bool IsEnabled,
    bool IsProperties);
#endif
