namespace DeskBox.Tests;

/// <summary>
/// Source-scan contract for the todo orphan-data cleanup wiring (pluginization
/// roadmap section 7, stage 2 data hygiene). The deletion logic itself is
/// covered by TodoWidgetStoreTests against an isolated root; these assertions
/// pin the two WidgetManager call sites so the hooks cannot be removed
/// silently. House precedent: IdleRuntimeLifecycleContractTests-style
/// Contains checks over production sources.
/// </summary>
public sealed class TodoOrphanCleanupContractTests
{
    [Fact]
    public void RemoveWidgetAsync_DeletesTodoStoreBeforeFeatureDisableCheck()
    {
        string manager = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/WidgetManager.cs"));

        int todoBranch = manager.IndexOf(
            "else if (config.WidgetKind == WidgetKind.Todo)",
            StringComparison.Ordinal);
        Assert.True(todoBranch >= 0, "RemoveWidgetAsync must handle WidgetKind.Todo.");

        int deleteCall = manager.IndexOf(
            "await TodoWidgetStore.DeleteForWidgetAsync(config.Id);",
            StringComparison.Ordinal);
        int remainingCheck = manager.IndexOf(
            "hasRemainingTodoWidget",
            StringComparison.Ordinal);
        int glanceBranch = manager.IndexOf(
            "if (config.WidgetKind == WidgetKind.Glance)",
            StringComparison.Ordinal);

        Assert.True(deleteCall > todoBranch, "The todo branch must delete the widget store.");
        Assert.True(
            remainingCheck > deleteCall,
            "The feature may only be disabled after checking for remaining todo widgets.");
        Assert.True(
            glanceBranch >= 0 && todoBranch > glanceBranch,
            "The todo branch mirrors the glance branch structure.");
    }

    [Fact]
    public void ResetFeatureWidgetAsync_DeletesDataForDuplicateTodoWidgets()
    {
        string features = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"));

        int duplicateLoop = features.IndexOf(
            "ResetFeatureWidget removed duplicate",
            StringComparison.Ordinal);
        Assert.True(duplicateLoop >= 0, "Duplicate removal loop not found.");

        string duplicateRegion = features[..duplicateLoop];
        Assert.Contains(
            "else if (kind == WidgetKind.Todo)",
            duplicateRegion,
            StringComparison.Ordinal);
        Assert.Contains(
            "await TodoWidgetStore.DeleteForWidgetAsync(duplicate.Id);",
            duplicateRegion,
            StringComparison.Ordinal);
    }
}
