using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox.Tests;

/// <summary>
/// The folder-mapping conflict flow explains itself in an in-app window
/// (WeChat case 2026-10-27): the old toast-plus-unasked-settings-jump was
/// invisible on machines where notifications never render — elevated
/// processes above all — so users read the rejection as "confirm does
/// nothing". These pins keep the buttons matched to what each conflict can
/// actually resolve, the conflict outcome logged (the flow used to be
/// zero-log, which misdirected the first diagnostics round), and the
/// settings jump behind an explicit user choice.
/// </summary>
public sealed class FolderMappingConflictDialogContractTests
{
    [Fact]
    public void PlanButtons_OpenWidgetOnlyForExistingWidgetConflicts()
    {
        var plan = FolderMappingConflictWindow.PlanButtons(
            FileWidgetPathConflictKind.ExistingWidget,
            FileWidgetPathRelation.CandidateInsideOther);
        Assert.True(plan.ShowOpenWidget);
        Assert.False(plan.ShowChangeStorage);
    }

    [Fact]
    public void PlanButtons_InsideStorageTreeOffersNeitherSettingsNorReveal()
    {
        // Moving the storage root migrates the whole tree with it: a folder
        // inside the tree stays inside, so "change storage location" would
        // be a dead-end action and must not be offered.
        var plan = FolderMappingConflictWindow.PlanButtons(
            FileWidgetPathConflictKind.ManagedStorageRoot,
            FileWidgetPathRelation.CandidateInsideOther);
        Assert.False(plan.ShowOpenWidget);
        Assert.False(plan.ShowChangeStorage);
    }

    [Theory]
    [InlineData(nameof(FileWidgetPathRelation.CandidateContainsOther))]
    [InlineData(nameof(FileWidgetPathRelation.SameDirectory))]
    [InlineData(nameof(FileWidgetPathRelation.UnresolvableOverlap))]
    public void PlanButtons_OtherStorageOverlapsOfferChangeStorage(
        string relationName)
    {
        var relation = (FileWidgetPathRelation)Enum.Parse(
            typeof(FileWidgetPathRelation),
            relationName);
        var plan = FolderMappingConflictWindow.PlanButtons(
            FileWidgetPathConflictKind.ManagedStorageRoot,
            relation);
        Assert.False(plan.ShowOpenWidget);
        Assert.True(plan.ShowChangeStorage);
    }

    [Fact]
    public void ConflictHandler_LogsOutcomeAndJumpsOnlyOnUserChoice()
    {
        string tray = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.Tray.cs"));

        // F2: the conflict is visible in diagnostics.
        Assert.Contains("[WidgetMapping] outcome=conflict kind=", tray, StringComparison.Ordinal);

        // The settings jump is an explicit user choice, not part of the
        // rejection itself.
        Assert.DoesNotContain("Widget.MapFolder.ConflictRootHint", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("Widget.MapFolder.ConflictWidgetHint", tray, StringComparison.Ordinal);
        Assert.Contains(
            "case FolderMappingConflictResult.ChangeStorage:",
            tray,
            StringComparison.Ordinal);

        // The inside-tree case carries the "move it out first" guidance.
        Assert.Contains("Widget.MapFolder.ConflictInsideExtraHint", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictDialogKeys_ExistInEveryLanguageAndOldHintsAreGone()
    {
        string[] languages =
        [
            "ar-SA", "bn-BD", "de-DE", "en-US", "es-ES", "fr-FR", "hi-IN",
            "ja-JP", "ko-KR", "pt-BR", "ru-RU", "zh-CN", "zh-TW",
        ];
        string[] newKeys =
        [
            "Widget.MapFolder.ConflictDialogTitle",
            "Widget.MapFolder.ConflictDialogWidgetBody",
            "Widget.MapFolder.ConflictInsideExtraHint",
            "Widget.MapFolder.ConflictDialogOpenWidget",
            "Widget.MapFolder.ConflictDialogPickAnother",
            "Widget.MapFolder.ConflictDialogChangeStorage",
            "Widget.MapFolder.ConflictDialogClose",
        ];

        foreach (string language in languages)
        {
            string source = File.ReadAllText(
                TestPaths.FromRepository($"src/DeskBox/Strings/{language}.json"));
            foreach (string key in newKeys)
            {
                Assert.Contains($"\"{key}\":", source, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("Widget.MapFolder.ConflictRootHint", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Widget.MapFolder.ConflictWidgetHint", source, StringComparison.Ordinal);
        }
    }
}
