using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Controls.WidgetContents;

public sealed class CustomWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetFeedbackSource,
    IWidgetResponsiveLayoutContent,
    IWidgetGroupContentCacheable
{
    public CustomWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        CustomWidgetDataStore? store = null,
        Func<CustomWidgetViewModel, FrameworkElement>? viewFactory = null)
        : this(
            config,
            new CustomWidgetViewModel(
                config,
                localizationService,
                store,
                notificationServiceProvider: static () => App.Current?.NativeNotificationService),
            viewFactory)
    {
    }

    private CustomWidgetContentAdapter(
        WidgetConfig config,
        CustomWidgetViewModel viewModel,
        Func<CustomWidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new CustomWidgetContent(vm)))(viewModel))
    {
        if (config.WidgetKind != WidgetKind.Custom)
        {
            throw new ArgumentException("Custom widget content requires a Custom widget config.", nameof(config));
        }

        ViewModel = viewModel;
        ViewModel.FeedbackRequested += ViewModel_FeedbackRequested;
        ViewModel.ManifestIdentityChanged += ViewModel_ManifestIdentityChanged;
    }

    public CustomWidgetViewModel ViewModel { get; }

    public bool IsReadyForReuse =>
        !IsDisposed && (MaterializedView is not null || ViewModel.IsInitialized);

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    private void ViewModel_FeedbackRequested(
        object? sender,
        WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    private void ViewModel_ManifestIdentityChanged(string newName, string newIcon)
    {
        App.Current?.SettingsService.UpdateWidget(Config);
    }

    public override Task InitializeAsync() => ViewModel.InitializeAsync();

    public override Task RefreshAsync() => ViewModel.ReloadManifestFromDiskAsync();

    public override void ApplyAppearance()
    {
        if (MaterializedView is CustomWidgetContent content)
        {
            content.RequestCoalescedRebuild();
        }
    }

    private bool _isCompactCollapsed;

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        base.OnViewMaterialized(view);
        if (view is CustomWidgetContent content)
        {
            content.OnCompactStateChanged(_isCompactCollapsed);
        }
    }

    public override void OnWindowVisibilityChanged(bool visible) =>
        ViewModel.OnWindowVisibilityChanged(visible);

    public override void OnWindowLongHidden()
    {
        // Leaf hibernation is owned exclusively by WidgetShell.HibernateHostedContentView()
        // after detaching ShellContent when collapsed.
    }

    public override void OnCompactStateChanged(bool collapsed)
    {
        _isCompactCollapsed = collapsed;
        ViewModel.OnCompactStateChanged(collapsed);
        (MaterializedView as CustomWidgetContent)?.OnCompactStateChanged(collapsed);
    }

    public bool CanImportExternalDrop(DataPackageView dataView) =>
        ViewModel.AcceptsFileDrops &&
        dataView.Contains(StandardDataFormats.StorageItems);

    public async Task<bool> ImportExternalDropAsync(DataPackageView dataView)
    {
        if (!CanImportExternalDrop(dataView))
        {
            return false;
        }

        try
        {
            IReadOnlyList<IStorageItem> items = await dataView.GetStorageItemsAsync();
            string[] paths = items
                .Select(i => i.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
            return await ViewModel.ImportDroppedFilesAsync(paths);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Capsule drop import failed: {ex.Message}");
            return false;
        }
    }

    public Task<bool> ImportNativeDroppedFilesAsync(IReadOnlyList<string> paths) =>
        ViewModel.ImportDroppedFilesAsync(paths);

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
    }

    public void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
    }

    public void CancelResponsiveLayoutTransition()
    {
    }

    protected override void OnHibernatingView(FrameworkElement view)
    {
        (view as CustomWidgetContent)?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        ViewModel.FeedbackRequested -= ViewModel_FeedbackRequested;
        ViewModel.ManifestIdentityChanged -= ViewModel_ManifestIdentityChanged;
        if (MaterializedView is CustomWidgetContent content)
        {
            content.Dispose();
        }
        ViewModel.Dispose();
    }
}
