using System.Globalization;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class CrossPlatformAndLocaleCompatibilityTests
{
    private const uint FileAttributeRecallOnDataAccess = 0x00400000u;
    private const uint FileAttributeOffline = 0x00001000u;
    private const uint FileAttributeNormal = 0x00000080u;

    [Fact]
    public void OneDriveCloudReparseRevalidation_AllowsHydratedCloudReparseAndBlocksSymlinkOrUnhydrated()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), $"DeskBox-OneDriveRevalidate-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(sandbox);
            string sampleFile = Path.Combine(sandbox, "SyncedDoc.docx");
            File.WriteAllText(sampleFile, "hydrated-onedrive-content", Encoding.UTF8);
            var info = new FileInfo(sampleFile);

            var snapshot = new DesktopOrganizationFileSnapshot(
                SourcePath: sampleFile,
                Name: info.Name,
                Extension: info.Extension,
                Size: info.Length,
                LastWriteTimeUtc: info.LastWriteTimeUtc,
                CategoryId: "documents",
                SubtypeId: null,
                ExclusionReason: DesktopOrganizationExclusionReason.None);

            // Hydrated OneDrive KFM file carries FileAttributes.ReparsePoint + IO_REPARSE_TAG_CLOUD,
            // where IsSafeTraversablePath returns true.
            Exception? hydratedEx = Record.Exception(() =>
                DesktopOrganizationTransaction.RevalidateSource(
                    snapshot,
                    safeTraversableProbe: static _ => true,
                    attributesProbe: static _ => FileAttributes.Archive | FileAttributes.ReparsePoint));
            Assert.Null(hydratedEx);

            // Symlinks, junctions, and unhydrated cloud placeholders cause IsSafeTraversablePath to return false.
            Exception? unsafeEx = Record.Exception(() =>
                DesktopOrganizationTransaction.RevalidateSource(
                    snapshot,
                    safeTraversableProbe: static _ => false,
                    attributesProbe: static _ => FileAttributes.Archive | FileAttributes.ReparsePoint));
            Assert.NotNull(unsafeEx);
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandbox))
                {
                    Directory.Delete(sandbox, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void CloudPlaceholderAttributeProbes_KeepDesktopDecision3AWhileBlockingPassiveReadHydration()
    {
        string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string desktopPlaceholder = Path.Combine(
            string.IsNullOrWhiteSpace(userDesktop) ? Path.GetTempPath() : userDesktop,
            "FakeCloudVideo.mp4");
        string nonDesktopPlaceholder = Path.Combine(Path.GetTempPath(), "FakeCloudArchive.zip");

        if (!string.IsNullOrWhiteSpace(userDesktop))
        {
            // Decision 3A: Desktop inbound drop classification keeps Move eligibility on Desktop cloud placeholders.
            Assert.False(DriveMediaInterop.IsUnhydratedCloudPlaceholderPath(
                desktopPlaceholder,
                userDesktopOverride: userDesktop,
                fileAttributesProbe: static _ => FileAttributeRecallOnDataAccess));
        }

        // Passive preview/icon/attachment readers MUST still detect unhydrated cloud attributes even on Desktop.
        Assert.True(DriveMediaInterop.HasUnhydratedCloudAttributesOnDisk(
            desktopPlaceholder,
            static _ => FileAttributeRecallOnDataAccess));
        Assert.True(DriveMediaInterop.HasUnhydratedCloudAttributesOnDisk(
            nonDesktopPlaceholder,
            static _ => FileAttributeOffline));
        Assert.False(DriveMediaInterop.HasUnhydratedCloudAttributesOnDisk(
            desktopPlaceholder,
            static _ => FileAttributeNormal));
    }

    [Fact]
    public void SingleInstanceAndActivationInterop_MergesPackagedLaunchArgsAndResolvesStoreAliasCandidates()
    {
        string[] baseArgs = [@"C:\Program Files\WindowsApps\DeskBox_1.6.0_x64\DeskBox.exe"];
        string[] merged = SingleInstanceAndActivationInterop.GetEffectiveLaunchCommandLineArgs(
            baseArgs,
            "--await-parent-exit 4242 --startup");
        Assert.Equal(4, merged.Length);
        Assert.Equal("--await-parent-exit", merged[1]);
        Assert.Equal("4242", merged[2]);
        Assert.Equal("--startup", merged[3]);
        Assert.Equal(4242, App.TryGetAwaitParentExitProcessId(merged));

        // Deduplicates when OS already mirrored packaged args onto the Win32 command line.
        string[] deduplicated = SingleInstanceAndActivationInterop.GetEffectiveLaunchCommandLineArgs(
            merged,
            "--await-parent-exit 4242 --startup");
        Assert.Same(merged, deduplicated);

        IReadOnlyList<string> aliases = DragDropPermissionService.GetPackagedExecutionAliasCandidates(
            "12345Publisher.DeskBox_abcdef!App",
            @"C:\Users\Test\AppData\Local");
        Assert.Equal(2, aliases.Count);
        Assert.Equal(
            @"C:\Users\Test\AppData\Local\Microsoft\WindowsApps\12345Publisher.DeskBox_abcdef\deskbox.exe",
            aliases[0]);
        Assert.Equal(
            @"C:\Users\Test\AppData\Local\Microsoft\WindowsApps\deskbox.exe",
            aliases[1]);
    }

    [Fact]
    public async Task SingleInstanceAndActivationInterop_CreatesMediumIlEventMutexAndMultiInstancePipe()
    {
        string uniqueSuffix = Guid.NewGuid().ToString("N");
        string eventName = $@"Local\DeskBoxTestEvent-{uniqueSuffix}";
        string mutexName = $@"Local\DeskBoxTestMutex-{uniqueSuffix}";
        string pipeName = $"deskbox-test-pipe-{uniqueSuffix}";

        using EventWaitHandle? primaryEvent = SingleInstanceAndActivationInterop.CreateOrOpenActivationEvent(eventName);
        using EventWaitHandle? secondaryEvent = SingleInstanceAndActivationInterop.CreateOrOpenActivationEvent(eventName);
        Assert.NotNull(primaryEvent);
        Assert.NotNull(secondaryEvent);
        Assert.True(secondaryEvent!.Set());
        Assert.True(primaryEvent!.WaitOne(500));

        // Keep primaryMutex acquire and ReleaseMutex on the same OS thread (Win32 Mutex is thread-affine).
        AssertPrimaryAndSecondaryMutexOwnership(mutexName);

        await using NamedPipeServerStream? server1 =
            SingleInstanceAndActivationInterop.TryCreateMediumIntegrityCurrentUserPipeServer(pipeName, maxInstances: 4);
        await using NamedPipeServerStream? server2 =
            SingleInstanceAndActivationInterop.TryCreateMediumIntegrityCurrentUserPipeServer(pipeName, maxInstances: 4);
        Assert.NotNull(server1);
        Assert.NotNull(server2);

        Task acceptTask = server1!.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        await acceptTask;

        byte[] payload = "ping"u8.ToArray();
        await client.WriteAsync(payload);
        await client.FlushAsync();

        byte[] buffer = new byte[4];
        int read = await server1.ReadAsync(buffer);
        Assert.Equal(4, read);
        Assert.Equal("ping", Encoding.UTF8.GetString(buffer));
    }

    private static void AssertPrimaryAndSecondaryMutexOwnership(string mutexName)
    {
        (Mutex? primaryMutex, bool primaryCreated) =
            SingleInstanceAndActivationInterop.CreateOrOpenSingleInstanceMutex(mutexName);
        Assert.True(primaryCreated);
        Assert.NotNull(primaryMutex);
        try
        {
            (Mutex? secondaryMutex, bool secondaryCreated) = (null, true);
            var worker = new Thread(() =>
            {
                (secondaryMutex, secondaryCreated) =
                    SingleInstanceAndActivationInterop.CreateOrOpenSingleInstanceMutex(mutexName);
            });
            worker.Start();
            worker.Join();
            Assert.False(secondaryCreated);
            Assert.Null(secondaryMutex);
        }
        finally
        {
            primaryMutex!.ReleaseMutex();
            primaryMutex.Dispose();
        }
    }

    [Fact]
    public void StartupService_StateChangedEventNotifiesSubscribers()
    {
        int notifications = 0;
        void Handler() => Interlocked.Increment(ref notifications);

        StartupService.StateChanged += Handler;
        try
        {
            StartupService.NotifyStateChanged();
            Assert.Equal(1, notifications);
        }
        finally
        {
            StartupService.StateChanged -= Handler;
        }
    }

    [Fact]
    public void ArabicHijriCulture_FormatsGregorianDatesAcrossCalendarGlanceAndWorldClock()
    {
        CultureInfo prevCulture = CultureInfo.CurrentCulture;
        CultureInfo prevUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo arSa = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentCulture = arSa;
            CultureInfo.CurrentUICulture = arSa;

            CultureInfo gregorianAr = LocalizationService.EnsureGregorianCalendar(arSa);
            Assert.IsType<GregorianCalendar>(gregorianAr.DateTimeFormat.Calendar);

            var sampleDate = new DateTime(2026, 11, 16, 14, 30, 0, DateTimeKind.Local);
            string glanceDate = GlanceWidgetViewModel.FormatDateText(sampleDate, arSa, includeYear: true);
            Assert.Contains("2026", glanceDate, StringComparison.Ordinal);
            Assert.DoesNotContain("1448", glanceDate, StringComparison.Ordinal);

            string monthTitle = CustomWidgetCalendarEngine.ResolveToken(
                "monthTitle",
                new DateTimeOffset(sampleDate),
                state: null,
                cultureName: "ar-SA");
            Assert.Contains("2026", monthTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("1448", monthTitle, StringComparison.Ordinal);

            string zonedDate = CustomWidgetUtilityEngine.ResolveTimeZoneFormatted(
                "UTC",
                "yyyy-MM-dd",
                new DateTimeOffset(2026, 11, 16, 14, 30, 0, TimeSpan.Zero),
                cultureName: "ar-SA");
            Assert.Equal("2026-11-16", zonedDate);

            // Windows FILETIME=0 epoch (1601-01-01) falls below UmAlQuraCalendar.MinSupportedDateTime (1900-04-30).
            string oldFileDate = FileMetaService.FormatDate(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Local));
            Assert.Contains("1601", oldFileDate, StringComparison.Ordinal);

            // Out-of-UmAlQura-range forecast dates (2099 > 2077-11-16) must not throw in CustomWidgetWeatherSource.
            var futureWeather = new WeatherData
            {
                LocationName = "Riyadh",
                Current = new WeatherCurrent { Temperature = 28, WindSpeed = 12.5, WeatherCode = 0, IsDay = 1 },
                Daily = new WeatherDaily
                {
                    Time = ["2099-05-01", "2099-05-02"],
                    WeatherCode = [0, 1],
                    TemperatureMax = [32, 33],
                    TemperatureMin = [20, 21]
                }
            };
            CustomWidgetWeatherSnapshot snapshot = CustomWidgetWeatherSource.BuildSnapshotFromWeatherData(
                futureWeather,
                "Riyadh",
                "ar-SA");
            Assert.Equal(2, snapshot.ForecastRecords.Count);
            Assert.Equal("12.5 km/h", snapshot.ResolveToken("windText", "de-DE"));

            // Untitled QuickCapture mirror filenames must use Gregorian ISO years even under Hijri/Thai calendars.
            var untitledNote = new QuickCaptureItem
            {
                Id = "abcdef123456",
                Title = string.Empty,
                Body = string.Empty,
                CreatedAt = new DateTimeOffset(2026, 11, 18, 10, 45, 0, TimeSpan.Zero)
            };
            string mirrorFileName = QuickCaptureMarkdownMirrorService.BuildMirrorFileName(untitledNote, "abcdef12");
            Assert.StartsWith("2026-11-18_", mirrorFileName, StringComparison.Ordinal);
            Assert.DoesNotContain("1448", mirrorFileName, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
            CultureInfo.CurrentUICulture = prevUiCulture;
        }
    }

    [Fact]
    public void DesktopOutboundDropCoordinator_IncludesLocalizedCopySuffixesForAll13ShippedLocales()
    {
        HashSet<string> candidates = DesktopOutboundDropCoordinator
            .GetAllArrivalNameCandidates("budget.xlsx")
            .ToHashSet(StringComparer.Ordinal);

        string[] expectedCandidates =
        [
            "budget - Copy.xlsx",
            "budget - 副本.xlsx",
            "budget - 複製.xlsx",
            "budget - コピー.xlsx",
            "budget - 복사본.xlsx",
            "budget - Kopie.xlsx",
            "budget - Copie.xlsx",
            "budget — Copie.xlsx",
            "budget - Copia.xlsx",
            "budget - Cópia.xlsx",
            "budget - копия.xlsx",
            "budget - نسخة.xlsx",
            "budget - प्रतिलिपि.xlsx",
            "budget - প্রতিলিপি.xlsx",
        ];

        foreach (string expected in expectedCandidates)
        {
            Assert.Contains(expected, candidates);
        }
    }

    [Fact]
    public void CommaDecimalCulture_PreservesDotDecimalAndMigratesCommaDimOverrides()
    {
        CultureInfo prevCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var config = new WidgetConfig
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [WidgetBackgroundCustomization.ImageMetadataKey] = "background.png",
                    [WidgetBackgroundCustomization.DimMetadataKey] = "37,5",
                }
            };

            Assert.Equal(37.5, WidgetBackgroundCustomization.GetDimPercentOverride(config));

            bool changed = WidgetBackgroundCustomization.NormalizeOverrides(config);
            Assert.True(changed);
            Assert.Equal("37.5", config.Metadata[WidgetBackgroundCustomization.DimMetadataKey]);

            WidgetBackgroundCustomization.SetDimPercent(config, 42.5);
            Assert.Equal("42.5", config.Metadata[WidgetBackgroundCustomization.DimMetadataKey]);
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
        }
    }

    [Fact]
    public void LocalizationAndUpdateErrors_ResolveAll13ApiLanguageCodesNetworkErrorsAndLocaleStringCleanliness()
    {
        Assert.Equal("zh", LocalizationService.ResolveApiLanguageCode("zh-CN"));
        Assert.Equal("zh", LocalizationService.ResolveApiLanguageCode("zh-TW"));
        Assert.Equal("ja", LocalizationService.ResolveApiLanguageCode("ja-JP"));
        Assert.Equal("de", LocalizationService.ResolveApiLanguageCode("de-DE"));
        Assert.Equal("pt", LocalizationService.ResolveApiLanguageCode("pt-BR"));
        Assert.Equal("hi", LocalizationService.ResolveApiLanguageCode("hi-IN"));
        Assert.Equal("es", LocalizationService.ResolveApiLanguageCode("es-ES"));
        Assert.Equal("fr", LocalizationService.ResolveApiLanguageCode("fr-FR"));
        Assert.Equal("ar", LocalizationService.ResolveApiLanguageCode("ar-SA"));
        Assert.Equal("bn", LocalizationService.ResolveApiLanguageCode("bn-BD"));
        Assert.Equal("ru", LocalizationService.ResolveApiLanguageCode("ru-RU"));
        Assert.Equal("ko", LocalizationService.ResolveApiLanguageCode("ko-KR"));
        Assert.Equal("en", LocalizationService.ResolveApiLanguageCode("en-US"));

        var localizedSocketError = new HttpRequestException(
            "そのホスト名は見つかりません。",
            new SocketException((int)SocketError.HostNotFound));
        string formattedError = AppUpdateService.FormatUpdateCheckErrorMessage(localizedSocketError);
        Assert.StartsWith("NETWORK_UNAVAILABLE:", formattedError, StringComparison.Ordinal);

        string stringsDir = TestPaths.FromRepository("src/DeskBox/Strings");
        using var enDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(stringsDir, "en-US.json"), Encoding.UTF8));
        HashSet<string> enKeys = enDoc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        string zhTwRaw = File.ReadAllText(Path.Combine(stringsDir, "zh-TW.json"), Encoding.UTF8);
        Assert.DoesNotContain("滑鼠滑鼠", zhTwRaw, StringComparison.Ordinal);
        Assert.DoesNotContain("系統系統", zhTwRaw, StringComparison.Ordinal);
        Assert.DoesNotContain("自身的資料資料夾", zhTwRaw, StringComparison.Ordinal);

            foreach (string code in LocalizationService.SupportedLanguageCodes)
        {
            string localePath = Path.Combine(stringsDir, $"{code}.json");
            Assert.True(File.Exists(localePath), $"Missing locale file: {localePath}");
            using var doc = JsonDocument.Parse(File.ReadAllText(localePath, Encoding.UTF8));
            HashSet<string> keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Assert.True(enKeys.SetEquals(keys), $"Key parity mismatch in {code}.json");
        }
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("he-IL")]
    [InlineData("fa-IR")]
    [InlineData("sv-SE")]
    [InlineData("th-TH")]
    public void NonAsciiNegativeSignAndNonGregorianCultures_PreserveIconIndicesRtfMonitorKeysAndTodoDates(string cultureName)
    {
        CultureInfo prevCulture = CultureInfo.CurrentCulture;
        CultureInfo prevUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            // Negative shell icon resource IDs ("imageres.dll,-102").
            (string iconPath, int? iconIndex) = IconHelper.SplitIconLocation(@"C:\Windows\System32\imageres.dll,-102");
            Assert.Equal(@"C:\Windows\System32\imageres.dll", iconPath);
            Assert.Equal(-102, iconIndex);

            // RTF signed 16-bit negative escapes for CJK >= U+8000 (\u-29705 = '请', \u-27367 = '错').
            Assert.True(ClipboardHistoryService.TryExtractVisiblePlainTextFromRtf(
                @"{\rtf1\ansi \u-29705?\u-27367?}",
                out string? rtfPlain));
            Assert.Equal("请错", rtfPlain);

            // Multi-monitor negative coordinates (secondary monitor left/above primary).
            var leftMonitor = new Windows.Graphics.RectInt32(-1920, -1080, 1920, 1040);
            string monitorKey = WidgetPositioningService.CreateMonitorKey(leftMonitor);
            Assert.Equal("-1920:-1080:1920:1040", monitorKey);
            Assert.True(WidgetPositioningService.TryParseMonitorKeyForResolver(monitorKey, out Windows.Graphics.RectInt32 parsedArea));
            Assert.Equal(leftMonitor, parsedArea);
            Assert.True(WidgetPositioningService.TryParseMonitorKeyForResolver("\u061C-1920:\u22121080:1920:1040", out Windows.Graphics.RectInt32 migratedArea));
            Assert.Equal(leftMonitor, migratedArea);

            // Todo item with default CreatedAt (0001-01-01) and far-future DueDate (2099 > UmAlQura 2077 ceiling).
            var todoModel = new TodoItem
            {
                Text = "Milestone",
                CreatedAt = default,
                DueDate = new DateTimeOffset(2099, 12, 31, 9, 30, 0, TimeSpan.Zero),
                HasDueTime = false
            };
            var todoVm = new TodoItemViewModel(
                todoModel,
                clock: () => new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
            Assert.NotEmpty(todoVm.CreatedText);
            Assert.Contains("2099/", todoVm.DueStatusText, StringComparison.Ordinal);
            Assert.Contains("2099/", todoVm.MetadataDueDateText, StringComparison.Ordinal);

            // Overdue Todo custom due-date picker relaxes MinDate to preserve past due date instead of clamping to today.
            var localToday = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Local);
            var overdueDate = new DateTimeOffset(localToday.AddDays(-5));
            DateTimeOffset minDate = DeskBox.Controls.WidgetContents.TodoWidgetContent.ResolveCustomDuePickerMinDate(overdueDate, localToday);
            Assert.Equal(overdueDate.Date, minDate.Date);

            var futureDue = new DateTimeOffset(localToday.AddDays(3));
            DateTimeOffset futureMinDate = DeskBox.Controls.WidgetContents.TodoWidgetContent.ResolveCustomDuePickerMinDate(futureDue, localToday);
            Assert.Equal(localToday.Date, futureMinDate.Date);
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
            CultureInfo.CurrentUICulture = prevUiCulture;
        }
    }
}
