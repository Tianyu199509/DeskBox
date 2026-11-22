#if DESKBOX_PGO_INSTRUMENTATION
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Core.Persistence;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

internal static class PgoProfileSeedRunner
{
    internal const string SeedDataSwitch = "--pgo-seed-data";

    public static bool TryIntercept(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 1 ||
            !string.Equals(args[0], SeedDataSwitch, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length < 3 ||
            string.IsNullOrWhiteSpace(args[1]) ||
            string.IsNullOrWhiteSpace(args[2]))
        {
            Console.Error.WriteLine("Usage: DeskBox.exe --pgo-seed-data <dataRoot> <sampleFilesDir>");
            exitCode = 2;
            return true;
        }

        try
        {
            SeedStartupProfileData(
                Path.GetFullPath(args[1]),
                Path.GetFullPath(args[2]));
            exitCode = 0;
        }
        catch (Exception ex)
        {
            try
            {
                string errDir = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(errDir);
                File.WriteAllText(Path.Combine(errDir, "pgo-seed-error.txt"), ex.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }

            exitCode = 1;
        }

        return true;
    }

    private static void SeedStartupProfileData(string dataRoot, string sampleFilesDir)
    {
        string dataDir = Path.Combine(dataRoot, "data");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(sampleFilesDir);

        for (int index = 1; index <= 24; index++)
        {
            string samplePath = Path.Combine(sampleFilesDir, $"note-{index:D2}.txt");
            if (!File.Exists(samplePath))
            {
                File.WriteAllText(samplePath, $"PGO sample document {index}", new UTF8Encoding(false));
            }
        }

        var settings = new AppSettings
        {
            SchemaVersion = SettingsMigrationPipeline.CurrentSchemaVersion,
        };
        settings.Core.HasCompletedOnboarding = true;
        settings.Core.CompletedOnboardingVersion = OnboardingWindow.CurrentOnboardingVersion;
        settings.Core.HasResolvedInitialFileWidgetSetup = true;
        settings.Core.AutoStart = false;
        settings.Core.AutoStartDefaultApplied = true;
        settings.Core.AutoCheckForUpdates = false;
        settings.Core.AutoDownloadUpdates = false;
        settings.Core.ShowReleaseNotesAfterUpdate = false;
        settings.Core.LastRunVersion = AppUpdateService.GetCurrentAppVersion();
        settings.Core.GlobalHotkeyEnabled = false;
        settings.Core.FullyElevatedNoticeAcknowledgedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        settings.Search.SearchHotkeyEnabled = false;
        settings.Clipboard.ClipboardHotkeyEnabled = false;
        foreach (WidgetKind kind in FeatureWidgetSettings.FeatureKinds)
        {
            FeatureWidgetSettings.SetEnabled(settings, kind, true);
        }

        JsonObject settingsNode = JsonSerializer.SerializeToNode(
            settings,
            SettingsJsonContext.Default.AppSettings)!.AsObject();
        foreach (string layoutKey in WidgetLayoutStore.SettingsWireKeys)
        {
            settingsNode.Remove(layoutKey);
        }

        string settingsJson = settingsNode.ToJsonString(SettingsJsonContext.Default.Options);
        File.WriteAllText(
            Path.Combine(dataDir, "settings.json"),
            settingsJson,
            new UTF8Encoding(false));

        var layoutSlice = new WidgetLayoutSettingsSlice
        {
            Widgets =
            [
                new WidgetConfig
                {
                    Id = "pgo-files",
                    WidgetKind = WidgetKind.File,
                    Name = "Files",
                    MappedFolderPath = sampleFilesDir,
                    FollowsDefaultStoragePath = false,
                    X = 40,
                    Y = 40,
                    Width = 340,
                    Height = 260,
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    IsVisible = true
                },
                new WidgetConfig
                {
                    Id = "pgo-todo",
                    WidgetKind = WidgetKind.Todo,
                    Name = "Todo",
                    X = 400,
                    Y = 40,
                    Width = 320,
                    Height = 260,
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    IsVisible = true
                },
                new WidgetConfig
                {
                    Id = "pgo-weather",
                    WidgetKind = WidgetKind.Weather,
                    Name = "Weather",
                    X = 740,
                    Y = 40,
                    Width = 320,
                    Height = 220,
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    IsVisible = true
                },
                new WidgetConfig
                {
                    Id = "pgo-glance",
                    WidgetKind = WidgetKind.Glance,
                    Name = "Glance",
                    X = 40,
                    Y = 320,
                    Width = 340,
                    Height = 220,
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    IsVisible = true
                },
                new WidgetConfig
                {
                    Id = "pgo-music",
                    WidgetKind = WidgetKind.Music,
                    Name = "Music",
                    X = 400,
                    Y = 320,
                    Width = 320,
                    Height = 220,
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    IsVisible = true
                },
                new WidgetConfig
                {
                    Id = "pgo-custom",
                    WidgetKind = WidgetKind.Custom,
                    Name = "System Pulse",
                    X = 740,
                    Y = 280,
                    Width = 320,
                    Height = 260,
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    IsVisible = true
                },
                new WidgetConfig
                {
                    Id = "pgo-clipboard",
                    WidgetKind = WidgetKind.Clipboard,
                    Name = "Clipboard",
                    X = 1080,
                    Y = 40,
                    Width = 320,
                    Height = 280,
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    IsVisible = true
                },
                new WidgetConfig
                {
                    Id = "pgo-notes",
                    WidgetKind = WidgetKind.QuickCapture,
                    Name = "Quick Notes",
                    X = 1080,
                    Y = 340,
                    Width = 320,
                    Height = 240,
                    BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                    IsVisible = true
                }
            ]
        };

        var layoutDocument = new WidgetLayoutDocument
        {
            SchemaVersion = WidgetLayoutStore.CurrentSchemaVersion,
            Layout = layoutSlice
        };
        string layoutJson = JsonSerializer.Serialize(
            layoutDocument,
            WidgetLayoutJsonContext.Default.WidgetLayoutDocument);
        File.WriteAllText(
            Path.Combine(dataDir, "widget-layout.json"),
            layoutJson,
            new UTF8Encoding(false));

        string todoDir = Path.Combine(dataDir, "widgets", "pgo-todo");
        Directory.CreateDirectory(todoDir);
        var todoData = new TodoWidgetData
        {
            Items =
            [
                new TodoItem { Text = "Prepare desktop workspace" },
                new TodoItem { Text = "Verify startup widget rendering", IsCompleted = true }
            ]
        };
        string todoJson = JsonSerializer.Serialize(todoData, TodoJsonContext.Default.StoreData);
        File.WriteAllText(
            Path.Combine(todoDir, "todo.json"),
            todoJson,
            new UTF8Encoding(false));

        var customStore = new CustomWidgetDataStore("pgo-custom", dataDir);
        CustomWidgetTemplateBundle pulseBundle = CustomWidgetTemplates.GetTemplateBundle(
            CustomWidgetTemplates.SystemPulseTemplateId);
        customStore.WriteWorkspaceBundleAsync(
                pulseBundle.ManifestJson,
                pulseBundle.LogicJs,
                updateLogicJs: true,
                locales: pulseBundle.Locales,
                updateLocales: true)
            .GetAwaiter()
            .GetResult();
    }
}
#endif
