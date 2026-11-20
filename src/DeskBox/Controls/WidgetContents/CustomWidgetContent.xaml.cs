using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetContent : UserControl, IDisposable
{
    private readonly CustomWidgetViewModel _viewModel;
    private readonly CustomWidgetNativeRenderer _renderer;
    private readonly SemaphoreSlim _consentDialogGate = new(1, 1);
    private int _consentDialogOpenCount;
    private bool _isDisposed;
    private bool _isCompactCollapsed;
    private bool _isWindowVisible = true;
    private bool _rebuildPending;
    private bool _forceFullRebuildPending;
    private bool _coalescedRebuildScheduled;
    private bool _coalescedRebuildDirty;

    private CustomWidgetManifest? _lastRenderedManifest;
    private ElementTheme _lastRenderedTheme;
    private FlowDirection _lastRenderedFlowDirection;
    private string _lastRenderedCultureName = string.Empty;
    private CustomWidgetHostAppearanceProfile _lastRenderedAppearance;
    private bool _hasCapturedAppearance;
    private bool _lastRenderedNodeSelectionEnabled;
    private string? _lastRenderedSelectedNodePath;
    private string? _lastRenderedActivePopoverPreviewId;
    private int _lastRenderedMissingSecretsCount = -1;
    private bool _lastRenderedMissingGlobalAi;
    private bool _lastRenderedHasErrors;

    internal event Action<string>? SettingsNavigationRequested;

    internal void OnCompactStateChanged(bool collapsed)
    {
        if (_isCompactCollapsed == collapsed)
        {
            return;
        }

        _isCompactCollapsed = collapsed;
        UpdateMediaPlaybackSuspension();

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
            if (_rebuildPending && DispatcherQueue is { } dq)
            {
                dq.TryEnqueue(() =>
                {
                    if (!_isDisposed && !_isCompactCollapsed && _rebuildPending)
                    {
                        RebuildNativeTree(forceFullRebuild: _forceFullRebuildPending);
                        FluentMotionHelper.PlaySubtleFadeIn(RootPresenter, durationMs: 120);
                    }
                });
            }
            else if (_rebuildPending)
            {
                RebuildNativeTree(forceFullRebuild: _forceFullRebuildPending);
            }
            else if (!_renderer.TryPatchRetainedTree())
            {
                _renderer.RefreshLiveMetrics();
            }
        }
    }

    internal void SetWindowVisibility(bool visible)
    {
        if (_isWindowVisible == visible)
        {
            return;
        }

        _isWindowVisible = visible;
        UpdateMediaPlaybackSuspension();
    }

    private void UpdateMediaPlaybackSuspension()
    {
        if (!_isDisposed)
        {
            _renderer.SetMediaPlaybackSuspended(!_isWindowVisible || _isCompactCollapsed);
        }
    }

    internal void CloseOpenPopovers() => _renderer.CloseOpenPopovers();

    private readonly Func<CustomWidgetSecurityPrompt, Task<CustomWidgetSecurityConsentDecision>> _decisionPromptDelegate;
    private readonly Func<IntPtr> _ownerWindowHandleDelegate;

    public CustomWidgetContent(CustomWidgetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _decisionPromptDelegate = ShowExternalActionConsentDialogAsync;
        _ownerWindowHandleDelegate = ResolveOwnerWindowHandle;
        _renderer = new CustomWidgetNativeRenderer(
            viewModel,
            this,
            appearanceProvider: static () => CustomWidgetHostAppearanceProfile.FromSettings(App.Current?.SettingsService.Settings));

        InitializeComponent();

        _renderer.AllFlyoutsClosed += Renderer_AllFlyoutsClosed;
        _renderer.SettingsNavigationRequested += Renderer_SettingsNavigationRequested;
        _viewModel.ViewRebuildRequested += ViewModel_ViewRebuildRequested;
        _viewModel.MetricsRefreshed += ViewModel_MetricsRefreshed;
        _viewModel.PopoverCommandRequested += ViewModel_PopoverCommandRequested;
        _viewModel.TeachingTipCommandRequested += ViewModel_TeachingTipCommandRequested;
        _viewModel.ExternalActionDecisionPrompt = _decisionPromptDelegate;
        _viewModel.OwnerWindowHandleResolver = _ownerWindowHandleDelegate;
        ActualThemeChanged += CustomWidgetContent_ActualThemeChanged;
        Loaded += CustomWidgetContent_Loaded;
        Unloaded += CustomWidgetContent_Unloaded;

        if (IsLoaded)
        {
            RebuildNativeTree(forceFullRebuild: true);
        }
    }

    public CustomWidgetViewModel ViewModel => _viewModel;

    public CustomWidgetNativeRenderer Renderer => _renderer;

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

                RebuildNativeTree(forceFullRebuild: true);
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
                RebuildNativeTree(forceFullRebuild: true);
            }
        }
    }

    public event Action<CustomWidgetPickedNode>? NodePicked
    {
        add => _renderer.NodePicked += value;
        remove => _renderer.NodePicked -= value;
    }

    public bool InterceptPopoversForStudioPreview
    {
        get => _renderer.InterceptPopoversForStudioPreview;
        set => _renderer.InterceptPopoversForStudioPreview = value;
    }

    public string? ActivePopoverPreviewId
    {
        get => _renderer.ActivePopoverPreviewId;
        set
        {
            if (_renderer.UpdateActivePopoverPreviewId(value))
            {
                RebuildNativeTree(forceFullRebuild: true);
            }
        }
    }

    public event Action<string, string>? StudioPopoverPreviewRequested
    {
        add => _renderer.StudioPopoverPreviewRequested += value;
        remove => _renderer.StudioPopoverPreviewRequested -= value;
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

    internal void RequestCoalescedRebuild(bool forceFullRebuild = false)
    {
        if (_isDisposed)
        {
            return;
        }

        if (forceFullRebuild)
        {
            _forceFullRebuildPending = true;
        }

        if (!IsLoaded || _isCompactCollapsed || _renderer.HasOpenOverlayFlyout || _consentDialogOpenCount > 0)
        {
            _rebuildPending = true;
            _coalescedRebuildDirty = false;
            if (IsLoaded && !_isCompactCollapsed && _renderer.HasOpenOverlayFlyout)
            {
                if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
                {
                    dq.TryEnqueue(() =>
                    {
                        if (!_isDisposed)
                        {
                            _renderer.TryPatchRetainedTree();
                            _renderer.RefreshOpenPopovers();
                        }
                    });
                }
                else
                {
                    _renderer.TryPatchRetainedTree();
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
                        RebuildNativeTree(forceFullRebuild: _forceFullRebuildPending);
                    }
                }))
            {
                return;
            }

            _coalescedRebuildScheduled = false;
        }

        RebuildNativeTree(forceFullRebuild: _forceFullRebuildPending);
    }

    public void RebuildNativeTree(bool forceFullRebuild = false)
    {
        if (_isDisposed)
        {
            return;
        }

        _coalescedRebuildDirty = false;
        if (forceFullRebuild)
        {
            _forceFullRebuildPending = true;
        }

        if (!IsLoaded || _isCompactCollapsed || _renderer.HasOpenOverlayFlyout || _consentDialogOpenCount > 0)
        {
            _rebuildPending = true;
            return;
        }

        _rebuildPending = false;
        bool mustForceFull = _forceFullRebuildPending;
        _forceFullRebuildPending = false;

        try
        {
            if (!mustForceFull && CanPatchExistingRootInPlace() && _renderer.TryPatchRetainedTree())
            {
                return;
            }

            bool hadExistingRoot = RootPresenter.Content is not null;
            UIElement nextRoot = _renderer.BuildRootElement();
            RootPresenter.Content = nextRoot;
            if (hadExistingRoot)
            {
                FluentMotionHelper.PlaySubtleFadeIn(nextRoot, durationMs: 135);
            }
            CaptureStructuralSignature();
            UpdateMediaPlaybackSuspension();
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetContent] RebuildNativeTree failed for widget '{_viewModel.Config.Id}': {ex}");
            _hasCapturedAppearance = false;
            RootPresenter.Content = new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = _viewModel.Manifest.Name,
                Message = ex.Message,
                Margin = new Thickness(10)
            };
        }
    }

    private bool CanPatchExistingRootInPlace()
    {
        if (RootPresenter.Content is null || _lastRenderedManifest is null)
        {
            return false;
        }

        ElementTheme currentTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(this);
        FlowDirection currentFlow = CustomWidgetLocaleCatalog.IsRightToLeftCulture(_viewModel.ActiveCultureName)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        CustomWidgetHostAppearanceProfile currentAppearance = CustomWidgetHostAppearanceProfile.FromSettings(
            App.Current?.SettingsService.Settings);
        int missingSecretsCount = _viewModel.GetUnconfiguredSecretSettings().Count;
        bool missingGlobalAi = CustomWidgetNativeRenderer.NeedsGlobalAiSetupBanner(_viewModel);
        bool hasErrors = _viewModel.Diagnostics.Any(static d => d.IsError);

        return _hasCapturedAppearance &&
               ReferenceEquals(_lastRenderedManifest, _viewModel.Manifest) &&
               _lastRenderedTheme == currentTheme &&
               _lastRenderedFlowDirection == currentFlow &&
               string.Equals(_lastRenderedCultureName, _viewModel.ActiveCultureName, StringComparison.OrdinalIgnoreCase) &&
               _lastRenderedAppearance.ApproximatelyEquals(currentAppearance) &&
               _lastRenderedNodeSelectionEnabled == IsNodeSelectionEnabled &&
               string.Equals(_lastRenderedSelectedNodePath, SelectedNodePath, StringComparison.Ordinal) &&
               string.Equals(_lastRenderedActivePopoverPreviewId, _renderer.ActivePopoverPreviewId, StringComparison.OrdinalIgnoreCase) &&
               _lastRenderedMissingSecretsCount == missingSecretsCount &&
               _lastRenderedMissingGlobalAi == missingGlobalAi &&
               _lastRenderedHasErrors == hasErrors;
    }

    private void CaptureStructuralSignature()
    {
        _lastRenderedManifest = _viewModel.Manifest;
        _lastRenderedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(this);
        _lastRenderedFlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(_viewModel.ActiveCultureName)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        _lastRenderedCultureName = _viewModel.ActiveCultureName ?? string.Empty;
        _lastRenderedAppearance = CustomWidgetHostAppearanceProfile.FromSettings(
            App.Current?.SettingsService.Settings);
        _hasCapturedAppearance = true;
        _lastRenderedNodeSelectionEnabled = IsNodeSelectionEnabled;
        _lastRenderedSelectedNodePath = SelectedNodePath;
        _lastRenderedActivePopoverPreviewId = _renderer.ActivePopoverPreviewId;
        _lastRenderedMissingSecretsCount = _viewModel.GetUnconfiguredSecretSettings().Count;
        _lastRenderedMissingGlobalAi = CustomWidgetNativeRenderer.NeedsGlobalAiSetupBanner(_viewModel);
        _lastRenderedHasErrors = _viewModel.Diagnostics.Any(static d => d.IsError);
    }

    private void Renderer_SettingsNavigationRequested(string sectionTag)
    {
        if (!_isDisposed)
        {
            SettingsNavigationRequested?.Invoke(sectionTag);
        }
    }

    private void OnAiCredentialsChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || !IsLoaded)
        {
            return;
        }

        if (DispatcherQueue is { } dq && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(() =>
            {
                if (!_isDisposed && IsLoaded)
                {
                    RequestCoalescedRebuild();
                }
            });
            return;
        }

        RequestCoalescedRebuild();
    }

    private void Renderer_AllFlyoutsClosed()
    {
        if (!_isDisposed && !_isCompactCollapsed && _rebuildPending)
        {
            RebuildNativeTree(forceFullRebuild: _forceFullRebuildPending);
        }
    }

    private void CustomWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (App.Current?.ThemeService is { } themeService)
        {
            themeService.AppearanceChanged -= OnThemeAppearanceChanged;
            themeService.AppearanceChanged += OnThemeAppearanceChanged;
        }
        CustomWidgetAiCredentialStore.Shared.CredentialsChanged -= OnAiCredentialsChanged;
        CustomWidgetAiCredentialStore.Shared.CredentialsChanged += OnAiCredentialsChanged;

        RebuildNativeTree(forceFullRebuild: RootPresenter.Content is null);
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
        CustomWidgetAiCredentialStore.Shared.CredentialsChanged -= OnAiCredentialsChanged;
    }

    private void OnThemeAppearanceChanged()
    {
        if (_isDisposed || !IsLoaded)
        {
            return;
        }

        RequestCoalescedRebuild(forceFullRebuild: true);
    }

    private void ViewModel_ViewRebuildRequested(object? sender, EventArgs e)
    {
        RequestCoalescedRebuild();
    }

    private void CustomWidgetContent_ActualThemeChanged(FrameworkElement sender, object args)
    {
        RequestCoalescedRebuild(forceFullRebuild: true);
    }

    private void ViewModel_MetricsRefreshed(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isCompactCollapsed)
        {
            _rebuildPending = true;
            return;
        }

        if (!_renderer.TryPatchRetainedTree())
        {
            RequestCoalescedRebuild();
        }
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

    private void ViewModel_TeachingTipCommandRequested(string mode, string tipId)
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
                    _renderer.HandleTeachingTipCommand(mode, tipId);
                }
            });
            return;
        }

        _renderer.HandleTeachingTipCommand(mode, tipId);
    }

    internal CustomWidgetRecycleBinCard? TryGetHoveredRecycleBinCard(
        int screenX,
        int screenY,
        IntPtr hostHwnd) =>
        _isDisposed || _isCompactCollapsed
            ? null
            : _renderer.TryGetHoveredRecycleBinCard(screenX, screenY, hostHwnd);

    internal bool UpdateRecycleBinNativeDragHover(
        int screenX,
        int screenY,
        IntPtr hostHwnd,
        bool canAcceptRecyclePayload) =>
        !_isDisposed &&
        !_isCompactCollapsed &&
        _renderer.UpdateRecycleBinNativeDragHover(
            screenX,
            screenY,
            hostHwnd,
            canAcceptRecyclePayload);

    internal void ClearRecycleBinNativeDragHover()
    {
        if (!_isDisposed)
        {
            _renderer.ClearRecycleBinNativeDragHover();
        }
    }

    internal async Task<int> HandleNativeRecycleBinDropAsync(
        IReadOnlyList<string> paths,
        int? screenX,
        int? screenY,
        IntPtr hostHwnd)
    {
        if (_isDisposed || paths.Count == 0)
        {
            _renderer.ClearRecycleBinNativeDragHover();
            return 0;
        }

        CustomWidgetRecycleBinCard? hit = _renderer.ResolveDropTargetRecycleBinCard(screenX, screenY, hostHwnd);
        _renderer.ClearRecycleBinNativeDragHover(except: hit);
        if (hit is not null)
        {
            return await hit.HandleDroppedPathsAsync(paths);
        }

        CustomWidgetRecycleBatchOutcome outcome = await CustomWidgetRecycleBinService.Shared
            .RecycleDroppedFilesWithUndoAsync(
                paths,
                hostHwnd,
                _viewModel.ActiveCultureName);
        return outcome.RecycledCount;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled)
        {
            DropOverlayBorder.Visibility = Visibility.Collapsed;
            return;
        }

        if (!_viewModel.AcceptsDrops)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropOverlayBorder.Visibility = Visibility.Collapsed;
            return;
        }

        bool isCopy = string.Equals(_viewModel.EffectiveDropMode, "copy", StringComparison.OrdinalIgnoreCase);
        if (!DeskBoxDragData.CanAcceptCustomWidgetDrop(
                e.DataView,
                _viewModel.EffectiveDropAccept,
                e.AllowedOperations,
                preferLinkForFiles: !isCopy,
                out DataPackageOperation negotiated,
                out string matchedKind))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropOverlayBorder.Visibility = Visibility.Collapsed;
            return;
        }

        e.AcceptedOperation = negotiated;
        string culture = _viewModel.ActiveCultureName;
        if (string.Equals(matchedKind, "files", StringComparison.Ordinal))
        {
            DropOverlayIcon.Glyph = isCopy ? "\uE723" : "\uE71B";
            DropOverlayText.Text = LocalizationService.TranslateForCulture(
                culture,
                isCopy ? "CustomWidget.DropHint.Copy" : "CustomWidget.DropHint.Link");
        }
        else
        {
            DropOverlayIcon.Glyph = matchedKind switch
            {
                "todo" => "\uE9D5",
                "quickCapture" => "\uE70F",
                _ => "\uE8C8"
            };
            DropOverlayText.Text = LocalizationService.TranslateForCulture(
                culture,
                "CustomWidget.Renderer.FileDrop.Hint");
        }

        DropOverlayBorder.Visibility = (_viewModel.HasDropZoneNode && _viewModel.Manifest.OnDrop is null)
            ? Visibility.Collapsed
            : Visibility.Visible;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropOverlayBorder.Visibility = Visibility.Collapsed;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlayBorder.Visibility = Visibility.Collapsed;
        if (e.Handled || !_viewModel.AcceptsDrops)
        {
            return;
        }

        bool isCopy = string.Equals(_viewModel.EffectiveDropMode, "copy", StringComparison.OrdinalIgnoreCase);
        if (!DeskBoxDragData.CanAcceptCustomWidgetDrop(
                e.DataView,
                _viewModel.EffectiveDropAccept,
                e.AllowedOperations,
                preferLinkForFiles: !isCopy,
                out DataPackageOperation expectedOp,
                out _))
        {
            return;
        }

        e.Handled = true;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            using CustomWidgetExtractedDropBatch? extracted =
                await DeskBoxDragData.TryExtractCustomWidgetDropAsync(e.DataView, _viewModel.EffectiveDropAccept);
            if (extracted is not null)
            {
                bool handled = await _viewModel.HandleStructuredDropAsync(extracted.Payload);
                e.AcceptedOperation = handled ? expectedOp : DataPackageOperation.None;
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] XAML drop failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<CustomWidgetSecurityConsentDecision> ShowExternalActionConsentDialogAsync(CustomWidgetSecurityPrompt prompt)
    {
        if (_isDisposed || XamlRoot is null)
        {
            return CustomWidgetSecurityConsentDecision.Deny;
        }

        TaskCompletionSource<CustomWidgetSecurityConsentDecision> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task ShowOnUiAsync()
        {
            await _consentDialogGate.WaitAsync();
            try
            {
                if (_isDisposed || XamlRoot is null)
                {
                    tcs.TrySetResult(CustomWidgetSecurityConsentDecision.Deny);
                    return;
                }

                string culture = _viewModel.ActiveCultureName;
                string title = LocalizationService.TranslateForCulture(culture, "CustomWidget.SecurityPrompt.Title");
                IntPtr ownerHwnd = ResolveOwnerWindowHandle();
                var popover = new DeskBox.Views.WidgetEditorPopoverWindow(ownerHwnd, title);
                var decisionTcs = new TaskCompletionSource<CustomWidgetSecurityConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

                CustomWidgetSecurityConsentDecision resolvedDecision;
                try
                {
                    var root = new StackPanel
                    {
                        Width = 344,
                        Spacing = 10,
                        FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(culture)
                            ? FlowDirection.RightToLeft
                            : FlowDirection.LeftToRight
                    };

                    var headerGrid = new Grid { ColumnSpacing = 8 };
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var shieldIcon = new FontIcon
                    {
                        Glyph = "\uEA18",
                        FontSize = 15,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(shieldIcon, 0);
                    headerGrid.Children.Add(shieldIcon);

                    var titleTb = new TextBlock
                    {
                        Text = title,
                        FontSize = 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(titleTb, 1);
                    headerGrid.Children.Add(titleTb);

                    Button closeBtn = DeskBox.Views.WidgetEditorPopoverWindow.CreateHeaderCloseButton(
                        LocalizationService.TranslateForCulture(culture, "Common.Close"),
                        () =>
                        {
                            decisionTcs.TrySetResult(CustomWidgetSecurityConsentDecision.Deny);
                            popover.ClosePopover(restoreOwnerForeground: true);
                        },
                        this);
                    Grid.SetColumn(closeBtn, 2);
                    headerGrid.Children.Add(closeBtn);
                    root.Children.Add(headerGrid);

                    root.Children.Add(new TextBlock
                    {
                        Text = LocalizationService.FormatForCulture(culture, "CustomWidget.SecurityPrompt.Message", prompt.WidgetName),
                        FontSize = 12.5,
                        TextWrapping = TextWrapping.Wrap
                    });

                    if (!string.IsNullOrWhiteSpace(prompt.ActionKind))
                    {
                        root.Children.Add(new Border
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Padding = new Thickness(7, 2, 7, 2),
                            CornerRadius = new CornerRadius(4),
                            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(32, 96, 165, 250)),
                            FlowDirection = FlowDirection.LeftToRight,
                            Child = new TextBlock
                            {
                                Text = prompt.ActionKind,
                                FontSize = 11,
                                FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI")
                            }
                        });
                    }

                    root.Children.Add(new Border
                    {
                        Padding = new Thickness(10, 8, 10, 8),
                        CornerRadius = new CornerRadius(6),
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 128, 128, 128)),
                        FlowDirection = FlowDirection.LeftToRight,
                        Child = new TextBlock
                        {
                            Text = prompt.TargetDisplay,
                            IsTextSelectionEnabled = true,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 11.5,
                            FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI")
                        }
                    });

                    var footerGrid = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 4, 0, 0) };
                    footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var denyBtn = new Button
                    {
                        Content = LocalizationService.TranslateForCulture(culture, "Common.Cancel"),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 5, 10, 5)
                    };
                    denyBtn.Click += (_, _) =>
                    {
                        decisionTcs.TrySetResult(CustomWidgetSecurityConsentDecision.Deny);
                        popover.ClosePopover(restoreOwnerForeground: true);
                    };
                    Grid.SetColumn(denyBtn, 0);
                    footerGrid.Children.Add(denyBtn);

                    string allowOnceLabel = LocalizationService.TranslateForCulture(culture, "CustomWidget.SecurityPrompt.AllowOnce");
                    if (string.Equals(allowOnceLabel, "CustomWidget.SecurityPrompt.AllowOnce", StringComparison.Ordinal))
                    {
                        allowOnceLabel = "Allow Once";
                    }
                    var allowOnceBtn = new Button
                    {
                        Content = allowOnceLabel,
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 5, 10, 5)
                    };
                    allowOnceBtn.Click += (_, _) =>
                    {
                        decisionTcs.TrySetResult(CustomWidgetSecurityConsentDecision.AllowOnce);
                        popover.ClosePopover(restoreOwnerForeground: true);
                    };
                    Grid.SetColumn(allowOnceBtn, 2);
                    footerGrid.Children.Add(allowOnceBtn);

                    var allowRememberBtn = new Button
                    {
                        Content = LocalizationService.TranslateForCulture(culture, "CustomWidget.SecurityPrompt.AllowRemember"),
                        Style = Application.Current?.Resources.TryGetValue("AccentButtonStyle", out object? styleObj) == true ? styleObj as Style : null,
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 5, 12, 5)
                    };
                    allowRememberBtn.Click += (_, _) =>
                    {
                        decisionTcs.TrySetResult(CustomWidgetSecurityConsentDecision.AllowAndRemember);
                        popover.ClosePopover(restoreOwnerForeground: true);
                    };
                    Grid.SetColumn(allowRememberBtn, 3);
                    footerGrid.Children.Add(allowRememberBtn);
                    root.Children.Add(footerGrid);

                    popover.PopoverClosed += (_, _) =>
                    {
                        decisionTcs.TrySetResult(CustomWidgetSecurityConsentDecision.Deny);
                    };

                    _consentDialogOpenCount++;
                    try
                    {
                        popover.ShowAnchored(
                            this,
                            root,
                            DeskBox.Views.WidgetPopoverPlacement.Bottom,
                            minWidthDip: 358,
                            maxWidthDip: 384,
                            initialFocusTarget: denyBtn,
                            triggerElement: this,
                            logScope: "CustomWidgetSecurityConsent",
                            logicalKey: $"custom-consent:{_viewModel.Config.Id}");
                        resolvedDecision = await decisionTcs.Task;
                    }
                    finally
                    {
                        _consentDialogOpenCount = Math.Max(0, _consentDialogOpenCount - 1);
                    }
                }
                catch
                {
                    try { popover.ClosePopover(restoreOwnerForeground: false); } catch { }
                    throw;
                }

                tcs.TrySetResult(resolvedDecision);
                if (!_isDisposed && !_isCompactCollapsed && _consentDialogOpenCount == 0 && _rebuildPending)
                {
                    RebuildNativeTree(forceFullRebuild: _forceFullRebuildPending);
                }
            }
            catch
            {
                tcs.TrySetResult(CustomWidgetSecurityConsentDecision.Deny);
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
                return CustomWidgetSecurityConsentDecision.Deny;
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
        if (ReferenceEquals(_viewModel.ExternalActionDecisionPrompt, _decisionPromptDelegate))
        {
            _viewModel.ExternalActionDecisionPrompt = null;
        }
        if (ReferenceEquals(_viewModel.OwnerWindowHandleResolver, _ownerWindowHandleDelegate))
        {
            _viewModel.OwnerWindowHandleResolver = null;
        }
        _renderer.AllFlyoutsClosed -= Renderer_AllFlyoutsClosed;
        _renderer.SettingsNavigationRequested -= Renderer_SettingsNavigationRequested;
        _viewModel.ViewRebuildRequested -= ViewModel_ViewRebuildRequested;
        _viewModel.MetricsRefreshed -= ViewModel_MetricsRefreshed;
        _viewModel.PopoverCommandRequested -= ViewModel_PopoverCommandRequested;
        _viewModel.TeachingTipCommandRequested -= ViewModel_TeachingTipCommandRequested;
        ActualThemeChanged -= CustomWidgetContent_ActualThemeChanged;
        Loaded -= CustomWidgetContent_Loaded;
        Unloaded -= CustomWidgetContent_Unloaded;
        CustomWidgetAiCredentialStore.Shared.CredentialsChanged -= OnAiCredentialsChanged;
        if (App.Current?.ThemeService is { } themeService)
        {
            themeService.AppearanceChanged -= OnThemeAppearanceChanged;
        }
        _renderer.ReleaseResources();
        RootPresenter.Content = null;
        Content = null;
    }
}
