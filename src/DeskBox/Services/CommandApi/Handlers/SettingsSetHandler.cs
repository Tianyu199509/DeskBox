using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

public sealed record SettingsSetResult(string Key, string Value, bool Applied, bool RequiresNothingElse);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SettingsSetResult), TypeInfoPropertyName = "SettingsSetResult")]
internal sealed partial class SettingsSetJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Writes an explicitly allowlisted settings key through the service that
/// makes it take effect immediately — never by editing persisted state
/// directly. Only low-risk, instantly-applied appearance keys are exposed:
/// theme and language. Storage paths, autostart, hotkeys, and the command
/// API switches themselves are deliberately unreachable.
/// </summary>
public sealed class SettingsSetHandler : ICommandHandler
{
    private static readonly string[] ValidThemes = ["System", "Light", "Dark"];
    private static readonly string[] ValidLanguages =
    [
        "System", "zh-CN", "zh-TW", "en-US", "ja-JP", "de-DE",
        "pt-BR", "hi-IN", "es-ES", "fr-FR", "ar-SA", "bn-BD", "ru-RU",
    ];

    private readonly Func<ThemeService?> _themeService;
    private readonly Func<LocalizationService?> _localizationService;

    public SettingsSetHandler(
        Func<ThemeService?> themeService,
        Func<LocalizationService?> localizationService)
    {
        _themeService = themeService;
        _localizationService = localizationService;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "settings/set",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.SettingsWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Sets one allowlisted setting: theme (System|Light|Dark) or language (System|zh-CN|zh-TW|en-US|ja-JP|de-DE|pt-BR|hi-IN|es-ES|fr-FR|ar-SA|bn-BD|ru-RU).",
        Arguments:
        [
            new CommandArgumentDescriptor("key", "string", true, "Setting key: theme | language.", "\"theme\""),
            new CommandArgumentDescriptor("value", "string", true, "New value.", "\"Dark\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":31,"method":"settings/set","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"key":"theme","value":"Dark"}}}""",
        ExampleResponseJson: """{"result":{"data":{"key":"theme","value":"Dark","applied":true,"requiresNothingElse":true}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!CommandArguments.TryGetString(arguments, "key", out string key)
            || !CommandArguments.TryGetString(arguments, "value", out string value)
            || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'key' and 'value' arguments are required.",
                """Retry with {"key":"theme","value":"Dark"} or {"key":"language","value":"en-US"}.""");
        }

        switch (key.ToLowerInvariant())
        {
            case "theme":
                string theme = ValidThemes.FirstOrDefault(candidate =>
                    candidate.Equals(value, StringComparison.OrdinalIgnoreCase))
                    ?? throw CommandValidationException.ValidationFailed(
                        $"Unknown theme '{value}'.",
                        $"Valid themes: {string.Join(", ", ValidThemes)}.");
                ThemeService? themeService = _themeService()
                    ?? throw WidgetLifecycle.NotLoaded("theme-service", "DeskBox is still starting; retry shortly.");
                themeService.SetTheme(theme);
                SettingsSetResult themeResult = new("theme", theme, true, true);
                return Task.FromResult(JsonSerializer.SerializeToElement(themeResult, SettingsSetJsonContext.Default.SettingsSetResult));

            case "language":
                string language = ValidLanguages.FirstOrDefault(candidate =>
                    candidate.Equals(value, StringComparison.OrdinalIgnoreCase))
                    ?? throw CommandValidationException.ValidationFailed(
                        $"Unknown language '{value}'.",
                        $"Valid languages: {string.Join(", ", ValidLanguages)}.");
                LocalizationService? localization = _localizationService()
                    ?? throw WidgetLifecycle.NotLoaded("localization-service", "DeskBox is still starting; retry shortly.");
                localization.SetLanguage(language);
                SettingsSetResult languageResult = new("language", language, true, true);
                return Task.FromResult(JsonSerializer.SerializeToElement(languageResult, SettingsSetJsonContext.Default.SettingsSetResult));

            default:
                throw CommandValidationException.ValidationFailed(
                    $"Setting key '{key}' is not settable through the command API.",
                    "Settable keys: theme, language. Storage paths, autostart, hotkeys, and command API switches are intentionally not exposed.");
        }
    }
}
