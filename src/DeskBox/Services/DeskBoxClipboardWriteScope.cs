namespace DeskBox.Services;

public static class DeskBoxClipboardWriteScope
{
    private static readonly TimeSpan s_ignoreWindow = TimeSpan.FromSeconds(2);
    private static readonly object s_gate = new();

    private static DateTimeOffset s_lastWriteUtc;
    private static string? s_lastText;
    private static bool s_lastWriteHasImage;
    private static string[] s_lastPaths = [];

    public static void MarkWrite(
        string? text = null,
        bool hasImage = false,
        IEnumerable<string>? paths = null)
    {
        string? normalizedText = NormalizeText(text);
        string[] normalizedPaths = paths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        lock (s_gate)
        {
            s_lastWriteUtc = DateTimeOffset.UtcNow;
            s_lastText = normalizedText;
            s_lastPaths = normalizedPaths;
            s_lastWriteHasImage = hasImage || normalizedPaths.Any(IsImageFile);
        }
    }

    public static void MarkText(string? text)
    {
        MarkWrite(text: text);
    }

    public static bool ShouldIgnore(QuickCaptureClipboardContent content)
    {
        if (content.HasImage && ShouldIgnoreImage())
        {
            return true;
        }

        return ShouldIgnoreText(content.Text);
    }

    public static bool ShouldIgnoreText(string? text)
    {
        string? normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return false;
        }

        ClipboardWriteSnapshot snapshot = GetFreshSnapshot();
        if (!snapshot.IsFresh)
        {
            return false;
        }

        if (string.Equals(snapshot.Text, normalizedText, StringComparison.Ordinal))
        {
            return true;
        }

        if (snapshot.Paths.Length == 0)
        {
            return false;
        }

        string[] textPaths = normalizedText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        return textPaths.Length > 0 &&
               textPaths.Length == snapshot.Paths.Length &&
               textPaths.All(path => snapshot.Paths.Contains(path, StringComparer.OrdinalIgnoreCase));
    }

    internal static void ClearForTesting()
    {
        lock (s_gate)
        {
            s_lastWriteUtc = default;
            s_lastText = null;
            s_lastWriteHasImage = false;
            s_lastPaths = [];
        }
    }

    private static bool ShouldIgnoreImage()
    {
        ClipboardWriteSnapshot snapshot = GetFreshSnapshot();
        return snapshot.IsFresh && snapshot.HasImage;
    }

    private static ClipboardWriteSnapshot GetFreshSnapshot()
    {
        lock (s_gate)
        {
            bool isFresh = DateTimeOffset.UtcNow - s_lastWriteUtc <= s_ignoreWindow;
            return new ClipboardWriteSnapshot(isFresh, s_lastText, s_lastWriteHasImage, s_lastPaths);
        }
    }

    private static string? NormalizeText(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? null
            : text.Trim();
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static bool IsImageFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct ClipboardWriteSnapshot(
        bool IsFresh,
        string? Text,
        bool HasImage,
        string[] Paths);
}
