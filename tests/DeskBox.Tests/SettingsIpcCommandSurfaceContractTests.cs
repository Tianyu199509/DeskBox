namespace DeskBox.Tests;

using System.Text.RegularExpressions;
using DeskBox.Services;

/// <summary>
/// Ratchet for the settings-IPC command surface (deep review R13): every
/// request-type method constant declared in <c>SettingsIpcMethods</c> must
/// be (a) routed by the main-process <c>SettingsIpcMethodRouter</c> and
/// (b) referenced by production code outside the router and protocol files
/// (i.e., something sends it), unless explicitly allowlisted. Event and
/// host-push channels bypass the router by design and are listed verbatim;
/// each of those must still be referenced somewhere in production code.
/// This pins the two failure families the R13 census found live: a command
/// whose sender was retired while its router case lingered
/// (widget.is-feature-enabled), and router cases wired for commands no
/// host facade ever sends (app.schedule-*-cleanup).
/// </summary>
public sealed class SettingsIpcCommandSurfaceContractTests
{
    // Channels that never hit the main-process router by design: main→host
    // pushes (sent through the server's event queue / coordinator) and
    // host→main notifications. nameof() keeps this list compile-safe.
    private static readonly HashSet<string> NonRoutedChannels =
    [
        nameof(SettingsIpcMethods.SettingsReloaded),
        nameof(SettingsIpcMethods.LayoutChanged),
        nameof(SettingsIpcMethods.HotkeyRegistrationChanged),
        nameof(SettingsIpcMethods.AppDisplayTopologyChanged),
        nameof(SettingsIpcMethods.AppScreenHomeChanged),
        nameof(SettingsIpcMethods.BackupUploadCompleted),
        nameof(SettingsIpcMethods.UpdateCheckCompleted),
        nameof(SettingsIpcMethods.UpdateDownloadProgress),
        nameof(SettingsIpcMethods.UpdateDownloadCompleted),
        nameof(SettingsIpcMethods.WidgetMigrationProgress),
        nameof(SettingsIpcMethods.HostShow),
        nameof(SettingsIpcMethods.HostShowGlance),
        nameof(SettingsIpcMethods.HostUpdateStateRefresh),
        nameof(SettingsIpcMethods.HostQueueUpdateResult),
        nameof(SettingsIpcMethods.HostDesktopOrgRefresh),
        nameof(SettingsIpcMethods.HostOneClickUpdate),
        nameof(SettingsIpcMethods.HostShutdown),
    ];

    // Request-type constants allowed to lack a production sender.
    private static readonly HashSet<string> SenderAllowlist =
    [
        // Connectivity probe: used by transport tests and pipe diagnostics.
        nameof(SettingsIpcMethods.Ping),
    ];

    private static readonly Regex ConstantPattern =
        new(@"public\s+const\s+string\s+(\w+)\s*=\s*""[^""]+""", RegexOptions.Compiled);

    [Fact]
    public void EveryRequestConstant_IsRoutedAndSent()
    {
        string root = FindRepositoryRoot();
        string protocolSource = File.ReadAllText(Path.Combine(
            root, "src/DeskBox/Services/SettingsIpcProtocol.cs"));
        string routerSource = File.ReadAllText(Path.Combine(
            root, "src/DeskBox/Services/SettingsIpcMethodRouter.cs"));

        List<string> constants = ConstantPattern.Matches(protocolSource)
            .Select(match => match.Groups[1].Value)
            // The envelope's three Type* constants are not method names.
            .Where(name => !name.StartsWith("Type", StringComparison.Ordinal))
            .ToList();

        // Mass-deletion guard: the surface legitimately shrinks only through
        // deliberate review; anything below this floor is an accident.
        Assert.True(constants.Count >= 120, $"command surface shrank to {constants.Count}");

        var senderSources = Directory.EnumerateFiles(
                Path.Combine(root, "src/DeskBox"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !Path.GetFileName(path).Equals("SettingsIpcMethodRouter.cs", StringComparison.Ordinal) &&
                !Path.GetFileName(path).Equals("SettingsIpcProtocol.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();
        string senders = string.Join("\n", senderSources);

        var unrouted = new List<string>();
        var unsent = new List<string>();
        foreach (string name in constants)
        {
            if (NonRoutedChannels.Contains(name))
            {
                if (!senders.Contains($"SettingsIpcMethods.{name}", StringComparison.Ordinal))
                {
                    unsent.Add(name);
                }

                continue;
            }

            if (!routerSource.Contains($"SettingsIpcMethods.{name}", StringComparison.Ordinal))
            {
                unrouted.Add(name);
            }

            if (!SenderAllowlist.Contains(name) &&
                !senders.Contains($"SettingsIpcMethods.{name}", StringComparison.Ordinal))
            {
                unsent.Add(name);
            }
        }

        Assert.True(unrouted.Count == 0,
            "request constants missing a router case (they would answer " +
            "'unavailable' at runtime): " + string.Join(", ", unrouted));
        Assert.True(unsent.Count == 0,
            "constants nothing sends or receives (dead wire — remove them " +
            "or list them in NonRoutedChannels/SenderAllowlist with a " +
            "reason): " + string.Join(", ", unsent));
    }

    [Fact]
    public void NonRoutedChannelList_MatchesDeclaredConstants()
    {
        string root = FindRepositoryRoot();
        string protocolSource = File.ReadAllText(Path.Combine(
            root, "src/DeskBox/Services/SettingsIpcProtocol.cs"));

        HashSet<string> declared = ConstantPattern.Matches(protocolSource)
            .Select(match => match.Groups[1].Value)
            .Where(name => !name.StartsWith("Type", StringComparison.Ordinal))
            .ToHashSet();

        Assert.Subset(declared, NonRoutedChannels);
        Assert.Subset(declared, SenderAllowlist);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DeskBox.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
    }
}
