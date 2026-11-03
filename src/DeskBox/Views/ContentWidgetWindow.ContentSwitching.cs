using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    internal int LiveMemberCount => _contentHost.LiveContentCount;

    internal int CachedGroupContentCount => _cachedGroupContents.Count;

    internal IEnumerable<WidgetKind> CachedGroupContentKinds =>
        _cachedGroupContents.Values.Select(content => content.WidgetKind);

    internal IEnumerable<IWidgetContent> CachedGroupContents =>
        _cachedGroupContents.Values;

    internal bool HasPresentableContentFrame =>
        ContentWidgetShell.HasPresentableContentFrame;

    internal async Task<ContentWidgetSwitchPreparation?> PrepareContentSwitchAsync(
        WidgetConfig targetConfig,
        IWidgetContent targetContent,
        WidgetContentDescriptor targetDescriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetConfig);
        ArgumentNullException.ThrowIfNull(targetContent);
        ArgumentNullException.ThrowIfNull(targetDescriptor);

        WidgetShellContentHost.WidgetShellPreparedContent? prepared =
            await _contentHost.PrepareContentAsync(targetContent, cancellationToken);
        return prepared is null
            ? null
            : new ContentWidgetSwitchPreparation(
                this,
                targetConfig,
                targetDescriptor,
                prepared);
    }

    private ContentWidgetSwitchTransition? BeginContentSwitch(
        WidgetConfig targetConfig,
        WidgetContentDescriptor targetDescriptor,
        WidgetShellContentHost.WidgetShellPreparedContent prepared)
    {
        WidgetShellContentHost.WidgetShellContentTransition? hostTransition =
            _contentHost.CommitPreparedContent(prepared);
        return hostTransition is null
            ? null
            : new ContentWidgetSwitchTransition(
                this,
                _config,
                _descriptor,
                targetConfig,
                targetDescriptor,
                hostTransition);
    }

    private Task AnimateContentSwitchAsync(
        bool directional,
        bool forward,
        CancellationToken cancellationToken)
    {
        return ContentWidgetShell.AnimateContentTransitionAsync(
            directional,
            forward,
            cancellationToken);
    }

    private void ApplyMemberContext(
        WidgetConfig config,
        WidgetContentDescriptor descriptor,
        IWidgetContent content,
        bool animateGroupIdentity = false,
        WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Programmatic,
        bool forward = true)
    {
        if (animateGroupIdentity &&
            origin is not WidgetGroupSwitchOrigin.Programmatic)
        {
            ContentWidgetShell.CaptureOutgoingCompactSwitchState();
        }

        ContentWidgetShell.ClearFeedback();
        _config = config;
        _descriptor = descriptor;
        Diagnostics.SetWidgetContext(config);
        _titleViewModel.SetConfig(config);
        ApplyTitleIdentity(config, descriptor, content);
        // Paint the Glance yield gate from the incoming member's config
        // before the view's Loaded pass — a cached Glance view carries the
        // previous member's flag and would draw one stale frame otherwise.
        if (content is GlanceWidgetContentAdapter glanceAdapter)
        {
            glanceAdapter.SetShellCustomBackgroundActive(
                WidgetBackgroundCustomization.HasCustomBackground(config));
        }
        if (content is FileWidgetContentAdapter fileAdapter)
        {
            fileAdapter.HostHasActiveShellDragWindowProbe = () => HasActiveNativeShellDragWindow;
        }
        AttachCompactPresentationSource(content);
        AttachFeedbackSource(content);
        AttachHostContextMenuSource(content);
        ApplyLocalizedTitleActionTooltips();
        // The window chrome changes with the active member. A cached member's
        // content only needs updating if a real appearance notification occurred.
        ApplyAppearancePreview(invalidateContent: false);
        RefreshCompactPresentation();
        RefreshWidgetGroupPresentation(
            animateGroupIdentity,
            origin,
            forward);
    }

    private void ApplyTitleIdentity(
        WidgetConfig config,
        WidgetContentDescriptor descriptor,
        IWidgetContent? content)
    {
        // File widgets own their managed/mapped identity in the view model so
        // runtime remapping and group switches stay consistent; every other
        // kind keeps the descriptor defaults.
        if (content is FileWidgetContentAdapter fileSurface)
        {
            ContentWidgetShell.TitleGlyph = fileSurface.ViewModel.IconGlyph;
            ContentWidgetShell.TitleIconKind = fileSurface.ViewModel.TitleIconKind;
            return;
        }

        if (content is CustomWidgetContentAdapter customSurface)
        {
            ContentWidgetShell.TitleGlyph = customSurface.ViewModel.TitleGlyph;
            ContentWidgetShell.TitleIconKind = customSurface.ViewModel.TitleIconKind;
            return;
        }

        ContentWidgetShell.TitleGlyph = descriptor.DefaultGlyph;
        ContentWidgetShell.TitleIconKind =
            WidgetTitleIconKindNames.FromWidgetConfig(config);
    }

    internal IWidgetContent? TakeCachedGroupContent(string widgetId)
    {
        TrimCachedGroupContentsToCapacity(GetCachedGroupContentCapacity());
        if (string.IsNullOrWhiteSpace(widgetId) ||
            !_cachedGroupContents.Remove(widgetId, out IWidgetContent? content))
        {
            return null;
        }

        if (content is IWidgetGroupContentCacheable cacheable)
        {
            cacheable.OutboundDragCompleted -= OnCachedGroupContentOutboundDragCompleted;
        }

        RemoveCachedGroupContentOrderEntry(widgetId);
        App.LogVerbose($"[WidgetGroup] Reusing cached content member={widgetId}");
        return content;
    }

    private bool TryRetainGroupContent(IWidgetContent content)
    {
        int capacity = GetCachedGroupContentCapacity();
        bool hasActiveDrag = content is IWidgetGroupContentCacheable
        {
            HasActiveOutboundDrag: true
        };
        if (IsClosing ||
            (capacity <= 0 && !hasActiveDrag) ||
            content is not IWidgetGroupContentCacheable ||
            string.IsNullOrWhiteSpace(content.WidgetId))
        {
            return false;
        }

        if (_cachedGroupContents.Remove(content.WidgetId, out IWidgetContent? replaced))
        {
            RemoveCachedGroupContentOrderEntry(content.WidgetId);
            if (!ReferenceEquals(replaced, content))
            {
                DisposeCachedGroupContent(replaced);
            }
        }

        TrimCachedGroupContentsToCapacity(Math.Max(0, capacity - 1));

        if (hasActiveDrag && content is IWidgetGroupContentCacheable cacheable)
        {
            cacheable.OutboundDragCompleted -= OnCachedGroupContentOutboundDragCompleted;
            cacheable.OutboundDragCompleted += OnCachedGroupContentOutboundDragCompleted;
        }
        else if (!hasActiveDrag)
        {
            content.OnWindowLongHidden();
        }
        _cachedGroupContents[content.WidgetId] = content;
        _cachedGroupContentOrder.AddLast(content.WidgetId);
        App.LogVerbose($"[WidgetGroup] Cached inactive content member={content.WidgetId}");
        return true;
    }

    private int GetCachedGroupContentCapacity()
    {
        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(SettingsService.Settings);
        return PerformanceSettingsPolicy.ResolveInactiveGroupContentCacheCapacity(
            performance.CacheBudget);
    }

    private void TrimCachedGroupContentsToCapacity(int capacity)
    {
        capacity = Math.Max(0, capacity);
        LinkedListNode<string>? node = _cachedGroupContentOrder.First;
        while (_cachedGroupContents.Count > capacity && node is not null)
        {
            LinkedListNode<string>? next = node.Next;
            if (_cachedGroupContents.TryGetValue(node.Value, out IWidgetContent? candidate) &&
                candidate is IWidgetGroupContentCacheable { HasActiveOutboundDrag: true })
            {
                node = next;
                continue;
            }

            _cachedGroupContentOrder.Remove(node);
            if (_cachedGroupContents.Remove(node.Value, out IWidgetContent? evicted))
            {
                DisposeCachedGroupContent(evicted);
            }

            node = next;
        }
    }

    internal void RunLongHiddenNoRebuildMaintenance()
    {
        if (IsClosing)
        {
            return;
        }

        // Keep both the current and bounded inactive-member views warm. Disposing
        // either here turns the next reveal or group switch into a full XAML/data
        // reconstruction, which is more disruptive than the retained memory.
        if (!Visible)
        {
            SuspendBackdropTargetsForHiddenWindow();
        }

        _contentHost.CurrentContent?.OnWindowLongHidden();
        ContentWidgetShell.ClearCompactBitmaps();
        // Custom backgrounds follow the same hidden-widget discipline: no
        // decoded bitmaps stay alive while the window is long-hidden.
        ReleaseCustomBackgroundSurface();
    }

    /// <summary>
    /// Releases only inactive group members after a long hidden period. The
    /// current content host remains alive, so the next reveal does not rebuild
    /// the visible widget tree; a later group switch can recreate these members.
    /// </summary>
    internal int ReleaseLongHiddenContentResources()
    {
        if (Visible || IsClosing)
        {
            return 0;
        }

        SuspendBackdropTargetsForHiddenWindow();
        _contentHost.CurrentContent?.OnWindowLongHidden();
        ContentWidgetShell.ClearCompactBitmaps();
        ReleaseCustomBackgroundSurface();
        int releasedCachedContents = _cachedGroupContents.Count;
        if (IsTargetCollapsed)
        {
            _contentHost.TryHibernateCurrentContentView();
        }
        DisposeCachedGroupContents();
        return releasedCachedContents;
    }

    private void DisposeCachedGroupContents()
    {
        foreach (IWidgetContent content in _cachedGroupContents.Values.ToArray())
        {
            DisposeCachedGroupContent(content);
        }

        _cachedGroupContents.Clear();
        _cachedGroupContentOrder.Clear();
    }

    private void RemoveCachedGroupContentOrderEntry(string widgetId)
    {
        LinkedListNode<string>? node = _cachedGroupContentOrder.First;
        while (node is not null)
        {
            LinkedListNode<string>? next = node.Next;
            if (string.Equals(node.Value, widgetId, StringComparison.Ordinal))
            {
                _cachedGroupContentOrder.Remove(node);
                return;
            }

            node = next;
        }
    }

    private void OnCachedGroupContentOutboundDragCompleted(object? sender, EventArgs e)
    {
        if (IsClosing ||
            sender is not IWidgetContent content ||
            string.IsNullOrWhiteSpace(content.WidgetId) ||
            !_cachedGroupContents.TryGetValue(content.WidgetId, out IWidgetContent? cached) ||
            !ReferenceEquals(cached, content) ||
            content is IWidgetGroupContentCacheable { HasActiveOutboundDrag: true })
        {
            return;
        }

        if (content is IWidgetGroupContentCacheable cacheable)
        {
            cacheable.OutboundDragCompleted -= OnCachedGroupContentOutboundDragCompleted;
        }

        content.OnWindowLongHidden();
        TrimCachedGroupContentsToCapacity(GetCachedGroupContentCapacity());
    }

    private void DisposeCachedGroupContent(IWidgetContent content)
    {
        try
        {
            if (content is IWidgetGroupContentCacheable cacheable)
            {
                cacheable.OutboundDragCompleted -= OnCachedGroupContentOutboundDragCompleted;
            }

            (content as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Cached content cleanup failed " +
                $"member={content.WidgetId}: {ex}");
        }
    }

    internal sealed class ContentWidgetSwitchPreparation : IDisposable
    {
        private readonly ContentWidgetWindow _owner;
        private readonly WidgetConfig _targetConfig;
        private readonly WidgetContentDescriptor _targetDescriptor;
        private WidgetShellContentHost.WidgetShellPreparedContent? _prepared;

        internal ContentWidgetSwitchPreparation(
            ContentWidgetWindow owner,
            WidgetConfig targetConfig,
            WidgetContentDescriptor targetDescriptor,
            WidgetShellContentHost.WidgetShellPreparedContent prepared)
        {
            _owner = owner;
            _targetConfig = targetConfig;
            _targetDescriptor = targetDescriptor;
            _prepared = prepared;
        }

        internal ContentWidgetSwitchTransition? BeginTransition()
        {
            WidgetShellContentHost.WidgetShellPreparedContent? prepared =
                Interlocked.Exchange(ref _prepared, null);
            return prepared is null
                ? null
                : _owner.BeginContentSwitch(
                    _targetConfig,
                    _targetDescriptor,
                    prepared);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _prepared, null)?.Dispose();
        }
    }

    internal sealed class ContentWidgetSwitchTransition : IDisposable
    {
        private readonly ContentWidgetWindow _owner;
        private readonly WidgetConfig _previousConfig;
        private readonly WidgetContentDescriptor _previousDescriptor;
        private readonly WidgetConfig _targetConfig;
        private readonly WidgetContentDescriptor _targetDescriptor;
        private readonly WidgetShellContentHost.WidgetShellContentTransition _hostTransition;
        private bool _isCompleted;

        internal ContentWidgetSwitchTransition(
            ContentWidgetWindow owner,
            WidgetConfig previousConfig,
            WidgetContentDescriptor previousDescriptor,
            WidgetConfig targetConfig,
            WidgetContentDescriptor targetDescriptor,
            WidgetShellContentHost.WidgetShellContentTransition hostTransition)
        {
            _owner = owner;
            _previousConfig = previousConfig;
            _previousDescriptor = previousDescriptor;
            _targetConfig = targetConfig;
            _targetDescriptor = targetDescriptor;
            _hostTransition = hostTransition;
        }

        internal IWidgetContent IncomingContent => _hostTransition.IncomingContent;

        internal IWidgetContent? OutgoingContent => _hostTransition.OutgoingContent;

        internal void Complete()
        {
            CompleteCore();
        }

        internal async Task CompleteAsync(
            WidgetGroupSwitchOrigin origin,
            bool forward,
            CancellationToken cancellationToken)
        {
            if (_isCompleted)
            {
                return;
            }

            try
            {
                _owner.ApplyMemberContext(
                    _targetConfig,
                    _targetDescriptor,
                    IncomingContent,
                    animateGroupIdentity: true,
                    origin,
                    forward);
                await _owner.AnimateContentSwitchAsync(
                    origin is not WidgetGroupSwitchOrigin.Programmatic,
                    forward,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _hostTransition.Complete();
                // The persistent group HWND never changes, so a pointer that
                // stayed over the capsule will not naturally enter the newly
                // hosted member. Re-arm Smart hover after the visual swap has
                // fully settled as well as when SetContent first fires.
                _owner.RearmCompactHoverAfterHostedContentChange();
                _isCompleted = true;
            }
            catch
            {
                try
                {
                    _hostTransition.Rollback();
                    if (OutgoingContent is { } outgoingContent)
                    {
                        _owner.ApplyMemberContext(
                            _previousConfig,
                            _previousDescriptor,
                            outgoingContent);
                    }
                }
                finally
                {
                    _isCompleted = true;
                }

                throw;
            }
        }

        private void CompleteCore()
        {
            if (_isCompleted)
            {
                return;
            }

            try
            {
                _owner.ApplyMemberContext(
                    _targetConfig,
                    _targetDescriptor,
                    IncomingContent);
                _hostTransition.Complete();
                _isCompleted = true;
            }
            catch
            {
                try
                {
                    _hostTransition.Rollback();
                    if (OutgoingContent is { } outgoingContent)
                    {
                        _owner.ApplyMemberContext(
                            _previousConfig,
                            _previousDescriptor,
                            outgoingContent);
                    }
                }
                finally
                {
                    _isCompleted = true;
                }

                throw;
            }
        }

        internal void Rollback()
        {
            if (_isCompleted)
            {
                return;
            }

            _hostTransition.Rollback();
            _isCompleted = true;
        }

        public void Dispose()
        {
            Rollback();
        }
    }
}
