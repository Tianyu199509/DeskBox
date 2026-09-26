using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using System.Reflection;

namespace DeskBox.Tests;

/// <summary>
/// Legislates the residency roadmap's "one content mode" contract
/// (widget-group-residency-roadmap-20260918 section 4.6, item 4): every
/// widget member content must be an adapter that owns its view model and
/// a lazy leaf view. Non-member host contents live on an explicit
/// allowlist, and "control is the content" (View => this) is a violation
/// wherever it appears.
/// </summary>
public sealed class WidgetContentModeContractTests
{
    private static readonly string[] AllowlistNonAdapterContents =
    [
        nameof(ExistingWidgetContent),
        nameof(PlaceholderWidgetContent),
    ];

    [Fact]
    public void EveryWidgetContentIsAnAdapterOrOnTheExplicitAllowlist()
    {
        Type[] types;
        try
        {
            types = typeof(WidgetContentAdapterBase).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(type => type is not null).Select(type => type!).ToArray();
        }

        List<string> offenders = types
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                typeof(IWidgetContent).IsAssignableFrom(type))
            .Where(type => !typeof(WidgetContentAdapterBase).IsAssignableFrom(type))
            .Where(type => !AllowlistNonAdapterContents.Contains(type.Name))
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "IWidgetContent implementations must derive from " +
            "WidgetContentAdapterBase (or join the explicit non-member " +
            $"allowlist). Offenders: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void AllowlistedNonAdapterContentsAreStillExplicit()
    {
        // The allowlist must name real types; a rename that silently empties
        // the list would weaken the contract above.
        foreach (string allowlisted in AllowlistNonAdapterContents)
        {
            Assert.Contains(
                typeof(WidgetContentAdapterBase).Assembly.GetTypes(),
                type => type.Name == allowlisted);
        }
    }

    [Fact]
    public async Task NoWidgetContentReturnsItselfAsTheView()
    {
        string srcRoot = TestPaths.FromRepository("src/DeskBox");
        List<string> offenders = [];
        foreach (string path in Directory.EnumerateFiles(
                     srcRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string source = await File.ReadAllTextAsync(path);
            if (source.Contains("View => this", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(
                    TestPaths.FromRepository("."),
                    path));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A widget content must never be its own view (View => this); " +
            $"offenders: {string.Join(", ", offenders)}");
    }
}
