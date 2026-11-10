#if DESKBOX_NATIVE_AOT
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private const string AotTodoStepsTaskTitle = "AOT Todo steps task";
    private const string AotTodoInitialStepText = "AOT Todo initial step";
    private const string AotTodoPersistedStepText = "AOT Todo persisted edited step";

    internal async Task<AotTodoStepMutationResult> RunAotTodoStepMutationAsync()
    {
        if (ViewModel is null || !ViewModel.IsInitialized || ViewModel.Items.Count != 0)
        {
            throw new InvalidOperationException(
                "The Todo step mutation surface did not start initialized and empty.");
        }

        await OpenAddEditorAsync();
        DetailTitleTextBox.Text = AotTodoStepsTaskTitle;
        TodoItemViewModel item = await ViewModel.FinalizeDetailAsync(
            DetailTitleTextBox.Text,
            closeDetail: false) ??
            throw new InvalidOperationException(
                "The Todo step matrix could not persist its owned task.");

        DetailNewStepTextBox.Text = AotTodoInitialStepText;
        await AddDetailStepAsync();
        TodoStepViewModel step = item.Steps.Single();
        AotTodoStepRowControls initialRow = await WaitForAotTodoStepRowAsync(
            step.Id,
            AotTodoInitialStepText,
            expectedCompleted: false);
        bool initialStepUiProjected =
            initialRow.TextBox.DataContext is TodoStepViewModel initialDataContext &&
            string.Equals(initialDataContext.Id, step.Id, StringComparison.Ordinal);

        initialRow.TextBox.Text = AotTodoPersistedStepText;
        if (!await SaveDetailStepTextAsync(initialRow.TextBox))
        {
            throw new InvalidOperationException(
                "The real Todo step text save path did not complete.");
        }
        AotTodoStepRowControls editedRow = await WaitForAotTodoStepRowAsync(
            step.Id,
            AotTodoPersistedStepText,
            expectedCompleted: false);
        bool stepTextEditObserved = string.Equals(
            step.Text,
            AotTodoPersistedStepText,
            StringComparison.Ordinal);

        editedRow.CheckBox.IsChecked = true;
        if (!await SetDetailStepCompletedAsync(editedRow.CheckBox))
        {
            throw new InvalidOperationException(
                "The real Todo step completion path did not complete.");
        }
        await WaitForAotTodoStepRowAsync(
            step.Id,
            AotTodoPersistedStepText,
            expectedCompleted: true);

        if (!initialStepUiProjected || !stepTextEditObserved || !step.IsCompleted)
        {
            throw new InvalidOperationException(
                "The Todo step UI projection, text edit, or completion was incomplete.");
        }

        return new AotTodoStepMutationResult(
            item.Id,
            step.Id,
            initialStepUiProjected,
            stepTextEditObserved);
    }

    internal async Task<AotTodoStepRestartResult>
        ApplyAotTodoStepRestartMutationAsync(string itemId, string stepId)
    {
        TodoItemViewModel item = await OpenAotTodoItemAsync(itemId);
        TodoStepViewModel step = item.Steps.Single(candidate => string.Equals(
            candidate.Id,
            stepId,
            StringComparison.Ordinal));
        AotTodoStepRowControls row = await WaitForAotTodoStepRowAsync(
            stepId,
            AotTodoPersistedStepText,
            expectedCompleted: true);
        bool wasCompleted = step.IsCompleted && row.CheckBox.IsChecked == true;

        row.CheckBox.IsChecked = false;
        bool updated = await SetDetailStepCompletedAsync(row.CheckBox);
        await WaitForAotTodoStepRowAsync(
            stepId,
            AotTodoPersistedStepText,
            expectedCompleted: false);
        bool stepCompletionRoundTripObserved =
            wasCompleted && updated && !step.IsCompleted;
        if (!stepCompletionRoundTripObserved)
        {
            throw new InvalidOperationException(
                "The Todo step completion state did not round-trip after restart.");
        }

        return new AotTodoStepRestartResult(
            item.Id,
            step.Id,
            stepCompletionRoundTripObserved);
    }

    internal async Task WaitForAotTodoStepProjectionAsync(
        string stepId,
        bool expectedCompleted)
    {
        await WaitForAotTodoStepRowAsync(
            stepId,
            AotTodoPersistedStepText,
            expectedCompleted);
    }

    internal async Task DeleteAotTodoStepAsync(string stepId)
    {
        AotTodoStepRowControls row = await WaitForAotTodoStepRowAsync(
            stepId,
            AotTodoPersistedStepText,
            expectedCompleted: false);
        if (!await DeleteDetailStepAsync(row.DeleteButton))
        {
            throw new InvalidOperationException(
                "The real Todo step deletion path did not complete.");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            UpdateLayout();
            if (DetailStepsItemsControl.Items.Count == 0 &&
                ViewModel?.SelectedDetailItem?.Steps.Count == 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            "The deleted Todo step remained in the real detail surface.");
    }

    private AotTodoStepUiSnapshot CaptureAotTodoStepUiSnapshot()
    {
        if (DetailStepsItemsControl is null)
        {
            return new AotTodoStepUiSnapshot(
                ItemCount: 0,
                ContainerRealized: false,
                DataContextId: null,
                Text: string.Empty,
                IsChecked: null,
                Opacity: null);
        }

        int itemCount = DetailStepsItemsControl.Items.Count;
        AotTodoStepRowControls? row = TryGetAotTodoStepRowControls();
        if (row is null)
        {
            return new AotTodoStepUiSnapshot(
                itemCount,
                ContainerRealized: false,
                DataContextId: null,
                Text: string.Empty,
                IsChecked: null,
                Opacity: null);
        }

        return new AotTodoStepUiSnapshot(
            itemCount,
            ContainerRealized: true,
            row.Step.Id,
            row.TextBox.Text,
            row.CheckBox.IsChecked,
            row.TextBox.Opacity);
    }

    private async Task<AotTodoStepRowControls> WaitForAotTodoStepRowAsync(
        string stepId,
        string expectedText,
        bool expectedCompleted)
    {
        double expectedOpacity = expectedCompleted ? 0.58 : 1;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            UpdateLayout();
            AotTodoStepRowControls? row = TryGetAotTodoStepRowControls();
            if (row is not null &&
                string.Equals(row.Step.Id, stepId, StringComparison.Ordinal) &&
                string.Equals(row.Step.Text, expectedText, StringComparison.Ordinal) &&
                row.Step.IsCompleted == expectedCompleted &&
                string.Equals(row.TextBox.Text, expectedText, StringComparison.Ordinal) &&
                row.CheckBox.IsChecked == expectedCompleted &&
                Math.Abs(row.TextBox.Opacity - expectedOpacity) < 0.01)
            {
                return row;
            }

            await Task.Delay(50);
        }

        AotTodoStepUiSnapshot snapshot = CaptureAotTodoStepUiSnapshot();
        throw new InvalidOperationException(
            "The real Todo step row did not project the expected AOT state. " +
            $"ItemCount={snapshot.ItemCount}; " +
            $"ContainerRealized={snapshot.ContainerRealized}; " +
            $"DataContextId={snapshot.DataContextId ?? "<null>"}; " +
            $"Text={snapshot.Text}; IsChecked={snapshot.IsChecked}; " +
            $"Opacity={snapshot.Opacity}.");
    }

    private AotTodoStepRowControls? TryGetAotTodoStepRowControls()
    {
        if (DetailStepsItemsControl.Items.Count != 1 ||
            DetailStepsItemsControl.ContainerFromIndex(0) is not DependencyObject container)
        {
            return null;
        }

        TextBox? textBox = FindAotTodoStepDescendant<TextBox>(
            container,
            "DetailStepTextBox");
        CheckBox? checkBox = FindAotTodoStepDescendant<CheckBox>(
            container,
            "DetailStepCheckBox");
        Button? deleteButton = FindAotTodoStepDescendant<Button>(
            container,
            "DetailDeleteStepButton");
        TodoStepViewModel? step = textBox?.DataContext as TodoStepViewModel ??
            checkBox?.DataContext as TodoStepViewModel ??
            deleteButton?.DataContext as TodoStepViewModel;
        return textBox is null || checkBox is null || deleteButton is null || step is null
            ? null
            : new AotTodoStepRowControls(step, textBox, checkBox, deleteButton);
    }

    private static T? FindAotTodoStepDescendant<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && string.Equals(
                    match.Name,
                    name,
                    StringComparison.Ordinal))
            {
                return match;
            }

            T? nested = FindAotTodoStepDescendant<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}

internal sealed record AotTodoStepUiSnapshot(
    int ItemCount,
    bool ContainerRealized,
    string? DataContextId,
    string Text,
    bool? IsChecked,
    double? Opacity);

internal sealed record AotTodoStepRowControls(
    TodoStepViewModel Step,
    TextBox TextBox,
    CheckBox CheckBox,
    Button DeleteButton);

internal sealed record AotTodoStepMutationResult(
    string ItemId,
    string StepId,
    bool InitialStepUiProjected,
    bool StepTextEditObserved);

internal sealed record AotTodoStepRestartResult(
    string ItemId,
    string StepId,
    bool StepCompletionRoundTripObserved);
#endif
