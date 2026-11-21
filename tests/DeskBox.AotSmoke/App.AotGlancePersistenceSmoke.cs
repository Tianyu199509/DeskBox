#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox;

public partial class App
{
    private const double AotGlanceBaselineRotationMinutes = 30;
    private const double AotGlanceMutatedRotationMinutes = 0;
    private const double AotGlanceMutatedBackgroundImageTransparency = 0.64;

    private async Task CaptureAotManagedUiGlancePersistenceAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotGlancePersistenceHost host =
            await manager.GetAotGlancePersistenceHostAsync();
        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 && host.HasXamlRoot && host.Visible,
            "GlanceHostReady",
            "The real Glance widget HWND or XamlRoot is unavailable.");

        string fixturePath = ResolveAotGlanceFixturePath(result);
        AotManagedUiGlancePersistenceEvidence evidence =
            result.GlancePersistence ??
            throw new InvalidOperationException("Glance persistence evidence is unavailable.");
        evidence.FixturePath = fixturePath;
        evidence.FixtureLength = new FileInfo(fixturePath).Length;
        evidence.WindowHandle = host.WindowHandle;
        evidence.HasXamlRoot = host.HasXamlRoot;
        evidence.Visible = host.Visible;

        bool beforeMutation = phase == AotManagedUiGlanceVerifyRestorePhase;
        evidence.Before = await CaptureAotGlanceStateAsync(
            host,
            fixturePath,
            expectMutation: beforeMutation);
        RequireAotGlanceState(result, evidence.Before, fixturePath, beforeMutation);

        switch (phase)
        {
            case AotManagedUiGlanceMutatePhase:
                await ApplyAotGlanceMutationAsync(host.ViewModel, fixturePath);
                evidence.After = await CaptureAotGlanceStateAsync(
                    host,
                    fixturePath,
                    expectMutation: true);
                RequireAotGlanceState(result, evidence.After, fixturePath, expectMutation: true);
                RequireAotManagedUi(
                    result,
                    File.Exists(fixturePath),
                    "GlanceOwnedImageRetained",
                    "The owned Glance image disappeared during mutation.");
                break;

            case AotManagedUiGlanceVerifyRestorePhase:
                await RestoreAotGlanceBaselineAsync(host.ViewModel);
                evidence.After = await CaptureAotGlanceStateAsync(
                    host,
                    fixturePath,
                    expectMutation: false);
                RequireAotGlanceState(result, evidence.After, fixturePath, expectMutation: false);
                RequireAotManagedUi(
                    result,
                    File.Exists(fixturePath),
                    "GlanceOwnedImagePreservedAfterRestore",
                    "Restoring Glance preferences must not delete the source image.");
                break;

            case AotManagedUiGlancePostflightPhase:
                evidence.After = await CaptureAotGlanceStateAsync(
                    host,
                    fixturePath,
                    expectMutation: false);
                RequireAotGlanceState(result, evidence.After, fixturePath, expectMutation: false);
                RequireAotManagedUi(
                    result,
                    File.Exists(fixturePath),
                    "GlancePostflightFixtureRetained",
                    "The postflight process unexpectedly changed the owned image fixture.");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Glance persistence phase '{phase}'.");
        }
    }

    private static async Task ApplyAotGlanceMutationAsync(
        GlanceWidgetViewModel viewModel,
        string fixturePath)
    {
        await viewModel.SetLocalImageFilesAsync([fixturePath]);
        await viewModel.SetBackgroundImageTransparencyAsync(
            AotGlanceMutatedBackgroundImageTransparency);
        await viewModel.SetDisplayElementAsync(GlanceDisplayElement.Time, isVisible: true);
        await viewModel.SetDisplayElementAsync(GlanceDisplayElement.Date, isVisible: true);
        await viewModel.SetDisplayElementAsync(GlanceDisplayElement.Year, isVisible: true);
        await viewModel.SetDisplayElementAsync(GlanceDisplayElement.Weekday, isVisible: false);
        await viewModel.SetDisplayElementAsync(GlanceDisplayElement.Calendar, isVisible: false);
        await viewModel.SetLayoutAsync(GlanceLayoutMode.Editorial);
        await viewModel.SetPhotoPlaybackAsync(
            AotGlanceMutatedRotationMinutes,
            randomOrder: false,
            GlanceTransitionMode.None,
            GlanceTransitionSpeed.Fast,
            GlanceReadabilityMode.Strong,
            showPhotoControls: true);
    }

    private static async Task RestoreAotGlanceBaselineAsync(
        GlanceWidgetViewModel viewModel)
    {
        await viewModel.SetLocalImageFilesAsync([]);
        await viewModel.SetBackgroundImageTransparencyAsync(0);
        await viewModel.SetDisplayElementAsync(GlanceDisplayElement.Time, isVisible: true);
        await viewModel.SetDisplayElementAsync(GlanceDisplayElement.Year, isVisible: false);
        await viewModel.SetDisplayElementAsync(GlanceDisplayElement.Date, isVisible: true);
        await viewModel.SetDisplayElementAsync(GlanceDisplayElement.Weekday, isVisible: true);
        await viewModel.SetDisplayElementAsync(GlanceDisplayElement.Calendar, isVisible: false);
        await viewModel.SetLayoutAsync(GlanceLayoutMode.Centered);
        await viewModel.SetPhotoPlaybackAsync(
            AotGlanceBaselineRotationMinutes,
            randomOrder: false,
            GlanceTransitionMode.CrossFade,
            GlanceTransitionSpeed.Standard,
            GlanceReadabilityMode.Soft,
            showPhotoControls: true);
    }

    private async Task<AotManagedUiGlanceStateEvidence> CaptureAotGlanceStateAsync(
        AotGlancePersistenceHost host,
        string fixturePath,
        bool expectMutation)
    {
        AotGlanceViewModelSnapshot viewModel =
            await WaitForAotGlanceViewModelAsync(host.ViewModel, fixturePath, expectMutation);
        AotGlanceSurfaceSnapshot surface =
            await host.Surface.WaitForAotGlanceSurfaceAsync(
                expectMutation ? fixturePath : null,
                expectMutation ? GlanceLayoutMode.Editorial : GlanceLayoutMode.Centered,
                expectImage: expectMutation,
                expectedBackgroundImageOpacity: expectMutation
                    ? 1.0 - AotGlanceMutatedBackgroundImageTransparency
                    : 1.0);
        GlanceWidgetData store = await GlanceWidgetStore
            .ForWidget(AotManagedUiGlanceWidgetId)
            .LoadAsync();

        return new AotManagedUiGlanceStateEvidence
        {
            Store = MapAotGlanceStore(store),
            ViewModel = MapAotGlanceViewModel(viewModel),
            Surface = MapAotGlanceSurface(surface)
        };
    }

    private static async Task<AotGlanceViewModelSnapshot> WaitForAotGlanceViewModelAsync(
        GlanceWidgetViewModel viewModel,
        string fixturePath,
        bool expectMutation)
    {
        AotGlanceViewModelSnapshot last = viewModel.CaptureAotGlanceSnapshot();
        for (int attempt = 0; attempt < 120; attempt++)
        {
            last = viewModel.CaptureAotGlanceSnapshot();
            bool ready = expectMutation
                ? IsAotGlanceViewModelMutation(last, fixturePath)
                : IsAotGlanceViewModelBaseline(last);
            if (ready)
            {
                return last;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            $"The Glance ViewModel did not stabilize. Snapshot={last}");
    }

    private string ResolveAotGlanceFixturePath(AotManagedUiSmokeResult result)
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            AotManagedUiGlanceFixtureEnvironmentVariable);
        string previewRoot = DeskBoxDataPathService.Current.RootPath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            RequireAotManagedUi(
                result,
                condition: false,
                "GlanceFixtureConfigured",
                "The Glance persistence scenario requires an owned image fixture.");
        }

        string path = Path.GetFullPath(configuredPath!);
        RequireAotManagedUi(
            result,
            IsAotManagedUiPathEqualOrInside(previewRoot, path) &&
            !IsAotManagedUiPathEqual(previewRoot, path) &&
            string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(path),
            "GlanceFixtureOwned",
            "The Glance image fixture must be an existing PNG inside the isolated preview root.");
        return path;
    }

    private static AotManagedUiGlanceStoreEvidence MapAotGlanceStore(
        GlanceWidgetData store)
    {
        return new AotManagedUiGlanceStoreEvidence
        {
            Version = store.Version,
            BackgroundSource = store.BackgroundSource.ToString(),
            LocalImagePaths = store.LocalImagePaths.ToList(),
            LocalFolderPath = store.LocalFolderPath,
            ShowTime = store.ShowTime,
            ShowDate = store.ShowDate,
            ShowYear = store.ShowYear,
            ShowWeekday = store.ShowWeekday,
            ShowCalendar = store.ShowCalendar,
            Layout = store.Layout.ToString(),
            RotationIntervalMinutes = store.RotationIntervalMinutes,
            RandomOrder = store.RandomOrder,
            Transition = store.Transition.ToString(),
            TransitionSpeed = store.TransitionSpeed.ToString(),
            Readability = store.Readability.ToString(),
            BackgroundImageTransparency = store.BackgroundImageTransparency,
            ShowPhotoControls = store.ShowPhotoControls
        };
    }

    private static AotManagedUiGlanceViewModelEvidence MapAotGlanceViewModel(
        AotGlanceViewModelSnapshot viewModel)
    {
        return new AotManagedUiGlanceViewModelEvidence
        {
            BackgroundSource = viewModel.BackgroundSource,
            LocalImagePaths = viewModel.LocalImagePaths.ToList(),
            ShowTime = viewModel.ShowTime,
            ShowDate = viewModel.ShowDate,
            ShowYear = viewModel.ShowYear,
            ShowWeekday = viewModel.ShowWeekday,
            ShowCalendar = viewModel.ShowCalendar,
            Layout = viewModel.Layout,
            RotationIntervalMinutes = viewModel.RotationIntervalMinutes,
            RandomOrder = viewModel.RandomOrder,
            Transition = viewModel.Transition,
            TransitionSpeed = viewModel.TransitionSpeed,
            Readability = viewModel.Readability,
            BackgroundImageTransparency = viewModel.BackgroundImageTransparency,
            BackgroundImageOpacity = viewModel.BackgroundImageOpacity,
            ShowPhotoControlsSetting = viewModel.ShowPhotoControlsSetting,
            ImageCount = viewModel.ImageCount,
            CurrentImagePath = viewModel.CurrentImagePath,
            HasCurrentImage = viewModel.HasCurrentImage,
            IsCenteredLayout = viewModel.IsCenteredLayout,
            IsEditorialLayout = viewModel.IsEditorialLayout,
            ReadabilityOpacity = viewModel.ReadabilityOpacity,
            ShowPhotoControls = viewModel.ShowPhotoControls
        };
    }

    private static AotManagedUiGlanceSurfaceEvidence MapAotGlanceSurface(
        AotGlanceSurfaceSnapshot surface)
    {
        return new AotManagedUiGlanceSurfaceEvidence
        {
            IsLoaded = surface.IsLoaded,
            HasXamlRoot = surface.HasXamlRoot,
            DataContextMatchesViewModel = surface.DataContextMatchesViewModel,
            ActualWidth = surface.ActualWidth,
            ActualHeight = surface.ActualHeight,
            DecodedImagePath = surface.DecodedImagePath,
            BackgroundAHasBrush = surface.BackgroundAHasBrush,
            BackgroundBHasBrush = surface.BackgroundBHasBrush,
            ActiveBackgroundIsImageBrush = surface.ActiveBackgroundIsImageBrush,
            ActiveImageUri = surface.ActiveImageUri,
            ActiveBackgroundOpacity = surface.ActiveBackgroundOpacity,
            BackgroundImageLayerOpacity = surface.BackgroundImageLayerOpacity,
            ImageStretch = surface.ImageStretch,
            ImageAlignmentX = surface.ImageAlignmentX,
            ImageAlignmentY = surface.ImageAlignmentY,
            ImmersiveLayoutVisible = surface.ImmersiveLayoutVisible,
            CenteredLayoutVisible = surface.CenteredLayoutVisible,
            EditorialLayoutVisible = surface.EditorialLayoutVisible,
            CalendarLayoutVisible = surface.CalendarLayoutVisible,
            ReadabilityLayerVisible = surface.ReadabilityLayerVisible,
            ReadabilityLayerOpacity = surface.ReadabilityLayerOpacity,
            ActionLayerVisible = surface.ActionLayerVisible
        };
    }

    private static void RequireAotGlanceState(
        AotManagedUiSmokeResult result,
        AotManagedUiGlanceStateEvidence state,
        string fixturePath,
        bool expectMutation)
    {
        bool valid = expectMutation
            ? IsAotGlanceMutation(state, fixturePath)
            : IsAotGlanceBaseline(state);
        RequireAotManagedUi(
            result,
            valid,
            expectMutation ? "GlanceMutationApplied" : "GlanceBaselineVerified",
            expectMutation
                ? "The persisted Glance mutation did not reach the store, ViewModel, and real surface."
                : "The Glance baseline was not restored across the store, ViewModel, and real surface.");
    }

    private static bool IsAotGlanceBaseline(AotManagedUiGlanceStateEvidence state)
    {
        return IsAotGlanceStoreBaseline(state.Store) &&
            IsAotGlanceViewModelBaseline(state.ViewModel) &&
            state.Surface.IsLoaded &&
            state.Surface.HasXamlRoot &&
            state.Surface.DataContextMatchesViewModel &&
            state.Surface.ActualWidth > 0 &&
            state.Surface.ActualHeight > 0 &&
            state.Surface.DecodedImagePath is null &&
            !state.Surface.BackgroundAHasBrush &&
            !state.Surface.BackgroundBHasBrush &&
            Math.Abs(state.Surface.BackgroundImageLayerOpacity - 1.0) < 0.001 &&
            state.Surface.ActiveImageUri is null &&
            !state.Surface.ImmersiveLayoutVisible &&
            state.Surface.CenteredLayoutVisible &&
            !state.Surface.EditorialLayoutVisible &&
            !state.Surface.CalendarLayoutVisible &&
            !state.Surface.ReadabilityLayerVisible &&
            !state.Surface.ActionLayerVisible;
    }

    private static bool IsAotGlanceMutation(
        AotManagedUiGlanceStateEvidence state,
        string fixturePath)
    {
        return IsAotGlanceStoreMutation(state.Store, fixturePath) &&
            IsAotGlanceViewModelMutation(state.ViewModel, fixturePath) &&
            state.Surface.IsLoaded &&
            state.Surface.HasXamlRoot &&
            state.Surface.DataContextMatchesViewModel &&
            state.Surface.ActualWidth > 0 &&
            state.Surface.ActualHeight > 0 &&
            string.Equals(state.Surface.DecodedImagePath, fixturePath, StringComparison.OrdinalIgnoreCase) &&
            state.Surface.ActiveBackgroundIsImageBrush &&
            string.Equals(state.Surface.ActiveImageUri, fixturePath, StringComparison.OrdinalIgnoreCase) &&
            state.Surface.ActiveBackgroundOpacity > 0.99 &&
            Math.Abs(
                state.Surface.BackgroundImageLayerOpacity -
                (1.0 - AotGlanceMutatedBackgroundImageTransparency)) < 0.001 &&
            string.Equals(state.Surface.ImageStretch, "UniformToFill", StringComparison.Ordinal) &&
            string.Equals(state.Surface.ImageAlignmentX, "Center", StringComparison.Ordinal) &&
            string.Equals(state.Surface.ImageAlignmentY, "Center", StringComparison.Ordinal) &&
            !state.Surface.ImmersiveLayoutVisible &&
            !state.Surface.CenteredLayoutVisible &&
            state.Surface.EditorialLayoutVisible &&
            !state.Surface.CalendarLayoutVisible &&
            state.Surface.ReadabilityLayerVisible &&
            Math.Abs(state.Surface.ReadabilityLayerOpacity - 0.5) < 0.001 &&
            state.Surface.ActionLayerVisible;
    }

    private static bool IsAotGlanceStoreBaseline(AotManagedUiGlanceStoreEvidence store)
    {
        return store.Version == GlanceWidgetData.CurrentVersion &&
            store.BackgroundSource == nameof(GlanceBackgroundSource.LocalFiles) &&
            store.LocalImagePaths.Count == 0 &&
            store.LocalFolderPath is null &&
            store.ShowTime &&
            store.ShowDate &&
            !store.ShowYear &&
            store.ShowWeekday &&
            !store.ShowCalendar &&
            store.Layout == nameof(GlanceLayoutMode.Centered) &&
            Math.Abs(store.RotationIntervalMinutes - AotGlanceBaselineRotationMinutes) < 0.001 &&
            !store.RandomOrder &&
            store.Transition == nameof(GlanceTransitionMode.CrossFade) &&
            store.TransitionSpeed == nameof(GlanceTransitionSpeed.Standard) &&
            store.Readability == nameof(GlanceReadabilityMode.Soft) &&
            Math.Abs(store.BackgroundImageTransparency) < 0.001 &&
            store.ShowPhotoControls;
    }

    private static bool IsAotGlanceStoreMutation(
        AotManagedUiGlanceStoreEvidence store,
        string fixturePath)
    {
        return store.Version == GlanceWidgetData.CurrentVersion &&
            store.BackgroundSource == nameof(GlanceBackgroundSource.LocalFiles) &&
            store.LocalImagePaths.Count == 1 &&
            string.Equals(store.LocalImagePaths[0], fixturePath, StringComparison.OrdinalIgnoreCase) &&
            store.LocalFolderPath is null &&
            store.ShowTime &&
            store.ShowDate &&
            store.ShowYear &&
            !store.ShowWeekday &&
            !store.ShowCalendar &&
            store.Layout == nameof(GlanceLayoutMode.Editorial) &&
            Math.Abs(store.RotationIntervalMinutes - AotGlanceMutatedRotationMinutes) < 0.001 &&
            !store.RandomOrder &&
            store.Transition == nameof(GlanceTransitionMode.None) &&
            store.TransitionSpeed == nameof(GlanceTransitionSpeed.Fast) &&
            store.Readability == nameof(GlanceReadabilityMode.Strong) &&
            Math.Abs(
                store.BackgroundImageTransparency -
                AotGlanceMutatedBackgroundImageTransparency) < 0.001 &&
            store.ShowPhotoControls;
    }

    private static bool IsAotGlanceViewModelBaseline(
        AotGlanceViewModelSnapshot viewModel)
    {
        return viewModel.BackgroundSource == nameof(GlanceBackgroundSource.LocalFiles) &&
            viewModel.LocalImagePaths.Count == 0 &&
            viewModel.ShowTime &&
            viewModel.ShowDate &&
            !viewModel.ShowYear &&
            viewModel.ShowWeekday &&
            !viewModel.ShowCalendar &&
            viewModel.Layout == nameof(GlanceLayoutMode.Centered) &&
            Math.Abs(viewModel.RotationIntervalMinutes - AotGlanceBaselineRotationMinutes) < 0.001 &&
            !viewModel.RandomOrder &&
            viewModel.Transition == nameof(GlanceTransitionMode.CrossFade) &&
            viewModel.TransitionSpeed == nameof(GlanceTransitionSpeed.Standard) &&
            viewModel.Readability == nameof(GlanceReadabilityMode.Soft) &&
            Math.Abs(viewModel.BackgroundImageTransparency) < 0.001 &&
            Math.Abs(viewModel.BackgroundImageOpacity - 1.0) < 0.001 &&
            viewModel.ShowPhotoControlsSetting &&
            viewModel.ImageCount == 0 &&
            viewModel.CurrentImagePath is null &&
            !viewModel.HasCurrentImage &&
            viewModel.IsCenteredLayout &&
            !viewModel.IsEditorialLayout &&
            Math.Abs(viewModel.ReadabilityOpacity - 0.28) < 0.001 &&
            !viewModel.ShowPhotoControls;
    }

    private static bool IsAotGlanceViewModelBaseline(
        AotManagedUiGlanceViewModelEvidence viewModel)
    {
        return viewModel.BackgroundSource == nameof(GlanceBackgroundSource.LocalFiles) &&
            viewModel.LocalImagePaths.Count == 0 &&
            viewModel.ShowTime &&
            viewModel.ShowDate &&
            !viewModel.ShowYear &&
            viewModel.ShowWeekday &&
            !viewModel.ShowCalendar &&
            viewModel.Layout == nameof(GlanceLayoutMode.Centered) &&
            Math.Abs(viewModel.RotationIntervalMinutes - AotGlanceBaselineRotationMinutes) < 0.001 &&
            !viewModel.RandomOrder &&
            viewModel.Transition == nameof(GlanceTransitionMode.CrossFade) &&
            viewModel.TransitionSpeed == nameof(GlanceTransitionSpeed.Standard) &&
            viewModel.Readability == nameof(GlanceReadabilityMode.Soft) &&
            Math.Abs(viewModel.BackgroundImageTransparency) < 0.001 &&
            Math.Abs(viewModel.BackgroundImageOpacity - 1.0) < 0.001 &&
            viewModel.ShowPhotoControlsSetting &&
            viewModel.ImageCount == 0 &&
            viewModel.CurrentImagePath is null &&
            !viewModel.HasCurrentImage &&
            viewModel.IsCenteredLayout &&
            !viewModel.IsEditorialLayout &&
            Math.Abs(viewModel.ReadabilityOpacity - 0.28) < 0.001 &&
            !viewModel.ShowPhotoControls;
    }

    private static bool IsAotGlanceViewModelMutation(
        AotGlanceViewModelSnapshot viewModel,
        string fixturePath)
    {
        return viewModel.BackgroundSource == nameof(GlanceBackgroundSource.LocalFiles) &&
            viewModel.LocalImagePaths.Count == 1 &&
            string.Equals(viewModel.LocalImagePaths[0], fixturePath, StringComparison.OrdinalIgnoreCase) &&
            viewModel.ShowTime &&
            viewModel.ShowDate &&
            viewModel.ShowYear &&
            !viewModel.ShowWeekday &&
            !viewModel.ShowCalendar &&
            viewModel.Layout == nameof(GlanceLayoutMode.Editorial) &&
            Math.Abs(viewModel.RotationIntervalMinutes - AotGlanceMutatedRotationMinutes) < 0.001 &&
            !viewModel.RandomOrder &&
            viewModel.Transition == nameof(GlanceTransitionMode.None) &&
            viewModel.TransitionSpeed == nameof(GlanceTransitionSpeed.Fast) &&
            viewModel.Readability == nameof(GlanceReadabilityMode.Strong) &&
            Math.Abs(
                viewModel.BackgroundImageTransparency -
                AotGlanceMutatedBackgroundImageTransparency) < 0.001 &&
            Math.Abs(
                viewModel.BackgroundImageOpacity -
                (1.0 - AotGlanceMutatedBackgroundImageTransparency)) < 0.001 &&
            viewModel.ShowPhotoControlsSetting &&
            viewModel.ImageCount == 1 &&
            string.Equals(viewModel.CurrentImagePath, fixturePath, StringComparison.OrdinalIgnoreCase) &&
            viewModel.HasCurrentImage &&
            !viewModel.IsCenteredLayout &&
            viewModel.IsEditorialLayout &&
            Math.Abs(viewModel.ReadabilityOpacity - 0.5) < 0.001 &&
            viewModel.ShowPhotoControls;
    }

    private static bool IsAotGlanceViewModelMutation(
        AotManagedUiGlanceViewModelEvidence viewModel,
        string fixturePath)
    {
        return viewModel.BackgroundSource == nameof(GlanceBackgroundSource.LocalFiles) &&
            viewModel.LocalImagePaths.Count == 1 &&
            string.Equals(viewModel.LocalImagePaths[0], fixturePath, StringComparison.OrdinalIgnoreCase) &&
            viewModel.ShowTime &&
            viewModel.ShowDate &&
            viewModel.ShowYear &&
            !viewModel.ShowWeekday &&
            !viewModel.ShowCalendar &&
            viewModel.Layout == nameof(GlanceLayoutMode.Editorial) &&
            Math.Abs(viewModel.RotationIntervalMinutes - AotGlanceMutatedRotationMinutes) < 0.001 &&
            !viewModel.RandomOrder &&
            viewModel.Transition == nameof(GlanceTransitionMode.None) &&
            viewModel.TransitionSpeed == nameof(GlanceTransitionSpeed.Fast) &&
            viewModel.Readability == nameof(GlanceReadabilityMode.Strong) &&
            Math.Abs(
                viewModel.BackgroundImageTransparency -
                AotGlanceMutatedBackgroundImageTransparency) < 0.001 &&
            Math.Abs(
                viewModel.BackgroundImageOpacity -
                (1.0 - AotGlanceMutatedBackgroundImageTransparency)) < 0.001 &&
            viewModel.ShowPhotoControlsSetting &&
            viewModel.ImageCount == 1 &&
            string.Equals(viewModel.CurrentImagePath, fixturePath, StringComparison.OrdinalIgnoreCase) &&
            viewModel.HasCurrentImage &&
            !viewModel.IsCenteredLayout &&
            viewModel.IsEditorialLayout &&
            Math.Abs(viewModel.ReadabilityOpacity - 0.5) < 0.001 &&
            viewModel.ShowPhotoControls;
    }
}

internal sealed class AotManagedUiGlancePersistenceEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool NormalShutdownRequested { get; set; }
    public string FixturePath { get; set; } = string.Empty;
    public long FixtureLength { get; set; }
    public long WindowHandle { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool Visible { get; set; }
    public AotManagedUiGlanceStateEvidence Before { get; set; } = new();
    public AotManagedUiGlanceStateEvidence After { get; set; } = new();
}

internal sealed class AotManagedUiGlanceStateEvidence
{
    public AotManagedUiGlanceStoreEvidence Store { get; set; } = new();
    public AotManagedUiGlanceViewModelEvidence ViewModel { get; set; } = new();
    public AotManagedUiGlanceSurfaceEvidence Surface { get; set; } = new();
}

internal sealed class AotManagedUiGlanceStoreEvidence
{
    public int Version { get; set; }
    public string BackgroundSource { get; set; } = string.Empty;
    public List<string> LocalImagePaths { get; set; } = [];
    public string? LocalFolderPath { get; set; }
    public bool ShowTime { get; set; }
    public bool ShowDate { get; set; }
    public bool ShowYear { get; set; }
    public bool ShowWeekday { get; set; }
    public bool ShowCalendar { get; set; }
    public string Layout { get; set; } = string.Empty;
    public double RotationIntervalMinutes { get; set; }
    public bool RandomOrder { get; set; }
    public string Transition { get; set; } = string.Empty;
    public string TransitionSpeed { get; set; } = string.Empty;
    public string Readability { get; set; } = string.Empty;
    public double BackgroundImageTransparency { get; set; }
    public bool ShowPhotoControls { get; set; }
}

internal sealed class AotManagedUiGlanceViewModelEvidence
{
    public string BackgroundSource { get; set; } = string.Empty;
    public List<string> LocalImagePaths { get; set; } = [];
    public bool ShowTime { get; set; }
    public bool ShowDate { get; set; }
    public bool ShowYear { get; set; }
    public bool ShowWeekday { get; set; }
    public bool ShowCalendar { get; set; }
    public string Layout { get; set; } = string.Empty;
    public double RotationIntervalMinutes { get; set; }
    public bool RandomOrder { get; set; }
    public string Transition { get; set; } = string.Empty;
    public string TransitionSpeed { get; set; } = string.Empty;
    public string Readability { get; set; } = string.Empty;
    public double BackgroundImageTransparency { get; set; }
    public double BackgroundImageOpacity { get; set; }
    public bool ShowPhotoControlsSetting { get; set; }
    public int ImageCount { get; set; }
    public string? CurrentImagePath { get; set; }
    public bool HasCurrentImage { get; set; }
    public bool IsCenteredLayout { get; set; }
    public bool IsEditorialLayout { get; set; }
    public double ReadabilityOpacity { get; set; }
    public bool ShowPhotoControls { get; set; }
}

internal sealed class AotManagedUiGlanceSurfaceEvidence
{
    public bool IsLoaded { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool DataContextMatchesViewModel { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string? DecodedImagePath { get; set; }
    public bool BackgroundAHasBrush { get; set; }
    public bool BackgroundBHasBrush { get; set; }
    public bool ActiveBackgroundIsImageBrush { get; set; }
    public string? ActiveImageUri { get; set; }
    public double ActiveBackgroundOpacity { get; set; }
    public double BackgroundImageLayerOpacity { get; set; }
    public string? ImageStretch { get; set; }
    public string? ImageAlignmentX { get; set; }
    public string? ImageAlignmentY { get; set; }
    public bool ImmersiveLayoutVisible { get; set; }
    public bool CenteredLayoutVisible { get; set; }
    public bool EditorialLayoutVisible { get; set; }
    public bool CalendarLayoutVisible { get; set; }
    public bool ReadabilityLayerVisible { get; set; }
    public double ReadabilityLayerOpacity { get; set; }
    public bool ActionLayerVisible { get; set; }
}
#endif
