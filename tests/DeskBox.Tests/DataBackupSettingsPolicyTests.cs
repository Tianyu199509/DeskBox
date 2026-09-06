using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DataBackupSettingsPolicyTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void NormalizeIntervalMinutes_KeepsPresetValuesAndDefaultsOtherwise()
    {
        int[] presets = [5, 30, 60, 720, 1440, 7200];
        Assert.All(presets, preset =>
            Assert.Equal(preset, DataBackupSettingsPolicy.NormalizeIntervalMinutes(preset)));

        Assert.All(new[] { 0, -5, 1, 15, 90, 1441, int.MaxValue, int.MinValue }, invalid =>
            Assert.Equal(
                DataBackupSettingsPolicy.DefaultIntervalMinutes,
                DataBackupSettingsPolicy.NormalizeIntervalMinutes(invalid)));
    }

    [Fact]
    public void NormalizeRetentionCount_KeepsPresetValuesAndDefaultsOtherwise()
    {
        int[] presets = [3, 5, 7, 14, 30];
        Assert.All(presets, preset =>
            Assert.Equal(preset, DataBackupSettingsPolicy.NormalizeRetentionCount(preset)));

        Assert.All(new[] { 0, -1, 2, 4, 8, 100, int.MaxValue }, invalid =>
            Assert.Equal(
                DataBackupSettingsPolicy.DefaultRetentionCount,
                DataBackupSettingsPolicy.NormalizeRetentionCount(invalid)));
    }

    [Fact]
    public void NormalizeCustomDirectory_TrimsAndNullsBlankValues()
    {
        Assert.Null(DataBackupSettingsPolicy.NormalizeCustomDirectory(null));
        Assert.Null(DataBackupSettingsPolicy.NormalizeCustomDirectory(string.Empty));
        Assert.Null(DataBackupSettingsPolicy.NormalizeCustomDirectory("   "));
        Assert.Equal(@"D:\Backups", DataBackupSettingsPolicy.NormalizeCustomDirectory(@"  D:\Backups  "));
    }

    [Fact]
    public void Normalize_RepairsHandEditedValuesAndReportsChange()
    {
        var settings = new AppSettings
        {
            AutomaticBackupIntervalMinutes = 90,
            AutomaticBackupRetentionCount = 2,
            AutomaticBackupDirectory = "  ",
        };

        bool changed = DataBackupSettingsPolicy.Normalize(settings);

        Assert.True(changed);
        Assert.Equal(DataBackupSettingsPolicy.DefaultIntervalMinutes, settings.AutomaticBackupIntervalMinutes);
        Assert.Equal(DataBackupSettingsPolicy.DefaultRetentionCount, settings.AutomaticBackupRetentionCount);
        Assert.Equal(string.Empty, settings.AutomaticBackupDirectory);

        Assert.False(DataBackupSettingsPolicy.Normalize(settings));
    }

    [Fact]
    public void GetOptions_MapsNormalizedSettings()
    {
        var settings = new AppSettings
        {
            AutomaticBackupEnabled = false,
            AutomaticBackupIntervalMinutes = 5,
            AutomaticBackupRetentionCount = 30,
            AutomaticBackupDirectory = @" D:\Backups ",
        };

        AutomaticBackupOptions options = DataBackupSettingsPolicy.GetOptions(settings);

        Assert.False(options.IsEnabled);
        Assert.Equal(5, options.IntervalMinutes);
        Assert.Equal(30, options.RetentionCount);
        Assert.Equal(@"D:\Backups", options.CustomDirectory);
    }

    [Fact]
    public void ReadStartupOptions_ReturnsDefaultsWhenFileIsMissingOrInvalid()
    {
        AutomaticBackupOptions missing = DataBackupSettingsPolicy.ReadStartupOptions(
            Path.Combine(_tempRoot, "missing.json"));
        Assert.Equal(AutomaticBackupOptions.Default, missing);

        string corruptPath = Path.Combine(_tempRoot, "corrupt.json");
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllText(corruptPath, "{ not json");
        Assert.Equal(
            AutomaticBackupOptions.Default,
            DataBackupSettingsPolicy.ReadStartupOptions(corruptPath));
    }

    [Fact]
    public void ReadStartupOptions_ReadsRawSettingsWithoutFullDeserialization()
    {
        Directory.CreateDirectory(_tempRoot);
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "schemaVersion": 42,
              "theme": "Dark",
              "automaticBackupEnabled": false,
              "automaticBackupIntervalMinutes": 30,
              "automaticBackupRetentionCount": 14,
              "automaticBackupDirectory": "D:\\DeskBox Backups"
            }
            """);

        AutomaticBackupOptions options = DataBackupSettingsPolicy.ReadStartupOptions(settingsPath);

        Assert.False(options.IsEnabled);
        Assert.Equal(30, options.IntervalMinutes);
        Assert.Equal(14, options.RetentionCount);
        Assert.Equal(@"D:\DeskBox Backups", options.CustomDirectory);
    }

    [Fact]
    public void ReadStartupOptions_FallsBackToDefaultsForUnrecognizedValues()
    {
        Directory.CreateDirectory(_tempRoot);
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "automaticBackupEnabled": "yes",
              "automaticBackupIntervalMinutes": 90,
              "automaticBackupRetentionCount": null,
              "automaticBackupDirectory": 12345
            }
            """);

        AutomaticBackupOptions options = DataBackupSettingsPolicy.ReadStartupOptions(settingsPath);

        Assert.True(options.IsEnabled);
        Assert.Equal(DataBackupSettingsPolicy.DefaultIntervalMinutes, options.IntervalMinutes);
        Assert.Equal(DataBackupSettingsPolicy.DefaultRetentionCount, options.RetentionCount);
        Assert.Null(options.CustomDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
