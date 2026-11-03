using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetContent : UserControl, IDisposable
{
    private readonly CustomWidgetViewModel _viewModel;
    private readonly CustomWidgetNativeRenderer _renderer;
    private readonly SemaphoreSlim _consentDialogGate = new(1, 1);
    private int _consentDialogOpenCount;
    private bool _isDisposed;
    private bool _isCompactCollapsed;
    private bool _rebuildPending;
    private bool _coalescedRebuildScheduled;
    private bool _coalescedRebuildDirty;

    internal void OnCompactStateChanged(bool collapsed)
    {
        if (_isCompactCollapsed == collapsed)
        {
            return;
        }

        _isCompactCollapsed = collapsed;
        if (collapsed)
        {
            _renderer.CloseOpenPopovers();
            IntPtr ownerHwnd = ResolveOwnerWindowHandle();
            if (ownerHwnd != IntPtr.Zero)
            {
                DeskBox.Views.WidgetEditorPopoverWindow.ClosePopoversForOwner(ownerHwnd, restoreOwnerForeground: false);
            }
            return;
        }

        if (!_isDisposed)
        {
            if (_rebuildPending)
            {
                RebuildNativeTree();
            }
            else
            {
                _renderer.RefreshLiveMetrics();
            }
        }
    }

    internal void CloseOpenPopovers() => _renderer.CloseOpenPopovers();

    private readonly Func<CustomWidgetSecurityPrompt, Task<bool>> _consentPromptDelegate;
    private readonly Func<IntPtr> _ownerWindowHandleDelegate;

    public CustomWidgetContent(CustomWidgetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _consentPromptDelegate = ShowExternalActionConsentDialogAsync;
        _ownerWindowHandleDelegate = ResolveOwnerWindowHandle;
        _renderer = new CustomWidgetNativeRenderer(
            viewModel,
            this,
            static () => App.Current?.SettingsService.Settings.WidgetShell.TextSize ?? SettingsService.DefaultTextSize);

        InitializeComponent();

        _renderer.AllFlyoutsClosed += Renderer_AllFlyoutsClosed;
        _viewModel.ViewRebuildRequested += ViewModel_ViewRebuildRequested;
        _viewModel.MetricsRefreshed += ViewModel_MetricsRefreshed;
        _viewModel.PopoverCommandRequested += ViewModel_PopoverCommandRequested;
        _viewModel.ExternalActionConsentPrompt = _consentPromptDelegate;
        _viewModel.OwnerWindowHandleResolver = _ownerWindowHandleDelegate;
        ActualThemeChanged += CustomWidgetContent_ActualThemeChanged;
        Loaded += CustomWidgetContent_Loaded;
        Unloaded += CustomWidgetContent_Unloaded;

        if (IsLoaded)
        {
            RebuildNativeTree();
        }
    }

    public CustomWidgetViewModel ViewModel => _viewModel;

    public bool IsNodeSelectionEnabled
    {
        get => _renderer.IsNodeSelectionEnabled;
        set
        {
            if (_renderer.IsNodeSelectionEnabled != value)
            {
                _renderer.IsNodeSelectionEnabled = value;
                try
                {
                    ProtectedCursor = value
                        ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand)
                        : null;
                }
                catch
                {
                }

                RebuildNativeTree();
            }
        }
    }

    public string? SelectedNodePath
    {
        get => _renderer.SelectedNodePath;
        set
        {
            if (_renderer.UpdateSelectedNodePath(value))
            {
                RebuildNativeTree();
            }
        }
    }

    public event Action<CustomWidgetPickedNode>? NodePicked
    {
        add => _renderer.NodePicked += value;
        remove => _renderer.NodePicked -= value;
    }

    public (double MeasuredWidth, double MeasuredHeight) MeasureNaturalContentSize(double constrainedWidth)
    {
        try
        {
            if (RootPresenter?.Content is UIElement content)
            {
                content.Measure(new Windows.Foundation.Size(Math.Max(160, constrainedWidth), double.PositiveInfinity));
                return (content.DesiredSize.Width, content.DesiredSize.Height);
            }
        }
        catch
        {
        }

        return (0, 0);
    }

    internal void RequestCoalescedRebuild()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!IsLoaded || _isCompactCollapsed || _renderer.HasOpenFlyout || _consentDialogOpenCount > 0)
        {
            _rebuildPending = true;
            _coalescedRebuildDirty = false;
            if (IsLoaded && !_isCompactCollapsed && _renderer.HasOpenFlyout)
            {
                if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
                {
                    dq.TryEnqueue(() =>
                    {
                        if (!_isDisposed)
                        {
                            _renderer.RefreshOpenPopovers();
                        }
                    });
                }
                else
                {
                    _renderer.RefreshOpenPopovers();
                }
            }
            return;
        }

        if (DispatcherQueue is { } dispatcher)
        {
            _coalescedRebuildDirty = true;
            if (_coalescedRebuildScheduled)
            {
                return;
            }

            _coalescedRebuildScheduled = true;
            if (dispatcher.TryEnqueue(() =>
                {
                    _coalescedRebuildScheduled = false;
                    if (_coalescedRebuildDirty)
                    {
                        RebuildNativeTree();
                    }
                }))
            {
                return;
            }

            _coalescedRebuildScheduled = false;
        }

        RebuildNativeTree();
    }

    public void RebuildNativeTree()
    {
        if (_isDisposed)
        {
            return;
        }

        _coalescedRebuildDirty = false;
        if (!IsLoaded || _isCompactCollapsed || _renderer.HasOpenFlyout || _consentDialogOpenCount > 0)
        {
            _rebuildPending = true;
            return;
        }

        _rebuildPending = false;
        RootPresenter.Content = _renderer.BuildRootElement();
    }

    private void Renderer_AllFlyoutsClosed()
    {
        if (!_isDisposed && !_isCompactCollapsed && _rebuildPending)
        {
            RebuildNativeTree();
        }
    }

    private void CustomWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (App.Current?.ThemeService is { } themeService)
        {
            themeService.AppearanceChanged -= OnThemeAppearanceChanged;
            themeService.AppearanceChanged += OnThemeAppearanceChanged;
        }

        RebuildNativeTree();
    }

    private void CustomWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        _renderer.CloseOpenPopovers();
        IntPtr ownerHwnd = ResolveOwnerWindowHandle();
        if (ownerHwnd != IntPtr.Zero)
        {
            DeskBox.Views.WidgetEditorPopoverWindow.ClosePopoversForOwner(ownerHwnd, restoreOwnerForeground: false);
        }
        if (App.Current?.ThemeService is { } themeService)
        {
            themeService.AppearanceChanged -= OnThemeAppearanceChanged;
        }
    }

    private void OnThemeAppearanceChanged()
    {
        if (_isDisposed || !IsLoaded)
        {
            return;
        }

        RequestCoalescedRebuild();
    }

    private void ViewModel_ViewRebuildRequested(object? sender, EventArgs e)
    {
        RequestCoalescedRebuild();
    }

    private void CustomWidgetContent_ActualThemeChanged(FrameworkElement sender, object args)
    {
        RequestCoalescedRebuild();
    }

    private void ViewModel_MetricsRefreshed(object? sender, EventArgs e)
    {
        if (_isDisposed || _isCompactCollapsed)
        {
            return;
        }

        _renderer.RefreshLiveMetrics();
    }

    private void ViewModel_PopoverCommandRequested(string mode, string popoverId)
    {
        if (_isDisposed || _isCompactCollapsed)
        {
            return;
        }

        if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(() =>
            {
                if (!_isDisposed && !_isCompactCollapsed)
                {
                    _renderer.HandlePopoverCommand(mode, popoverId);
                }
            });
            return;
        }

        _renderer.HandlePopoverCommand(mode, popoverId);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!_viewModel.AcceptsFileDrops ||
            !DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropOverlayBorder.Visibility = Visibility.Collapsed;
            return;
        }

        bool isCopy = string.Equals(_viewModel.EffectiveDropMode, "copy", StringComparison.OrdinalIgnoreCase);
        DataPackageOperation negotiated = isCopy
            ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, e.AllowedOperations)
            : e.AllowedOperations.HasFlag(DataPackageOperation.Link)
                ? DataPackageOperation.Link
                : DeskBoxDragData.GetFileAssociationOperation(e.DataView, e.AllowedOperations);
        if (negotiated == DataPackageOperation.None)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropOverlayBorder.Visibility = Visibility.Collapsed;
            return;
        }

        e.AcceptedOperation = negotiated;
        DropOverlayIcon.Glyph = isCopy ? "\uE723" : "\uE71B";
        DropOverlayText.Text = isCopy
            ? App.Current.LocalizationService.T("CustomWidget.DropHint.Copy")
            : App.Current.LocalizationService.T("CustomWidget.DropHint.Link");
        DropOverlayBorder.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropOverlayBorder.Visibility = Visibility.Collapsed;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlayBorder.Visibility = Visibility.Collapsed;
        if (!_viewModel.AcceptsFileDrops ||
            !DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        e.Handled = true;
        DataPackageOperation allowedOperations = e.AllowedOperations;
        bool isCopy = string.Equals(_viewModel.EffectiveDropMode, "copy", StringComparison.OrdinalIgnoreCase);
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            string[] paths = batch.Files
                .Select(static f => f.Path)
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
            if (paths.Length > 0)
            {
                bool forceCopy = batch.Files.Any(static f => f.ForceManagedCopy);
                bool imported = await _viewModel.ImportDroppedFilesAsync(
                    paths,
                    forceCopyToPortableStore: forceCopy);
                e.AcceptedOperation = imported
                    ? (isCopy
                        ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations)
                        : allowedOperations.HasFlag(DataPackageOperation.Link)
                            ? DataPackageOperation.Link
                            : DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations))
                    : DataPackageOperation.None;
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] XAML file drop failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<bool> ShowExternalActionConsentDialogAsync(CustomWidgetSecurityPrompt prompt)
    {
        if (_isDisposed || XamlRoot is null)
        {
            return false;
        }

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task ShowOnUiAsync()
        {
            await _consentDialogGate.WaitAsync();
            try
            {
                if (_isDisposed || XamlRoot is null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                LocalizationService loc = App.Current.LocalizationService;
                var body = new StackPanel { Spacing = 8 };
                body.Children.Add(new TextBlock
                {
                    Text = loc.Format("CustomWidget.SecurityPrompt.Message", prompt.WidgetName),
                    TextWrapping = TextWrapping.Wrap
                });
                body.Children.Add(new Border
                {
                    Padding = new Thickness(10, 8, 10, 8),
                    CornerRadius = new CornerRadius(6),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(24, 128, 128, 128)),
                    Child = new TextBlock
                    {
                        Text = prompt.TargetDisplay,
                        IsTextSelectionEnabled = true,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Cascadia Mono, Segoe UI")
                    }
                });

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = ActualTheme,
                    Title = loc.T("CustomWidget.SecurityPrompt.Title"),
                    Content = body,
                    PrimaryButtonText = loc.T("CustomWidget.SecurityPrompt.AllowRemember"),
                    CloseButtonText = loc.T("Common.Cancel"),
                    DefaultButton = ContentDialogButton.Close
                };

                _consentDialogOpenCount++;
                ContentDialogResult result;
                try
                {
                    result = await dialog.ShowAsync();
                }
                finally
                {
                    _consentDialogOpenCount = Math.Max(0, _consentDialogOpenCount - 1);
                }
                tcs.TrySetResult(result == ContentDialogResult.Primary);
                if (!_isDisposed && !_isCompactCollapsed && _consentDialogOpenCount == 0 && _rebuildPending)
                {
                    RebuildNativeTree();
                }
            }
            catch
            {
                tcs.TrySetResult(false);
            }
            finally
            {
                _consentDialogGate.Release();
            }
        }

        if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
        {
            if (!dq.TryEnqueue(() => _ = ShowOnUiAsync()))
            {
                return false;
            }
        }
        else
        {
            _ = ShowOnUiAsync();
        }

        return await tcs.Task;
    }

    private IntPtr ResolveOwnerWindowHandle()
    {
        try
        {
            if (XamlRoot?.ContentIslandEnvironment is { } env && env.AppWindowId.Value != 0)
            {
                return Microsoft.UI.Win32Interop.GetWindowFromWindowId(env.AppWindowId);
            }
        }
        catch
        {
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _coalescedRebuildDirty = false;
        if (ReferenceEquals(_viewModel.ExternalActionConsentPrompt, _consentPromptDelegate))
        {
            _viewModel.ExternalActionConsentPrompt = null;
        }
        if (ReferenceEquals(_viewModel.OwnerWindowHandleResolver, _ownerWindowHandleDelegate))
        {
            _viewModel.OwnerWindowHandleResolver = null;
        }
        _renderer.AllFlyoutsClosed -= Renderer_AllFlyoutsClosed;
        _viewModel.ViewRebuildRequested -= ViewModel_ViewRebuildRequested;
        _viewModel.MetricsRefreshed -= ViewModel_MetricsRefreshed;
        _viewModel.PopoverCommandRequested -= ViewModel_PopoverCommandRequested;
        ActualThemeChanged -= CustomWidgetContent_ActualThemeChanged;
        Loaded -= CustomWidgetContent_Loaded;
        Unloaded -= CustomWidgetContent_Unloaded;
        if (App.Current?.ThemeService is { } themeService)
        {
            themeService.AppearanceChanged -= OnThemeAppearanceChanged;
        }
        _renderer.ReleaseResources();
        RootPresenter.Content = null;
        Content = null;
    }
}
