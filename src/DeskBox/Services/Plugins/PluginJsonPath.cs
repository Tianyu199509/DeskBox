using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBox.Services.Plugins;

/// <summary>The same bounded JSON-path grammar is used at installation and evaluation.</summary>
internal static partial class PluginJsonPath
{
    [GeneratedRegex(@"^\$\.[A-Za-z0-9_]+(?:\[[0-9]+\])*(?:\.[A-Za-z0-9_]+(?:\[[0-9]+\])*)*$")]
    private static partial Regex Grammar();
    [GeneratedRegex(@"(?<property>[A-Za-z0-9_]+)|\[(?<index>[0-9]+)\]")]
    private static partial Regex Tokens();

    internal static bool IsValid(string? path)
    {
        if (path is not { Length: >= 3 and <= 512 } || !Grammar().IsMatch(path)) return false;
        MatchCollection tokens = Tokens().Matches(path[2..]);
        return tokens.Count <= 64 && tokens.All(token =>
            !token.Groups["index"].Success || int.TryParse(token.Groups["index"].Value, out _));
    }

    internal static bool TryResolve(JsonElement document, string path, out JsonElement result)
    {
        result = default;
        if (!IsValid(path)) return false;
        JsonElement current = document;
        foreach (Match token in Tokens().Matches(path[2..]))
        {
            if (token.Groups["index"].Success)
            {
                if (!int.TryParse(token.Groups["index"].Value, out int index) ||
                    current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength()) return false;
                current = current[index];
            }
            else if (current.ValueKind != JsonValueKind.Object ||
                     !current.TryGetProperty(token.Groups["property"].Value, out current)) return false;
        }
        if (current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        result = current;
        return true;
    }

    internal static string? ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };
}

