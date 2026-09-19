namespace DeskBox.Services;

/// <summary>
/// Independently toggleable cloud-backup domains (roadmap §10). Each flag
/// owns a disjoint set of data-directory paths; the widget-style domain
/// carries no files and is delivered as a projection document instead.
/// </summary>
[Flags]
public enum CloudBackupDomain
{
    None = 0,
    TodoData = 1,
    QuickCaptureData = 2,
    WidgetStyle = 4
}

/// <summary>
/// Single source of truth for which data-relative paths belong to which
/// cloud-backup domain, used by both the scoped export filter and the
/// scoped restore overlay so they can never disagree.
///
/// Path ownership rules (see roadmap §10):
///   TodoData         — data/widgets/&lt;id&gt;/todo.json only. The widget's
///                      attachments/ subtree is deliberately NOT in the
///                      domain for now: uploads have no size bound, so one
///                      large attached file would ship whole. Excluding at
///                      domain level (not export-filter level) also keeps
///                      restore semantics right — the snapshot-faithful
///                      delete phase only touches in-domain files, so a
///                      restore never wipes the user's live attachments.
///                      todo.json still carries attachment FilePaths; they
///                      simply dangle on a machine that lacks the files.
///   QuickCaptureData — data/quick-capture/quick-capture.json only;
///                      attachments/ excluded for the same reason
///                      (thumbnails/ and exports/ are derived artifacts and
///                      stay out either way).
///   WidgetStyle      — no data files; carried as widget-style.json.
///
/// Everything else — settings.json, widget-layout.json (device-local layout
/// state), FileSafety history/journal, sidecars, caches, device.id,
/// file-widget contents (which only ever hold path references anyway) — is
/// never part of a scoped cloud backup.
/// </summary>
internal static class CloudBackupDomains
{
    internal const string BackupKind = "cloud-backup";
    internal const string WidgetStyleEntryName = "widget-style.json";

    internal static readonly CloudBackupDomain[] FileDomains =
    [
        CloudBackupDomain.TodoData,
        CloudBackupDomain.QuickCaptureData
    ];

    internal static bool IsInDomain(CloudBackupDomain domain, string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        return domain switch
        {
            CloudBackupDomain.TodoData => IsTodoDataPath(normalized),
            CloudBackupDomain.QuickCaptureData => IsQuickCaptureDataPath(normalized),
            // WidgetStyle owns no data-directory files.
            _ => false
        };
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> belongs to ANY enabled
    /// domain in <paramref name="scope"/> — the export-time filter.
    /// </summary>
    internal static bool IsInScope(CloudBackupDomain scope, string relativePath)
    {
        foreach (CloudBackupDomain domain in FileDomains)
        {
            if (scope.HasFlag(domain) && IsInDomain(domain, relativePath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Manifest wire name for a domain (stable, additive).</summary>
    internal static string ToManifestName(CloudBackupDomain domain) => domain switch
    {
        CloudBackupDomain.TodoData => "todo-data",
        CloudBackupDomain.QuickCaptureData => "quick-capture-data",
        CloudBackupDomain.WidgetStyle => "widget-style",
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, null)
    };

    internal static bool TryFromManifestName(string name, out CloudBackupDomain domain)
    {
        domain = name switch
        {
            "todo-data" => CloudBackupDomain.TodoData,
            "quick-capture-data" => CloudBackupDomain.QuickCaptureData,
            "widget-style" => CloudBackupDomain.WidgetStyle,
            _ => CloudBackupDomain.None
        };
        return domain != CloudBackupDomain.None;
    }

    internal static string[] ToManifestNames(CloudBackupDomain scope)
    {
        var names = new List<string>(3);
        foreach (CloudBackupDomain domain in Enum.GetValues<CloudBackupDomain>())
        {
            if (domain != CloudBackupDomain.None && scope.HasFlag(domain))
            {
                names.Add(ToManifestName(domain));
            }
        }

        return names.ToArray();
    }

    internal static CloudBackupDomain FromManifestNames(IEnumerable<string>? names)
    {
        var scope = CloudBackupDomain.None;
        if (names is null)
        {
            return scope;
        }

        foreach (string name in names)
        {
            if (TryFromManifestName(name, out CloudBackupDomain domain))
            {
                scope |= domain;
            }
        }

        return scope;
    }

    // widgets/<id>/todo.json — the todo store, keyed off the widget dir.
    // attachments/ stays out of the domain until upload size bounds land.
    private static bool IsTodoDataPath(string normalizedPath)
    {
        const string prefix = "widgets/";
        if (!normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = normalizedPath[prefix.Length..];
        int slash = rest.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        string tail = rest[(slash + 1)..];
        return tail.Equals("todo.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuickCaptureDataPath(string normalizedPath) =>
        normalizedPath.Equals("quick-capture/quick-capture.json", StringComparison.OrdinalIgnoreCase);
}
