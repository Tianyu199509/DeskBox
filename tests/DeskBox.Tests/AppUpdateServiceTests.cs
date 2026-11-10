using System.Net;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class AppUpdateServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void InstallerAssetSelection_UsesTheCurrentProcessArchitecture()
    {
        Assert.Equal("x64", AppUpdateService.GetInstallerArchitectureSuffix(Architecture.X64));
        Assert.Equal("arm64", AppUpdateService.GetInstallerArchitectureSuffix(Architecture.Arm64));
        Assert.True(AppUpdateService.IsInstallerAssetName("DeskBox_Setup_1.2.4_x64.exe", "x64"));
        Assert.False(AppUpdateService.IsInstallerAssetName("DeskBox_Setup_1.2.4_arm64.exe", "x64"));
        Assert.True(AppUpdateService.IsInstallerAssetName("DeskBox_Setup_1.2.4_arm64.exe", "arm64"));
        Assert.True(AppUpdateService.IsInstallerAssetName("DeskBox_Setup_1.4.8_x64.exe", "x64"));
        Assert.True(AppUpdateService.IsInstallerAssetName("DeskBox_Setup_1.4.8_arm64.exe", "arm64"));
        Assert.False(AppUpdateService.IsInstallerAssetName("DeskBox_Setup_1.4.3_x64_Full.exe", "x64"));
        Assert.False(AppUpdateService.IsInstallerAssetName("DeskBox_Setup_1.4.3_arm64_Full.exe", "arm64"));
        Assert.True(AppUpdateService.IsInstallerDownloadCompatibleWithArchitecture(
            "https://deskbox.fun/DeskBox_Setup_1.2.4_arm64.exe",
            "arm64"));
        Assert.False(AppUpdateService.IsInstallerDownloadCompatibleWithArchitecture(
            "https://deskbox.fun/DeskBox_Setup_1.2.4_x64.exe",
            "arm64"));
        Assert.True(AppUpdateService.IsInstallerDownloadCompatibleWithArchitecture(
            "https://example.com/download/latest",
            "arm64"));
        Assert.Equal(
            $"DeskBox_Setup_1.2.4_{AppUpdateService.CurrentInstallerArchitectureSuffix}.exe",
            AppUpdateService.GetInstallerFileName(new Uri("https://example.com/download/latest"), "1.2.4"));
    }

    [Fact]
    public void ManifestInstallerSelection_UsesIndependentArm64Metadata()
    {
        var manifest = new AppUpdateManifest
        {
            Version = "1.3.8",
            DownloadUrl = "https://deskbox.fun/DeskBox_Setup_1.3.8_x64.exe",
            Sha256 = new string('A', 64),
            Size = 101,
            Arm64DownloadUrl = "https://deskbox.fun/DeskBox_Setup_1.3.8_arm64.exe",
            Arm64Sha256 = new string('B', 64),
            Arm64Size = 202
        };

        Assert.True(AppUpdateService.TrySelectInstallerForArchitecture(manifest, "arm64"));
        Assert.EndsWith("_arm64.exe", manifest.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new string('B', 64), manifest.Sha256);
        Assert.Equal(202, manifest.Size);
    }

    [Fact]
    public void ManifestInstallerSelection_LeavesPrimaryMetadataForX64()
    {
        var manifest = new AppUpdateManifest
        {
            Version = "1.3.8",
            DownloadUrl = "https://deskbox.fun/DeskBox_Setup_1.3.8_x64.exe",
            Sha256 = new string('A', 64),
            Size = 101,
            Arm64DownloadUrl = "https://deskbox.fun/DeskBox_Setup_1.3.8_arm64.exe",
            Arm64Sha256 = new string('B', 64),
            Arm64Size = 202
        };

        Assert.True(AppUpdateService.TrySelectInstallerForArchitecture(manifest, "x64"));
        Assert.EndsWith("_x64.exe", manifest.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new string('A', 64), manifest.Sha256);
        Assert.Equal(101, manifest.Size);
    }

    [Fact]
    public void LocalizedSummary_PrefersEnglishWhenLocaleIsMissing()
    {
        var manifest = new AppUpdateManifest
        {
            Summary =
            {
                ["zh-CN"] = "中文摘要",
                ["zh-TW"] = "繁體摘要",
                ["en-US"] = "English summary"
            }
        };

        Assert.Equal("English summary", manifest.GetLocalizedSummary("de-DE"));
        Assert.Equal("中文摘要", manifest.GetLocalizedSummary("zh-CN"));
        Assert.Equal("繁體摘要", manifest.GetLocalizedSummary("zh-TW"));
        Assert.Equal("繁體摘要", manifest.GetLocalizedSummary("zh-MO"));
    }

    [Theory]
    [InlineData("https://pan.quark.cn/s/version-specific", "https://mirror.example.com/deskbox")]
    [InlineData("javascript:alert('unsafe')", "http://mirror.example.com/deskbox")]
    [InlineData("", "")]
    public void ManualUpdateDownloadUrl_AlwaysOpensOfficialDownloadPage(string manualUrl, string mirrorUrl)
    {
        var manifest = new AppUpdateManifest
        {
            ManualDownloadUrl = manualUrl,
            MirrorUrl = mirrorUrl
        };

        Assert.Equal(
            "https://deskbox.fun/download/",
            SettingsViewModel.GetManualUpdateDownloadUrl(manifest));
        Assert.Equal("https://deskbox.fun/download/", SettingsViewModel.GetManualUpdateDownloadUrl(null));
    }

    [Theory]
    [InlineData("1.2.1", "1.2.2", true)]
    [InlineData("1.2.1", "v1.2.2", true)]
    [InlineData("1.2.1", "1.2.2-beta.1", true)]
    [InlineData("1.2.1", "1.2.1", false)]
    [InlineData("1.2.1", "1.2.0", false)]
    [InlineData("1.2.1", "not-a-version", false)]
    public void IsRemoteVersionNewer_ComparesSemanticVersionPrefix(string currentVersion, string remoteVersion, bool expected)
    {
        Assert.Equal(expected, AppUpdateService.IsRemoteVersionNewer(currentVersion, remoteVersion));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReadsManifestAndDetectsUpdate()
    {
        using var httpClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "ScHeMaVeRsIoN": "1",
                  "version": "1.2.2",
                  "downloadUrl": "https://github.com/Tianyu199509/DeskBox/releases/download/v1.2.2/DeskBox_Setup_1.2.2_x64.exe",
                  "manualDownloadUrl": "https://pan.quark.cn/s/version-specific",
                  "sha256": "abc",
                  "SiZe": "1234",
                  "summary": {
                    "zh-CN": "修复多屏定位"
                  },
                  "futureManifestField": { "ignored": true }
                }
                """,
                Encoding.UTF8,
                "application/json")
        });

        var service = new AppUpdateService(httpClient, "https://deskbox.fun/update/stable.json", _tempRoot);

        var result = await service.CheckForUpdatesAsync("1.2.1");

        Assert.Equal(AppUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Manifest);
        Assert.Equal(1, result.Manifest.SchemaVersion);
        Assert.Equal("1.2.2", result.Manifest.Version);
        Assert.Equal(1234, result.Manifest.Size);
        Assert.Equal("https://pan.quark.cn/s/version-specific", result.Manifest.ManualDownloadUrl);
        Assert.Same(result, service.LastCheckResult);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FallsBackToGitHubReleaseWhenManifestUnavailable()
    {
        using var httpClient = CreateHttpClient(request =>
        {
            string url = request.RequestUri!.ToString();
            if (url.Contains("stable.json", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "tag_name": "v1.2.4",
                      "html_url": "https://github.com/Tianyu199509/DeskBox/releases/tag/v1.2.4",
                      "body": "## Highlights\n- Improved update flow",
                      "assets": [
                        {
                          "name": "DeskBox_Setup_1.2.4_x64.exe",
                          "browser_download_url": "https://github.com/Tianyu199509/DeskBox/releases/download/v1.2.4/DeskBox_Setup_1.2.4_x64.exe",
                          "size": 23423211,
                          "digest": "sha256:8ecb3092ae5bd6883f8a75bbea03d9800251e0c23fdbe1bf4d91fd3a62565561"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var service = new AppUpdateService(
            httpClient,
            "https://deskbox.fun/update/stable.json",
            _tempRoot,
            AppUpdateService.DefaultGitHubLatestReleaseApiUrl);

        var result = await service.CheckForUpdatesAsync("1.2.3");

        Assert.Equal(AppUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Manifest);
        Assert.Equal("1.2.4", result.Manifest.Version);
        Assert.Equal("https://github.com/Tianyu199509/DeskBox/releases/download/v1.2.4/DeskBox_Setup_1.2.4_x64.exe", result.Manifest.DownloadUrl);
        Assert.Equal("8ECB3092AE5BD6883F8A75BBEA03D9800251E0C23FDBE1BF4D91FD3A62565561", result.Manifest.Sha256);
        Assert.Equal(23423211, result.Manifest.Size);
        Assert.Equal("https://github.com/Tianyu199509/DeskBox/releases/tag/v1.2.4", result.Manifest.ReleaseNotesUrl);
        Assert.Equal("## Highlights\n- Improved update flow", result.Manifest.GetReleaseNotesForLocale("en-US"));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FallsBackToGitHubShaAssetWhenDigestMissing()
    {
        using var httpClient = CreateHttpClient(request =>
        {
            string url = request.RequestUri!.ToString();
            if (url.Contains("stable.json", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (url.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "8ECB3092AE5BD6883F8A75BBEA03D9800251E0C23FDBE1BF4D91FD3A62565561  DeskBox_Setup_1.2.4_x64.exe",
                        Encoding.UTF8,
                        "text/plain")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "tag_name": "v1.2.4",
                      "html_url": "https://github.com/Tianyu199509/DeskBox/releases/tag/v1.2.4",
                      "assets": [
                        {
                          "name": "DeskBox_Setup_1.2.4_x64.exe",
                          "browser_download_url": "https://github.com/Tianyu199509/DeskBox/releases/download/v1.2.4/DeskBox_Setup_1.2.4_x64.exe",
                          "size": 23423211
                        },
                        {
                          "name": "DeskBox_Setup_1.2.4_x64.exe.sha256",
                          "browser_download_url": "https://github.com/Tianyu199509/DeskBox/releases/download/v1.2.4/DeskBox_Setup_1.2.4_x64.exe.sha256",
                          "size": 95
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var service = new AppUpdateService(
            httpClient,
            "https://deskbox.fun/update/stable.json",
            _tempRoot,
            AppUpdateService.DefaultGitHubLatestReleaseApiUrl);

        var result = await service.CheckForUpdatesAsync("1.2.3");

        Assert.Equal(AppUpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Manifest);
        Assert.Equal("8ECB3092AE5BD6883F8A75BBEA03D9800251E0C23FDBE1BF4D91FD3A62565561", result.Manifest.Sha256);
    }

    [Fact]
    public async Task DownloadUpdateAsync_RejectsInstallerWithoutHash()
    {
        using var httpClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        var service = new AppUpdateService(httpClient, updateRootPath: _tempRoot);

        var result = await service.DownloadUpdateAsync(new AppUpdateManifest
        {
            Version = "1.2.2",
            DownloadUrl = "https://deskbox.fun/DeskBox_Setup_1.2.2_x64.exe"
        });

        Assert.False(result.Success);
        Assert.Equal(AppUpdateDownloadFailureKind.HashMissing, result.FailureKind);
    }

    [Fact]
    public async Task DownloadUpdateAsync_VerifiesSha256AndWritesInstaller()
    {
        byte[] payload = Encoding.UTF8.GetBytes("deskbox-installer");
        string sha256 = Convert.ToHexString(SHA256.HashData(payload));
        using var httpClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        var service = new AppUpdateService(httpClient, updateRootPath: _tempRoot);

        var result = await service.DownloadUpdateAsync(new AppUpdateManifest
        {
            Version = "1.2.2",
            DownloadUrl = "https://deskbox.fun/DeskBox_Setup_1.2.2_x64.exe",
            Sha256 = sha256,
            Size = payload.Length
        });

        Assert.True(result.Success, $"{result.FailureKind}: {result.ErrorMessage}");
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task DownloadUpdateAsync_RejectsInstallerForAnotherArchitecture()
    {
        byte[] payload = Encoding.UTF8.GetBytes("deskbox-installer");
        string sha256 = Convert.ToHexString(SHA256.HashData(payload));
        string otherArchitecture = AppUpdateService.CurrentInstallerArchitectureSuffix == "arm64"
            ? "x64"
            : "arm64";
        using var httpClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        var service = new AppUpdateService(httpClient, updateRootPath: _tempRoot);

        var result = await service.DownloadUpdateAsync(new AppUpdateManifest
        {
            Version = "1.2.2",
            DownloadUrl = $"https://deskbox.fun/DeskBox_Setup_1.2.2_{otherArchitecture}.exe",
            Sha256 = sha256,
            Size = payload.Length
        });

        Assert.False(result.Success);
        Assert.Equal(AppUpdateDownloadFailureKind.InvalidManifest, result.FailureKind);
    }

    [Fact]
    public async Task DownloadUpdateAsync_ReportsCallerCancellationSeparately()
    {
        using var httpClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        var service = new AppUpdateService(httpClient, updateRootPath: _tempRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.DownloadUpdateAsync(new AppUpdateManifest
        {
            Version = "1.2.2",
            DownloadUrl = $"https://deskbox.fun/DeskBox_Setup_1.2.2_{AppUpdateService.CurrentInstallerArchitectureSuffix}.exe",
            Sha256 = "00"
        }, cancellationToken: cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(AppUpdateDownloadFailureKind.Cancelled, result.FailureKind);
    }

    [Fact]
    public async Task PrepareDetachedUpdaterHelper_CopiesUpdaterOutsideAppDirectory()
    {
        string appDirectory = Path.Combine(_tempRoot, "app");
        string updateRoot = Path.Combine(_tempRoot, "updates");
        Directory.CreateDirectory(appDirectory);

        string sourceExe = Path.Combine(appDirectory, "DeskBox.Updater.exe");
        string sourceRuntimeConfig = Path.Combine(appDirectory, "DeskBox.Updater.runtimeconfig.json");
        string nativeModule = Path.Combine(appDirectory, "deskbox_native.dll");
        await File.WriteAllTextAsync(sourceExe, "exe");
        await File.WriteAllTextAsync(sourceRuntimeConfig, "{}");
        await File.WriteAllTextAsync(nativeModule, "native");

        string detachedExe = AppUpdateService.PrepareDetachedUpdaterHelper(appDirectory, updateRoot);

        Assert.True(File.Exists(detachedExe));
        Assert.NotEqual(sourceExe, detachedExe);
        Assert.StartsWith(Path.Combine(updateRoot, "helper"), detachedExe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("exe", await File.ReadAllTextAsync(detachedExe));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(detachedExe)!, "DeskBox.Updater.runtimeconfig.json")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(detachedExe)!, "deskbox_native.dll")));
    }

    [Fact]
    public void TryGetInstallDirectory_ReturnsCurrentDeskBoxExecutableDirectory()
    {
        string installDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "D-drive", "DeskBox")).FullName;
        string appPath = Path.Combine(installDirectory, "DeskBox.exe");
        File.WriteAllText(appPath, "exe");

        bool resolved = AppUpdateService.TryGetInstallDirectory(appPath, out string actualDirectory);

        Assert.True(resolved);
        Assert.Equal(installDirectory, actualDirectory, ignoreCase: true);
        Assert.False(AppUpdateService.TryGetInstallDirectory(
            Path.Combine(installDirectory, "OtherApp.exe"),
            out _));
    }

    [Fact]
    public async Task ContentManifestFetch_AcceptsNotesOnlyManifest_AndRejectsInstallerOnlyManifest()
    {
        // The Store channel's synthesized manifest carries a
        // ms-windowsstore:// download target; the display path must accept a
        // manifest that has notes but no installer link at all.
        string notesOnlyJson =
            """{"schemaVersion":1,"channel":"stable","version":"1.5.9","releaseNotesUrl":"https://github.com/Tianyu199509/DeskBox/releases/tag/v1.5.9"}""";
        string installerOnlyJson =
            """{"schemaVersion":1,"channel":"stable","version":"1.5.9","downloadUrl":"https://deskbox.fun/DeskBox_Setup.exe","sha256":"aa"}""";

        var notesClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(notesOnlyJson, Encoding.UTF8, "application/json")
        });
        AppUpdateManifest? accepted = await AppUpdateService.FetchContentManifestAsync(
            "https://deskbox.fun/update/stable.json", notesClient, CancellationToken.None);
        Assert.NotNull(accepted);
        Assert.Equal("1.5.9", accepted!.Version);
        Assert.True(AppUpdateManifest.IsSafeReleaseNotesUrl(accepted.ReleaseNotesUrl));

        var installerClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(installerOnlyJson, Encoding.UTF8, "application/json")
        });
        AppUpdateManifest? rejected = await AppUpdateService.FetchContentManifestAsync(
            "https://deskbox.fun/update/stable.json", installerClient, CancellationToken.None);
        Assert.Null(rejected);
    }

    [Fact]
    public void ReleaseNotesOnlyManifest_PointsAtGitHubTagForExactVersion()
    {
        AppUpdateManifest manifest = AppUpdateService.CreateReleaseNotesOnlyManifest("1.5.9");

        Assert.Equal("1.5.9", manifest.Version);
        Assert.Equal(
            "https://github.com/Tianyu199509/DeskBox/releases/tag/v1.5.9",
            manifest.ReleaseNotesUrl);
        Assert.True(AppUpdateManifest.IsSafeReleaseNotesUrl(manifest.ReleaseNotesUrl));
        Assert.True(manifest.HasReleaseNotesOrUrl);
        Assert.False(AppUpdateService.IsManifestUsable(manifest));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new HttpClient(new StubHttpMessageHandler(handler));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Theory]
    [InlineData("https://deskbox.fun/update/DeskBox_Setup.exe", true)]
    [InlineData("https://cdn.deskbox.fun/DeskBox_Setup.exe", true)]
    [InlineData("https://github.com/Tianyu199509/DeskBox/releases/download/v1.5.2/DeskBox_Setup.exe", true)]
    [InlineData("http://deskbox.fun/update/DeskBox_Setup.exe", false)]
    [InlineData("https://evil.example.com/DeskBox_Setup.exe", false)]
    [InlineData("https://deskbox.fun.evil.example.com/DeskBox_Setup.exe", false)]
    [InlineData("https://github.evil.example.com/DeskBox_Setup.exe", false)]
    [InlineData("https://github.com/attacker/evil/releases/download/v1.0.0/DeskBox_Setup.exe", false)]
    [InlineData("https://github.com/Tianyu199509/DeskBox/blob/main/DeskBox_Setup.exe", false)]
    [InlineData("https://objects.githubusercontent.com/some-asset-path", false)]
    public void IsManifestUsable_PinsTheInstallerOriginToTrustedHttpsHosts(
        string downloadUrl,
        bool expected)
    {
        var manifest = new AppUpdateManifest
        {
            Version = "1.5.2.0",
            DownloadUrl = downloadUrl
        };

        Assert.Equal(expected, AppUpdateService.IsManifestUsable(manifest));
    }

    [Fact]
    public void IsManifestUsable_RejectsAnUntrustedArm64DownloadUrl()
    {
        var manifest = new AppUpdateManifest
        {
            Version = "1.5.2.0",
            DownloadUrl = "https://deskbox.fun/update/DeskBox_Setup_x64.exe",
            Arm64DownloadUrl = "http://evil.example.com/DeskBox_Setup_arm64.exe"
        };

        Assert.False(AppUpdateService.IsManifestUsable(manifest));
    }

    public static TheoryData<string, string> AppCultureToInstallerLanguageCases => new()
    {
        { LocalizationService.LanguageChinese, "chinesesimplified" },
        { LocalizationService.LanguageChineseTraditional, "chinesetraditional" },
        { LocalizationService.LanguageEnglish, "english" },
        { LocalizationService.LanguageJapanese, "japanese" },
        { LocalizationService.LanguageGerman, "german" },
        { LocalizationService.LanguagePortuguese, "brazilianportuguese" },
        { LocalizationService.LanguageHindi, "hindi" },
        { LocalizationService.LanguageSpanish, "spanish" },
        { LocalizationService.LanguageFrench, "french" },
        { LocalizationService.LanguageArabic, "arabic" },
        { LocalizationService.LanguageBengali, "bengali" },
        { LocalizationService.LanguageRussian, "russian" },
        { LocalizationService.LanguageKorean, "korean" },
    };

    [Theory]
    [MemberData(nameof(AppCultureToInstallerLanguageCases))]
    public void InstallerLanguageMapping_CoversEveryAppLanguage(
        string appCultureName,
        string installerLanguage)
    {
        // Reverse of InstallLanguageCode in installer/DeskBox.iss (identical
        // [Languages] in DeskBox.arm64.iss) — extend both sides together.
        Assert.Equal(installerLanguage, AppUpdateService.MapAppCultureToInstallerLanguage(appCultureName));
    }

    [Fact]
    public void InstallerLanguageMapping_RejectsValuesOutsideTheAppLanguageDomain()
    {
        Assert.Null(AppUpdateService.MapAppCultureToInstallerLanguage(null));
        Assert.Null(AppUpdateService.MapAppCultureToInstallerLanguage(string.Empty));
        Assert.Null(AppUpdateService.MapAppCultureToInstallerLanguage(LocalizationService.LanguageSystem));
        Assert.Null(AppUpdateService.MapAppCultureToInstallerLanguage("zh-HK"));
        Assert.Null(AppUpdateService.MapAppCultureToInstallerLanguage("fr-CA"));
    }

    [Fact]
    public void InstallerLanguageMapping_AgreesWithTheUpdaterWhitelist()
    {
        // The app-side mapping and the updater-side whitelist are two halves
        // of one contract; every mapped name must survive the whitelist so
        // the /LANG pin can never be silently dropped for a shipped language.
        foreach (object?[] row in AppCultureToInstallerLanguageCases)
        {
            string installerLanguage = (string)row[1]!;
            Assert.Equal(
                installerLanguage,
                DeskBox.Updater.Program.ResolveInstallerLanguage(installerLanguage));
        }
    }

    [Fact]
    public void ManifestNotificationPolicy_HonorsSilentNormalAndLightMandatory()
    {
        var silentByFlag = new AppUpdateManifest { Version = "1.6.0", Notify = false, NotifyMode = "normal" };
        Assert.True(silentByFlag.ShouldSuppressToastNotification("1.5.2"));
        Assert.False(silentByFlag.IsMandatoryForVersion("1.5.2"));

        var silentByMode = new AppUpdateManifest { Version = "1.6.0", Notify = true, NotifyMode = "silent" };
        Assert.True(silentByMode.ShouldSuppressToastNotification("1.5.2"));

        var mandatoryByMode = new AppUpdateManifest { Version = "1.6.0", Notify = false, NotifyMode = "mandatory" };
        Assert.True(mandatoryByMode.IsMandatoryForVersion("1.5.2"));
        Assert.False(mandatoryByMode.ShouldSuppressToastNotification("1.5.2"));

        var minSupported = new AppUpdateManifest
        {
            Version = "1.6.0",
            Notify = false,
            NotifyMode = "silent",
            MinimumSupportedVersion = "1.5.2"
        };
        Assert.True(minSupported.IsMandatoryForVersion("1.5.1"));
        Assert.False(minSupported.ShouldSuppressToastNotification("1.5.1"));
        Assert.False(minSupported.IsMandatoryForVersion("1.5.2"));
        Assert.True(minSupported.ShouldSuppressToastNotification("1.5.2"));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_TreatsPushPausedSentinelAsUpToDateWithoutGitHubFallback()
    {
        int githubCalls = 0;
        string arch = AppUpdateService.CurrentInstallerArchitectureSuffix;
        using var httpClient = CreateHttpClient(request =>
        {
            string url = request.RequestUri!.ToString();
            if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                githubCalls++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "schemaVersion": 1,
                      "channel": "stable",
                      "version": "1.0.0",
                      "publicVersion": "1.5.2",
                      "pushPaused": true,
                      "serverControlled": true,
                      "downloadUrl": "https://deskbox.fun/update/DeskBox_Setup_1.5.2_{{arch}}.exe",
                      "sha256": "8ECB3092AE5BD6883F8A75BBEA03D9800251E0C23FDBE1BF4D91FD3A62565561",
                      "size": 1024
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var service = new AppUpdateService(
            httpClient,
            "https://example.test/update/stable.json",
            _tempRoot,
            "https://api.github.com/repos/Tianyu199509/DeskBox/releases/latest");

        var result = await service.CheckForUpdatesAsync("1.5.2");
        Assert.Equal(AppUpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal(0, githubCalls);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_BlockedVersionRetiresDownloadedInstallerAndBlocksGitHubFallback()
    {
        byte[] installerBytes = Encoding.UTF8.GetBytes("bad-build-160");
        string sha256 = Convert.ToHexString(SHA256.HashData(installerBytes));
        string arch = AppUpdateService.CurrentInstallerArchitectureSuffix;
        bool manifestReachable = true;

        Func<HttpRequestMessage, HttpResponseMessage> responder = request =>
        {
            string url = request.RequestUri!.ToString();
            if (url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(installerBytes)
                };
            }

            if (url.Contains("stable.json", StringComparison.OrdinalIgnoreCase))
            {
                if (!manifestReachable)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "schemaVersion": 1,
                          "channel": "stable",
                          "version": "1.5.2",
                          "blockedVersions": ["1.6.0"],
                          "downloadUrl": "https://deskbox.fun/update/DeskBox_Setup_1.5.2_{{arch}}.exe",
                          "sha256": "{{sha256}}",
                          "size": {{installerBytes.Length}}
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "tag_name": "v1.6.0",
                      "html_url": "https://github.com/Tianyu199509/DeskBox/releases/tag/v1.6.0",
                      "assets": [
                        {
                          "name": "DeskBox_Setup_1.6.0_{{arch}}.exe",
                          "browser_download_url": "https://github.com/Tianyu199509/DeskBox/releases/download/v1.6.0/DeskBox_Setup_1.6.0_{{arch}}.exe",
                          "size": {{installerBytes.Length}},
                          "digest": "sha256:{{sha256}}"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        };

        using var httpClient = CreateHttpClient(responder);
        var service = new AppUpdateService(
            httpClient,
            "https://example.test/update/stable.json",
            _tempRoot,
            "https://api.github.com/repos/Tianyu199509/DeskBox/releases/latest");

        // Pre-download 1.6.0 before it gets withdrawn by the admin.
        var preDownload = await service.DownloadUpdateAsync(new AppUpdateManifest
        {
            Version = "1.6.0",
            DownloadUrl = $"https://deskbox.fun/update/DeskBox_Setup_1.6.0_{arch}.exe",
            Sha256 = sha256,
            Size = installerBytes.Length
        });
        Assert.True(preDownload.Success);
        Assert.NotNull(service.LastDownloadedInstallerPath);
        Assert.True(File.Exists(service.LastDownloadedInstallerPath));

        // Admin withdraws 1.6.0 and rolls stable.json back to 1.5.2 -> downloaded 1.6.0 installer is retired.
        var rollbackCheck = await service.CheckForUpdatesAsync("1.5.2");
        Assert.Equal(AppUpdateCheckStatus.UpToDate, rollbackCheck.Status);
        Assert.Null(service.LastDownloadedInstallerPath);
        Assert.False(File.Exists(preDownload.FilePath));

        // Even across a fresh service instance when stable.json goes offline, GitHub /releases/latest (still 1.6.0) is rejected.
        manifestReachable = false;
        using var restartedHttpClient = CreateHttpClient(responder);
        var restartedService = new AppUpdateService(
            restartedHttpClient,
            "https://example.test/update/stable.json",
            _tempRoot,
            "https://api.github.com/repos/Tianyu199509/DeskBox/releases/latest");
        var offlineManifestCheck = await restartedService.CheckForUpdatesAsync("1.5.1");
        Assert.Equal(AppUpdateCheckStatus.Failed, offlineManifestCheck.Status);
    }

    [Fact]
    public void NormalizeManifestForReleaseNotes_ReplacesPausedSentinelAndFiltersBlockedVersions()
    {
        var pausedManifest = new AppUpdateManifest
        {
            Version = "1.0.0",
            PublicVersion = "1.5.2",
            PushPaused = true,
            BlockedVersions = ["1.6.0"],
            ReleaseNotes = new Dictionary<string, string> { ["zh-CN"] = "## 1.5.2 更新说明" }
        };

        AppUpdateManifest normalized = AppUpdateService.NormalizeManifestForReleaseNotes(pausedManifest, "1.5.2");
        Assert.Equal("1.5.2", normalized.Version);
        Assert.Equal("## 1.5.2 更新说明", normalized.GetLocalizedReleaseNotes("zh-CN"));
        Assert.True(AppUpdateService.IsVersionBlockedForReleaseNotes("1.0.0", normalized));
        Assert.True(AppUpdateService.IsVersionBlockedForReleaseNotes("1.6.0", normalized));
        Assert.False(AppUpdateService.IsVersionBlockedForReleaseNotes("1.5.2", normalized));
    }
}
