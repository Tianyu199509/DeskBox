using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Compatibility layer for singleton feature widget enabled states.
/// Keep legacy AppSettings booleans mirrored until all callers are descriptor-driven.
/// </summary>
public static class FeatureWidgetSettings
{
    private static readonly WidgetKind[] s_featureKinds =
    [
        WidgetKind.QuickCapture,
        WidgetKind.Todo,
        WidgetKind.Music
    ];

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
            WidgetKind.Music => false,
            _ => false
        };
    }

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
