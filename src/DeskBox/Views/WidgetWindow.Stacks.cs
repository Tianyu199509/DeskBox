using System.Numerics;
using System.Runtime.CompilerServices;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
    private const int StackExpandDurationMs = 210;
    private const int StackCollapseDurationMs = 190;
    private const int StackMaximumStaggerMs = 60;
    private const int StackPreviewScatterDurationMs = 125;
    private const int StackPreviewAssembleDurationMs = 145;
    private const int StackDuplicateInputWindowMs = 120;
    private const int StackTransitionSafetyTimeoutMs = 2200;
    private static readonly ConditionalWeakTable<FrameworkElement, StackRestingOffset>
        s_stackRestingOffsets = new();
    private readonly HashSet<Border> _stackSurfaces = [];
    private long _lastStackInputTick;
    private string? _lastStackInputKey;
    private bool _isStackTransitionRunning;
    private WidgetStackItem? _activeStackTransition;
    private bool _activeStackTransitionExpanded;
    private WidgetStackItem? _pendingStackTransition;
    private bool _pendingStackTransitionExpanded;
    private long _stackTransitionRequestGeneration;
    private long _stackExpansionPreparationGeneration = -1;
    private Task _stackTransitionRunnerTask = Task.CompletedTask;
    private IDisposable? _stackCompactInteractionLease;

    private void WidgetStackSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        _stackSurfaces.Add(border);
        ResetStackSurfaceVisuals(border);
        ApplyStackSurfaceLayout(border);
        ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
    }

    private void WidgetStackSurface_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ResetStackSurfaceVisuals(border);
            _stackSurfaces.Remove(border);
        }
    }

    private void WidgetStackSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy || _isClosing || _isHideAnimationRunning || !_isVisibleOnDesktop)
        {
            return;
        }

        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Hover);
        }
    }

    private void WidgetStackSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isClosing || _isHideAnimationRunning || !_isVisibleOnDesktop)
        {
            return;
        }

        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetStackSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (sender is Border border && e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
        {
            RootGrid.Focus(FocusState.Programmatic);
            ClearOtherWidgetSelections();
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Pressed);
        }
    }

    private void WidgetStackSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        var point = e.GetCurrentPoint(border).Position;
        bool isInside = point.X >= 0 && point.Y >= 0 &&
            point.X <= border.ActualWidth && point.Y <= border.ActualHeight;
        ApplyWidgetItemSurfaceState(
            border,
            isInside ? ItemSurfaceState.Hover : ItemSurfaceState.Normal);
    }

    private void UpdateStackSurfaces()
    {
        foreach (var border in _stackSurfaces.ToArray())
        {
            if (border.XamlRoot is null)
            {
                _stackSurfaces.Remove(border);
                continue;
            }

            ApplyStackSurfaceLayout(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void ApplyStackSurfaceLayout(Border border)
    {
        CornerRadius cornerRadius = GetItemSurfaceCornerRadius();
        border.CornerRadius = cornerRadius;
        foreach (var button in FindDescendants<Button>(border)
                     .Where(button => string.Equals(
                         button.Tag as string,
                         "StackExpandedAnchor",
                         StringComparison.Ordinal)))
        {
            button.CornerRadius = cornerRadius;
        }
    }

    private async void StackCollapseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WidgetStackItem stack })
        {
            RecordStackInput(stack);
            await SetStackExpandedWithAnimationAsync(stack, expanded: false);
        }
    }

    private async Task ToggleStackFromInputAsync(WidgetStackItem stack)
    {
        if (!TryAcceptStackInput(stack))
        {
            return;
        }

        RemoveVirtualStackSelection();
        await SetStackExpandedWithAnimationAsync(stack, !GetRequestedStackExpandedState(stack));
    }

    private bool TryAcceptStackInput(WidgetStackItem stack)
    {
        long now = Environment.TickCount64;
        if (string.Equals(_lastStackInputKey, stack.StackKey, StringComparison.Ordinal) &&
            now - _lastStackInputTick < StackDuplicateInputWindowMs)
        {
            return false;
        }

        RecordStackInput(stack, now);
        return true;
    }

    private void RecordStackInput(WidgetStackItem stack, long? tick = null)
    {
        _lastStackInputKey = stack.StackKey;
        _lastStackInputTick = tick ?? Environment.TickCount64;
    }

    private async Task SetStackExpandedWithAnimationAsync(WidgetStackItem stack, bool expanded)
    {
        if (_isClosing)
        {
            return;
        }

        if (GetRequestedStackExpandedState(stack) == expanded)
        {
            if (_isStackTransitionRunning)
            {
                await _stackTransitionRunnerTask;
            }

            return;
        }

        _pendingStackTransition = stack;
        _pendingStackTransitionExpanded = expanded;
        _stackTransitionRequestGeneration++;
        if (!_isStackTransitionRunning)
        {
            _stackTransitionRunnerTask = RunStackTransitionQueueAsync();
        }

        await _stackTransitionRunnerTask;
    }

    private bool GetRequestedStackExpandedState(WidgetStackItem stack)
    {
        if (ReferenceEquals(_pendingStackTransition, stack))
        {
            return _pendingStackTransitionExpanded;
        }

        if (ReferenceEquals(_activeStackTransition, stack))
        {
            return _activeStackTransitionExpanded;
        }

        return stack.IsExpanded;
    }

    private WidgetStackItem? GetExpandedStack() =>
        ViewModel.VisibleItems
            .OfType<WidgetStackItem>()
            .FirstOrDefault(stack => stack.IsExpanded);

    private async Task RunStackTransitionQueueAsync()
    {
        _isStackTransitionRunning = true;
        bool ownsContainerTransitionSuppression = !_areItemTransitionsSuppressed;
        if (ownsContainerTransitionSuppression)
        {
            SuppressItemContainerTransitions();
        }

        try
        {
            while (!_isClosing && _pendingStackTransition is { } stack)
            {
                bool expanded = _pendingStackTransitionExpanded;
                long requestGeneration = _stackTransitionRequestGeneration;
                _pendingStackTransition = null;
                _activeStackTransition = stack;
                _activeStackTransitionExpanded = expanded;

                if (stack.IsExpanded == expanded)
                {
                    continue;
                }

                long transitionStartedAt = Environment.TickCount64;
                App.Log(
                    $"[FileStack] Transition start category={stack.Category} " +
                    $"expanded={expanded} mode={ViewModel.ViewMode}");
                bool completed = false;
                try
                {
                    completed = await ExecuteStackTransitionWithRecoveryAsync(
                        stack,
                        expanded,
                        requestGeneration);
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[FileStack] Transition failed category={stack.Category} " +
                        $"expanded={expanded}: {ex}");
                    if (!_isClosing && IsCurrentStackTransitionRequest(requestGeneration))
                    {
                        ApplyStackTransitionFinalState(stack, expanded);
                    }
                }
                finally
                {
                    ResetAllStackAnimationVisuals();
                    App.Log(
                        $"[FileStack] Transition end category={stack.Category} expanded={expanded} " +
                        $"completed={completed} " +
                        $"elapsedMs={Environment.TickCount64 - transitionStartedAt}");
                }
            }
        }
        finally
        {
            ResetAllStackAnimationVisuals();
            _activeStackTransition = null;
            if (_isClosing)
            {
                _pendingStackTransition = null;
            }

            if (ownsContainerTransitionSuppression)
            {
                RestoreItemContainerTransitions();
            }

            _isStackTransitionRunning = false;
            ReleaseStackInteractionLeaseIfIdle();
            ScheduleStackPostTransitionStabilization();
        }
    }

    private async Task<bool> ExecuteStackTransitionWithRecoveryAsync(
        WidgetStackItem stack,
        bool expanded,
        long requestGeneration)
    {
        Task<bool> transitionTask = ExecuteStackTransitionAsync(
            stack,
            expanded,
            requestGeneration);
        Task completedTask = await Task.WhenAny(
            transitionTask,
            Task.Delay(StackTransitionSafetyTimeoutMs));
        if (ReferenceEquals(completedTask, transitionTask))
        {
            return await transitionTask;
        }

        if (!IsCurrentStackTransitionRequest(requestGeneration))
        {
            return false;
        }

        _stackTransitionRequestGeneration++;
        App.Log(
            $"[FileStack] Transition watchdog recovered category={stack.Category} " +
            $"expanded={expanded}");
        ResetAllStackAnimationVisuals();
        Task quiescedTask = await Task.WhenAny(transitionTask, Task.Delay(500));
        if (ReferenceEquals(quiescedTask, transitionTask))
        {
            try
            {
                await transitionTask;
            }
            catch (Exception ex)
            {
                App.Log($"[FileStack] Timed-out transition stopped with error: {ex.Message}");
            }
        }

        ResetAllStackAnimationVisuals();
        ApplyStackTransitionFinalState(stack, expanded);
        if (!transitionTask.IsCompleted)
        {
            _ = transitionTask.ContinueWith(
                task => App.Log($"[FileStack] Timed-out transition later failed: {task.Exception}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        return true;
    }

    private async Task<bool> ExecuteStackTransitionAsync(
        WidgetStackItem stack,
        bool expanded,
        long requestGeneration)
    {
        if (!SystemStackAnimationsEnabled())
        {
            if (IsCurrentStackTransitionRequest(requestGeneration))
            {
                ApplyStackTransitionFinalState(stack, expanded);
                return true;
            }

            return false;
        }

        if (expanded)
        {
            var previouslyExpanded = ViewModel.VisibleItems
                .OfType<WidgetStackItem>()
                .FirstOrDefault(candidate =>
                    candidate.IsExpanded &&
                    !ReferenceEquals(candidate, stack));
            if (previouslyExpanded is not null)
            {
                return await SwitchExpandedStackCoreAsync(
                    previouslyExpanded,
                    stack,
                    requestGeneration);
            }

            if (!_isClosing)
            {
                return await ExpandStackCoreAsync(stack, requestGeneration);
            }
        }
        else
        {
            return await CollapseStackCoreAsync(stack, requestGeneration);
        }

        return false;
    }

    private bool IsCurrentStackTransitionRequest(long requestGeneration) =>
        requestGeneration == _stackTransitionRequestGeneration;

    private async Task<bool> ExpandStackCoreAsync(
        WidgetStackItem stack,
        long requestGeneration)
    {
        Border? stackSurface = FindStackSurface(stack);
        var previewVisuals = FindStackPreviewVisuals(stackSurface);
        await AnimateStackPreviewAsync(previewVisuals, dispersing: true);
        if (_isClosing || !IsCurrentStackTransitionRequest(requestGeneration))
        {
            ResetStackPreviewVisuals(previewVisuals);
            return false;
        }

        ResetStackPreviewVisuals(previewVisuals);
        EnsureStackInteractionLease();
        BeginStackExpansionPreparation(requestGeneration);
        try
        {
            ViewModel.SetStackExpanded(stack, expanded: true);
            await WaitForStackLayoutAsync(
                stack,
                requireMembers: true,
                requestGeneration: requestGeneration,
                prepareForExpansion: true);
        }
        finally
        {
            EndStackExpansionPreparation(requestGeneration);
        }
        if (_isClosing || !IsCurrentStackTransitionRequest(requestGeneration))
        {
            return false;
        }

        stackSurface = FindStackSurface(stack);
        var memberSurfaces = FindStackMemberSurfaces(stack);
        FrameworkElement? anchor = FindStackAnchor(stackSurface);
        CompositionScopedBatch? batch = TryCreateStackAnimationBatch(
            anchor ?? memberSurfaces.FirstOrDefault());
        int maximumDelay = AnimateStackMembers(
            stackSurface,
            memberSurfaces,
            expanding: true);
        AnimateExpandedAnchor(stackSurface, appearing: true);
        await EndStackAnimationBatchAsync(
            batch,
            StackExpandDurationMs + maximumDelay + 80);
        if (_isClosing || !IsCurrentStackTransitionRequest(requestGeneration))
        {
            return false;
        }

        ResetStackMemberVisuals(memberSurfaces);
        ResetStackAnchorVisual(stackSurface);
        await SettleStackLayoutAfterAnimationAsync(
            stack,
            requireMembers: true,
            requestGeneration: requestGeneration);
        return IsCurrentStackTransitionRequest(requestGeneration);
    }

    private async Task<bool> CollapseStackCoreAsync(
        WidgetStackItem stack,
        long requestGeneration)
    {
        Border? stackSurface = FindStackSurface(stack);
        var memberSurfaces = FindStackMemberSurfaces(stack);
        FrameworkElement? anchor = FindStackAnchor(stackSurface);
        CompositionScopedBatch? batch = TryCreateStackAnimationBatch(anchor);
        int maximumDelay = AnimateStackMembers(stackSurface, memberSurfaces, expanding: false);
        AnimateExpandedAnchor(stackSurface, appearing: false);
        await EndStackAnimationBatchAsync(
            batch,
            StackCollapseDurationMs + maximumDelay + 80);
        if (_isClosing || !IsCurrentStackTransitionRequest(requestGeneration))
        {
            return false;
        }

        ResetStackMemberVisuals(memberSurfaces);
        ResetStackAnchorVisual(stackSurface);

        ViewModel.SetStackExpanded(stack, expanded: false);
        await WaitForStackLayoutAsync(
            stack,
            requireMembers: false,
            requestGeneration: requestGeneration);
        stackSurface = FindStackSurface(stack);
        var previewVisuals = FindStackPreviewVisuals(stackSurface);
        if (_isClosing || !IsCurrentStackTransitionRequest(requestGeneration))
        {
            ResetStackPreviewVisuals(previewVisuals);
            return false;
        }

        await AnimateStackPreviewAsync(previewVisuals, dispersing: false);
        if (_isClosing || !IsCurrentStackTransitionRequest(requestGeneration))
        {
            return false;
        }

        ResetStackPreviewVisuals(previewVisuals);
        await SettleStackLayoutAfterAnimationAsync(
            stack,
            requireMembers: false,
            requestGeneration: requestGeneration);
        await RestoreStackAnchorFocusAsync(stack, requestGeneration);
        ReleaseStackInteractionLeaseIfIdle();
        return IsCurrentStackTransitionRequest(requestGeneration);
    }

    private async Task<bool> SwitchExpandedStackCoreAsync(
        WidgetStackItem previousStack,
        WidgetStackItem nextStack,
        long requestGeneration)
    {
        Border? previousSurface = FindStackSurface(previousStack);
        var previousMembers = FindStackMemberSurfaces(previousStack);
        FrameworkElement? previousAnchor = FindStackAnchor(previousSurface);
        CompositionScopedBatch? collapseBatch = TryCreateStackAnimationBatch(previousAnchor);
        int maximumDelay = AnimateStackMembers(
            previousSurface,
            previousMembers,
            expanding: false);
        AnimateExpandedAnchor(previousSurface, appearing: false);
        await EndStackAnimationBatchAsync(
            collapseBatch,
            StackCollapseDurationMs + maximumDelay + 80);
        if (_isClosing || !IsCurrentStackTransitionRequest(requestGeneration))
        {
            return false;
        }

        ResetStackMemberVisuals(previousMembers);
        ResetStackAnchorVisual(previousSurface);

        // Change the expanded key once so the primary GridView never observes an
        // intermediate state where both groups are removed and reinserted.
        BeginStackExpansionPreparation(requestGeneration);
        try
        {
            ViewModel.SetStackExpanded(nextStack, expanded: true);
            await WaitForStackLayoutAsync(
                nextStack,
                requireMembers: true,
                requestGeneration: requestGeneration,
                prepareForExpansion: true);
        }
        finally
        {
            EndStackExpansionPreparation(requestGeneration);
        }
        if (_isClosing || !IsCurrentStackTransitionRequest(requestGeneration))
        {
            return false;
        }

        Border? nextSurface = FindStackSurface(nextStack);
        var nextMembers = FindStackMemberSurfaces(nextStack);
        FrameworkElement? nextAnchor = FindStackAnchor(nextSurface);
        CompositionScopedBatch? expandBatch = TryCreateStackAnimationBatch(
            nextAnchor ?? nextMembers.FirstOrDefault());
        maximumDelay = AnimateStackMembers(
            nextSurface,
            nextMembers,
            expanding: true);
        AnimateExpandedAnchor(nextSurface, appearing: true);
        await EndStackAnimationBatchAsync(
            expandBatch,
            StackExpandDurationMs + maximumDelay + 80);
        if (_isClosing || !IsCurrentStackTransitionRequest(requestGeneration))
        {
            return false;
        }

        ResetStackMemberVisuals(nextMembers);
        ResetStackAnchorVisual(nextSurface);
        await SettleStackLayoutAfterAnimationAsync(
            nextStack,
            requireMembers: true,
            requestGeneration: requestGeneration);
        return IsCurrentStackTransitionRequest(requestGeneration);
    }

    private void EnsureStackInteractionLease()
    {
        _stackCompactInteractionLease ??= AcquireCompactInteraction("file-stack-inline");
    }

    private void ReleaseStackInteractionLeaseIfIdle()
    {
        if (ViewModel.VisibleItems.OfType<WidgetStackItem>().Any(stack => stack.IsExpanded) ||
            (_pendingStackTransition is not null && _pendingStackTransitionExpanded))
        {
            return;
        }

        _stackCompactInteractionLease?.Dispose();
        _stackCompactInteractionLease = null;
    }

    private void ApplyStackTransitionFinalState(WidgetStackItem stack, bool expanded)
    {
        ResetAllStackAnimationVisuals();
        if (expanded)
        {
            EnsureStackInteractionLease();
        }

        ViewModel.SetStackExpanded(stack, expanded);
        if (!expanded)
        {
            DispatcherQueue.TryEnqueue(() => RestoreStackAnchorFocus(stack));
            ReleaseStackInteractionLeaseIfIdle();
        }
    }

    private async Task RestoreStackAnchorFocusAsync(
        WidgetStackItem stack,
        long requestGeneration)
    {
        await WaitForNextStackRenderAsync();
        if (IsCurrentStackTransitionRequest(requestGeneration))
        {
            RestoreStackAnchorFocus(stack);
        }
    }

    private void RestoreStackAnchorFocus(WidgetStackItem stack)
    {
        if (GetActiveItemsView()?.ContainerFromItem(stack) is Control container)
        {
            container.Focus(FocusState.Programmatic);
        }
    }

    private void CleanupStackTransitions()
    {
        _stackTransitionRequestGeneration++;
        _stackExpansionPreparationGeneration = -1;
        _pendingStackTransition = null;
        ResetAllStackAnimationVisuals();
        _stackCompactInteractionLease?.Dispose();
        _stackCompactInteractionLease = null;
    }

    private Border? FindStackSurface(WidgetStackItem stack) =>
        _stackSurfaces.FirstOrDefault(surface =>
            surface.XamlRoot is not null &&
            ReferenceEquals(surface.DataContext, stack));

    private static IReadOnlyList<FrameworkElement> FindStackPreviewVisuals(Border? stackSurface)
    {
        if (stackSurface is null)
        {
            return [];
        }

        return FindDescendants<FrameworkElement>(stackSurface)
            .Where(element => string.Equals(
                element.Tag as string,
                "StackCollapsedPreview",
                StringComparison.Ordinal))
            .ToArray();
    }

    private async Task AnimateStackPreviewAsync(
        IReadOnlyList<FrameworkElement> previewVisuals,
        bool dispersing)
    {
        FrameworkElement? batchAnchor = previewVisuals.FirstOrDefault(element =>
            element.XamlRoot is not null && element.Visibility == Visibility.Visible);
        CompositionScopedBatch? batch = TryCreateStackAnimationBatch(batchAnchor);
        foreach (var element in previewVisuals)
        {
            if (element.Visibility != Visibility.Visible)
            {
                continue;
            }

            StartStackPreviewAnimation(element, dispersing);
        }

        await EndStackAnimationBatchAsync(
            batch,
            (dispersing ? StackPreviewScatterDurationMs : StackPreviewAssembleDurationMs) +
            80);
    }

    private static CompositionScopedBatch? TryCreateStackAnimationBatch(FrameworkElement? anchor)
    {
        if (anchor?.XamlRoot is null)
        {
            return null;
        }

        try
        {
            return ElementCompositionPreview
                .GetElementVisual(anchor)
                .Compositor
                .CreateScopedBatch(CompositionBatchTypes.Animation);
        }
        catch
        {
            return null;
        }
    }

    private static async Task EndStackAnimationBatchAsync(
        CompositionScopedBatch? batch,
        int fallbackTimeoutMs)
    {
        if (batch is null)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        batch.Completed += (_, _) => completion.TrySetResult();
        batch.End();
        await Task.WhenAny(
            completion.Task,
            Task.Delay(Math.Max(80, fallbackTimeoutMs)));
        batch.Dispose();
    }

    private async Task WaitForStackLayoutAsync(
        WidgetStackItem stack,
        bool requireMembers,
        long requestGeneration,
        bool prepareForExpansion = false)
    {
        Point? previousStackCenter = null;
        IReadOnlyDictionary<WidgetItem, Point>? previousMemberCenters = null;
        for (int attempt = 0;
             attempt < 6 && !_isClosing && IsCurrentStackTransitionRequest(requestGeneration);
             attempt++)
        {
            var itemsView = GetActiveItemsView();
            itemsView?.InvalidateMeasure();
            itemsView?.InvalidateArrange();
            itemsView?.UpdateLayout();
            if (prepareForExpansion)
            {
                PrepareStackExpansionVisuals(stack);
            }

            await WaitForNextStackRenderAsync();
            if (!IsCurrentStackTransitionRequest(requestGeneration))
            {
                return;
            }

            itemsView?.UpdateLayout();
            if (prepareForExpansion)
            {
                PrepareStackExpansionVisuals(stack);
            }

            if (!TryCaptureStackLayout(
                    stack,
                    requireMembers,
                    out Point stackCenter,
                    out IReadOnlyDictionary<WidgetItem, Point> memberCenters))
            {
                previousStackCenter = null;
                previousMemberCenters = null;
                continue;
            }

            if (previousStackCenter is { } previousCenter &&
                previousMemberCenters is not null &&
                StackLayoutsMatch(
                    previousCenter,
                    previousMemberCenters,
                    stackCenter,
                    memberCenters))
            {
                return;
            }

            previousStackCenter = stackCenter;
            previousMemberCenters = memberCenters;
        }
    }

    private bool TryCaptureStackLayout(
        WidgetStackItem stack,
        bool requireMembers,
        out Point stackCenter,
        out IReadOnlyDictionary<WidgetItem, Point> memberCenters)
    {
        stackCenter = default;
        memberCenters = new Dictionary<WidgetItem, Point>();
        Border? stackSurface = FindStackSurface(stack);
        if (stackSurface is not { ActualWidth: > 0, ActualHeight: > 0 } ||
            !TryGetElementCenter(stackSurface, out stackCenter))
        {
            return false;
        }

        if (!requireMembers)
        {
            return true;
        }

        var centers = new Dictionary<WidgetItem, Point>();
        foreach (var surface in FindStackMemberSurfaces(stack))
        {
            if (surface.DataContext is WidgetItem item &&
                surface.ActualWidth > 0 &&
                surface.ActualHeight > 0 &&
                TryGetElementCenter(surface, out Point center))
            {
                centers[item] = center;
            }
        }

        memberCenters = centers;
        return centers.Count >= Math.Min(3, stack.Members.Count);
    }

    private static bool StackLayoutsMatch(
        Point previousStackCenter,
        IReadOnlyDictionary<WidgetItem, Point> previousMemberCenters,
        Point stackCenter,
        IReadOnlyDictionary<WidgetItem, Point> memberCenters)
    {
        if (!PointsAreNear(previousStackCenter, stackCenter) ||
            previousMemberCenters.Count != memberCenters.Count)
        {
            return false;
        }

        foreach (var (item, center) in memberCenters)
        {
            if (!previousMemberCenters.TryGetValue(item, out Point previousCenter) ||
                !PointsAreNear(previousCenter, center))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PointsAreNear(Point first, Point second) =>
        Math.Abs(first.X - second.X) <= 0.75 &&
        Math.Abs(first.Y - second.Y) <= 0.75;

    private async Task SettleStackLayoutAfterAnimationAsync(
        WidgetStackItem stack,
        bool requireMembers,
        long requestGeneration)
    {
        if (!IsCurrentStackTransitionRequest(requestGeneration))
        {
            return;
        }

        ResetAllStackAnimationVisuals();
        var itemsView = GetActiveItemsView();
        itemsView?.InvalidateMeasure();
        itemsView?.InvalidateArrange();
        itemsView?.UpdateLayout();
        await WaitForStackLayoutAsync(stack, requireMembers, requestGeneration);
        if (!IsCurrentStackTransitionRequest(requestGeneration))
        {
            return;
        }

        ResetAllStackAnimationVisuals();
        itemsView?.UpdateLayout();
    }

    private static async Task WaitForNextStackRenderAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? renderingHandler = null;
        renderingHandler = (_, _) =>
        {
            CompositionTarget.Rendering -= renderingHandler;
            completion.TrySetResult();
        };

        CompositionTarget.Rendering += renderingHandler;
        await Task.WhenAny(completion.Task, Task.Delay(80));
        CompositionTarget.Rendering -= renderingHandler;
    }

    private static void StartStackPreviewAnimation(
        FrameworkElement element,
        bool dispersing)
    {
        ResetStackElementVisual(element);
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;
        visual.CenterPoint = new Vector3(
            (float)Math.Max(0, element.ActualWidth / 2),
            (float)Math.Max(0, element.ActualHeight / 2),
            0);
        TimeSpan duration = TimeSpan.FromMilliseconds(
            dispersing ? StackPreviewScatterDurationMs : StackPreviewAssembleDurationMs);
        var easing = compositor.CreateCubicBezierEasingFunction(
            dispersing ? new Vector2(0.36f, 0.0f) : new Vector2(0.16f, 0.84f),
            dispersing ? new Vector2(0.72f, 0.25f) : new Vector2(0.24f, 1.0f));

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = duration;
        scale.InsertKeyFrame(0, dispersing ? Vector3.One : new Vector3(0.94f, 0.94f, 1));
        scale.InsertKeyFrame(1, dispersing ? new Vector3(0.94f, 0.94f, 1) : Vector3.One, easing);

        float baseOpacity = (float)Math.Clamp(element.Opacity, 0, 1);
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = duration;
        opacity.InsertKeyFrame(0, dispersing ? baseOpacity : 0);
        opacity.InsertKeyFrame(1, dispersing ? 0 : baseOpacity, easing);

        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    private static void ResetStackPreviewVisuals(IEnumerable<FrameworkElement> previewVisuals)
    {
        foreach (var element in previewVisuals)
        {
            ResetStackElementVisual(element);
        }
    }

    private void ResetStackMemberVisuals(IEnumerable<Border> memberSurfaces)
    {
        foreach (var surface in memberSurfaces)
        {
            ApplyWidgetItemSurfaceState(surface, ItemSurfaceState.Normal);
            ResetStackElementVisual(surface);
        }
    }

    private static void ResetStackAnchorVisual(Border? stackSurface)
    {
        if (stackSurface is null)
        {
            return;
        }

        foreach (var anchor in FindDescendants<FrameworkElement>(stackSurface)
                     .Where(element => string.Equals(
                         element.Tag as string,
                         "StackExpandedAnchor",
                         StringComparison.Ordinal)))
        {
            ResetStackElementVisual(anchor);
        }
    }

    private static void ResetStackSurfaceVisuals(Border stackSurface)
    {
        ResetStackPreviewVisuals(FindStackPreviewVisuals(stackSurface));
        foreach (var element in FindDescendants<FrameworkElement>(stackSurface)
                     .Where(element => element.Tag is string tag &&
                         (tag.StartsWith("StackPreviewLayer", StringComparison.Ordinal) ||
                          string.Equals(tag, "StackPreviewBadge", StringComparison.Ordinal))))
        {
            ResetStackElementVisual(element);
        }

        ResetStackAnchorVisual(stackSurface);
    }

    private void PrepareStackExpansionVisuals(WidgetStackItem stack)
    {
        Border? stackSurface = FindStackSurface(stack);
        if (stackSurface is not null)
        {
            foreach (var anchor in FindDescendants<FrameworkElement>(stackSurface)
                         .Where(element => string.Equals(
                             element.Tag as string,
                             "StackExpandedAnchor",
                             StringComparison.Ordinal) &&
                             element.Visibility == Visibility.Visible))
            {
                PrepareStackElementForExpansion(anchor);
            }
        }

        foreach (var surface in FindStackMemberSurfaces(stack))
        {
            PrepareStackElementForExpansion(surface);
        }
    }

    private void PrepareStackMemberSurfaceForExpansion(Border surface)
    {
        if (!_isStackTransitionRunning ||
            _stackExpansionPreparationGeneration != _stackTransitionRequestGeneration ||
            !_activeStackTransitionExpanded ||
            _activeStackTransition is not { } stack ||
            surface.DataContext is not WidgetItem item ||
            !stack.Members.Any(member => ReferenceEquals(member, item)))
        {
            return;
        }

        PrepareStackElementForExpansion(surface);
    }

    private void BeginStackExpansionPreparation(long requestGeneration)
    {
        if (IsCurrentStackTransitionRequest(requestGeneration))
        {
            _stackExpansionPreparationGeneration = requestGeneration;
        }
    }

    private void EndStackExpansionPreparation(long requestGeneration)
    {
        if (_stackExpansionPreparationGeneration == requestGeneration)
        {
            _stackExpansionPreparationGeneration = -1;
        }
    }

    private void ScheduleStackPostTransitionStabilization()
    {
        long requestGeneration = _stackTransitionRequestGeneration;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await WaitForNextStackRenderAsync();
            if (_isClosing || _isStackTransitionRunning ||
                !IsCurrentStackTransitionRequest(requestGeneration))
            {
                return;
            }

            ResetAllStackAnimationVisuals();
            ViewModel.StabilizeStackDisplay();
            var itemsView = GetActiveItemsView();
            itemsView?.InvalidateMeasure();
            itemsView?.InvalidateArrange();
            itemsView?.UpdateLayout();

            await WaitForNextStackRenderAsync();
            if (_isClosing || _isStackTransitionRunning ||
                !IsCurrentStackTransitionRequest(requestGeneration))
            {
                return;
            }

            ResetAllStackAnimationVisuals();
            itemsView?.UpdateLayout();
        });
    }

    private static void PrepareStackElementForExpansion(FrameworkElement element)
    {
        try
        {
            ResetStackElementVisual(element);
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            visual.CenterPoint = new Vector3(
                (float)Math.Max(0, element.ActualWidth / 2),
                (float)Math.Max(0, element.ActualHeight / 2),
                0);
            visual.Scale = new Vector3(0.92f, 0.92f, 1);
            visual.Opacity = 0;
        }
        catch
        {
            // The next layout pass will prepare any container still being realized.
        }
    }

    private void ResetAllStackAnimationVisuals()
    {
        foreach (var surface in _interactiveSurfaces.ToArray())
        {
            if (surface.XamlRoot is null)
            {
                continue;
            }

            ApplyWidgetItemSurfaceState(surface, ItemSurfaceState.Normal);
            ResetStackElementVisual(surface);
        }

        foreach (var stackSurface in _stackSurfaces.ToArray())
        {
            ResetStackSurfaceVisuals(stackSurface);
        }

    }

    private static void ResetStackElementVisual(FrameworkElement element)
    {
        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            StopStackVisualAnimations(visual);
            if (s_stackRestingOffsets.TryGetValue(element, out StackRestingOffset? state))
            {
                TryStopStackVisualAnimation(visual, "Offset");
                visual.Offset = state.Value;
                s_stackRestingOffsets.Remove(element);
            }
            visual.Scale = Vector3.One;
            visual.Opacity = (float)Math.Clamp(element.Opacity, 0, 1);
            visual.RotationAngleInDegrees = 0;
            visual.CenterPoint = Vector3.Zero;
        }
        catch
        {
            // A virtualized item can unload while a collection update is completing.
        }
    }

    private static void StopStackVisualAnimations(Visual visual)
    {
        TryStopStackVisualAnimation(visual, "Scale");
        TryStopStackVisualAnimation(visual, "Opacity");
        TryStopStackVisualAnimation(visual, "RotationAngleInDegrees");
    }

    private static void TryStopStackVisualAnimation(Visual visual, string propertyName)
    {
        try
        {
            visual.StopAnimation(propertyName);
        }
        catch (ArgumentException)
        {
            // A virtualized visual can lose an animatable facade while unloading.
        }
    }

    private IReadOnlyList<Border> FindStackMemberSurfaces(WidgetStackItem stack)
    {
        var surfacesByItem = _interactiveSurfaces
            .Where(surface => surface.XamlRoot is not null && surface.DataContext is WidgetItem { IsStackChild: true })
            .GroupBy(surface => (WidgetItem)surface.DataContext)
            .ToDictionary(group => group.Key, group => group.First());

        return stack.Members
            .Where(surfacesByItem.ContainsKey)
            .Select(item => surfacesByItem[item])
            .ToArray();
    }

    private int AnimateStackMembers(
        Border? stackSurface,
        IReadOnlyList<Border> memberSurfaces,
        bool expanding)
    {
        FrameworkElement? anchor = FindStackAnchor(stackSurface);
        if (anchor is null || memberSurfaces.Count == 0 ||
            !TryGetElementCenter(anchor, out Point stackCenter))
        {
            return 0;
        }

        int staggerStep = memberSurfaces.Count <= 1
            ? 0
            : Math.Min(30, StackMaximumStaggerMs / (memberSurfaces.Count - 1));
        for (int index = 0; index < memberSurfaces.Count; index++)
        {
            Border surface = memberSurfaces[index];
            if (!TryGetElementCenter(surface, out Point memberCenter))
            {
                continue;
            }

            var towardStack = LimitVector(
                new Vector2(
                    (float)(stackCenter.X - memberCenter.X),
                    (float)(stackCenter.Y - memberCenter.Y)),
                maximumLength: index < 3
                    ? float.MaxValue
                    : ViewModel.ViewMode == ViewMode.List ? 64 : 92);
            int order = expanding ? index : memberSurfaces.Count - 1 - index;
            StartStackMemberAnimation(surface, towardStack, order * staggerStep, expanding);
        }

        return staggerStep * Math.Max(0, memberSurfaces.Count - 1);
    }

    private static FrameworkElement? FindStackAnchor(Border? stackSurface)
    {
        if (stackSurface is null)
        {
            return null;
        }

        return FindDescendants<FrameworkElement>(stackSurface)
            .FirstOrDefault(element => string.Equals(
                element.Tag as string,
                "StackExpandedAnchor",
                StringComparison.Ordinal) &&
                element.Visibility == Visibility.Visible)
            ?? FindDescendants<FrameworkElement>(stackSurface)
                .FirstOrDefault(element => string.Equals(
                    element.Tag as string,
                    "StackCollapsedPreview",
                    StringComparison.Ordinal) &&
                    element.Visibility == Visibility.Visible)
            ?? stackSurface;
    }

    private static void StartStackMemberAnimation(
        FrameworkElement element,
        Vector2 towardStack,
        int delayMs,
        bool expanding)
    {
        ResetStackElementVisual(element);
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;
        Vector3 restingOffset = visual.Offset;
        s_stackRestingOffsets.Remove(element);
        s_stackRestingOffsets.Add(element, new StackRestingOffset(restingOffset));
        visual.CenterPoint = new Vector3(
            (float)Math.Max(0, element.ActualWidth / 2),
            (float)Math.Max(0, element.ActualHeight / 2),
            0);
        TimeSpan duration = TimeSpan.FromMilliseconds(
            expanding ? StackExpandDurationMs : StackCollapseDurationMs);
        TimeSpan delay = TimeSpan.FromMilliseconds(delayMs);
        var easeOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 0.84f),
            new Vector2(0.24f, 1.0f));
        var easeIn = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.42f, 0.0f),
            new Vector2(0.74f, 0.32f));
        CompositionEasingFunction easing = expanding ? easeOut : easeIn;
        const float foldedScale = 0.92f;

        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Duration = duration;
        offset.DelayTime = delay;
        offset.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        offset.InsertKeyFrame(
            0,
            expanding
                ? restingOffset + new Vector3(towardStack, 0)
                : restingOffset);
        offset.InsertKeyFrame(
            1,
            expanding
                ? restingOffset
                : restingOffset + new Vector3(towardStack, 0),
            easing);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = duration;
        scale.DelayTime = delay;
        scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        scale.InsertKeyFrame(0, expanding ? new Vector3(foldedScale, foldedScale, 1) : Vector3.One);
        scale.InsertKeyFrame(1, expanding ? Vector3.One : new Vector3(foldedScale, foldedScale, 1), easing);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = duration;
        opacity.DelayTime = delay;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        opacity.InsertKeyFrame(0, expanding ? 0 : 1.0f);
        opacity.InsertKeyFrame(1, expanding ? 1.0f : 0, easing);

        visual.StartAnimation("Offset", offset);
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    private static void AnimateExpandedAnchor(Border? stackSurface, bool appearing)
    {
        if (stackSurface is null)
        {
            return;
        }

        var anchor = FindDescendants<Button>(stackSurface)
            .FirstOrDefault(button => string.Equals(
                button.Tag as string,
                "StackExpandedAnchor",
                StringComparison.Ordinal));
        if (anchor is null || anchor.Visibility != Visibility.Visible)
        {
            return;
        }

        ResetStackElementVisual(anchor);
        Visual visual = ElementCompositionPreview.GetElementVisual(anchor);
        Compositor compositor = visual.Compositor;
        visual.CenterPoint = new Vector3(
            (float)Math.Max(0, anchor.ActualWidth / 2),
            (float)Math.Max(0, anchor.ActualHeight / 2),
            0);
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 0.84f),
            new Vector2(0.24f, 1.0f));
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = TimeSpan.FromMilliseconds(appearing ? 175 : StackCollapseDurationMs);
        scale.InsertKeyFrame(0, appearing ? new Vector3(0.78f, 0.78f, 1) : Vector3.One);
        scale.InsertKeyFrame(
            1,
            appearing ? Vector3.One : new Vector3(0.94f, 0.94f, 1),
            easing);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = scale.Duration;
        opacity.InsertKeyFrame(0, appearing ? 0 : 1);
        opacity.InsertKeyFrame(1, appearing ? 1 : 0.68f, easing);

        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    private bool TryGetElementCenter(FrameworkElement element, out Point center)
    {
        try
        {
            center = element.TransformToVisual(RootGrid).TransformPoint(
                new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            return true;
        }
        catch
        {
            center = default;
            return false;
        }
    }

    private static Vector2 LimitVector(Vector2 vector, float maximumLength)
    {
        float length = vector.Length();
        return length > maximumLength && length > 0
            ? vector / length * maximumLength
            : vector;
    }

    private static bool SystemStackAnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }

    private sealed class StackRestingOffset(Vector3 value)
    {
        public Vector3 Value { get; } = value;
    }

    private void RemoveVirtualStackSelection()
    {
        var listView = GetActiveItemsView();
        if (listView is null)
        {
            return;
        }

        _isSynchronizingSelection = true;
        try
        {
            foreach (var stack in listView.SelectedItems.OfType<WidgetStackItem>().ToArray())
            {
                listView.SelectedItems.Remove(stack);
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        ApplySelectionState(listView);
    }

    private MenuFlyout CreateStackFlyout(WidgetStackItem stack)
    {
        var flyout = new MenuFlyout();
        var toggleItem = new MenuFlyoutItem
        {
            Text = _localizationService.T(stack.IsExpanded
                ? "Widget.Stack.Collapse"
                : "Widget.Stack.Expand"),
            Icon = new FontIcon { Glyph = stack.ChevronGlyph }
        };
        toggleItem.Click += async (_, _) =>
            await SetStackExpandedWithAnimationAsync(stack, !stack.IsExpanded);
        flyout.Items.Add(toggleItem);

        flyout.Items.Add(new MenuFlyoutSeparator());
        var selectContentsItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Stack.SelectContents"),
            Icon = new FontIcon { Glyph = "\uE762" }
        };
        selectContentsItem.Click += async (_, _) => await SelectStackMembersAsync(stack);
        flyout.Items.Add(selectContentsItem);

        var copyPathsItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Stack.CopyContentPaths"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPathsItem.Click += async (_, _) =>
        {
            await SelectStackMembersAsync(stack);
            CopySelectedPathsToClipboard();
        };
        flyout.Items.Add(copyPathsItem);
        return flyout;
    }

    private async Task SelectStackMembersAsync(WidgetStackItem stack)
    {
        await SetStackExpandedWithAnimationAsync(stack, expanded: true);
        var listView = GetActiveItemsView();
        if (listView is null)
        {
            return;
        }

        ClearOtherWidgetSelections();
        _isSynchronizingSelection = true;
        try
        {
            listView.SelectedItems.Clear();
            foreach (var item in stack.Members.Where(ViewModel.Items.Contains))
            {
                listView.SelectedItems.Add(item);
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        ApplySelectionState(listView);
    }

    private MenuFlyoutSubItem CreateStackSettingsMenu()
    {
        var stackMenu = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("Widget.Stack.Menu"),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };

        var followDefaultsItem = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Stack.FollowDefaults"),
            IsChecked = ViewModel.FileStacksFollowGlobalDefaults
        };
        followDefaultsItem.Click += (_, _) => ViewModel.ClearFileStackOverrides();
        stackMenu.Items.Add(followDefaultsItem);

        var enabledItem = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Stack.EnableForWidget"),
            IsChecked = ViewModel.FileStacksEnabled
        };
        enabledItem.Click += (_, _) => ViewModel.SetFileStacksEnabledOverride(enabledItem.IsChecked);
        stackMenu.Items.Add(enabledItem);
        stackMenu.Items.Add(new MenuFlyoutSeparator());

        var defaultGroupingItem = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Stack.UseDefaultGrouping"),
            IsChecked = ViewModel.FileStackGroupByFollowsGlobal,
            IsEnabled = ViewModel.FileStacksEnabled
        };
        defaultGroupingItem.Click += (_, _) => ViewModel.SetFileStackGroupByOverride(null);
        stackMenu.Items.Add(defaultGroupingItem);

        AddStackGroupingMenuItem(
            stackMenu,
            SettingsService.FileStackGroupByKind,
            "Settings.FileStacks.GroupBy.Kind");
        AddStackGroupingMenuItem(
            stackMenu,
            SettingsService.FileStackGroupByDateModified,
            "Settings.FileStacks.GroupBy.DateModified");
        AddStackGroupingMenuItem(
            stackMenu,
            SettingsService.FileStackGroupByCustom,
            "Settings.FileStacks.GroupBy.Custom");

        stackMenu.Items.Add(new MenuFlyoutSeparator());
        stackMenu.Items.Add(CreateStackThresholdMenu());
        stackMenu.Items.Add(CreateStackOrderMenu());
        return stackMenu;
    }

    private void AddStackGroupingMenuItem(
        MenuFlyoutSubItem stackMenu,
        string groupBy,
        string localizationKey)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T(localizationKey),
            IsChecked = !ViewModel.FileStackGroupByFollowsGlobal &&
                string.Equals(ViewModel.FileStackGroupBy, groupBy, StringComparison.Ordinal),
            IsEnabled = ViewModel.FileStacksEnabled
        };
        item.Click += (_, _) => ViewModel.SetFileStackGroupByOverride(groupBy);
        stackMenu.Items.Add(item);
    }

    private MenuFlyoutSubItem CreateStackThresholdMenu()
    {
        var thresholdMenu = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("Settings.FileStacks.Threshold.Title")
        };
        var useDefault = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Stack.UseDefaultThreshold"),
            IsChecked = ViewModel.FileStackThresholdFollowsGlobal,
            IsEnabled = ViewModel.FileStacksEnabled
        };
        useDefault.Click += (_, _) => ViewModel.SetFileStackThresholdOverride(null);
        thresholdMenu.Items.Add(useDefault);
        thresholdMenu.Items.Add(new MenuFlyoutSeparator());

        foreach (int threshold in new[] { 2, 3, 5 })
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = _localizationService.Format(
                    "Settings.FileStacks.Threshold.Option",
                    threshold),
                IsChecked = !ViewModel.FileStackThresholdFollowsGlobal &&
                    ViewModel.FileStackThreshold == threshold,
                IsEnabled = ViewModel.FileStacksEnabled
            };
            item.Click += (_, _) => ViewModel.SetFileStackThresholdOverride(threshold);
            thresholdMenu.Items.Add(item);
        }

        return thresholdMenu;
    }

    private MenuFlyoutSubItem CreateStackOrderMenu()
    {
        var orderMenu = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("Settings.FileStacks.OrderBy.Title")
        };
        var useDefault = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Stack.UseDefaultOrder"),
            IsChecked = ViewModel.FileStackOrderByFollowsGlobal,
            IsEnabled = ViewModel.FileStacksEnabled
        };
        useDefault.Click += (_, _) => ViewModel.SetFileStackOrderByOverride(null);
        orderMenu.Items.Add(useDefault);
        orderMenu.Items.Add(new MenuFlyoutSeparator());

        AddStackOrderMenuItem(
            orderMenu,
            SettingsService.FileStackOrderByWidget,
            "Settings.FileStacks.OrderBy.Widget");
        AddStackOrderMenuItem(
            orderMenu,
            SettingsService.FileStackOrderByName,
            "Settings.FileStacks.OrderBy.Name");
        AddStackOrderMenuItem(
            orderMenu,
            SettingsService.FileStackOrderByDateAdded,
            "Settings.FileStacks.OrderBy.DateAdded");
        AddStackOrderMenuItem(
            orderMenu,
            SettingsService.FileStackOrderByDateModified,
            "Settings.FileStacks.OrderBy.DateModified");
        return orderMenu;
    }

    private void AddStackOrderMenuItem(
        MenuFlyoutSubItem orderMenu,
        string orderBy,
        string localizationKey)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T(localizationKey),
            IsChecked = !ViewModel.FileStackOrderByFollowsGlobal &&
                string.Equals(ViewModel.FileStackOrderBy, orderBy, StringComparison.Ordinal),
            IsEnabled = ViewModel.FileStacksEnabled
        };
        item.Click += (_, _) => ViewModel.SetFileStackOrderByOverride(orderBy);
        orderMenu.Items.Add(item);
    }
}
