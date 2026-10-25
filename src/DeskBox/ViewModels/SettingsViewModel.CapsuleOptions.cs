using DeskBox.Contracts;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    // Batch 44: the capsule family's XAML binding surface (behavior, geometry,
    // content/privacy, arrangement, bar placement/direction/spacing, animation
    // presets, hover-response derived view state, delay sliders and the
    // override-list gates) lives on the capsule editor; this shell keeps the
    // widget/group override state machine (counts, projections, resets) and
    // pushes the projection into the editor after every real change.

    public int CapsuleCustomWidthCount =>
        _settingsService.Settings.Widgets.Count(widget => widget.CompactWidth is not null) +
        _settingsService.Settings.WidgetGroups.Count(group => group.CompactWidth is not null);

    public int CapsuleOverrideWidgetCount => _settingsService.Settings.Widgets.Count(HasCapsuleOverride);

    public bool HasCapsuleOverrides => CapsuleOverrideWidgetCount > 0;

    public bool HasSmartCapsuleCollapseOverride =>
        _settingsService.Settings.Widgets.Any(widget =>
            WidgetCollapseBehaviorNames.GetOverride(widget) == WidgetCollapseBehavior.Smart) ||
        _settingsService.Settings.WidgetGroups.Any(group =>
            WidgetCollapseBehaviorNames.Normalize(
                group.CollapseBehavior,
                WidgetCollapseBehavior.System,
                allowSystem: true) == WidgetCollapseBehavior.Smart);

    [RelayCommand]
    private void ResetCapsuleBehaviorOverrides()
    {
        int changed = 0;
        foreach (var widget in _settingsService.Settings.Widgets)
        {
            if (widget.Metadata?.Remove(WidgetCollapseBehaviorNames.MetadataKey) == true)
            {
                changed++;
            }
        }
        foreach (WidgetGroupConfig group in _settingsService.Settings.WidgetGroups)
        {
            if (!string.Equals(
                    group.CollapseBehavior,
                    WidgetCollapseBehaviorNames.System,
                    StringComparison.Ordinal))
            {
                group.CollapseBehavior = WidgetCollapseBehaviorNames.System;
                changed++;
            }
        }

        if (changed > 0)
        {
            _settingsService.SaveDebounced();
            NotifyCapsuleOverridePropertiesChanged();
            _ = _widgetActions.ResetCapsuleOverrides(new CapsuleOverrideReset(
                CapsuleOverrideResetScope.BehaviorOnly));
        }
    }

    // ResetCapsuleGeometryOverrides removed (deep review R13-2, D8 debt
    // census): zero XAML bindings, zero callers, and unlike its three
    // siblings it never sent the layout-write command — a host-side call
    // would have silently written nothing but the local mirror (the
    // authoritative layout domain is preserved away from host commits).
    // Reviving this needs a Geometry scope on CapsuleOverrideReset plus
    // the command pair.

    public void ResetCapsuleOverridesForWidget(string widgetId)
    {
        var widget = _settingsService.Settings.Widgets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, widgetId, StringComparison.Ordinal));
        if (widget is null)
        {
            return;
        }

        bool changed = ClearCapsuleOverrides(widget);
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            widgetId);
        if (group is not null)
        {
            foreach (WidgetConfig member in _settingsService.Settings.Widgets.Where(candidate =>
                         group.MemberIds.Contains(candidate.Id, StringComparer.Ordinal)))
            {
                changed |= ClearCapsuleOverrides(member);
            }
            changed |= ClearCapsuleOverrides(group);
        }

        if (!changed)
        {
            return;
        }

        _settingsService.SaveDebounced();
        NotifyCapsuleOverridePropertiesChanged();
        _ = _widgetActions.ResetCapsuleOverrides(new CapsuleOverrideReset(
            CapsuleOverrideResetScope.WidgetAndGroup,
            widgetId));
    }

    [RelayCommand]
    private void ResetAllCapsuleOverrides()
    {
        bool changed = false;
        foreach (var widget in _settingsService.Settings.Widgets)
        {
            changed |= ClearCapsuleOverrides(widget);
        }
        foreach (WidgetGroupConfig group in _settingsService.Settings.WidgetGroups)
        {
            changed |= ClearCapsuleOverrides(group);
        }

        if (!changed)
        {
            return;
        }

        _settingsService.SaveDebounced();
        NotifyCapsuleOverridePropertiesChanged();
        _ = _widgetActions.ResetCapsuleOverrides(new CapsuleOverrideReset(
            CapsuleOverrideResetScope.All));
    }

    private static bool HasCapsuleOverride(WidgetConfig widget) =>
        widget.Metadata?.ContainsKey(WidgetCollapseBehaviorNames.MetadataKey) == true ||
        widget.CompactWidth is not null ||
        widget.CompactPlacement is not null;

    private static bool ClearCapsuleOverrides(WidgetConfig widget)
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

    [RelayCommand]
    private void ResetCapsuleWidthOverrides()
    {
        int changed = 0;
        foreach (WidgetConfig widget in _settingsService.Settings.Widgets)
        {
            if (widget.CompactWidth is not null)
            {
                widget.CompactWidth = null;
                changed++;
            }
        }
        foreach (WidgetGroupConfig group in _settingsService.Settings.WidgetGroups)
        {
            if (group.CompactWidth is not null)
            {
                group.CompactWidth = null;
                changed++;
            }
        }

        if (changed > 0)
        {
            _settingsService.SaveDebounced();
            NotifyCapsuleOverridePropertiesChanged();
            _ = _widgetActions.ResetCapsuleOverrides(new CapsuleOverrideReset(
                CapsuleOverrideResetScope.WidthsOnly));
        }
    }

    private static bool ClearCapsuleOverrides(WidgetGroupConfig group)
    {
        bool changed = !string.Equals(
            group.CollapseBehavior,
            WidgetCollapseBehaviorNames.System,
            StringComparison.Ordinal);
        group.CollapseBehavior = WidgetCollapseBehaviorNames.System;
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

        return changed;
    }

    private CapsuleOverrideSettingsItem CreateCapsuleOverrideSettingsItem(WidgetConfig widget)
    {
        var details = new List<string>(3);
        if (widget.Metadata is not null &&
            widget.Metadata.TryGetValue(WidgetCollapseBehaviorNames.MetadataKey, out string? behavior))
        {
            details.Add(_localizationService.Format(
                "Settings.Capsule.Overrides.Item.Behavior",
                GetWidgetCollapseBehaviorDisplayName(behavior)));
        }

        if (widget.CompactWidth is { } width)
        {
            details.Add(_localizationService.Format(
                "Settings.Capsule.Overrides.Item.Width",
                Math.Round(width)));
        }

        if (widget.CompactPlacement is not null)
        {
            details.Add(_localizationService.T("Settings.Capsule.Overrides.Item.Position"));
        }

        string displayName = string.IsNullOrWhiteSpace(widget.Name)
            ? GetWidgetKindDisplayName(widget.WidgetKind)
            : widget.Name.Trim();
        return new CapsuleOverrideSettingsItem(
            widget.Id,
            displayName,
            string.Join(" · ", details),
            GetWidgetKindGlyph(widget.WidgetKind));
    }

    private string GetWidgetKindDisplayName(WidgetKind kind) => kind switch
    {
        WidgetKind.QuickCapture => _localizationService.T("WidgetTitleIcon.Label.QuickCapture"),
        WidgetKind.Todo => _localizationService.T("WidgetTitleIcon.Label.Todo"),
        WidgetKind.Music => _localizationService.T("WidgetTitleIcon.Label.Music"),
        WidgetKind.Weather => _localizationService.T("WidgetTitleIcon.Label.Weather"),
        WidgetKind.Tags => _localizationService.T("WidgetTitleIcon.Label.Tags"),
        WidgetKind.SystemMonitor => _localizationService.T("WidgetTitleIcon.Label.SystemMonitor"),
        _ => _localizationService.T("WidgetTitleIcon.Label.Default")
    };

    private static string GetWidgetKindGlyph(WidgetKind kind) => kind switch
    {
        WidgetKind.QuickCapture => "\uE70F",
        WidgetKind.Todo => "\uE73E",
        WidgetKind.Music => "\uE8D6",
        WidgetKind.Weather => "\uE706",
        _ => "\uE8A5"
    };

    /// <summary>
    /// Rebuilds the widget/group override projection and pushes it into the
    /// capsule editor (the section family's binding surface since batch 44).
    /// </summary>
    private void NotifyCapsuleOverridePropertiesChanged()
    {
        _capsuleSettings.UpdateOverridePresentation(
            _settingsService.Settings.Widgets
                .Where(HasCapsuleOverride)
                .Select(CreateCapsuleOverrideSettingsItem)
                .ToArray(),
            HasCapsuleOverrides,
            _localizationService.Format(
                "Settings.Capsule.Overrides.Summary",
                CapsuleOverrideWidgetCount),
            CapsuleCustomWidthCount > 0,
            HasSmartCapsuleCollapseOverride);
        ResetCapsuleBehaviorOverridesCommand.NotifyCanExecuteChanged();
        ResetCapsuleWidthOverridesCommand.NotifyCanExecuteChanged();
        ResetAllCapsuleOverridesCommand.NotifyCanExecuteChanged();
    }
}
