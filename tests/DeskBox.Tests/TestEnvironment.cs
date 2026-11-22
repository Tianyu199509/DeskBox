using System.Runtime.CompilerServices;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Assembly-wide test environment. DeskBoxDataPathService.Current, DeviceIdentity,
/// and App.Log otherwise fall back to the real production profile under
/// %LOCALAPPDATA%\DeskBox when no dev-root environment variable is set — without
/// isolating DESKBOX_DEV_DATA_ROOT before the first SettingsService() or store
/// construction, any test calling the parameterless SettingsService() + SaveDebounced()
/// overwrites the developer's live settings.json with a blank schema-1 default graph.
/// </summary>
internal static class TestEnvironment
{
    internal static readonly string IsolatedDataRoot = Path.Combine(
        Path.GetTempPath(),
        "deskbox-tests",
        "data-root",
        $"pid-{Environment.ProcessId}");

    [ModuleInitializer]
    internal static void Initialize()
    {
        string? existingRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.DevelopmentRootEnvironmentVariable);
        string effectiveRoot = string.IsNullOrWhiteSpace(existingRoot)
            ? IsolatedDataRoot
            : existingRoot;
        if (string.IsNullOrWhiteSpace(existingRoot))
        {
            Environment.SetEnvironmentVariable(
                DeskBoxDataPathService.DevelopmentRootEnvironmentVariable,
                effectiveRoot);
        }

        DeskBoxDataPathService.TestRootOverride = effectiveRoot;
        DeviceIdentity.DataRootOverride = Path.Combine(
            Path.GetTempPath(), "deskbox-tests", "device-root");
        DeskBox.App.TestLogFilePathOverride = Path.Combine(
            Path.GetTempPath(), "deskbox-tests", "DeskBox.Tests.log");
    }
}
