namespace DeskBox.Services;

using DeskBox.Models;

/// <summary>
/// Capsule-override reset operations on an AppSettings graph (batch R2).
/// Shared by the settings view model (host-local mirror, for immediate UI
/// feedback) and the layout-write command executor (main-process
/// authoritative state) so both sides run identical logic. Extracted
/// verbatim from the pre-split SettingsViewModel capsule commands.
/// </summary>
internal static class CapsuleOverrideResetter
{
    internal static bool ClearWidget(WidgetConfig widget)
    {
        bool changed = widget.Metadata?.Remove(WidgetCollapseBehaviorNames.MetadataKey) == true;
        if (widget.CompactWidth is not null)
        {
            widget.CompactWidth = null;
            changed = true;
        }

        if (widget.CompactPlacement is not null)
        {
            widget.CompactPlacement = null;
            changed = true;
        }

        return changed;
    }

    internal static bool ClearGroup(WidgetGroupConfig group)
    {
        bool changed = false;
        if (group.CompactWidth is not null)
        {
            group.CompactWidth = null;
            changed = true;
        }

        if (group.CompactPlacement is not null)
        {
            group.CompactPlacement = null;
            changed = true;
        }

        if (!string.Equals(group.CollapseBehavior, WidgetCollapseBehaviorNames.System, StringComparison.Ordinal))
        {
            group.CollapseBehavior = WidgetCollapseBehaviorNames.System;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Runs the requested reset scope; returns true when anything changed
    /// (callers skip the persist/notify when false).
    /// </summary>
    internal static bool Run(AppSettings settings, Contracts.CapsuleOverrideReset reset)
    {
        return reset.Scope switch
        {
            Contracts.CapsuleOverrideResetScope.BehaviorOnly => ResetBehaviorOnly(settings),
            Contracts.CapsuleOverrideResetScope.WidthsOnly => ResetWidthsOnly(settings),
            Contracts.CapsuleOverrideResetScope.WidgetAndGroup =>
                ResetForWidget(settings, reset.WidgetId ?? string.Empty),
            Contracts.CapsuleOverrideResetScope.All => ResetAll(settings),
            _ => false,
        };
    }

    private static bool ResetBehaviorOnly(AppSettings settings)
    {
        int changed = 0;
        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (widget.Metadata?.Remove(WidgetCollapseBehaviorNames.MetadataKey) == true)
            {
                changed++;
            }
        }

        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            if (!string.Equals(group.CollapseBehavior, WidgetCollapseBehaviorNames.System, StringComparison.Ordinal))
            {
                group.CollapseBehavior = WidgetCollapseBehaviorNames.System;
                changed++;
            }
        }

        return changed > 0;
    }

    private static bool ResetWidthsOnly(AppSettings settings)
    {
        int changed = 0;
        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (widget.CompactWidth is not null)
            {
                widget.CompactWidth = null;
                changed++;
            }
        }

        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            if (group.CompactWidth is not null)
            {
                group.CompactWidth = null;
                changed++;
            }
        }

        return changed > 0;
    }

    private static bool ResetForWidget(AppSettings settings, string widgetId)
    {
        WidgetConfig? widget = settings.Widgets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, widgetId, StringComparison.Ordinal));
        if (widget is null)
        {
            return false;
        }

        bool changed = ClearWidget(widget);
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(settings, widgetId);
        if (group is not null)
        {
            foreach (WidgetConfig member in settings.Widgets.Where(candidate =>
                         group.MemberIds.Contains(candidate.Id, StringComparer.Ordinal)))
            {
                changed |= ClearWidget(member);
            }

            changed |= ClearGroup(group);
        }

        return changed;
    }

    private static bool ResetAll(AppSettings settings)
    {
        bool changed = false;
        foreach (WidgetConfig widget in settings.Widgets)
        {
            changed |= ClearWidget(widget);
        }

        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            changed |= ClearGroup(group);
        }

        return changed;
    }
}
