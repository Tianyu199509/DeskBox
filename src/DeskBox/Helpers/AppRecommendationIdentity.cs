using System.Text;
using DeskBox.Models;

namespace DeskBox.Helpers;

/// <summary>
/// Computes normalized, Native AOT-safe canonical keys for application recommendations
/// so duplicate shortcuts (.lnk/.url) and direct executables (.exe) collapse cleanly
/// while preserving user ordering, folder grouping, and hidden-item state.
/// </summary>
internal static class AppRecommendationIdentity
{
    private static readonly HashSet<string> s_sharedHostExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update.exe",
        "chrome_proxy.exe",
        "msedge_proxy.exe",
        "brave_proxy.exe",
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "wscript.exe",
        "cscript.exe",
        "mshta.exe",
        "rundll32.exe",
        "msiexec.exe",
        "explorer.exe",
        "javaw.exe",
        "java.exe"
    };

    internal readonly record struct Keys(
        string CanonicalKey,
        string? TargetKey,
        string? PathKey,
        string NameKey);

    public static string GetCanonicalKey(SearchResultItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.RecommendationKey))
        {
            return item.RecommendationKey;
        }

        string key = ResolveKeys(item.DetailPath, item.Title).CanonicalKey;
        item.RecommendationKey = key;
        return key;
    }

    public static Keys ResolveKeys(string? detailPath, string title)
    {
        string? normalizedDetailPath = string.IsNullOrWhiteSpace(detailPath)
            ? null
            : NormalizePath(detailPath);
        string? pathKey = normalizedDetailPath is null
            ? null
            : $"path:{normalizedDetailPath.ToLowerInvariant()}";

        string? targetKey = ResolveTargetKey(detailPath, normalizedDetailPath, title);
        string normalizedDisplayName = NormalizeDisplayName(title);
        string nameKey = normalizedDisplayName.Length > 0
            ? $"name:{normalizedDisplayName}"
            : string.Empty;

        string canonicalKey = targetKey
            ?? (nameKey.Length > 0 ? nameKey : null)
            ?? pathKey
            ?? $"raw:{title.Trim().ToLowerInvariant()}";

        return new Keys(canonicalKey, targetKey, pathKey, nameKey);
    }

    private static string? ResolveTargetKey(
        string? rawDetailPath,
        string? normalizedDetailPath,
        string title)
    {
        if (string.IsNullOrWhiteSpace(rawDetailPath) || normalizedDetailPath is null)
        {
            return null;
        }

        if (ShortcutHelper.IsShortcutPath(rawDetailPath))
        {
            ShortcutInfo? metadata = ShortcutHelper.ReadStoredMetadata(rawDetailPath);
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.TargetPath))
            {
                return null;
            }

            return ResolveShortcutMetadataTargetKey(metadata.TargetPath, metadata.Arguments);
        }

        if (FileCategoryHelper.Categorize(title) == FileCategory.App)
        {
            return $"target:{normalizedDetailPath.ToLowerInvariant()}";
        }

        return null;
    }

    internal static string? ResolveShortcutMetadataTargetKey(string? targetPath, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        string rawTarget = targetPath.Trim().Trim('"');
        if (rawTarget.Length == 0)
        {
            return null;
        }

        string expandedTarget;
        try
        {
            expandedTarget = Environment.ExpandEnvironmentVariables(rawTarget).Trim();
        }
        catch
        {
            expandedTarget = rawTarget;
        }

        if (Uri.TryCreate(expandedTarget, UriKind.Absolute, out Uri? parsedUri) &&
            !parsedUri.IsFile)
        {
            return $"url:{expandedTarget.ToLowerInvariant()}";
        }

        string normalizedTarget = NormalizePath(expandedTarget);
        if (normalizedTarget.Length == 0)
        {
            return null;
        }

        string exeName = Path.GetFileName(normalizedTarget);
        if (exeName.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) &&
            TryExtractProcessStartExecutable(arguments, out string? subExecutable))
        {
            return $"squirrel:{subExecutable.ToLowerInvariant()}";
        }

        if (s_sharedHostExecutables.Contains(exeName))
        {
            string normalizedArgs = NormalizeWhitespace(arguments);
            return normalizedArgs.Length > 0
                ? $"host:{normalizedTarget.ToLowerInvariant()}|args:{normalizedArgs.ToLowerInvariant()}"
                : null;
        }

        return $"target:{normalizedTarget.ToLowerInvariant()}";
    }

    internal static bool TryExtractProcessStartExecutable(
        string? arguments,
        out string executableName)
    {
        executableName = string.Empty;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        const string flag = "--processStart";
        int flagIndex = arguments.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (flagIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<char> rest = arguments.AsSpan(flagIndex + flag.Length);
        if (!rest.IsEmpty && !char.IsWhiteSpace(rest[0]) && rest[0] != '=')
        {
            // Avoid matching "--processStartArgs" before "--processStart".
            int retryIndex = arguments.IndexOf(
                flag + " ",
                flagIndex + 1,
                StringComparison.OrdinalIgnoreCase);
            if (retryIndex < 0)
            {
                return false;
            }

            rest = arguments.AsSpan(retryIndex + flag.Length);
        }

        rest = rest.TrimStart();
        if (!rest.IsEmpty && rest[0] == '=')
        {
            rest = rest[1..].TrimStart();
        }

        if (rest.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<char> token;
        if (rest[0] == '"')
        {
            rest = rest[1..];
            int closingQuote = rest.IndexOf('"');
            token = closingQuote >= 0 ? rest[..closingQuote] : rest;
        }
        else
        {
            int nextSpace = rest.IndexOfAny(' ', '\t');
            token = nextSpace >= 0 ? rest[..nextSpace] : rest;
        }

        string candidate = Path.GetFileName(token.Trim().ToString());
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        executableName = candidate;
        return true;
    }

    internal static string NormalizeDisplayName(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string stem = Path.GetFileNameWithoutExtension(title);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = title;
        }

        return NormalizeWhitespace(stem).ToLowerInvariant();
    }

    internal static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        var builder = new StringBuilder(trimmed.Length);
        bool previousWasSpace = false;
        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }
            }
            else
            {
                builder.Append(c);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().Replace('/', '\\').TrimEnd('\\');
        }
    }
}

/// <summary>
/// Stateful deduplicator that preserves first-seen priority order across multiple
/// recommendation sources (File widgets -> Start Menu -> Recent launches).
/// </summary>
internal sealed class AppRecommendationDeduplicator
{
    private readonly HashSet<string> _seenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenNames = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAccept(string? detailPath, string title, out string canonicalKey) =>
        TryAccept(detailPath, title, precomputedKeys: null, out canonicalKey);

    public bool TryAccept(
        string? detailPath,
        string title,
        AppRecommendationIdentity.Keys? precomputedKeys,
        out string canonicalKey)
    {
        return TryAcceptWithKeys(detailPath, title, precomputedKeys, out _, out canonicalKey);
    }

    public bool TryAcceptWithKeys(
        string? detailPath,
        string title,
        AppRecommendationIdentity.Keys? precomputedKeys,
        out AppRecommendationIdentity.Keys resolvedKeys,
        out string canonicalKey)
    {
        resolvedKeys = precomputedKeys ?? AppRecommendationIdentity.ResolveKeys(detailPath, title);
        canonicalKey = resolvedKeys.CanonicalKey;

        if (resolvedKeys.PathKey is not null && _seenPaths.Contains(resolvedKeys.PathKey))
        {
            return false;
        }

        if (resolvedKeys.TargetKey is not null && _seenTargets.Contains(resolvedKeys.TargetKey))
        {
            return false;
        }

        if (resolvedKeys.NameKey.Length > 0 && _seenNames.Contains(resolvedKeys.NameKey))
        {
            return false;
        }

        if (resolvedKeys.PathKey is not null)
        {
            _seenPaths.Add(resolvedKeys.PathKey);
        }

        if (resolvedKeys.TargetKey is not null)
        {
            _seenTargets.Add(resolvedKeys.TargetKey);
        }

        if (resolvedKeys.NameKey.Length > 0)
        {
            _seenNames.Add(resolvedKeys.NameKey);
        }

        return true;
    }
}
