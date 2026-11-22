using DeskBox.Contracts;
using DeskBox.Controls.Todo;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    internal async Task FlushPendingEditsForExportAsync()
    {
        if (_isDisposed || ViewModel is not { } vm)
        {
            return;
        }

        CommitOrCloseTodoInlineEdit();
        await FlushDetailTitleAsync(finalizeBlankDraft: false, closeDetailOnFinalize: false);

        if (vm.SelectedDetailItem is { } persistedDetailItem &&
            DetailStepsItemsControl is not null)
        {
            int stepCount = DetailStepsItemsControl.Items.Count;
            for (int index = 0; index < stepCount; index++)
            {
                if (DetailStepsItemsControl.ContainerFromIndex(index) is DependencyObject container &&
                    FindVisualChild<TextBox>(container, "DetailStepTextBox") is { } stepTextBox &&
                    stepTextBox.DataContext is TodoStepViewModel step &&
                    !string.IsNullOrWhiteSpace(stepTextBox.Text) &&
                    !string.Equals(step.Text, stepTextBox.Text.Trim(), StringComparison.Ordinal))
                {
                    await vm.UpdateStepTextAsync(persistedDetailItem.Id, step.Id, stepTextBox.Text);
                }
            }
        }

        _notesAutosaveTimer.Stop();
        if (_notesEditingItemId is not null)
        {
            await SaveActiveNotesAsync(keepEditing: true);
        }
    }

    internal async Task ExportSelectedItemsAsync(
        IReadOnlyList<TodoItemViewModel> selectedItems,
        FrameworkElement anchor)
    {
        if (_isDisposed || selectedItems.Count == 0)
        {
            return;
        }

        await FlushPendingEditsForExportAsync();

        TodoItem[] snapshot = selectedItems
            .Select(vm => vm.Item)
            .Where(item => !item.IsDeleted && !string.IsNullOrWhiteSpace(item.Text))
            .ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        LocalizationService loc = App.Current.LocalizationService;
        IntPtr ownerHwnd = ResolveExportOwnerWindowHandle();
        FrameworkElement targetAnchor = anchor.IsLoaded ? anchor : RootGrid;
        if (WidgetEditorPopoverWindow.TryConsumeToggleOrCloseExisting(
                ownerHwnd,
                "todo-selected-export",
                targetAnchor))
        {
            return;
        }

        var panel = new TodoExportPanel(
            snapshot,
            loc,
            initialScope: TodoExportStatusScope.All,
            isSelectionSubset: true,
            showFlyoutChrome: true);

        var exportPopover = new WidgetEditorPopoverWindow(
            ownerHwnd,
            loc.T("Todo.Export.SelectedTitle"));

        WidgetManager? widgetManager = App.Current.WidgetManager;
        bool exportTriggered = false;

        panel.CancelRequested += (_, _) => exportPopover.ClosePopover(restoreOwnerForeground: true);
        panel.ExportRequested += async (_, _) =>
        {
            if (exportTriggered || !panel.CanExport)
            {
                return;
            }

            exportTriggered = true;
            TodoExportOptions chosenOptions = panel.CurrentOptions;
            TodoItem[] itemsToWrite = panel.CurrentPreview.Items.ToArray();

            widgetManager?.BeginWidgetInteraction("todo-selected-export-picker");
            try
            {
                exportPopover.ClosePopover(restoreOwnerForeground: false);
                IntPtr pickerOwnerHwnd = ResolveExportOwnerWindowHandle();
                string? savedPath = await TodoExportWorkflow.PickAndExportAsync(
                    pickerOwnerHwnd,
                    itemsToWrite,
                    chosenOptions,
                    loc,
                    isSelectionSubset: true);

                if (!string.IsNullOrWhiteSpace(savedPath))
                {
                    RaiseExportSuccessFeedback(itemsToWrite.Length, savedPath);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[TodoExport] Multi-select export failed: {ex}");
                RaiseExportErrorFeedback(ex.Message);
            }
            finally
            {
                widgetManager?.EndWidgetInteraction("todo-selected-export-picker-done");
            }
        };

        try
        {
            exportPopover.ShowAnchored(
                targetAnchor,
                panel,
                WidgetPopoverPlacement.Bottom,
                minWidthDip: 372,
                maxWidthDip: 392,
                triggerElement: targetAnchor,
                logScope: "TodoExport",
                logicalKey: "todo-selected-export");
        }
        catch (Exception ex)
        {
            App.Log($"[TodoExport] Failed to open multi-select export popover: {ex}");
        }
    }

    internal void RaiseExportSuccessFeedback(int exportedCount, string savedPath)
    {
        LocalizationService loc = App.Current.LocalizationService;
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(new WidgetFeedbackRequest(
                loc.Format("Todo.Export.Status.Exported", exportedCount),
                WidgetFeedbackSeverity.Success,
                "todo-export-succeeded",
                loc.T("Todo.Export.Action.OpenFile"),
                () =>
                {
                    TodoExportWorkflow.OpenExportedFile(savedPath);
                    return Task.CompletedTask;
                })));
    }

    internal void RaiseExportErrorFeedback(string reason)
    {
        LocalizationService loc = App.Current.LocalizationService;
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(new WidgetFeedbackRequest(
                loc.Format("Todo.Export.Status.Failed", reason),
                WidgetFeedbackSeverity.Error,
                "todo-export-failed")));
    }

    private IntPtr ResolveExportOwnerWindowHandle()
    {
        if (XamlRoot?.ContentIslandEnvironment is { } env && env.AppWindowId.Value != 0)
        {
            IntPtr xamlHwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(env.AppWindowId);
            if (xamlHwnd != IntPtr.Zero && Win32Helper.IsWindow(xamlHwnd))
            {
                return xamlHwnd;
            }
        }

        IntPtr foreground = Win32Helper.GetForegroundWindow();
        IntPtr root = Win32Helper.GetAncestor(foreground, Win32Helper.GA_ROOT);
        return root != IntPtr.Zero ? root : foreground;
    }
}
