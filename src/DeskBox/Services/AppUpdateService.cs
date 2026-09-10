using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed partial class AppUpdateService : IAppUpdateService
{
    public const string DefaultManifestUrl = "https://deskbox.fun/update/stable.json";
    public const string DefaultGitHubLatestReleaseApiUrl = "https://api.github.com/repos/Tianyu199509/DeskBox/releases/latest";
    public const string DefaultManualDownloadUrl = "https://deskbox.fun/download";

    private readonly HttpClient _httpClient;
    private readonly string _manifestUrl;
    private readonly string _githubLatestReleaseApiUrl;
    private readonly string _updateRootPath;

    public AppUpdateService(
        HttpClient? httpClient = null,
        string? manifestUrl = null,
        string? updateRootPath = null,
        string? githubLatestReleaseApiUrl = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskBox", GetCurrentAppVersion()));

        _manifestUrl = string.IsNullOrWhiteSpace(manifestUrl)
            ? DefaultManifestUrl
            : manifestUrl;
        _githubLatestReleaseApiUrl = string.IsNullOrWhiteSpace(githubLatestReleaseApiUrl)
            ? DefaultGitHubLatestReleaseApiUrl
            : githubLatestReleaseApiUrl;
        _updateRootPath = string.IsNullOrWhiteSpace(updateRootPath)
            ? DeskBoxDataPathService.Current.UpdatesDirectory
            : updateRootPath;
    }

    public AppUpdateCheckResult? LastCheckResult { get; private set; }
    public DateTime? LastCheckTimeUtc { get; private set; }
    public event Action<AppUpdateCheckResult>? CheckCompleted;

    public string ManifestUrl => _manifestUrl;
    public AppUpdateDeliveryKind DeliveryKind => AppUpdateDeliveryKind.DirectInstaller;
    internal static string CurrentInstallerArchitectureSuffix =>
        GetInstallerArchitectureSuffix(RuntimeInformation.ProcessArchitecture);

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        return await CheckForUpdatesAsync(GetCurrentAppVersion(), cancellationToken);
    }

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var manifestResult = await CheckManifestForUpdatesAsync(currentVersion, cancellationToken);
        if (manifestResult.Status is AppUpdateCheckStatus.UpdateAvailable or AppUpdateCheckStatus.UpToDate)
        {
            return SetLastCheckResult(manifestResult);
        }

        var githubResult = await CheckGitHubLatestReleaseForUpdatesAsync(currentVersion, cancellationToken);
        if (githubResult.Status is AppUpdateCheckStatus.UpdateAvailable or AppUpdateCheckStatus.UpToDate)
        {
            return SetLastCheckResult(githubResult);
        }

        return SetLastCheckResult(manifestResult);
    }

    private async Task<AppUpdateCheckResult> CheckManifestForUpdatesAsync(string currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(_manifestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync(
                stream,
                AppUpdateJsonContext.Default.UpdateManifest,
                cancellationToken);
            if (manifest is null ||
                !TrySelectInstallerForArchitecture(
                    manifest,
                    CurrentInstallerArchitectureSuffix) ||
                !IsManifestUsable(manifest) ||
                string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                return new AppUpdateCheckResult(
                    AppUpdateCheckStatus.InvalidManifest,
                    currentVersion,
                    manifest,
                    "The update manifest is missing installer metadata for this processor architecture.");
            }

            return IsRemoteVersionNewer(currentVersion, manifest.Version)
                ? new AppUpdateCheckResult(AppUpdateCheckStatus.UpdateAvailable, currentVersion, manifest)
                : new AppUpdateCheckResult(AppUpdateCheckStatus.UpToDate, currentVersion, manifest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return new AppUpdateCheckResult(AppUpdateCheckStatus.Failed, currentVersion, errorMessage: ex.Message);
        }
    }

    private async Task<AppUpdateCheckResult> CheckGitHubLatestReleaseForUpdatesAsync(string currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(_githubLatestReleaseApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await JsonSerializer.DeserializeAsync(
                stream,
                AppUpdateJsonContext.Default.GitHubRelease,
                cancellationToken);
            var manifest = await CreateManifestFromGitHubReleaseAsync(release, cancellationToken);
            if (!IsManifestUsable(manifest) || string.IsNullOrWhiteSpace(manifest!.Sha256))
            {
                return new AppUpdateCheckResult(
                    AppUpdateCheckStatus.InvalidManifest,
                    currentVersion,
                    manifest,
                    "The GitHub release metadata is missing required fields.");
            }

            return IsRemoteVersionNewer(currentVersion, manifest.Version)
                ? new AppUpdateCheckResult(AppUpdateCheckStatus.UpdateAvailable, currentVersion, manifest)
                : new AppUpdateCheckResult(AppUpdateCheckStatus.UpToDate, currentVersion, manifest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return new AppUpdateCheckResult(AppUpdateCheckStatus.Failed, currentVersion, errorMessage: ex.Message);
        }
    }

    public async Task<AppUpdateDownloadResult> DownloadUpdateAsync(
        AppUpdateManifest manifest,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TrySelectInstallerForArchitecture(
                manifest,
                CurrentInstallerArchitectureSuffix) ||
            !IsManifestUsable(manifest))
        {
            return AppUpdateDownloadResult.Failed(AppUpdateDownloadFailureKind.InvalidManifest);
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            return AppUpdateDownloadResult.Failed(AppUpdateDownloadFailureKind.HashMissing);
        }

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri))
        {
            return AppUpdateDownloadResult.Failed(AppUpdateDownloadFailureKind.InvalidManifest);
        }

        // HttpClient only transports HTTP(S). Any other absolute scheme
        // (file:, ftp:, ms-windows-store:, ...) makes GetAsync throw
        // NotSupportedException, which is outside this method's catch
        // filters and would escape into the async-void UI caller.
        if (downloadUri.Scheme is not ("http" or "https"))
        {
            return AppUpdateDownloadResult.Failed(
                AppUpdateDownloadFailureKind.InvalidManifest,
                "The update download URL must use HTTP or HTTPS.");
        }

        if (!IsInstallerDownloadCompatibleWithArchitecture(
                manifest.DownloadUrl,
                CurrentInstallerArchitectureSuffix))
        {
            return AppUpdateDownloadResult.Failed(
                AppUpdateDownloadFailureKind.InvalidManifest,
                "The installer architecture does not match this DeskBox process.");
        }

        if (!IsSafeVersionSegment(manifest.Version))
        {
            // A version of "." or ".." would move targetDirectory outside
            // the update root even though SanitizeFileName leaves it intact.
            return AppUpdateDownloadResult.Failed(
                AppUpdateDownloadFailureKind.InvalidManifest,
                "The update manifest version is not a safe path segment.");
        }

        string targetDirectory = Path.Combine(_updateRootPath, SanitizePathSegment(manifest.Version));
        string fileName = GetInstallerFileName(downloadUri, manifest.Version);
        string targetPath = Path.Combine(targetDirectory, fileName);
        string tempPath = targetPath + ".tmp";

        try
        {
            Directory.CreateDirectory(targetDirectory);
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            using var response = await _httpClient.GetAsync(
                downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is null && manifest.Size > 0)
            {
                totalBytes = manifest.Size;
            }

            long bytesReceived = 0;
            string actualSha256;
            await using (var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 128,
                useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[1024 * 128];
                int bytesRead;
                while ((bytesRead = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    hash.AppendData(buffer, 0, bytesRead);
                    bytesReceived += bytesRead;
                    progress?.Report(new AppUpdateDownloadProgress(bytesReceived, totalBytes));
                }

                await fileStream.FlushAsync(cancellationToken);
                actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
            }

            if (!string.Equals(actualSha256, NormalizeSha256(manifest.Sha256), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempPath);
                return AppUpdateDownloadResult.Failed(AppUpdateDownloadFailureKind.HashMismatch);
            }

            File.Move(tempPath, targetPath, overwrite: true);
            progress?.Report(new AppUpdateDownloadProgress(bytesReceived, totalBytes ?? bytesReceived));
            return AppUpdateDownloadResult.Completed(targetPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(tempPath);
            return AppUpdateDownloadResult.Failed(AppUpdateDownloadFailureKind.Cancelled);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            TryDelete(tempPath);
            return AppUpdateDownloadResult.Failed(AppUpdateDownloadFailureKind.Network, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            return AppUpdateDownloadResult.Failed(AppUpdateDownloadFailureKind.FileSystem, ex.Message);
        }
    }

    public AppUpdateInstallResult StartInstallerHelper(string installerPath, bool silent = true)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return AppUpdateInstallResult.Failed("The downloaded installer no longer exists.");
        }

        string? appPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath))
        {
            appPath = Path.Combine(AppContext.BaseDirectory, "DeskBox.exe");
        }

        if (!TryGetInstallDirectory(appPath, out string installDirectory))
        {
            return AppUpdateInstallResult.Failed(
                "DeskBox could not determine the current installation directory. Please use the manual installer instead.");
        }

        try
        {
            string helperPath = PrepareDetachedUpdaterHelper(AppContext.BaseDirectory, _updateRootPath);
            if (!File.Exists(helperPath))
            {
                return AppUpdateInstallResult.Failed("DeskBox.Updater.exe was not found in the application directory.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--installer");
            startInfo.ArgumentList.Add(installerPath);
            startInfo.ArgumentList.Add("--app");
            startInfo.ArgumentList.Add(appPath);
            startInfo.ArgumentList.Add("--install-dir");
            startInfo.ArgumentList.Add(installDirectory);
            if (silent)
            {
                startInfo.ArgumentList.Add("--silent");
            }

            Process.Start(startInfo);
            return AppUpdateInstallResult.Started();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return AppUpdateInstallResult.Failed(ex.Message);
        }
    }

    internal static bool TryGetInstallDirectory(string? appPath, out string installDirectory)
    {
        installDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath))
        {
            return false;
        }

        try
        {
            string fullAppPath = Path.GetFullPath(appPath);
            if (!string.Equals(
                    Path.GetFileName(fullAppPath),
                    "DeskBox.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? directory = Path.GetDirectoryName(fullAppPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            installDirectory = directory;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static string PrepareDetachedUpdaterHelper(string appDirectory, string updateRootPath)
    {
        string sourceHelperPath = Path.Combine(appDirectory, "DeskBox.Updater.exe");
        if (!File.Exists(sourceHelperPath))
        {
            return sourceHelperPath;
        }

        string helperRoot = Path.Combine(updateRootPath, "helper");

        // Clean up stale helper directories from previous updates.
        try
        {
            if (Directory.Exists(helperRoot))
            {
                foreach (var oldDir in Directory.GetDirectories(helperRoot))
                {
                    try
                    {
                        Directory.Delete(oldDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[Update] Failed to clean old helper directory '{oldDir}': {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Update] Failed to enumerate helper directories: {ex.Message}");
        }

        string helperDirectory = Path.Combine(
            helperRoot,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");
        Directory.CreateDirectory(helperDirectory);

        foreach (var sourcePath in Directory.EnumerateFiles(appDirectory, "DeskBox.Updater.*", SearchOption.TopDirectoryOnly))
        {
            string targetPath = Path.Combine(helperDirectory, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        return Path.Combine(helperDirectory, "DeskBox.Updater.exe");
    }

    public static string GetCurrentAppVersion()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+')[0] ??
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ??
            "0.0.0";
    }

    public static bool IsRemoteVersionNewer(string currentVersion, string remoteVersion)
    {
        if (!TryParseVersion(currentVersion, out var current) ||
            !TryParseVersion(remoteVersion, out var remote))
        {
            return false;
        }

        return remote > current;
    }

    public static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        int suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        return Version.TryParse(normalized, out version!);
    }

    public static bool IsManifestUsable(AppUpdateManifest? manifest)
    {
        return manifest is not null &&
            manifest.SchemaVersion == 1 &&
            !string.IsNullOrWhiteSpace(manifest.Version) &&
            Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out _);
    }

    internal static bool TrySelectInstallerForArchitecture(
        AppUpdateManifest manifest,
        string architectureSuffix)
    {
        if (string.Equals(architectureSuffix, "x64", StringComparison.OrdinalIgnoreCase))
        {
            return IsInstallerDownloadCompatibleWithArchitecture(
                manifest.DownloadUrl,
                architectureSuffix);
        }

        if (!string.Equals(architectureSuffix, "arm64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.Arm64DownloadUrl))
        {
            // Backward compatibility for an ARM64-only manifest that already
            // used the primary installer fields.
            return IsInstallerDownloadCompatibleWithArchitecture(
                manifest.DownloadUrl,
                architectureSuffix);
        }

        manifest.DownloadUrl = manifest.Arm64DownloadUrl;
        manifest.Sha256 = manifest.Arm64Sha256;
        manifest.Size = manifest.Arm64Size;
        return IsInstallerDownloadCompatibleWithArchitecture(
            manifest.DownloadUrl,
            architectureSuffix);
    }

    private async Task<AppUpdateManifest?> CreateManifestFromGitHubReleaseAsync(
        GitHubReleaseResponse? release,
        CancellationToken cancellationToken)
    {
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            return null;
        }

        var installerAsset = release.Assets.FirstOrDefault(IsInstallerAsset);
        if (installerAsset is null || string.IsNullOrWhiteSpace(installerAsset.BrowserDownloadUrl))
        {
            return null;
        }

        string sha256 = ExtractSha256FromDigest(installerAsset.Digest);
        if (string.IsNullOrWhiteSpace(sha256))
        {
            var shaAsset = release.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, installerAsset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
            sha256 = await DownloadSha256FromAssetAsync(shaAsset, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }

        string version = NormalizeVersionText(release.TagName);
        return new AppUpdateManifest
        {
            SchemaVersion = 1,
            Channel = "stable",
            Version = version,
            DownloadUrl = installerAsset.BrowserDownloadUrl,
            ManualDownloadUrl = DefaultManualDownloadUrl,
            MirrorUrl = DefaultManualDownloadUrl,
            Sha256 = sha256,
            Size = Math.Max(0, installerAsset.Size),
            ReleaseNotesUrl = release.HtmlUrl,
            ReleaseNotes = string.IsNullOrWhiteSpace(release.Body)
                ? []
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en-US"] = release.Body
                },
            Summary =
            {
                ["zh-CN"] = $"DeskBox {version} 已发布，可从 GitHub Releases 下载更新。",
                ["zh-TW"] = $"DeskBox {version} 已發布，可從 GitHub Releases 下載更新。",
                ["en-US"] = $"DeskBox {version} is available from GitHub Releases.",
                ["ja-JP"] = $"DeskBox {version} は GitHub Releases から入手できます。",
                ["de-DE"] = $"DeskBox {version} ist über GitHub Releases verfügbar.",
                ["pt-BR"] = $"O DeskBox {version} está disponível no GitHub Releases.",
                ["hi-IN"] = $"DeskBox {version} GitHub Releases से उपलब्ध है।",
                ["es-ES"] = $"DeskBox {version} está disponible en GitHub Releases.",
                ["fr-FR"] = $"DeskBox {version} est disponible sur GitHub Releases.",
                ["ar-SA"] = $"يتوفر DeskBox {version} في إصدارات GitHub.",
                ["bn-BD"] = $"DeskBox {version} GitHub Releases-এ পাওয়া যাচ্ছে।",
                ["ru-RU"] = $"DeskBox {version} доступен в GitHub Releases."
            }
        };
    }

    private async Task<string> DownloadSha256FromAssetAsync(GitHubReleaseAsset? shaAsset, CancellationToken cancellationToken)
    {
        if (shaAsset is null || string.IsNullOrWhiteSpace(shaAsset.BrowserDownloadUrl))
        {
            return string.Empty;
        }

        using var response = await _httpClient.GetAsync(shaAsset.BrowserDownloadUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ExtractSha256FromText(content);
    }

    private AppUpdateCheckResult SetLastCheckResult(AppUpdateCheckResult result)
    {
        LastCheckResult = result;
        LastCheckTimeUtc = DateTime.UtcNow;
        CheckCompleted?.Invoke(result);
        return result;
    }

    internal static string GetInstallerFileName(Uri downloadUri, string version)
    {
        string fileName = Path.GetFileName(downloadUri.LocalPath);
        return string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? $"DeskBox_Setup_{SanitizePathSegment(version)}_{CurrentInstallerArchitectureSuffix}.exe"
            : SanitizeFileName(fileName);
    }

    internal static string GetInstallerArchitectureSuffix(Architecture architecture)
    {
        return architecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => string.Empty
        };
    }

    /// <summary>
    /// The manifest version is used as a single directory segment under the
    /// update root. <see cref="SanitizeFileName"/> cannot reject "." or ".."
    /// because they contain no invalid characters, so callers that build a
    /// path from the version must use this check instead.
    /// </summary>
    internal static bool IsSafeVersionSegment(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        string trimmed = version.Trim();
        if (trimmed is "." or "..")
        {
            return false;
        }

        return trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               !trimmed.Contains('/') &&
               !trimmed.Contains('\\');
    }

    private static string SanitizePathSegment(string value)
    {
        string sanitized = SanitizeFileName(value);
        return string.IsNullOrWhiteSpace(sanitized) ? "latest" : sanitized;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
    }

    private static string NormalizeSha256(string sha256)
    {
        return sha256.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
    }

    private static bool IsInstallerAsset(GitHubReleaseAsset asset)
    {
        return IsInstallerAssetName(asset.Name, CurrentInstallerArchitectureSuffix);
    }

    internal static bool IsInstallerAssetName(string assetName, string architectureSuffix)
    {
        return !string.IsNullOrWhiteSpace(architectureSuffix) &&
            assetName.StartsWith("DeskBox_Setup_", StringComparison.OrdinalIgnoreCase) &&
            assetName.EndsWith($"_{architectureSuffix}.exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsInstallerDownloadCompatibleWithArchitecture(
        string downloadUrl,
        string architectureSuffix)
    {
        if (string.IsNullOrWhiteSpace(architectureSuffix) ||
            !Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string fileName = Path.GetFileName(uri.LocalPath);
        if (!fileName.StartsWith("DeskBox_Setup_", StringComparison.OrdinalIgnoreCase))
        {
            // Keep compatibility with a server endpoint that returns a
            // redirect or content-disposition filename instead of exposing
            // the final installer name in the URL.
            return true;
        }

        return fileName.EndsWith($"_{architectureSuffix}.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractSha256FromDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return string.Empty;
        }

        const string prefix = "sha256:";
        string value = digest.Trim();
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        return IsSha256Hex(value) ? NormalizeSha256(value) : string.Empty;
    }

    private static string ExtractSha256FromText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        foreach (var token in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = NormalizeSha256(token);
            if (IsSha256Hex(normalized))
            {
                return normalized;
            }
        }

        return string.Empty;
    }

    private static bool IsSha256Hex(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static string NormalizeVersionText(string value)
    {
        string normalized = value.Trim();
        return normalized.StartsWith('v') || normalized.StartsWith('V')
            ? normalized[1..]
            : normalized;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        public GitHubReleaseAsset[] Assets { get; set; } = [];
    }

    private sealed class GitHubReleaseAsset
    {
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        public long Size { get; set; }

        public string Digest { get; set; } = string.Empty;
    }

    [JsonSourceGenerationOptions(
        JsonSerializerDefaults.Web,
        GenerationMode = JsonSourceGenerationMode.Metadata,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(AppUpdateManifest), TypeInfoPropertyName = "UpdateManifest")]
    [JsonSerializable(typeof(GitHubReleaseResponse), TypeInfoPropertyName = "GitHubRelease")]
    private sealed partial class AppUpdateJsonContext : JsonSerializerContext
    {
    }
}
