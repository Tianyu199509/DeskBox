using System.IO;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    private readonly List<DesktopOrganizationPreviewCard> _targetCards = [];
    private readonly List<DesktopOrganizationPreviewCard> _completedCards = [];
    private readonly Dictionary<string, OrganizationHistoryItem> _completedItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OrganizationHistoryItem> _restoredItems = new(StringComparer.OrdinalIgnoreCase);
    private bool _isPreviewReady;
    private FrameworkElement? _newBoxGhostCard;
    private Action<bool>? _setGhostBoxHighlight;
    private string? _pendingEmptyCustomBoxRenameId;

    private void WirePointerDragController()
    {
        _dragController.DragStarted += OnPointerDragStarted;
        _dragController.DragMoved += OnPointerDragMoved;
        _dragController.DragCompleted += OnPointerDragCompleted;
        _dragController.DragCanceled += OnPointerDragCanceled;
    }

    private void UnwirePointerDragController()
    {
        _dragController.DragStarted -= OnPointerDragStarted;
        _dragController.DragMoved -= OnPointerDragMoved;
        _dragController.DragCompleted -= OnPointerDragCompleted;
        _dragController.DragCanceled -= OnPointerDragCanceled;
    }

    private void ReleasePreviewCards()
    {
        _isPreviewReady = false;
        ExecuteButton.IsEnabled = false;
        ResetDropVisuals();
        _newBoxGhostCard = null;
        _setGhostBoxHighlight = null;
        foreach (var card in _targetCards)
        {
            card.Dispose();
        }

        _targetCards.Clear();
        TargetRows.Children.Clear();
    }

    private void ResetDropVisuals()
    {
        PointerDragOverlayCanvas.Visibility = Visibility.Collapsed;
        StayOnDesktopTrayBorder.BorderBrush =
            NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this);
        _setGhostBoxHighlight?.Invoke(false);
        foreach (var card in _targetCards)
        {
            card.SetDropTargetHighlight(false);
        }
    }

    private bool HasAnyActiveScopeItems(DesktopOrganizationPlan? plan)
    {
        if (plan is null || (!plan.IncludePersonalDesktop && !plan.IncludePublicDesktop))
        {
            return false;
        }

        return plan.SourceItems.Any(item => DesktopOrganizationPreviewSummary.IncludesSource(plan, item)) ||
               plan.Targets.Any(t => t.Items.Any(item => DesktopOrganizationPreviewSummary.IncludesSource(plan, item))) ||
               plan.Targets.Count > 0;
    }

    private void OnPointerDragStarted(object? sender, DesktopOrganizationDragStartedEventArgs e)
    {
        UpdatePointerDragVisuals(e.Origin, e.SourceBucketKey, e.SourcePaths, e.RootPosition);
    }

    private void OnPointerDragMoved(object? sender, DesktopOrganizationDragMovedEventArgs e)
    {
        UpdatePointerDragVisuals(e.Origin, e.SourceBucketKey, e.SourcePaths, e.RootPosition);
    }

    private void UpdatePointerDragVisuals(
        DesktopOrganizationDragOrigin origin,
        string sourceBucketKey,
        IReadOnlyList<string> draggedPaths,
        Windows.Foundation.Point rootPosition)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || draggedPaths.Count == 0)
        {
            ResetDropVisuals();
            return;
        }

        string itemSummary = draggedPaths.Count == 1
            ? Path.GetFileName(draggedPaths[0])
            : Format("DesktopOrganization.Layout.MoveCount", draggedPaths.Count);

        bool overTray = origin != DesktopOrganizationDragOrigin.StayOnDesktopTray &&
                        IsPointOverElement(rootPosition, StayOnDesktopTrayBorder);
        bool overNewBox = !overTray && IsPointOverElement(rootPosition, _newBoxGhostCard);
        DesktopOrganizationPreviewCard? hoveredCard = null;
        if (!overTray && !overNewBox)
        {
            hoveredCard = _targetCards.FirstOrDefault(c =>
                (origin != DesktopOrganizationDragOrigin.PreviewCard ||
                 !string.Equals(c.BucketKey, sourceBucketKey, StringComparison.Ordinal)) &&
                IsPointOverElement(rootPosition, c));
        }

        StayOnDesktopTrayBorder.BorderBrush = NeutralInteractionBrush.ResolveThemedResource(
            overTray ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush",
            this);
        _setGhostBoxHighlight?.Invoke(overNewBox);
        foreach (var card in _targetCards)
        {
            card.SetDropTargetHighlight(ReferenceEquals(card, hoveredCard));
        }

        PointerDragBadgeIcon.Glyph = overTray
            ? "\uE7F8"
            : overNewBox
                ? "\uE710"
                : "\uE8DE";

        string targetSuffix = overTray
            ? " \u2192 " + T("DesktopOrganization.Layout.KeptTitle")
            : overNewBox
                ? " \u2192 " + T("DesktopOrganization.Layout.RouteBadgeNew")
                : hoveredCard is not null
                    ? " \u2192 " + hoveredCard.EffectiveBoxTitle
                    : string.Empty;

        PointerDragBadgeText.Text = itemSummary + targetSuffix;
        PointerDragOverlayCanvas.Visibility = Visibility.Visible;

        PointerDragBadge.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double badgeW = PointerDragBadge.ActualWidth > 0 ? PointerDragBadge.ActualWidth : PointerDragBadge.DesiredSize.Width;
        double badgeH = PointerDragBadge.ActualHeight > 0 ? PointerDragBadge.ActualHeight : PointerDragBadge.DesiredSize.Height;
        double canvasW = PointerDragOverlayCanvas.ActualWidth > 0 ? PointerDragOverlayCanvas.ActualWidth : WorkspaceRootGrid.ActualWidth;
        double canvasH = PointerDragOverlayCanvas.ActualHeight > 0 ? PointerDragOverlayCanvas.ActualHeight : WorkspaceRootGrid.ActualHeight;

        Windows.Foundation.Point ptInCanvas = rootPosition;
        try
        {
            GeneralTransform toCanvas = WorkspaceRootGrid.TransformToVisual(PointerDragOverlayCanvas);
            ptInCanvas = toCanvas.TransformPoint(rootPosition);
        }
        catch
        {
            // Fall back to rootPosition if transform is transiently unavailable.
        }

        double rawX = ptInCanvas.X + 14;
        if (rawX + badgeW > canvasW - 4 && ptInCanvas.X - badgeW - 14 >= 4)
        {
            rawX = ptInCanvas.X - badgeW - 14;
        }

        double rawY = ptInCanvas.Y + 14;
        if (rawY + badgeH > canvasH - 4 && ptInCanvas.Y - badgeH - 14 >= 4)
        {
            rawY = ptInCanvas.Y - badgeH - 14;
        }

        double badgeX = Math.Clamp(rawX, 4, Math.Max(4, canvasW - badgeW - 4));
        double badgeY = Math.Clamp(rawY, 4, Math.Max(4, canvasH - badgeH - 4));
        Canvas.SetLeft(PointerDragBadge, badgeX);
        Canvas.SetTop(PointerDragBadge, badgeY);
    }

    private void OnPointerDragCompleted(object? sender, DesktopOrganizationDragCompletedEventArgs e)
    {
        ResetDropVisuals();

        if (_isScanning || _isExecuting || _hasCompletedExecution ||
            Session.EffectivePlan is null || e.SourcePaths.Count == 0)
        {
            return;
        }

        if (e.Origin != DesktopOrganizationDragOrigin.StayOnDesktopTray &&
            IsPointOverElement(e.RootPosition, StayOnDesktopTrayBorder))
        {
            DesktopOrganizationWorkbenchSession.MoveResult moveResult = Session.ExcludeItems(e.SourcePaths);
            if (moveResult.MovedCount > 0)
            {
                RenderSessionPlan();
            }

            return;
        }

        if (IsPointOverElement(e.RootPosition, _newBoxGhostCard))
        {
            CreateNewBoxWithFiles(e.SourcePaths);
            return;
        }

        DesktopOrganizationPreviewCard? targetCard = _targetCards.FirstOrDefault(c => IsPointOverElement(e.RootPosition, c));
        if (targetCard is not null &&
            (e.Origin != DesktopOrganizationDragOrigin.PreviewCard ||
             !string.Equals(targetCard.BucketKey, e.SourceBucketKey, StringComparison.Ordinal)))
        {
            MoveItemsToBox(e.SourcePaths, targetCard.BucketKey);
        }
    }

    private void OnPointerDragCanceled(object? sender, EventArgs e)
    {
        ResetDropVisuals();
    }

    private bool IsPointOverElement(Windows.Foundation.Point ptInRoot, FrameworkElement? element)
    {
        if (element is null ||
            element.Visibility != Visibility.Visible ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            GeneralTransform transform = WorkspaceRootGrid.TransformToVisual(element);
            Windows.Foundation.Point localPt = transform.TransformPoint(ptInRoot);
            return localPt.X >= 0 &&
                   localPt.Y >= 0 &&
                   localPt.X <= element.ActualWidth &&
                   localPt.Y <= element.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    private void RenderSessionPlan(string? renameBucketIdAfterRender = null)
    {
        DesktopOrganizationPlan? plan = Session.EffectivePlan;
        if (plan is null)
        {
            return;
        }

        double offset = PreviewScrollViewer.VerticalOffset;
        ReleasePreviewCards();
        UpdateStorageLocationDisplay(plan.StorageRootPath);
        RenderSources(plan);
        IReadOnlyList<DesktopOrganizationDestinationOption> destinations = CreateDestinationOptions();
        DesktopOrganizationPreviewCard? cardToRename = null;

        foreach (DesktopOrganizationTargetPlan target in plan.Targets)
        {
            var card = new DesktopOrganizationPreviewCard(
                target,
                Session,
                _dragController,
                destinations)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };

            card.ExcludeItemsRequested += (_, paths) =>
            {
                if (paths.Count == 0)
                {
                    if (string.Equals(_pendingEmptyCustomBoxRenameId, card.BucketKey, StringComparison.Ordinal))
                    {
                        _pendingEmptyCustomBoxRenameId = null;
                    }

                    Session.RemoveEmptyCustomBox(card.BucketKey);
                    RenderSessionPlan();
                    return;
                }

                if (Session.ExcludeItems(paths).MovedCount > 0)
                {
                    RenderSessionPlan();
                }
            };

            card.BoxTitleRenamed += (_, newTitle) =>
            {
                _pendingEmptyCustomBoxRenameId = null;
                if (Session.RenameBox(card.BucketKey, newTitle))
                {
                    RenderSessionPlan();
                }
            };

            card.InlineRenameCanceled += (_, _) =>
            {
                if (string.Equals(_pendingEmptyCustomBoxRenameId, card.BucketKey, StringComparison.Ordinal) &&
                    card.TargetPlan.Items.Count == 0)
                {
                    _pendingEmptyCustomBoxRenameId = null;
                    Session.RemoveEmptyCustomBox(card.BucketKey);
                    RenderSessionPlan();
                }
            };

            card.DestinationRouteSelected += (_, selection) =>
            {
                _pendingEmptyCustomBoxRenameId = null;
                Session.SetTargetDestinationSelection(card.BucketKey, selection);
                RenderSessionPlan();
            };

            card.TileSelectionChanged += (_, _) =>
            {
                foreach (var other in _targetCards)
                {
                    if (!ReferenceEquals(other, card))
                    {
                        other.RefreshSelectionVisuals();
                    }
                }
            };

            if (!string.IsNullOrWhiteSpace(renameBucketIdAfterRender) &&
                string.Equals(card.BucketKey, renameBucketIdAfterRender, StringComparison.Ordinal))
            {
                cardToRename = card;
            }

            _targetCards.Add(card);
            TargetRows.Children.Add(card);
        }

        if (!_hasCompletedExecution && HasAnyActiveScopeItems(plan))
        {
            _newBoxGhostCard = CreateNewBoxDropCard();
            _newBoxGhostCard.Visibility = Visibility.Visible;
            TargetRows.Children.Add(_newBoxGhostCard);
        }

        RenderExcludedItems(plan);
        UpdateSectionVisibility();
        UpdateDestinationPaths();
        _isPreviewReady = true;
        UpdateSummary();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                PreviewScrollViewer.ChangeView(null, offset, null, disableAnimation: true);
                cardToRename?.BeginInlineRename();
            }
        });
    }

    private FrameworkElement CreateNewBoxDropCard()
    {
        AppSettings appSettings = App.Current.SettingsService.Settings;
        double cornerRadius = WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
            WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                appSettings.WidgetShell.WidgetCornerPreference));
        Brush secondaryBrush = NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0x9E, 0x9E, 0x9E));
        Brush defaultStroke = NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x9E, 0x9E, 0x9E));
        Brush accentStroke = NeutralInteractionBrush.ResolveThemedResource("AccentFillColorDefaultBrush", this)
            ?? defaultStroke;

        var dashedOutline = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            RadiusX = cornerRadius,
            RadiusY = cornerRadius,
            Stroke = defaultStroke,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
            Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsHitTestVisible = false
        };

        var plusIcon = new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = secondaryBrush
        };
        string newBoxLabel = T("DesktopOrganization.Preview.DynamicDestination");
        var labelText = new TextBlock
        {
            Text = newBoxLabel,
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = secondaryBrush
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };
        stack.Children.Add(plusIcon);
        stack.Children.Add(labelText);

        Brush hoverFill = NeutralInteractionBrush.ResolveThemedResource("SubtleFillColorSecondaryBrush", this)
            ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        Brush transparentFill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var innerGrid = new Grid
        {
            Width = DesktopOrganizationPreviewCard.FixedBoxWidth,
            Height = DesktopOrganizationPreviewCard.FixedBoxHeight,
            Background = transparentFill
        };
        innerGrid.Children.Add(dashedOutline);
        innerGrid.Children.Add(stack);

        var dropCardHost = new ContentControl
        {
            Content = innerGrid,
            Width = DesktopOrganizationPreviewCard.FixedBoxWidth,
            Height = DesktopOrganizationPreviewCard.FixedBoxHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            IsTabStop = true,
            UseSystemFocusVisuals = true
        };
        AutomationProperties.SetName(dropCardHost, newBoxLabel);

        _setGhostBoxHighlight = highlighted =>
        {
            dashedOutline.Stroke = highlighted ? accentStroke : defaultStroke;
            dashedOutline.Fill = highlighted ? hoverFill : transparentFill;
            plusIcon.Foreground = highlighted ? accentStroke : secondaryBrush;
            labelText.Foreground = highlighted ? accentStroke : secondaryBrush;
        };

        dropCardHost.PointerEntered += (_, _) =>
        {
            if (!_dragController.IsDragging)
            {
                dashedOutline.Fill = hoverFill;
                Brush hoverText = NeutralInteractionBrush.ResolveThemedResource("TextFillColorPrimaryBrush", this) ?? secondaryBrush;
                plusIcon.Foreground = hoverText;
                labelText.Foreground = hoverText;
            }
        };
        dropCardHost.PointerExited += (_, _) =>
        {
            if (!_dragController.IsDragging)
            {
                dashedOutline.Fill = transparentFill;
                plusIcon.Foreground = secondaryBrush;
                labelText.Foreground = secondaryBrush;
            }
        };
        dropCardHost.Tapped += (_, _) => CreateEmptyNewBox();
        dropCardHost.KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Space)
            {
                e.Handled = true;
                CreateEmptyNewBox();
            }
        };

        return dropCardHost;
    }

    private void MoveItemsToBox(IReadOnlyList<string> sourcePaths, string targetBucketKey)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || Session.BasePlan is null ||
            sourcePaths.Count == 0)
        {
            return;
        }

        if (Session.MoveItemsToBucket(sourcePaths, targetBucketKey).MovedCount > 0)
        {
            if (string.Equals(_pendingEmptyCustomBoxRenameId, targetBucketKey, StringComparison.Ordinal))
            {
                _pendingEmptyCustomBoxRenameId = null;
            }

            RenderSessionPlan();
        }
    }

    private void CreateEmptyNewBox()
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || Session.BasePlan is null)
        {
            return;
        }

        _dragController.CancelActiveDrag();
        DesktopOrganizationWorkbenchSession.CustomBoxDefinition created =
            Session.CreateEmptyCustomBox(T("DesktopOrganization.Layout.New"));
        _pendingEmptyCustomBoxRenameId = created.WidgetId;
        RenderSessionPlan(renameBucketIdAfterRender: created.WidgetId);
    }

    private void CreateNewBoxWithFiles(IReadOnlyList<string> sourcePaths)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || Session.BasePlan is null ||
            sourcePaths.Count == 0)
        {
            return;
        }

        DesktopOrganizationWorkbenchSession.CustomBoxDefinition? created =
            Session.CreateCustomBox(sourcePaths, defaultTitlePrefix: T("DesktopOrganization.Layout.New"));
        if (created is not null)
        {
            _pendingEmptyCustomBoxRenameId = null;
            RenderSessionPlan(renameBucketIdAfterRender: created.WidgetId);
        }
    }

    private IReadOnlyList<DesktopOrganizationDestinationOption> CreateDestinationOptions()
    {
        try { return Coordinator.GetDestinationOptions(); }
        catch { return []; }
    }

    private void UpdateSectionVisibility()
    {
        DesktopOrganizationPlan? plan = Session.EffectivePlan;
        bool hasDesktopItems = HasAnyActiveScopeItems(plan);
        TargetSelectionHost.Visibility = !_hasCompletedExecution && hasDesktopItems
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompletedViewPanel.Visibility = _hasCompletedExecution
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyPreviewCard.Visibility = !_hasCompletedExecution && !hasDesktopItems
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyPreviewText.Text = T(plan is { IncludePersonalDesktop: false, IncludePublicDesktop: false }
            ? "DesktopOrganization.Layout.NoSource"
            : "DesktopOrganization.Preview.NothingAction");
        StayOnDesktopTrayBorder.Visibility = !_hasCompletedExecution && hasDesktopItems
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExecuteButton.Visibility = !_hasCompletedExecution ? Visibility.Visible : Visibility.Collapsed;
        ResetSelectionMenuItem.IsEnabled = !_hasCompletedExecution && !_isExecuting && !_isScanning;
    }

    private void UpdateSummary()
    {
        DesktopOrganizationPlan? plan = Session.EffectivePlan;
        if (plan is null)
        {
            FooterSummaryText.Text = string.Empty;
            FooterSafetyHintText.Visibility = Visibility.Collapsed;
            return;
        }

        DesktopOrganizationPreviewSummary summary = Session.CalculateSummary();

        if (_hasCompletedExecution)
        {
            FooterSummaryText.Text = T("DesktopOrganization.Preview.Completed");
            FooterSafetyHintText.Visibility = Visibility.Collapsed;
        }
        else if (!plan.IncludePersonalDesktop && !plan.IncludePublicDesktop)
        {
            FooterSummaryText.Text = T("DesktopOrganization.Layout.NoSource");
            FooterSafetyHintText.Visibility = Visibility.Collapsed;
        }
        else if (plan.EligibleItemCount == 0 && summary.SelectedCount == 0)
        {
            FooterSummaryText.Text = T("DesktopOrganization.Preview.NothingAction");
            FooterSafetyHintText.Visibility = Visibility.Collapsed;
        }
        else if (summary.SelectedCount == 0)
        {
            FooterSummaryText.Text = T("DesktopOrganization.Layout.NoSelection");
            FooterSafetyHintText.Visibility = Visibility.Collapsed;
        }
        else
        {
            string sizeText = DesktopOrganizationPreviewSummary.FormatBytes(
                summary.SelectedBytes,
                App.Current.LocalizationService);
            FooterSummaryText.Text = $"{Format("DesktopOrganization.Layout.MoveCount", summary.SelectedCount)} \u00b7 {sizeText}";
            FooterSafetyHintText.Text = "\u00b7 " + Format(
                "DesktopOrganization.Layout.TargetSummary",
                summary.DestinationCount,
                summary.NewDestinationCount);
            FooterSafetyHintText.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(FooterSummaryText, T("DesktopOrganization.Layout.HeaderSubtitle"));
        }

        ExecuteButton.Content = Format("DesktopOrganization.Layout.Execute", summary.SelectedCount);
        bool pendingUndo = App.Current.SettingsService.OrganizationHistory.Entries.Any(entry =>
            entry.ActionType == OrganizationActionType.DesktopOrganization && entry.UndoStarted && entry.CanUndo);
        bool overlapsDesktop = !string.IsNullOrWhiteSpace(plan.StorageRootPath) &&
            ManagedStoragePathService.OverlapsDesktop(plan.StorageRootPath);
        ExecuteButton.IsEnabled = _isPreviewReady &&
            summary.SelectedCount > 0 &&
            !_isScanning &&
            !_isExecuting &&
            !_hasCompletedExecution &&
            !pendingUndo &&
            !HasPendingRecovery &&
            !overlapsDesktop;
    }

    private void RenderExecutionResult(IEnumerable<OrganizationHistoryItem> completedItems)
    {
        foreach (var item in completedItems)
        {
            if (item.IsRestored)
            {
                _completedItems.Remove(item.SourcePath);
                _restoredItems[item.SourcePath] = item;
            }
            else
            {
                _completedItems[item.SourcePath] = item;
                _restoredItems.Remove(item.SourcePath);
            }
        }

        ReleaseCompletedCards();
        foreach (var group in _completedItems.Values.GroupBy(item => item.TargetWidgetId))
        {
            var items = group.Select(item => FindPreviewSnapshot(item.SourcePath, item.Name, item.SourceScope) with
            {
                SourcePath = item.DestinationPath,
                Name = Path.GetFileName(item.DestinationPath)
            }).ToList();

            var completedTarget = new DesktopOrganizationTargetPlan
            {
                SourceBucketId = group.Key,
                CategoryId = DesktopOrganizationCategoryIds.Other,
                TargetWidgetId = group.Key,
                SuggestedDisplayName = group.First().TargetWidgetName,
                TargetDirectoryPath = group.First().TargetWidgetName,
                CreatesWidget = false,
                AutoBindCategoryRule = false,
                Items = items
            };

            var card = new DesktopOrganizationPreviewCard(
                completedTarget,
                Session,
                _dragController,
                [],
                itemDescription: item => item.SourcePath,
                readOnlyMode: true)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            _completedCards.Add(card);
            CompletedItemsPanel.Children.Add(card);
        }

        DesktopOrganizationPlan? executionOrPreview = _lastExecutionPlan ?? Session.EffectivePlan;
        string storageRoot = executionOrPreview?.StorageRootPath ?? StoragePathText.Text;
        int untouchedCount = GetRetainedPreviewItems(executionOrPreview).Count + _runtimeRetainedItems.Count;
        CompletedHeroTitleText.Text = Format("DesktopOrganization.Layout.CompletedCount", _completedItems.Count);
        CompletedHeroBodyText.Text = Format(
            "DesktopOrganization.Layout.CompletedCardBody",
            storageRoot,
            untouchedCount);
        CompletedUndoButton.Visibility = !string.IsNullOrWhiteSpace(_lastHistoryId)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ReleasePreviewCards();
        RenderExcludedItems(executionOrPreview);
        UpdateSummary();
        UpdateSectionVisibility();
        UpdateDestinationPaths();
    }

    private void ReleaseCompletedCards()
    {
        foreach (var card in _completedCards) card.Dispose();
        _completedCards.Clear();
        CompletedItemsPanel.Children.Clear();
    }

    private void ShowSelectionFeedback(string text, bool showPendingLink = true)
    {
        SelectionFeedbackInfo.Message = text;
        ViewPendingButton.Visibility = showPendingLink ? Visibility.Visible : Visibility.Collapsed;
        SelectionFeedbackInfo.IsOpen = true;
        Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(SelectionFeedbackInfo)?
            .RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void ViewPendingButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateSectionVisibility();
        UpdateSummary();
        SelectionFeedbackInfo.IsOpen = false;
    }

    private void UpdateDestinationPaths()
    {
        DestinationPathsPanel.Children.Clear();
        DesktopOrganizationPlan? activePlan = _hasCompletedExecution ? _lastExecutionPlan : Session.EffectivePlan;
        if (activePlan is null) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in activePlan.Targets)
        {
            if (target.Items.Count == 0) continue;
            string path = target.TargetDirectoryPath;
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) continue;
            DestinationPathsPanel.Children.Add(new TextBlock
            {
                Text = target.SuggestedDisplayName + "\n" + path,
                Style = (Style)Resources["DesktopOrganizationSecondaryTextStyle"],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
        }
    }
}
