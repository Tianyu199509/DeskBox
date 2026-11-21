namespace DeskBox.Helpers;

/// <summary>
/// Marks materialized drop files with the Mark of the Web. Files DeskBox
/// writes to disk from a drag source (browser FileContents streams, bitmaps,
/// URL downloads) would otherwise carry no Zone.Identifier, so SmartScreen,
/// Protected View and similar heuristics would treat web-sourced executables
/// as ordinary local files - a safety downgrade versus downloading the same
/// file in a browser. Failures are logged and never fatal: the import itself
/// must not break over an optional security annotation.
/// </summary>
internal static class ZoneIdentifierWriter
{
    internal const int InternetZone = 3;
    internal const string VirtualDropSource = "deskbox://drag-drop/virtual";
    private static readonly System.Text.UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes a Zone.Identifier alternate stream with the internet zone and,
    /// when known, the originating host. Callers pass the concrete source
    /// (e.g. the downloaded URL); virtual browser drops have no retrievable
    /// origin and use the generic marker.
    /// </summary>
    internal static bool TryMarkFile(string path, string? source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                path + ":Zone.Identifier",
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            using var writer = new StreamWriter(
                stream,
                s_utf8NoBom,
                bufferSize: 128,
                leaveOpen: true);
            writer.NewLine = "\r\n";
            writer.WriteLine("[ZoneTransfer]");
            writer.WriteLine($"ZoneId={InternetZone}");
            if (!string.IsNullOrWhiteSpace(source))
            {
                string sanitizedSource = source
                    .Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", string.Empty, StringComparison.Ordinal)
                    .Trim();
                if (!string.IsNullOrEmpty(sanitizedSource))
                {
                    writer.WriteLine($"HostUrl={sanitizedSource}");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[DropTarget] Failed to mark web zone on '{path}': {ex.Message}");
            return false;
        }
    }
}
