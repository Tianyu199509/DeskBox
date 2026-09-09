using System.Text.Json;

namespace DeskBox.GlancePackage.Services;

/// <summary>
/// Package-local persistence primitives: atomic writes (temp + replace with
/// a .bak kept) and resilient reads (primary, then .bak). Mirrors the
/// durability the host's stores rely on so package-owned files never lose
/// user data to a torn write (audit round 18).
/// </summary>
internal static class PackageFileStore
{
    internal static string? TryReadText(string path)
    {
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                string content = File.ReadAllText(candidate);
                using JsonDocument document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                return content;
            }
            catch
            {
                // Torn/corrupt candidate: fall through to the backup.
            }
        }
        return null;
    }

    internal static void WriteAtomically(string path, Action<Utf8JsonWriter> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            write(writer);
        }
        if (File.Exists(path))
        {
            File.Replace(temp, path, path + ".bak");
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
