using DeskBox.Contracts;
using DeskBox.Features.FileStack;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FileStackSettingsCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SectionWrites_PersistThroughThePort_AndSkipUnchangedSaves()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FileStackSettingsCoordinator(settings);
        var editor = new FileStackSettingsViewModel(coordinator);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.SetFileStacksEnabled(false);
        editor.SetFileStackAutoStacking(true);
        editor.SetFileStackGroupBy(SettingsService.FileStackGroupByCustom);
        editor.SetFileStackThreshold(5);
        editor.SetFileStackOrderBy(SettingsService.FileStackOrderByDateModified);
        editor.SetFileStackOpenMode(SettingsService.FileStackOpenModePopover);
        editor.SetFileStackPopoverLayout(SettingsService.FileStackPopoverLayoutAdaptive);
        editor.SetFileStackPopoverStyle(SettingsService.FileStackPopoverStyleFollowMaterial);
        editor.SetFileStackUnmatchedBehavior(SettingsService.FileStackUnmatchedOther);
        editor.SetFileStackCustomRules(
        [
            new FileStackCustomRule { Id = "r1", Name = "Docs", Extensions = ["pdf", "docx"] },
            new FileStackCustomRule { Id = "r2", Name = "Images", Extensions = ["png"] },
        ]);

        FileWidgetSettingsSlice slice = settings.Settings.FileWidget;
        Assert.False(slice.FileStacksEnabled);
        Assert.True(slice.FileStackAutoStacking);
        Assert.Equal(SettingsService.FileStackGroupByCustom, slice.FileStackGroupBy);
        Assert.Equal(5, slice.FileStackThreshold);
        Assert.Equal(SettingsService.FileStackOrderByDateModified, slice.FileStackOrderBy);
        Assert.Equal(SettingsService.FileStackOpenModePopover, slice.FileStackOpenMode);
        Assert.Equal(SettingsService.FileStackPopoverLayoutAdaptive, slice.FileStackPopoverLayout);
        Assert.Equal(SettingsService.FileStackPopoverStyleFollowMaterial, slice.FileStackPopoverStyle);
        Assert.Equal(SettingsService.FileStackUnmatchedOther, slice.FileStackUnmatchedBehavior);
        Assert.Equal(2, slice.FileStackCustomRules.Count);

        // Ten real changes fire ten SettingsChanged passes; unchanged writes
        // must not save again — the stack projection rebuild only reacts to
        // real passes.
        Assert.Equal(10, notified);
        editor.SetFileStacksEnabled(false);
        editor.SetFileStackGroupBy(SettingsService.FileStackGroupByCustom);
        editor.SetFileStackUnmatchedBehavior(SettingsService.FileStackUnmatchedOther);
        Assert.Equal(10, notified);
    }

    [Fact]
    public void CustomRules_SingleWriteEntry_SkipsEquivalentRecommits_AndSavesRealChanges()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FileStackSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        coordinator.SetFileStackCustomRules(
        [
            new FileStackCustomRule { Id = "r1", Name = "Docs", Extensions = ["pdf", "docx"] },
            new FileStackCustomRule { Id = "r2", Name = "Images", Extensions = ["png"] },
        ]);
        Assert.Equal(1, notified);

        // A drag that lands back in the original order re-commits the same
        // projection; whitespace names and differently-cased/duplicated
        // extension entries normalize to the stored list, so no save.
        coordinator.SetFileStackCustomRules(
        [
            new FileStackCustomRule { Id = "r1", Name = "  Docs  ", Extensions = ["*.PDF", "pdf", "DocX"] },
            new FileStackCustomRule { Id = "r2", Name = "Images", Extensions = ["PNG"] },
        ]);
        Assert.Equal(1, notified);

        // A real reorder (rules evaluate in list order) is a change.
        coordinator.SetFileStackCustomRules(
        [
            new FileStackCustomRule { Id = "r2", Name = "Images", Extensions = ["png"] },
            new FileStackCustomRule { Id = "r1", Name = "Docs", Extensions = ["pdf", "docx"] },
        ]);
        Assert.Equal(2, notified);
        Assert.Equal("r2", settings.Settings.FileWidget.FileStackCustomRules[0].Id);

        // A rule edit, an add and a removal each go through the same single
        // write entry.
        coordinator.SetFileStackCustomRules(
        [
            new FileStackCustomRule { Id = "r2", Name = "Pictures", Extensions = ["png"] },
            new FileStackCustomRule { Id = "r1", Name = "Docs", Extensions = ["pdf"] },
        ]);
        coordinator.SetFileStackCustomRules([]);
        Assert.Equal(4, notified);
        Assert.Empty(settings.Settings.FileWidget.FileStackCustomRules);

        // Clearing an already-empty collection is a no-op.
        coordinator.SetFileStackCustomRules(null);
        Assert.Equal(4, notified);
    }

    [Fact]
    public async Task FileStackWrites_RoundTripThroughDisk()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FileStackSettingsCoordinator(settings);

        coordinator.SetFileStacksEnabled(true);
        coordinator.SetFileStackAutoStacking(true);
        coordinator.SetFileStackGroupBy(SettingsService.FileStackGroupByCustom);
        coordinator.SetFileStackThreshold(2);
        coordinator.SetFileStackOrderBy(SettingsService.FileStackOrderByName);
        coordinator.SetFileStackOpenMode(SettingsService.FileStackOpenModePopover);
        coordinator.SetFileStackPopoverLayout(SettingsService.FileStackPopoverLayoutGrid5);
        coordinator.SetFileStackPopoverStyle(SettingsService.FileStackPopoverStyleFollowMaterial);
        coordinator.SetFileStackUnmatchedBehavior(SettingsService.FileStackUnmatchedOther);
        coordinator.SetFileStackCustomRules(
        [
            new FileStackCustomRule { Id = "r1", Name = "Docs", Extensions = ["pdf"] },
            new FileStackCustomRule { Id = "r2", Name = "Archives", Extensions = ["zip", "7z"] },
        ]);
        await settings.SaveAsync();

        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        FileStackSettingsSnapshot snapshot =
            new FileStackSettingsCoordinator(reloaded).ReadAll();
        Assert.True(snapshot.FileStacksEnabled);
        Assert.True(snapshot.FileStackAutoStacking);
        Assert.Equal(SettingsService.FileStackGroupByCustom, snapshot.FileStackGroupBy);
        Assert.Equal(2, snapshot.FileStackThreshold);
        Assert.Equal(SettingsService.FileStackOrderByName, snapshot.FileStackOrderBy);
        Assert.Equal(SettingsService.FileStackOpenModePopover, snapshot.FileStackOpenMode);
        Assert.Equal(SettingsService.FileStackPopoverLayoutGrid5, snapshot.FileStackPopoverLayout);
        Assert.Equal(SettingsService.FileStackPopoverStyleFollowMaterial, snapshot.FileStackPopoverStyle);
        Assert.Equal(SettingsService.FileStackUnmatchedOther, snapshot.FileStackUnmatchedBehavior);
        Assert.Equal(2, snapshot.FileStackCustomRules.Count);
        Assert.Equal("r1", snapshot.FileStackCustomRules[0].Id);
        Assert.Equal([".pdf"], snapshot.FileStackCustomRules[0].Extensions);
        Assert.Equal([".zip", ".7z"], snapshot.FileStackCustomRules[1].Extensions);
    }

    [Fact]
    public void InvalidOptions_NormalizeLikeTheSettingsPageDid()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FileStackSettingsCoordinator(settings);

        // Invalid option values collapse to the page's normalization
        // defaults before the unchanged-write skip, exactly like the shell
        // setters normalized their view state before persisting.
        coordinator.SetFileStackGroupBy("Nonsense");
        Assert.Equal(SettingsService.FileStackGroupByKind,
            settings.Settings.FileWidget.FileStackGroupBy);
        coordinator.SetFileStackThreshold(7);
        Assert.Equal(SettingsService.DefaultFileStackThreshold,
            settings.Settings.FileWidget.FileStackThreshold);
        coordinator.SetFileStackOrderBy(null);
        Assert.Equal(SettingsService.FileStackOrderByWidget,
            settings.Settings.FileWidget.FileStackOrderBy);
        coordinator.SetFileStackOpenMode("Sidecar");
        Assert.Equal(SettingsService.FileStackOpenModeInline,
            settings.Settings.FileWidget.FileStackOpenMode);
        coordinator.SetFileStackPopoverLayout("Grid4");
        Assert.Equal(SettingsService.FileStackPopoverLayoutAdaptive,
            settings.Settings.FileWidget.FileStackPopoverLayout);
        coordinator.SetFileStackPopoverStyle(string.Empty);
        Assert.Equal(SettingsService.FileStackPopoverStyleNeutral,
            settings.Settings.FileWidget.FileStackPopoverStyle);
        coordinator.SetFileStackUnmatchedBehavior("Discard");
        Assert.Equal(SettingsService.FileStackUnmatchedKeepLoose,
            settings.Settings.FileWidget.FileStackUnmatchedBehavior);
    }

    [Fact]
    public async Task StoppedCoordinator_RejectsFurtherWrites()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FileStackSettingsCoordinator(settings);
        coordinator.Stop();

        Assert.Throws<ObjectDisposedException>(() => coordinator.SetFileStacksEnabled(false));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetFileStackAutoStacking(true));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetFileStackGroupBy(SettingsService.FileStackGroupByCustom));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetFileStackThreshold(2));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetFileStackOrderBy(SettingsService.FileStackOrderByName));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetFileStackOpenMode(SettingsService.FileStackOpenModePopover));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetFileStackPopoverLayout(SettingsService.FileStackPopoverLayoutGrid5));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetFileStackPopoverStyle(SettingsService.FileStackPopoverStyleFollowMaterial));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetFileStackUnmatchedBehavior(SettingsService.FileStackUnmatchedOther));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetFileStackCustomRules(
                [new FileStackCustomRule { Id = "r1", Name = "Docs", Extensions = ["pdf"] }]));
        Assert.True(coordinator.IsStopped);

        await settings.SaveAsync();
        Assert.True(settings.Settings.FileWidget.FileStacksEnabled);
        Assert.False(settings.Settings.FileWidget.FileStackAutoStacking);
        Assert.Empty(settings.Settings.FileWidget.FileStackCustomRules);
    }
}
