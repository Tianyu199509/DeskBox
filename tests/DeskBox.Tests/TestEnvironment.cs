using System.Runtime.CompilerServices;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Assembly-wide test environment. DeviceIdentity resolves through
/// DeskBoxDataPathService.Current, which falls back to the real production
/// root when no dev-root environment variable is set — without this
/// override every store-normalization test would read or write device.id
/// under the real application data directory.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        DeviceIdentity.DataRootOverride = Path.Combine(
            Path.GetTempPath(), "deskbox-tests", "device-root");
    }
}
