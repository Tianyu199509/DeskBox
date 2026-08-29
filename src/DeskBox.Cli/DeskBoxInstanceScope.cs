using System.Security.Cryptography;
using System.Text;

namespace DeskBox.Cli;

/// <summary>
/// Resolves the instance scope exactly the way
/// <c>DeskBoxDataPathService</c> does for a production root, without loading
/// any DeskBox assembly: development and preview roots hash their path, the
/// production root uses the fixed scope constant.
/// </summary>
public static class DeskBoxInstanceScope
{
    private const string ProductionInstanceScope = "7F3A9B2E";

    public static string Resolve(string rootPath)
    {
        string productionRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox"));
        string normalizedRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath)
            ? productionRoot
            : rootPath.Trim());
        bool isDevelopmentRoot = !string.Equals(
            normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            productionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        if (!isDevelopmentRoot)
        {
            return ProductionInstanceScope;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot.ToUpperInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
