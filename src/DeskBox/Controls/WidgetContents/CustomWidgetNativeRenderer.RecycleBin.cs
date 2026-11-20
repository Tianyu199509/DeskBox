using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    private readonly List<CustomWidgetRecycleBinCard> _activeRecycleBinCards = [];

    internal bool HasMountedRecycleBinCards =>
        _activeRecycleBinCards.Any(static card => card.IsLoaded && card.Visibility == Visibility.Visible);

    private void ResetRecycleBinState()
    {
        foreach (CustomWidgetRecycleBinCard card in _activeRecycleBinCards)
        {
            try
            {
                card.Dispose();
            }
            catch
            {
            }
        }

        _activeRecycleBinCards.Clear();
    }

    private void ForgetRecycleBinCard(CustomWidgetRecycleBinCard card)
    {
        _activeRecycleBinCards.Remove(card);
        foreach (OpenPopoverEntry entry in _openPopoversById.Values)
        {
            entry.RecycleBinCards.Remove(card);
        }

        try
        {
            card.Dispose();
        }
        catch
        {
        }
    }

    internal CustomWidgetRecycleBinCard? TryGetHoveredRecycleBinCard(
        int screenX,
        int screenY,
        IntPtr hostHwnd)
    {
        for (int i = _activeRecycleBinCards.Count - 1; i >= 0; i--)
        {
            CustomWidgetRecycleBinCard card = _activeRecycleBinCards[i];
            if (card.ContainsScreenPoint(screenX, screenY, hostHwnd))
            {
                return card;
            }
        }

        return null;
    }

    internal bool UpdateRecycleBinNativeDragHover(
        int screenX,
        int screenY,
        IntPtr hostHwnd,
        bool canAcceptRecyclePayload)
    {
        CustomWidgetRecycleBinCard? hit = canAcceptRecyclePayload
            ? TryGetHoveredRecycleBinCard(screenX, screenY, hostHwnd)
            : null;

        foreach (CustomWidgetRecycleBinCard card in _activeRecycleBinCards)
        {
            card.SetDragHover(ReferenceEquals(card, hit));
        }

        return hit is not null;
    }

    internal CustomWidgetRecycleBinCard? ResolveDropTargetRecycleBinCard(
        int? screenX,
        int? screenY,
        IntPtr hostHwnd)
    {
        if (screenX is int sx && screenY is int sy &&
            TryGetHoveredRecycleBinCard(sx, sy, hostHwnd) is { } pointHit)
        {
            return pointHit;
        }

        for (int i = _activeRecycleBinCards.Count - 1; i >= 0; i--)
        {
            if (_activeRecycleBinCards[i].IsDragHoverActive)
            {
                return _activeRecycleBinCards[i];
            }
        }

        return _activeRecycleBinCards.FirstOrDefault(
            static card => card.IsLoaded && card.Visibility == Visibility.Visible);
    }

    internal void ClearRecycleBinNativeDragHover(CustomWidgetRecycleBinCard? except = null)
    {
        foreach (CustomWidgetRecycleBinCard card in _activeRecycleBinCards)
        {
            if (!ReferenceEquals(card, except))
            {
                card.SetDragHover(false);
            }
        }
    }

    private FrameworkElement BuildRecycleBin(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string ResolveTitle(CustomWidgetEvaluationContext evalCtx)
        {
            string raw = spec.Title ?? spec.Text ?? string.Empty;
            string resolved = CustomWidgetExpressionEvaluator.Interpolate(raw, evalCtx).Trim();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            string culture = evalCtx.CultureName ?? _viewModel.ActiveCultureName;
            return LocalizationService.TranslateForCulture(culture, "CustomWidget.RecycleBin.Title");
        }

        string ResolveDropHint(CustomWidgetEvaluationContext evalCtx)
        {
            string raw = spec.Subtitle ?? spec.Placeholder ?? string.Empty;
            string resolved = CustomWidgetExpressionEvaluator.Interpolate(raw, evalCtx).Trim();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            string culture = evalCtx.CultureName ?? _viewModel.ActiveCultureName;
            return LocalizationService.TranslateForCulture(culture, "CustomWidget.RecycleBin.DropHint");
        }

        string ResolveClearTooltip(CustomWidgetEvaluationContext evalCtx)
        {
            string raw = spec.Tooltip ?? string.Empty;
            string resolved = CustomWidgetExpressionEvaluator.Interpolate(raw, evalCtx).Trim();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            string culture = evalCtx.CultureName ?? _viewModel.ActiveCultureName;
            return LocalizationService.TranslateForCulture(culture, "CustomWidget.RecycleBin.ClearTooltip");
        }

        bool interactiveAllowed = !_isNodeSelectionEnabled && !_viewModel.IsUntrustedStagePreview;
        string resolvedTone = CustomWidgetExpressionEvaluator.Interpolate(
            string.IsNullOrWhiteSpace(spec.Tone) || string.Equals(spec.Tone, "default", StringComparison.OrdinalIgnoreCase)
                ? "subtle"
                : spec.Tone,
            ctx);
        var card = new CustomWidgetRecycleBinCard(
            initialTitle: ResolveTitle(ctx),
            initialDropHint: ResolveDropHint(ctx),
            initialClearTooltip: ResolveClearTooltip(ctx),
            bodyFontSize: BodyFontSize,
            captionFontSize: CaptionFontSize,
            isDark: IsDark,
            ownerHwndResolver: () => _viewModel.OwnerWindowHandleResolver?.Invoke() ?? IntPtr.Zero,
            cultureResolver: () => _viewModel.ActiveCultureName,
            isInteractiveAllowed: interactiveAllowed,
            hostCornerRadius: CornerMedium,
            hostBorderThickness: CardBorderThickness,
            hostRestBackground: ResolveCardBackground(resolvedTone),
            hostHoverBackground: SubtleHoverBrush,
            hostBorderBrush: CardStrokeBrush);

        if (_currentPopoverBuildOwner is { } popoverOwner)
        {
            popoverOwner.RecycleBinCards.Add(card);
        }
        else
        {
            _activeRecycleBinCards.Add(card);
        }

        if (ContainsDynamicToken(spec.Title) ||
            ContainsDynamicToken(spec.Text) ||
            ContainsDynamicToken(spec.Subtitle) ||
            ContainsDynamicToken(spec.Placeholder) ||
            ContainsDynamicToken(spec.Tooltip))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                card.UpdateLocalizedCopy(
                    ResolveTitle(liveCtx),
                    ResolveDropHint(liveCtx),
                    ResolveClearTooltip(liveCtx));
            });
        }

        return card;
    }
}
