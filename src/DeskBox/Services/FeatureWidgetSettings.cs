using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Compatibility layer for singleton feature widget enabled states.
/// Keep legacy AppSettings booleans mirrored until all callers are descriptor-driven.
/// </summary>
public static class FeatureWidgetSettings
{
    // Derived from the content descriptor table so a widget kind is declared
    // once. A kind becomes user-enableable by setting IsFeatureWidget on its
    // descriptor; the ordered list below follows that table's declaration order.
    private static readonly WidgetKind[] s_featureKinds =
        WidgetContentFactory.DescriptorList
            .Where(descriptor => descriptor.IsFeatureWidget)
            .Select(descriptor => descriptor.WidgetKind)
            .ToArray();

    public static IReadOnlyList<WidgetKind> FeatureKinds => s_featureKinds;

    public static bool IsFeatureWidget(WidgetKind kind)
    {
        return s_featureKinds.Contains(kind);
    }

    public static bool IsEnabled(AppSettings settings, WidgetKind kind)
    {
        EnsureStateBag(settings);

        if (TryGetStoredState(settings, kind, out bool enabled))
        {
            return enabled;
        }

        return GetLegacyEnabled(settings, kind);
    }

    public static void SetEnabled(AppSettings settings, WidgetKind kind, bool enabled)
    {
        if (!IsFeatureWidget(kind))
        {
            return;
        }

        EnsureStateBag(settings);
        settings.FeatureWidgetEnabledStates[GetKey(kind)] = enabled;
        SetLegacyEnabled(settings, kind, enabled);
    }

    public static bool Normalize(AppSettings settings)
    {
        bool changed = false;
        EnsureStateBag(settings);

        foreach (var kind in s_featureKinds)
        {
            string key = GetKey(kind);
            if (TryGetStoredState(settings, kind, out bool enabled))
            {
                if (!settings.FeatureWidgetEnabledStates.ContainsKey(key))
                {
                    settings.FeatureWidgetEnabledStates[key] = enabled;
                    changed = true;
                }

                if (GetLegacyEnabled(settings, kind) != enabled)
                {
                    SetLegacyEnabled(settings, kind, enabled);
                    changed = true;
                }

                continue;
            }

            bool legacyEnabled = GetLegacyEnabled(settings, kind);
            settings.FeatureWidgetEnabledStates[key] = legacyEnabled;
            changed = true;
        }

        var invalidKeys = settings.FeatureWidgetEnabledStates.Keys
            .Where(key => !TryParseFeatureKind(key, out _))
            .ToList();
        foreach (var key in invalidKeys)
        {
            settings.FeatureWidgetEnabledStates.Remove(key);
            changed = true;
        }

        return changed;
    }

    private static void EnsureStateBag(AppSettings settings)
    {
        settings.FeatureWidgetEnabledStates ??= [];
    }

    private static bool TryGetStoredState(AppSettings settings, WidgetKind kind, out bool enabled)
    {
        string canonicalKey = GetKey(kind);
        if (settings.FeatureWidgetEnabledStates.TryGetValue(canonicalKey, out enabled))
        {
            return true;
        }

        foreach (var (key, value) in settings.FeatureWidgetEnabledStates)
        {
            if (TryParseFeatureKind(key, out var parsedKind) && parsedKind == kind)
            {
                enabled = value;
                return true;
            }
        }

        enabled = false;
        return false;
    }

    private static bool TryParseFeatureKind(string key, out WidgetKind kind)
    {
        if (Enum.TryParse(key, ignoreCase: true, out kind) &&
            IsFeatureWidget(kind))
        {
            return true;
        }

        kind = default;
        return false;
    }

    private static string GetKey(WidgetKind kind)
    {
        return kind.ToString();
    }

    private static bool GetLegacyEnabled(AppSettings settings, WidgetKind kind)
    {
        return kind switch
        {
            WidgetKind.QuickCapture => settings.QuickCaptureEnabled,
            WidgetKind.Todo => settings.TodoEnabled,
            // Music/Weather/Search/Glance never had persisted legacy mirrors.
            // A MISSING dictionary key means "never authored", and the live
            // widgets list is the truth then: a widget of this kind sitting
            // in the list IS the enabled state. Falling back to a constant
            // false turned every holey dictionary (a pre-dictionary backup
            // restore, or the layout write-back corruption) into "everything
            // off" on the next save — the search-toggle collateral.
            WidgetKind.Music => HasLiveWidget(settings, WidgetKind.Music),
            WidgetKind.Weather => HasLiveWidget(settings, WidgetKind.Weather),
            WidgetKind.Search => HasLiveWidget(settings, WidgetKind.Search),
            WidgetKind.Glance => HasLiveWidget(settings, WidgetKind.Glance),
            _ => false
        };
    }

    private static bool HasLiveWidget(AppSettings settings, WidgetKind kind) =>
        settings.Widgets.Any(widget =>
            widget.WidgetKind == kind &&
            !widget.IsDisabled &&
            !settings.DeletedWidgetIds.Contains(widget.Id));

    private static void SetLegacyEnabled(AppSettings settings, WidgetKind kind, bool enabled)
    {
        switch (kind)
        {
            case WidgetKind.QuickCapture:
                settings.QuickCaptureEnabled = enabled;
                break;
            case WidgetKind.Todo:
                settings.TodoEnabled = enabled;
                break;
        }
    }
}
