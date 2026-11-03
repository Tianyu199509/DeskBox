using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Residency adapter for the first-party Clipboard feature widget.
/// Owns <see cref="ClipboardWidgetViewModel"/> while allowing the leaf XAML
/// view to hibernate cleanly when collapsed to a capsule or switched out in a group.
/// </summary>
public sealed class ClipboardWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetFeedbackSource,
    IWidgetTransientStateContent,
    IWidgetGroupContentCacheable
{
    private readonly LocalizationService _localizationService;
    private bool _isWindowVisible = true;
    private bool _isCompactCollapsed;

    public ClipboardWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        ClipboardHistoryService? clipboardService = null,
        Func<ClipboardWidgetViewModel, FrameworkElement>? viewFactory = null)
        : this(
            config,
            CreateViewModel(config, localizationService, settingsService, clipboardService),
            settingsService ?? App.Current?.SettingsService ?? new SettingsService(),
            localizationService,
            viewFactory)
    {
    }

    private ClipboardWidgetContentAdapter(
        WidgetConfig config,
        ClipboardWidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService,
        Func<ClipboardWidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new ClipboardWidgetContent(vm, settingsService, localizationService)))(viewModel))
    {
        if (config.WidgetKind != WidgetKind.Clipboard)
        {
            throw new ArgumentException("Clipboard content requires a Clipboard widget config.", nameof(config));
        }

        _localizationService = localizationService;
        ViewModel = viewModel;
    }

    public ClipboardWidgetViewModel ViewModel { get; }

    public bool IsReadyForReuse =>
        !IsDisposed &&
        (MaterializedView is ClipboardWidgetContent content
            ? content.IsReadyForReuse
            : ViewModel.IsInitialized);

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        ViewModel.SetViewAttached(!_isCompactCollapsed);
        if (view is ClipboardWidgetContent content)
        {
            content.FeedbackRequested += Content_FeedbackRequested;
            content.OnCompactStateChanged(_isCompactCollapsed);
            content.OnWindowVisibilityChanged(_isWindowVisible);
            content.SyncFromResidentViewModel();
        }
    }

    protected override void OnHibernatingView(FrameworkElement view)
    {
        if (view is ClipboardWidgetContent content)
        {
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.DisposeLeafOnly();
        }

        ViewModel.SetViewAttached(false);
        ViewModel.ReleaseCachedThumbnails();
        IconHelper.ClearCacheScope(ClipboardWidgetViewModel.IconCacheScope);
    }

    public override Task InitializeAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public override async Task RefreshAsync()
    {
        await ViewModel.InitializeAsync().ConfigureAwait(true);
    }

    public override void ApplyAppearance()
    {
        ViewModel.ApplyAppearanceFromSettings();
    }

    public override void OnCompactStateChanged(bool collapsed)
    {
        _isCompactCollapsed = collapsed;
        ViewModel.SetViewAttached(!collapsed && MaterializedView is not null);
        if (MaterializedView is ClipboardWidgetContent content)
        {
            content.OnCompactStateChanged(collapsed);
        }

        if (collapsed)
        {
            ViewModel.ReleaseCachedThumbnails();
            IconHelper.ClearCacheScope(ClipboardWidgetViewModel.IconCacheScope);
        }
    }

    public override void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (MaterializedView is ClipboardWidgetContent content)
        {
            content.OnWindowVisibilityChanged(visible);
        }
    }

    public override void OnWindowLongHidden()
    {
        // Do not hibernate or detach the expanded view here: WidgetShell.ShellContent
        // still owns the live FrameworkElement while expanded, and collapsed views are
        // hibernated exclusively by WidgetShell.HibernateHostedContentView() after
        // detaching ShellContent.
        ViewModel.ReleaseCachedThumbnails();
        IconHelper.ClearCacheScope(ClipboardWidgetViewModel.IconCacheScope);
    }

    object? IWidgetTransientStateContent.CaptureTransientState()
    {
        if (MaterializedView is ClipboardWidgetContent content)
        {
            return content.CaptureTransientState();
        }

        return HasHibernatedTransientState ? HibernatedTransientState : null;
    }

    void IWidgetTransientStateContent.RestoreTransientState(object? state)
    {
        if (MaterializedView is ClipboardWidgetContent content && state is ClipboardTransientState typed)
        {
            content.RestoreTransientState(typed);
            return;
        }

        SaveHibernatedTransientState(state);
    }

    internal async Task<bool> ImportNativeDroppedFilesAsync(IReadOnlyList<DroppedFilePath> files)
    {
        if (files.Count == 0 || IsDisposed)
        {
            return false;
        }

        string[] paths = files
            .Select(static f => f.Path)
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (paths.Length == 0)
        {
            return false;
        }

        if (MaterializedView is ClipboardWidgetContent content)
        {
            return await content.HandleNativeDroppedFilesAsync(paths) > 0;
        }

        int imported = await ViewModel.ImportDroppedPathsAsync(paths);
        if (imported > 0)
        {
            FeedbackRequested?.Invoke(
                this,
                new WidgetFeedbackRequestedEventArgs(
                    new WidgetFeedbackRequest(
                        _localizationService.Format("Clipboard.Feedback.ImportedCount", imported),
                        WidgetFeedbackSeverity.Info,
                        "clipboard-imported")));
            return true;
        }

        return false;
    }

    private void Content_FeedbackRequested(object? sender, WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    protected override void Dispose(bool disposing)
    {
        if (MaterializedView is ClipboardWidgetContent content)
        {
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.DisposeLeafOnly();
        }

        ViewModel.Dispose();
        IconHelper.ClearCacheScope(ClipboardWidgetViewModel.IconCacheScope);
    }

    private static ClipboardWidgetViewModel CreateViewModel(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService,
        ClipboardHistoryService? clipboardService)
    {
        SettingsService resolvedSettings = settingsService ?? App.Current?.SettingsService ?? new SettingsService();
        ClipboardHistoryService? resolvedService = clipboardService ?? App.Current?.ClipboardHistoryService;

        var viewModel = new ClipboardWidgetViewModel(
            config,
            resolvedService,
            resolvedSettings,
            localizationService,
            uiDispatch: static action =>
            {
                var dq = App.UiDispatcherQueue;
                if (dq is null || dq.HasThreadAccess)
                {
                    action();
                }
                else
                {
                    dq.TryEnqueue(() => action());
                }
            },
            iconLoader: static (path, decodeWidth) => IconHelper.GetIconAsync(
                path,
                hideShortcutArrowOverlay: true,
                showImageFilesAsIcons: true,
                decodePixelWidth: decodeWidth,
                cacheScope: ClipboardWidgetViewModel.IconCacheScope),
            thumbnailLoader: static (path, decodeWidth) => IconHelper.GetIconAsync(
                path,
                hideShortcutArrowOverlay: true,
                showImageFilesAsIcons: false,
                decodePixelWidth: decodeWidth,
                cacheScope: ClipboardWidgetViewModel.IconCacheScope));
        viewModel.SetViewAttached(false);
        return viewModel;
    }
}
