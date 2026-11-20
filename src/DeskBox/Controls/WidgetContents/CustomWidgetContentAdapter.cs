using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Controls.WidgetContents;

public sealed class CustomWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetFeedbackSource,
    IWidgetResponsiveLayoutContent,
    IWidgetGroupContentCacheable
{
    private WidgetChromeCategory _lastChromeCategory;
    private bool _isWindowVisible = true;
    private bool _isPowerSuspended;
    private bool _isCompactCollapsed;
    private CustomWidgetContent? _subscribedContent;

    public CustomWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        CustomWidgetDataStore? store = null,
        Func<CustomWidgetViewModel, FrameworkElement>? viewFactory = null,
        SettingsService? settingsService = null)
        : this(
            config,
            new CustomWidgetViewModel(
                config,
                localizationService,
                store,
                notificationServiceProvider: static () => App.Current?.NativeNotificationService,
                settingsService: settingsService),
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
        _lastChromeCategory = CustomWidgetChromeCategoryClassifier.Classify(viewModel.Manifest);
        ViewModel.FeedbackRequested += ViewModel_FeedbackRequested;
        ViewModel.ManifestIdentityChanged += ViewModel_ManifestIdentityChanged;
        ViewModel.ViewRebuildRequested += ViewModel_ViewRebuildRequested;
    }

    public CustomWidgetViewModel ViewModel { get; }

    public FrameworkElement? ActiveContentElement => MaterializedView;

    public bool IsReadyForReuse =>
        !IsDisposed && (MaterializedView is not null || ViewModel.IsInitialized);

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;
    public event EventHandler? ChromePreferenceChanged;
    public event Action<string>? SettingsNavigationRequested;

    private void ViewModel_FeedbackRequested(
        object? sender,
        WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    private void ViewModel_ManifestIdentityChanged(string newName, string newIcon)
    {
        App.Current?.SettingsService.UpdateWidget(Config);
        NotifyChromePreferenceIfChanged();
    }

    private void ViewModel_ViewRebuildRequested(object? sender, EventArgs e)
    {
        NotifyChromePreferenceIfChanged();
    }

    private void NotifyChromePreferenceIfChanged()
    {
        WidgetChromeCategory current = CustomWidgetChromeCategoryClassifier.Classify(ViewModel.Manifest);
        if (_lastChromeCategory == current)
        {
            return;
        }

        _lastChromeCategory = current;
        ChromePreferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    public override async Task InitializeAsync()
    {
        await ViewModel.InitializeAsync();
        NotifyChromePreferenceIfChanged();
    }

    public override async Task RefreshAsync()
    {
        await ViewModel.ReloadManifestFromDiskAsync();
        NotifyChromePreferenceIfChanged();
    }

    public override void ApplyAppearance()
    {
        if (MaterializedView is CustomWidgetContent content)
        {
            content.RequestCoalescedRebuild(forceFullRebuild: true);
        }
    }

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        base.OnViewMaterialized(view);
        if (_subscribedContent is not null)
        {
            _subscribedContent.SettingsNavigationRequested -= OnContentSettingsNavigationRequested;
            _subscribedContent = null;
        }
        if (view is CustomWidgetContent content)
        {
            _subscribedContent = content;
            _subscribedContent.SettingsNavigationRequested += OnContentSettingsNavigationRequested;
            content.SetWindowVisibility(_isWindowVisible && !_isPowerSuspended);
            content.OnCompactStateChanged(_isCompactCollapsed);
        }
    }

    private void OnContentSettingsNavigationRequested(string sectionTag) =>
        SettingsNavigationRequested?.Invoke(sectionTag);

    public override void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        bool effectiveVisible = visible && !_isPowerSuspended;
        if (MaterializedView is CustomWidgetContent content)
        {
            if (!effectiveVisible)
            {
                content.CloseOpenPopovers();
            }
            content.SetWindowVisibility(effectiveVisible);
        }

        ViewModel.OnWindowVisibilityChanged(visible);
    }

    public void SetPowerSuspended(bool suspended)
    {
        _isPowerSuspended = suspended;
        bool effectiveVisible = _isWindowVisible && !suspended;
        if (MaterializedView is CustomWidgetContent content)
        {
            if (!effectiveVisible)
            {
                content.CloseOpenPopovers();
            }
            content.SetWindowVisibility(effectiveVisible);
        }

        ViewModel.SetPowerSuspended(suspended);
    }

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
        DeskBoxDragData.HasDroppedFiles(dataView);

    public async Task<bool> ImportExternalDropAsync(DataPackageView dataView)
    {
        if (!CanImportExternalDrop(dataView))
        {
            return false;
        }

        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(dataView);
            string[] paths = batch.Files
                .Select(static f => f.Path)
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
            bool forceCopy = batch.Files.Any(static f => f.ForceManagedCopy);
            return paths.Length > 0 &&
                await ViewModel.ImportDroppedFilesAsync(paths, forceCopyToPortableStore: forceCopy);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Capsule drop import failed: {ex.Message}");
            return false;
        }
    }

    public Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<string> paths,
        bool forceCopyToPortableStore = false) =>
        ViewModel.ImportDroppedFilesAsync(paths, forceCopyToPortableStore);

    public bool HasRecycleBinDropTarget => ViewModel.HasRecycleBinDropTarget;

    internal bool IsScreenPointOverRecycleBin(int screenX, int screenY, IntPtr hostHwnd) =>
        MaterializedView is CustomWidgetContent content &&
        content.TryGetHoveredRecycleBinCard(screenX, screenY, hostHwnd) is not null;

    internal bool UpdateNativeRecycleBinDragHover(
        int screenX,
        int screenY,
        IntPtr hostHwnd,
        bool canAcceptRecyclePayload) =>
        MaterializedView is CustomWidgetContent content &&
        content.UpdateRecycleBinNativeDragHover(screenX, screenY, hostHwnd, canAcceptRecyclePayload);

    internal void ClearNativeRecycleBinDragHover() =>
        (MaterializedView as CustomWidgetContent)?.ClearRecycleBinNativeDragHover();

    internal Task<int> HandleNativeRecycleBinDropAsync(
        IReadOnlyList<string> paths,
        int? screenX,
        int? screenY,
        IntPtr hostHwnd) =>
        MaterializedView is CustomWidgetContent content
            ? content.HandleNativeRecycleBinDropAsync(paths, screenX, screenY, hostHwnd)
            : Task.FromResult(0);

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
        if (view is CustomWidgetContent content)
        {
            if (ReferenceEquals(_subscribedContent, content))
            {
                content.SettingsNavigationRequested -= OnContentSettingsNavigationRequested;
                _subscribedContent = null;
            }
            content.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_subscribedContent is not null)
        {
            _subscribedContent.SettingsNavigationRequested -= OnContentSettingsNavigationRequested;
            _subscribedContent = null;
        }
        ViewModel.FeedbackRequested -= ViewModel_FeedbackRequested;
        ViewModel.ManifestIdentityChanged -= ViewModel_ManifestIdentityChanged;
        ViewModel.ViewRebuildRequested -= ViewModel_ViewRebuildRequested;
        try
        {
            if (MaterializedView is CustomWidgetContent content)
            {
                content.Dispose();
            }
        }
        finally
        {
            ViewModel.Dispose();
        }
    }
}
