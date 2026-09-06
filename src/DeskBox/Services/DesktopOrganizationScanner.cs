using DeskBox.Models;

namespace DeskBox.Services;

public sealed class DesktopOrganizationScanner
{
    // Files up to and including 100 MiB are eligible for quick organization.
    // Keep the comparison in CreateSnapshot strictly greater-than so the
    // advertised limit is inclusive for the boundary file.
    public const long SlowItemThresholdBytes = 100L * 1024 * 1024;
    public const long QuickBatchSizeLimitBytes = 100L * 1024 * 1024;
    public const int QuickBatchItemLimit = 200;

    private static readonly string[] TemporarySuffixes =
    [
        ".tmp",
        ".temp",
        ".part",
        ".partial",
        ".crdownload",
        ".download",
        ".opdownload",
        ".aria2",
        ".!ut",
        ".bc!"
    ];

    private readonly DesktopOrganizationClassifier _classifier;
    private readonly Func<string> _desktopPathProvider;
    private readonly Func<string> _publicDesktopPathProvider;

    public DesktopOrganizationScanner(
        DesktopOrganizationClassifier classifier,
        Func<string>? desktopPathProvider = null,
        Func<string>? publicDesktopPathProvider = null)
    {
        _classifier = classifier;
        _desktopPathProvider = desktopPathProvider ??
            (() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        _publicDesktopPathProvider = publicDesktopPathProvider ??
            (() => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
    }

    public Task<DesktopOrganizationScanResult> ScanAsync(
        bool includeSlowItems = false,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Scan(includeSlowItems, cancellationToken), cancellationToken);
    }

    internal DesktopOrganizationFileSnapshot CreateAutoOrganizationSnapshot(string path) =>
        CreateSnapshot(
            path,
            NormalizeOptionalPath(_publicDesktopPathProvider()),
            includeSlowItems: false);

    private DesktopOrganizationScanResult Scan(bool includeSlowItems, CancellationToken cancellationToken)
    {
        string desktopPath = Path.GetFullPath(_desktopPathProvider());
        string publicDesktopPath = NormalizeOptionalPath(_publicDesktopPathProvider());
        var items = new List<DesktopOrganizationFileSnapshot>();

        ScanSource(desktopPath, DesktopOrganizationSourceScope.Personal);
        bool publicUnavailable = string.IsNullOrWhiteSpace(publicDesktopPath);
        if (!publicUnavailable && !string.Equals(
                Path.TrimEndingDirectorySeparator(desktopPath),
                Path.TrimEndingDirectorySeparator(publicDesktopPath),
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                publicUnavailable = !Directory.Exists(publicDesktopPath);
                ScanSource(publicDesktopPath, DesktopOrganizationSourceScope.Public);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                publicUnavailable = true;
                App.Log($"[DesktopOrganization] Public desktop scan unavailable: {ex.Message}");
            }
        }

        return new DesktopOrganizationScanResult
        {
            DesktopPath = desktopPath,
            PublicDesktopPath = publicDesktopPath,
            PublicDesktopUnavailable = publicUnavailable,
            Items = items
        };

        void ScanSource(string root, DesktopOrganizationSourceScope scope)
        {
            if (!Directory.Exists(root)) return;
            var sourceItems = new List<DesktopOrganizationFileSnapshot>();
            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Manual scanning can opt into public items. Auto organization
                // continues to pass the public root to CreateSnapshot and excludes it.
                sourceItems.Add(CreateSnapshot(path, string.Empty, includeSlowItems) with { SourceScope = scope });
            }
            if (!includeSlowItems) ApplyQuickBatchLimit(sourceItems);
            items.AddRange(sourceItems);
        }
    }

    private static void ApplyQuickBatchLimit(
        IList<DesktopOrganizationFileSnapshot> items)
    {
        long acceptedBytes = 0;
        int acceptedCount = 0;
        foreach (int index in items
                     .Select((item, index) => new { item, index })
                     .Where(entry => entry.item.IsEligible)
                     .OrderBy(entry => entry.item.Size)
                     .ThenBy(entry => entry.item.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(entry => entry.index))
        {
            DesktopOrganizationFileSnapshot item = items[index];
            if (acceptedCount >= QuickBatchItemLimit ||
                acceptedBytes + item.Size > QuickBatchSizeLimitBytes)
            {
                items[index] = item with
                {
                    ExclusionReason = DesktopOrganizationExclusionReason.BatchLimit
                };
                continue;
            }

            acceptedCount++;
            acceptedBytes += item.Size;
        }
    }

    private DesktopOrganizationFileSnapshot CreateSnapshot(
        string path,
        string publicDesktopPath,
        bool includeSlowItems)
    {
        string fullPath = Path.GetFullPath(path);
        string name = Path.GetFileName(fullPath);
        var classification = _classifier.Classify(fullPath);
        long size = 0;
        DateTime lastWriteTimeUtc = DateTime.MinValue;
        DesktopOrganizationExclusionReason reason;
        bool isDirectory = false;

        try
        {
            FileAttributes attributes = File.GetAttributes(fullPath);
            isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isHiddenOrSystem = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                                    name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
            bool isTemporary = (attributes & FileAttributes.Temporary) != 0 ||
                               HasTemporarySuffix(name);

            // Safety classifications take precedence over the optional folder
            // classification. A junction, hidden system folder, placeholder,
            // or temporary directory must never become selectable merely
            // because it also carries the Directory attribute.
            reason = isHiddenOrSystem
                ? DesktopOrganizationExclusionReason.HiddenOrSystem
                : (attributes & FileAttributes.ReparsePoint) != 0
                    ? DesktopOrganizationExclusionReason.ReparsePoint
                    : (attributes & FileAttributes.Offline) != 0
                        ? DesktopOrganizationExclusionReason.OfflinePlaceholder
                        : isTemporary
                            ? DesktopOrganizationExclusionReason.TemporaryOrDownloading
                            : IsUnderPath(fullPath, publicDesktopPath)
                                ? DesktopOrganizationExclusionReason.PublicDesktopItem
                                : isDirectory
                                    ? DesktopOrganizationExclusionReason.Folder
                                    : DesktopOrganizationExclusionReason.None;

            if (!isDirectory)
            {
                var file = new FileInfo(fullPath);
                size = file.Length;
                lastWriteTimeUtc = file.LastWriteTimeUtc;
                if (reason == DesktopOrganizationExclusionReason.None &&
                    !includeSlowItems &&
                    size > SlowItemThresholdBytes)
                {
                    reason = DesktopOrganizationExclusionReason.SlowItem;
                }
            }
            else
            {
                lastWriteTimeUtc = Directory.GetLastWriteTimeUtc(fullPath);
            }
        }
        catch
        {
            reason = DesktopOrganizationExclusionReason.Unavailable;
        }

        return new DesktopOrganizationFileSnapshot(
            fullPath,
            name,
            classification.Extension,
            size,
            lastWriteTimeUtc,
            classification.CategoryId,
            classification.SubtypeId,
            reason,
            isDirectory);
    }

    private static bool HasTemporarySuffix(string name) =>
        TemporarySuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeOptionalPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static bool IsUnderPath(string candidate, string parent)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return false;
        }

        string normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(
            $"{normalizedParent}{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
    }
}
