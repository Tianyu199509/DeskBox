namespace DeskBox.Services;

/// <summary>
/// Extensions whose Shell class registers a create-a-copy default verb — the
/// Office Open XML template family, its legacy binary predecessors and the
/// OpenDocument templates. Windows resolves a NULL verb (the desktop
/// double-click dispatch) to that "New" verb, so double-clicking one of these
/// files produces a fresh document instead of opening the template itself.
/// An explicit "open" verb forces the template open as a regular document,
/// and saving then overwrites the template file (feedback 366 / confirmed 81),
/// so these extensions must always be dispatched with the default verb.
/// </summary>
/// <remarks>
/// Keyed on the dispatched path's extension rather than the ProgID: Office
/// renames template ProgIDs between releases (Excel.Template,
/// Excel.Template.12, ...), while the extensions are stable. Deliberately
/// excluded: <c>.xlam</c> (add-in, default verb is open), <c>.thmx</c> (theme,
/// open semantics) and <c>.vst</c> (legacy Visio template that doubles as a
/// Targa image extension — too ambiguous to route blindly).
/// </remarks>
internal static class TemplateDocumentVerbPolicy
{
    private static readonly HashSet<string> TemplateExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Office Open XML templates.
            ".xltx", ".xltm",
            ".dotx", ".dotm",
            ".potx", ".potm",
            ".vstx", ".vstm",
            // Legacy binary Office templates.
            ".xlt", ".dot",
            // OpenDocument templates.
            ".ots", ".ott"
        };

    /// <summary>
    /// Whether <paramref name="path"/> names a template document that must be
    /// launched with its default ("New") verb instead of an explicit "open".
    /// </summary>
    internal static bool IsTemplateDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string extension = Path.GetExtension(path);
        return extension.Length != 0 && TemplateExtensions.Contains(extension);
    }
}
