using DeskBox.Services;
using DeskBox.Services.DesktopOrganization;

namespace DeskBox.Tests;

/// <summary>
/// Builds OrganizerService instances whose suppression ledger and recovery
/// journal live in per-construction temp files. Tests that constructed the
/// public OrganizerService ctor were silently loading and rewriting the real
/// %LOCALAPPDATA% stores (and racing the running app on them, which is where
/// the quarantined corrupt backups came from). The recovery journal default
/// matters as much as the ledger: a null path resolves to the production
/// data directory inside DesktopOrganizationRecoveryStore.
/// </summary>
internal static class TestOrganizerServices
{
    public static OrganizerService Create(
        SettingsService settingsService,
        FileService fileService,
        Func<string>? desktopPathProvider = null,
        string? recoveryJournalPath = null,
        DesktopIconPositionService? desktopIconPositions = null)
    {
        return new OrganizerService(
            settingsService,
            fileService,
            desktopPathProvider,
            new DesktopAutoOrganizationSuppressionRegistry(
                ledgerPath: NewTempStorePath("suppressions")),
            recoveryJournalPath ?? NewTempStorePath("recovery"),
            desktopIconPositions);
    }

    private static string NewTempStorePath(string role) => Path.Combine(
        Path.GetTempPath(),
        $"deskbox-test-{role}-{Guid.NewGuid():N}.json");
}
