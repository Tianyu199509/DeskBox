#if DESKBOX_NATIVE_AOT
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotTodoStepsExpectedTaskTitle = "AOT Todo steps task";
    private const string AotTodoStepsExpectedStepText =
        "AOT Todo persisted edited step";

    private async Task CaptureAotManagedUiTodoStepsPersistenceAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        if (!DeskBoxDataPathService.Current.IsDevelopmentRoot)
        {
            throw new InvalidOperationException(
                "The Todo steps persistence matrix requires the isolated preview root.");
        }

        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotTodoPersistenceHost host =
            await manager.GetAotTodoPersistenceHostAsync(AotManagedUiTodoStepsWidgetId);
        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 && host.HasXamlRoot && host.Visible,
            "TodoStepsLiveHost",
            "The owned Todo steps HWND or XamlRoot is unavailable.");

        TodoWidgetContent surface = host.Surface;
        AotManagedUiTodoStepsPersistenceEvidence evidence =
            result.TodoStepsPersistence ??
            throw new InvalidOperationException(
                "The Todo steps persistence evidence was not initialized.");

        if (phase == AotManagedUiTodoStepsVerifyDeletePhase)
        {
            TodoWidgetData reloaded = await new TodoWidgetStore(
                AotManagedUiTodoStepsWidgetId).LoadAsync();
            TodoItem item = reloaded.Items.Single(entry => !entry.IsDeleted);
            await surface.OpenAotTodoItemAsync(item.Id);
            await surface.WaitForAotTodoStepProjectionAsync(
                item.Steps.Single().Id,
                expectedCompleted: true);
        }
        evidence.Before = await CaptureAotManagedUiTodoStateAsync(
            surface,
            AotManagedUiTodoStepsWidgetId);

        switch (phase)
        {
            case AotManagedUiTodoStepsMutatePhase:
            {
                RequireAotManagedUiTodoEmpty(evidence.Before);
                AotTodoStepMutationResult mutation =
                    await surface.RunAotTodoStepMutationAsync();
                evidence.InitialStepUiProjected = mutation.InitialStepUiProjected;
                evidence.StepTextEditObserved = mutation.StepTextEditObserved;
                evidence.After = await CaptureAotManagedUiTodoStateAsync(
                    surface,
                    AotManagedUiTodoStepsWidgetId);
                RequireAotManagedUiTodoStepPopulated(
                    evidence.After,
                    mutation.ItemId,
                    mutation.StepId,
                    expectedCompleted: true);
                RequireAotManagedUi(
                    result,
                    mutation.InitialStepUiProjected && mutation.StepTextEditObserved,
                    "TodoStepsTaskAndRowPersisted",
                    "The Todo task or initial non-empty step row did not project.");
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoStepTextAndCompletionPersisted",
                    "The Todo step text or completion did not persist.");
                break;
            }

            case AotManagedUiTodoStepsVerifyDeletePhase:
            {
                AotManagedUiTodoItemEvidence beforeItem =
                    RequireAotManagedUiTodoStepPopulated(
                        evidence.Before,
                        expectedCompleted: true);
                AotManagedUiTodoStepEvidence beforeStep = beforeItem.Steps.Single();
                AotTodoStepRestartResult restart =
                    await surface.ApplyAotTodoStepRestartMutationAsync(
                        beforeItem.Id,
                        beforeStep.Id);
                evidence.StepCompletionRoundTripObserved =
                    restart.StepCompletionRoundTripObserved;
                evidence.AfterStepMutation =
                    await CaptureAotManagedUiTodoStateAsync(
                        surface,
                        AotManagedUiTodoStepsWidgetId);
                RequireAotManagedUiTodoStepPopulated(
                    evidence.AfterStepMutation,
                    restart.ItemId,
                    restart.StepId,
                    expectedCompleted: false);
                RequireAotManagedUi(
                    result,
                    restart.StepCompletionRoundTripObserved,
                    "TodoStepsRestartProjectionVerified",
                    "The persisted Todo step did not reload through the real row UI.");
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoStepCompletionRoundTripVerified",
                    "The Todo step completion state did not round-trip.");

                await surface.DeleteAotTodoStepAsync(restart.StepId);
                evidence.AfterStepDelete =
                    await CaptureAotManagedUiTodoStateAsync(
                        surface,
                        AotManagedUiTodoStepsWidgetId);
                RequireAotManagedUiTodoTaskWithoutSteps(
                    evidence.AfterStepDelete,
                    restart.ItemId);
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoStepDeleted",
                    "The product Todo step deletion path did not complete.");

                await surface.DeleteAotTodoItemAsync(restart.ItemId);
                evidence.After = await CaptureAotManagedUiTodoStateAsync(
                    surface,
                    AotManagedUiTodoStepsWidgetId);
                RequireAotManagedUiTodoEmpty(evidence.After);
                RequireAotTodoTombstonesPersisted(evidence.After, restart.ItemId);
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoStepsItemDeleted",
                    "The Todo steps fixture task was not deleted.");
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoStepsDeletedItemTombstonePersisted",
                    "The deleted Todo steps fixture task was not written back into the store as a soft-delete tombstone.");
                break;
            }

            case AotManagedUiTodoStepsPostflightPhase:
                RequireAotManagedUiTodoEmpty(evidence.Before);
                RequireAotTodoTombstoneSurvivedRestart(evidence.Before);
                evidence.After = await CaptureAotManagedUiTodoStateAsync(
                    surface,
                    AotManagedUiTodoStepsWidgetId);
                RequireAotManagedUiTodoEmpty(evidence.After);
                RequireAotTodoTombstoneSurvivedRestart(evidence.After);
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoStepsDeletePostflightVerified",
                    "The Todo steps delete postflight was not clean.");
                RequireAotManagedUi(
                    result,
                    true,
                    "TodoStepsDeleteTombstoneSurvivedRestart",
                    "The deleted Todo steps fixture task tombstone did not survive the process restart.");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Todo steps persistence phase '{phase}'.");
        }
    }

    private static AotManagedUiTodoItemEvidence RequireAotManagedUiTodoStepPopulated(
        AotManagedUiTodoStateEvidence state,
        bool expectedCompleted)
    {
        AotManagedUiTodoItemEvidence item = state.Items.Single();
        AotManagedUiTodoStepEvidence step = item.Steps.Single();
        RequireAotManagedUiTodoStepPopulated(
            state,
            item.Id,
            step.Id,
            expectedCompleted);
        return item;
    }

    private static void RequireAotManagedUiTodoStepPopulated(
        AotManagedUiTodoStateEvidence state,
        string expectedItemId,
        string expectedStepId,
        bool expectedCompleted)
    {
        AotManagedUiTodoItemEvidence item = state.Items.Single();
        AotManagedUiTodoStepEvidence step = item.Steps.Single();
        double expectedOpacity = expectedCompleted ? 0.58 : 1;
        if (state.StoreVersion != 3 ||
            !state.StoreFileExists ||
            state.TombstoneIdCount != 0 ||
            state.TombstoneIds.Count != 0 ||
            !string.Equals(item.Id, expectedItemId, StringComparison.Ordinal) ||
            !string.Equals(item.Text, AotTodoStepsExpectedTaskTitle, StringComparison.Ordinal) ||
            item.Notes.Length != 0 ||
            item.IsCompleted ||
            item.HasCompletedAt ||
            item.IsImportant ||
            item.HasDueDate ||
            item.HasRecurrence ||
            item.StepCount != 1 ||
            item.AttachmentCount != 0 ||
            item.ReminderOffsetMinutes is not null ||
            item.SortOrder != 0 ||
            !string.Equals(step.Id, expectedStepId, StringComparison.Ordinal) ||
            !string.Equals(step.Text, AotTodoStepsExpectedStepText, StringComparison.Ordinal) ||
            step.IsCompleted != expectedCompleted ||
            step.SortOrder != 0 ||
            !state.SurfaceInitialized ||
            !state.SurfaceLoaded ||
            !state.SurfaceHasXamlRoot ||
            state.SurfaceItemCount != 1 ||
            state.VisibleItemCount != 1 ||
            !string.Equals(state.DetailItemId, item.Id, StringComparison.Ordinal) ||
            !string.Equals(state.DetailTitle, item.Text, StringComparison.Ordinal) ||
            state.DetailNotes.Length != 0 ||
            state.DetailIsCreating ||
            state.NotesEditingItemId is not null ||
            state.NotesAutoSavePending ||
            state.NotesSaveGateCount != 1 ||
            state.StepUiItemCount != 1 ||
            !state.StepUiContainerRealized ||
            !string.Equals(state.StepUiDataContextId, step.Id, StringComparison.Ordinal) ||
            !string.Equals(state.StepUiText, step.Text, StringComparison.Ordinal) ||
            state.StepUiIsChecked != expectedCompleted ||
            state.StepUiOpacity is not { } opacity ||
            Math.Abs(opacity - expectedOpacity) >= 0.01)
        {
            throw new InvalidOperationException(
                "The Todo step store, ViewModel, or real row projection is incomplete.");
        }
    }

    private static void RequireAotManagedUiTodoTaskWithoutSteps(
        AotManagedUiTodoStateEvidence state,
        string expectedItemId)
    {
        AotManagedUiTodoItemEvidence item = state.Items.Single();
        if (state.StoreVersion != 3 ||
            !state.StoreFileExists ||
            state.TombstoneIdCount != 0 ||
            state.TombstoneIds.Count != 0 ||
            !string.Equals(item.Id, expectedItemId, StringComparison.Ordinal) ||
            !string.Equals(item.Text, AotTodoStepsExpectedTaskTitle, StringComparison.Ordinal) ||
            item.StepCount != 0 ||
            item.Steps.Count != 0 ||
            item.AttachmentCount != 0 ||
            state.SurfaceItemCount != 1 ||
            state.VisibleItemCount != 1 ||
            !string.Equals(state.DetailItemId, item.Id, StringComparison.Ordinal) ||
            state.StepUiItemCount != 0 ||
            state.StepUiContainerRealized ||
            state.StepUiDataContextId is not null ||
            state.StepUiText.Length != 0 ||
            state.StepUiIsChecked is not null ||
            state.StepUiOpacity is not null)
        {
            throw new InvalidOperationException(
                "The Todo task did not retain a clean zero-step detail state.");
        }
    }
}

internal sealed class AotManagedUiTodoStepsPersistenceEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool InitialStepUiProjected { get; set; }
    public bool StepTextEditObserved { get; set; }
    public bool StepCompletionRoundTripObserved { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public AotManagedUiTodoStateEvidence Before { get; set; } = new();
    public AotManagedUiTodoStateEvidence? AfterStepMutation { get; set; }
    public AotManagedUiTodoStateEvidence? AfterStepDelete { get; set; }
    public AotManagedUiTodoStateEvidence After { get; set; } = new();
}
#endif
