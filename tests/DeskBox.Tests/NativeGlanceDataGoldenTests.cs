extern alias GlancePkg;

using System.Text.Json;

namespace DeskBox.Tests;

using GlanceDataFile = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceDataFile;
using GlanceData = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceData;
using PackageData = GlancePkg::DeskBox.Models.GlanceWidgetData;

/// <summary>
/// Golden data tests for the native package's persistence layer (audit
/// round 18: source-scan ratchets could not see data loss, so these run the
/// real code). The package must round-trip migrated files LOSSLESSLY -
/// settings it has not wired yet and unknown fields from future hosts
/// survive every save - and it must read the legacy wire formats the host
/// stores historically wrote.
/// </summary>
public class NativeGlanceDataGoldenTests
{
    private static string Root() => Directory.CreateTempSubdirectory("deskbox-glance-golden").FullName;

    private static void WriteData(string root, string json) =>
        File.WriteAllText(Path.Combine(root, GlanceDataFile.FileName), json);

    private static JsonDocument ReadSaved(string root) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(root, GlanceDataFile.FileName)));

    [Fact]
    public void RoundTripPreservesUnknownAndUnwiredFields()
    {
        string root = Root();
        try
        {
            WriteData(root, """
                {
                  "version": 7,
                  "showTime": false,
                  "layout": "Editorial",
                  "transition": "SlideFade",
                  "readability": "Strong",
                  "backgroundImageTransparency": 0.35,
                  "timeScale": 1.2,
                  "futureField": 123,
                  "showChineseFestivals": true,
                  "rotationIntervalMinutes": 30
                }
                """);
            GlanceData? loaded = GlanceDataFile.Load(root);
            Assert.NotNull(loaded);

            // The user flips one package-owned toggle and the rotation.
            loaded!.Settings.ShowChineseFestivals = false;
            loaded.Settings.RotationIntervalMinutes = 45;
            GlanceDataFile.Save(loaded, root);

            using JsonDocument saved = ReadSaved(root);
            JsonElement o = saved.RootElement;
            // Owned fields: updated.
            Assert.False(o.GetProperty("showChineseFestivals").GetBoolean());
            Assert.Equal(45, o.GetProperty("rotationIntervalMinutes").GetInt32());
            // Everything the package does not own: byte-identical values.
            Assert.False(o.GetProperty("showTime").GetBoolean());
            Assert.Equal("Editorial", o.GetProperty("layout").GetString());
            Assert.Equal("SlideFade", o.GetProperty("transition").GetString());
            Assert.Equal("Strong", o.GetProperty("readability").GetString());
            Assert.Equal(0.35, o.GetProperty("backgroundImageTransparency").GetDouble(), precision: 5);
            Assert.Equal(1.2, o.GetProperty("timeScale").GetDouble(), precision: 5);
            Assert.Equal(123, o.GetProperty("futureField").GetInt32());
            // The schema version is NOT owned by the partial writer (audit
            // 19): the original travels untouched - no false v10 stamp, no
            // downgrade of a future host's newer version.
            Assert.Equal(7, o.GetProperty("version").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsLegacyIntegerEnums()
    {
        string root = Root();
        try
        {
            // Old stores wrote numbers; the host still reads them back, so
            // the package must too (repo golden: write names, read integers).
            WriteData(root, """
                {
                  "version": 7,
                  "traditionalCalendarMode": 9,
                  "backgroundSource": 1,
                  "imageFit": 1
                }
                """);
            GlanceData? loaded = GlanceDataFile.Load(root);
            Assert.NotNull(loaded);
            Assert.Equal(
                GlancePkg::DeskBox.Models.GlanceTraditionalCalendarMode.Hebrew,
                loaded!.Settings.TraditionalCalendarMode);
            Assert.Equal(
                GlancePkg::DeskBox.Models.GlanceBackgroundSource.LocalFiles,
                loaded.Settings.BackgroundSource);
            Assert.Equal(
                GlancePkg::DeskBox.Models.GlanceImageFitMode.Fit,
                loaded.Settings.ImageFit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingFieldsKeepModelDefaults()
    {
        string root = Root();
        try
        {
            // A real v7-shaped file: no showChineseFestivals, no photo
            // controls, no image fit - defaults must apply.
            WriteData(root, """
                {
                  "version": 7,
                  "backgroundSource": "Bing",
                  "rotationIntervalMinutes": 30
                }
                """);
            GlanceData? loaded = GlanceDataFile.Load(root);
            Assert.NotNull(loaded);
            Assert.True(loaded!.Settings.ShowChineseFestivals);
            Assert.Equal(GlancePkg::DeskBox.Models.GlanceTraditionalCalendarMode.None, loaded.Settings.TraditionalCalendarMode);
            Assert.True(loaded.Settings.ShowPhotoControls);
            Assert.Equal(GlancePkg::DeskBox.Models.GlanceImageFitMode.Fill, loaded.Settings.ImageFit);
            Assert.Equal(30, loaded.Settings.RotationIntervalMinutes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptPrimaryFallsBackToBackup()
    {
        string root = Root();
        try
        {
            // First save creates the primary; a second save rotates the
            // previous content into the .bak via File.Replace.
            GlanceDataFile.Save(new GlanceData(new PackageData { RotationIntervalMinutes = 77 }, default), root);
            GlanceDataFile.Save(new GlanceData(new PackageData { RotationIntervalMinutes = 88 }, default), root);
            Assert.True(File.Exists(Path.Combine(root, GlanceDataFile.FileName + ".bak")));

            File.WriteAllText(Path.Combine(root, GlanceDataFile.FileName), "{ torn write");
            GlanceData? recovered = GlanceDataFile.Load(root);
            Assert.NotNull(recovered);
            Assert.Equal(77, recovered!.Settings.RotationIntervalMinutes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptFileDegradesToNull()
    {
        string root = Root();
        try
        {
            WriteData(root, "{ not json at all");
            Assert.Null(GlanceDataFile.Load(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
