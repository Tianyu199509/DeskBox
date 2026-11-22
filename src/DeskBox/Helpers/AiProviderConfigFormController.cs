using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Helpers;

/// <summary>
/// Shared AI provider configuration form logic (provider list, model catalog sync, thinking mode,
/// context window budget, endpoint normalization, connectivity test, model fetch, and DPAPI upsert)
/// shared between <c>AiSettingsSection</c> and <c>CustomWidgetAiConfigDialogHelper</c>.
/// </summary>
internal sealed class AiProviderConfigFormController
{
    private readonly Func<string, string> _translate;
    private readonly Func<string, object[], string> _format;
    private readonly Func<string> _resolveCulture;

    public AiProviderConfigFormController(
        Func<string, string> translate,
        Func<string, object[], string> format,
        Func<string> resolveCulture)
    {
        _translate = translate;
        _format = format;
        _resolveCulture = resolveCulture;
    }

    public static string Translate(LocalizationService? loc, string key) =>
        loc is not null
            ? loc.T(key)
            : LocalizationService.TranslateForCulture(Localized.ActiveCultureName, key);

    public static string Format(LocalizationService? loc, string key, params object[] args) =>
        loc is not null
            ? loc.Format(key, args)
            : LocalizationService.FormatForCulture(Localized.ActiveCultureName, key, args);

    public static string ResolveCultureName(LocalizationService? loc) =>
        loc?.CurrentCultureName ?? Localized.ActiveCultureName;

    public string PopulateProviderCombo(ComboBox comboBox, string? preferredProviderId)
    {
        string culture = _resolveCulture();
        IReadOnlyList<CustomWidgetAiProviderPreset> providers =
            DeskBoxAiService.GetOrderedProvidersForCulture(culture);
        IReadOnlyDictionary<string, AiProviderCredentialEntry> savedAll =
            CustomWidgetAiCredentialStore.Shared.GetAll();

        string? readyId = DeskBoxAiService.Shared.ResolveActiveProviderId(preferredProviderId);
        string targetId = !string.IsNullOrWhiteSpace(preferredProviderId)
            ? preferredProviderId
            : (readyId ?? DeskBoxAiService.GetDefaultStarterProviderId(culture));

        comboBox.Items.Clear();
        int selectedIndex = 0;
        for (int i = 0; i < providers.Count; i++)
        {
            CustomWidgetAiProviderPreset p = providers[i];
            savedAll.TryGetValue(p.Id, out AiProviderCredentialEntry? entry);
            bool configured = DeskBoxAiService.IsProviderConfigured(p, entry);
            comboBox.Items.Add(new ComboBoxItem
            {
                Content = configured ? $"{p.DisplayName}  ✓" : p.DisplayName,
                Tag = p.Id
            });
            if (string.Equals(p.Id, targetId, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
            }
        }

        comboBox.SelectedIndex = selectedIndex;
        return providers.Count > 0 ? providers[selectedIndex].Id : targetId;
    }

    public string ResolveSelectedProviderId(ComboBox providerCombo) =>
        (providerCombo.SelectedItem as ComboBoxItem)?.Tag as string
        ?? DeskBoxAiService.GetDefaultStarterProviderId(_resolveCulture());

    public static void PopulateModelCombo(
        ComboBox modelCombo,
        CustomWidgetAiProviderPreset preset,
        IReadOnlyList<string> selectableModels,
        string activeModel)
    {
        modelCombo.PlaceholderText = selectableModels.Count > 0 ? selectableModels[0] : preset.DefaultModel;
        bool itemsMatch = modelCombo.Items.Count == selectableModels.Count &&
                          modelCombo.Items.OfType<string>().SequenceEqual(selectableModels, StringComparer.Ordinal);
        if (!itemsMatch)
        {
            modelCombo.Items.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string model in selectableModels)
            {
                if (!string.IsNullOrWhiteSpace(model) && seen.Add(model))
                {
                    modelCombo.Items.Add(model);
                }
            }
            if (!string.IsNullOrWhiteSpace(activeModel) && seen.Add(activeModel))
            {
                modelCombo.Items.Insert(0, activeModel);
            }
        }

        string? matchingItem = modelCombo.Items.OfType<string>()
            .FirstOrDefault(m => string.Equals(m, activeModel, StringComparison.OrdinalIgnoreCase));
        modelCombo.SelectedItem = matchingItem ?? activeModel;
        modelCombo.Text = matchingItem ?? activeModel;
    }

    public void PopulateThinkingCombo(ComboBox thinkingCombo, AiThinkingMode mode, bool detailedLabels)
    {
        thinkingCombo.Items.Clear();
        thinkingCombo.Items.Add(new ComboBoxItem
        {
            Content = _translate(detailedLabels ? "CustomWidget.AiConfig.ThinkingAuto" : "CustomWidget.AiConfig.ThinkingAutoShort"),
            Tag = "auto"
        });
        thinkingCombo.Items.Add(new ComboBoxItem
        {
            Content = _translate(detailedLabels ? "CustomWidget.AiConfig.ThinkingEnabled" : "CustomWidget.AiConfig.ThinkingEnabledShort"),
            Tag = "enabled"
        });
        thinkingCombo.Items.Add(new ComboBoxItem
        {
            Content = _translate(detailedLabels ? "CustomWidget.AiConfig.ThinkingDisabled" : "CustomWidget.AiConfig.ThinkingDisabledShort"),
            Tag = "disabled"
        });
        thinkingCombo.SelectedIndex = mode switch
        {
            AiThinkingMode.Enabled => 1,
            AiThinkingMode.Disabled => 2,
            _ => 0
        };
    }

    public void PopulateContextWindowCombo(ComboBox contextCombo, AiContextWindowMode mode)
    {
        contextCombo.Items.Clear();
        contextCombo.Items.Add(new ComboBoxItem { Content = _translate("CustomWidget.AiConfig.ContextWindowAuto"), Tag = "auto" });
        contextCombo.Items.Add(new ComboBoxItem { Content = _translate("CustomWidget.AiConfig.ContextWindow16k"), Tag = "16k" });
        contextCombo.Items.Add(new ComboBoxItem { Content = _translate("CustomWidget.AiConfig.ContextWindow32k"), Tag = "32k" });
        contextCombo.Items.Add(new ComboBoxItem { Content = _translate("CustomWidget.AiConfig.ContextWindow128k"), Tag = "128k" });
        contextCombo.SelectedIndex = mode switch
        {
            AiContextWindowMode.Compact16k => 1,
            AiContextWindowMode.Standard32k => 2,
            AiContextWindowMode.Extended128k => 3,
            _ => 0
        };
    }

    public string ResolveEndpointPlaceholder(CustomWidgetAiProviderPreset preset, AiProviderCredentialEntry? entry)
    {
        bool isGlobalDefault = string.Equals(entry?.Region, "global", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(entry?.Region) &&
             !DeskBoxAiService.IsSimplifiedChineseRegion(_resolveCulture()));
        return isGlobalDefault && !string.IsNullOrWhiteSpace(preset.GlobalDefaultEndpoint)
            ? preset.GlobalDefaultEndpoint!
            : preset.DefaultEndpoint;
    }

    public string BuildContextWindowDescription(ResolvedAiInvocation resolved) =>
        AiContextBudgetResolver.IsOllamaRoute(resolved)
            ? $"{_translate("CustomWidget.AiConfig.ContextWindowDesc")}\n{_translate("CustomWidget.AiConfig.OllamaContextHint")}"
            : _translate("CustomWidget.AiConfig.ContextWindowDesc");

    public static string ReadModelComboText(ComboBox modelCombo, CustomWidgetAiProviderPreset preset, IReadOnlyList<string>? fetchedModels = null)
    {
        string chosen = modelCombo.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(chosen) && modelCombo.SelectedItem is string s)
        {
            chosen = s.Trim();
        }
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            return chosen;
        }
        return fetchedModels is { Count: > 0 } ? fetchedModels[0] : preset.DefaultModel;
    }

    public static string ReadThinkingWire(ComboBox thinkingCombo) =>
        (thinkingCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

    public static string ReadContextWindowWire(ComboBox contextCombo, string? fallbackWire = null) =>
        (contextCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallbackWire ?? "auto";

    public AiProviderCredentialEntry SaveProviderSnapshot(
        string providerId,
        string rawApiKey,
        string rawEndpoint,
        string rawChosenModel,
        string rawThinkingWire,
        string rawContextWindowWire,
        IReadOnlyList<string>? explicitFetchedModels = null)
    {
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        AiProviderCredentialEntry? prev = CustomWidgetAiCredentialStore.Shared.Get(preset.Id);
        string defaultRegion = DeskBoxAiService.IsSimplifiedChineseRegion(_resolveCulture()) ? "cn" : "global";
        string trimmedKey = rawApiKey.Trim();
        string customEndpoint = rawEndpoint.Trim();

        string prevEffectiveUrl = prev is not null
            ? AiProviderCatalog.ResolveConfiguredEndpointUrl(preset, prev.Region, prev.CustomEndpoint)
            : preset.DefaultEndpoint;
        string nextEffectiveUrl = AiProviderCatalog.ResolveConfiguredEndpointUrl(
            preset,
            prev?.Region ?? defaultRegion,
            customEndpoint);
        bool endpointChanged = prev is not null &&
                               !string.Equals(prevEffectiveUrl, nextEffectiveUrl, StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<string>? effectiveFetched = explicitFetchedModels is { Count: > 0 }
            ? CustomWidgetAiCredentialStore.NormalizeModelCatalog(explicitFetchedModels)
            : (endpointChanged ? null : prev?.FetchedModels);

        string fallbackModel = effectiveFetched is { Count: > 0 }
            ? effectiveFetched[0]
            : preset.DefaultModel;
        string modelToSync = string.IsNullOrWhiteSpace(rawChosenModel)
            ? fallbackModel
            : rawChosenModel.Trim();

        (string customModel, string thinkingWire) = DeskBoxAiService.SynchronizeModelAndThinkingSelection(
            preset,
            prev?.CustomModel,
            prev?.ThinkingMode,
            newChosenModel: modelToSync,
            newThinkingWire: rawThinkingWire,
            availableModels: effectiveFetched);

        // Always enable when a non-empty API key is saved (or for keyless local providers),
        // even if the provider entry was previously disabled.
        bool enabledState = preset.RequiresApiKey
            ? !string.IsNullOrWhiteSpace(trimmedKey)
            : true;

        var updated = new AiProviderCredentialEntry(
            ProviderId: preset.Id,
            ApiKey: trimmedKey,
            Region: prev?.Region ?? defaultRegion,
            PlanTier: prev?.PlanTier ?? "pro",
            Enabled: enabledState,
            CustomEndpoint: customEndpoint,
            CustomModel: customModel,
            ThinkingMode: thinkingWire,
            FetchedModels: effectiveFetched,
            ContextWindowMode: DeskBoxAiService.ToContextWindowWireValue(
                DeskBoxAiService.ParseContextWindowMode(rawContextWindowWire)));

        CustomWidgetAiCredentialStore.Shared.Upsert(updated);
        if (enabledState)
        {
            CustomWidgetStudioPreferencesStore.Shared.SetPreferredAiProviderId(preset.Id);
        }

        return updated;
    }

    public static void ClearOrDisableProvider(string providerId)
    {
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        AiProviderCredentialEntry? prev = CustomWidgetAiCredentialStore.Shared.Get(preset.Id);
        if (preset.RequiresApiKey)
        {
            CustomWidgetAiCredentialStore.Shared.RemoveKey(preset.Id);
        }
        else if (prev is not null)
        {
            CustomWidgetAiCredentialStore.Shared.Upsert(prev with
            {
                Enabled = false,
                ApiKey = string.Empty,
                FetchedModels = null
            });
        }
    }

    public string FormatTestSuccess(AiConnectionTestResult result) =>
        _format("CustomWidget.AiConfig.TestSuccess", [result.LatencyMs, result.EffectiveModel]);

    public string FormatTestFailure(AiConnectionTestResult result) =>
        result.ErrorMessage ?? _translate("CustomWidget.AiConfig.TestFailed");

    public string FormatFetchSuccess(int count) =>
        _format("CustomWidget.AiConfig.FetchModelsSuccess", [count]);

    public string FormatFetchFailure(AiModelListResult result) =>
        result.ErrorMessage ?? _translate("CustomWidget.AiConfig.FetchModelsFailed");
}
