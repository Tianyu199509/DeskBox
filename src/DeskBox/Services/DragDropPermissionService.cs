using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
#if !DESKBOX_NATIVE_AOT
using System.Text;
#endif
using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.Win32;

namespace DeskBox.Services;

public enum DragDropDiagnosticSeverity
{
    Ok,

    /// <summary>Environment worth knowing about, but nothing is broken on
    /// DeskBox's side (fully elevated desktops: features work, one path is
    /// Windows-limited). Distinct from Warning, which promises an actual
    /// DeskBox-side defect.</summary>
    Info,

    Warning,
    Error
}

public enum DragDropDiagnosticIssue
{
    None,
    UacDisabled,

    /// <summary>DeskBox and the Explorer shell are both elevated (built-in
    /// Administrator account, or another elevated desktop). Same-level
    /// drag-in and the tray work, but Windows/WinUI forbid XAML drag-out in
    /// an elevated process, so the session gets an informational
    /// explanation plus the restore path instead of silence.</summary>
    FullyElevatedSession,

    PermissionMismatch,
    AppCompatIssue,
    StartupShortcutIssue,
    ShellAboveApp,
    ShortcutRunAs
}

/// <summary>
/// Cause attribution for a fully elevated desktop session. The restore path
/// differs by cause — the built-in Administrator account needs the Admin
/// Approval Mode policy (FilterAdministratorToken), UAC-off needs UAC back
/// on — so the informed notice and the diagnostic card both branch on it.
/// </summary>
public enum FullyElevatedSessionAttribution
{
    /// <summary>Not a same-level elevated desktop (or DeskBox itself is not
    /// elevated) — no fully-elevated copy applies.</summary>
    None,

    UacDisabled,
    BuiltInAdminAccount,
    GenericElevatedDesktop
}

/// <summary>
/// Integrity relationship between the DeskBox process and the shell. OLE
/// drag/drop and Shell_NotifyIcon only work reliably when both sides sit at
/// the same integrity level, so the two actionable states are the asymmetric
/// ones. This is the single source of truth shared by the launch gate, the
/// settings diagnostic, and the updater's restart guard.
/// </summary>
public enum ShellIntegrityMismatch
{
    /// <summary>Process and Explorer share an integrity level; shell-level
    /// interaction (drag-in, tray) works. Fully elevated desktops land here
    /// too — OS-level drag is symmetric and fine, but WinUI cannot start
    /// XAML drags in an elevated process at all, so these sessions get a
    /// one-time informed notice (see
    /// <see cref="FullyElevatedSessionAttribution"/>) rather than a
    /// warning.</summary>
    None,

    /// <summary>DeskBox is elevated while Explorer is not: UIPI blocks OLE
    /// drag in both directions. Self-healable — relaunch unelevated.</summary>
    AppAboveShell,

    /// <summary>Explorer is elevated while DeskBox is not: UIPI blocks tray
    /// creation and cross-level drag. Not self-healable (the app cannot
    /// change the shell's token); degrade and explain instead.</summary>
    ShellAboveApp
}

public sealed record DragDropPermissionDiagnostic(
    DragDropDiagnosticSeverity Severity,
    DragDropDiagnosticIssue Issue,
    string Summary,
    string Detail,
    string CurrentProcessIntegrity,
    string ExplorerIntegrity,
    string UacStatus,
    string AppCompatStatus,
    string StartupStatus,
    string ShortcutStatus,
    bool IsCurrentProcessElevated,
    bool IsExplorerElevated,
    bool IsUacDisabled,
    bool HasAppCompatIssue,
    bool HasStartupIssue,
    bool HasShortcutIssue,
    bool NeedsRelaunch);

public sealed record DragDropPermissionRepairResult(
    bool Success,
    bool NeedsRelaunch,
    int RepairedCount,
    string FailureMessage,
    bool RequiresStartupSettings);

/// <summary>
/// Lightweight elevation snapshot for the launch gate: unlike
/// <see cref="DragDropPermissionService.Diagnose"/>, it touches no shortcuts,
/// startup registrations, or pending diagnostics — only the current token,
/// the Explorer token, and the UAC policy.
/// </summary>
public sealed record StartupElevationAssessment(
    bool IsCurrentProcessElevated,
    bool IsExplorerElevated,
    bool IsUacDisabled,
    bool CanRelaunchUnelevated,
    bool IsBuiltInAdminAccount = false)
{
    /// <summary>
    /// Asymmetric-integrity verdict derived from the two token snapshots.
    /// Same-level sessions read None: OS-level drag-in and the tray work at
    /// equal integrity, so the old asymmetry machinery stays out of their
    /// way.
    /// </summary>
    public ShellIntegrityMismatch Mismatch =>
        IsCurrentProcessElevated == IsExplorerElevated
            ? ShellIntegrityMismatch.None
            : IsCurrentProcessElevated
                ? ShellIntegrityMismatch.AppAboveShell
                : ShellIntegrityMismatch.ShellAboveApp;

    /// <summary>
    /// Cause attribution for same-level elevated desktops (2026-10-22
    /// informed-notice batch): same-level sessions are no longer fully
    /// silent — WinUI forbids XAML drag-out in an elevated process, so the
    /// one-time notice explains that limit and the restore path. Asymmetric
    /// sessions read None here; they keep their own notice machinery.
    /// </summary>
    public FullyElevatedSessionAttribution FullyElevatedAttribution =>
        DragDropPermissionService.DecideFullyElevatedSessionAttribution(
            IsCurrentProcessElevated,
            IsExplorerElevated,
            IsUacDisabled,
            IsBuiltInAdminAccount);
}

public static class DragDropPermissionService
{
    private const string AppCompatLayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string UacPolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string AppName = "DeskBox";
    private const uint TokenQuery = 0x0008;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SecurityMandatoryLowRid = 0x1000;
    private const int SecurityMandatoryMediumRid = 0x2000;
    private const int SecurityMandatoryHighRid = 0x3000;
    private const int SecurityMandatorySystemRid = 0x4000;
    private const int SecurityMandatoryProtectedProcessRid = 0x5000;

    private static readonly string[] IncompatibleAppCompatTokens =
    [
        "RUNASADMIN",
        "WIN95",
        "WIN98",
        "WIN2000",
        "WINXPSP2",
        "WINXPSP3",
        "VISTARTM",
        "VISTASP1",
        "VISTASP2",
        "WIN7RTM",
        "WIN8RTM"
    ];

    public static DragDropPermissionDiagnostic Diagnose(LocalizationService? localizationService = null)
    {
        string currentExePath = GetCurrentExePath();
        ProcessTokenSnapshot currentToken = GetCurrentProcessTokenSnapshot();
        ProcessTokenSnapshot explorerToken = GetExplorerTokenSnapshot();
        UacPolicySnapshot uacPolicy = GetUacPolicySnapshot();
        List<AppCompatEntry> appCompatEntries = GetRelevantAppCompatEntries(currentExePath);
        string? startupValue = StartupService.GetRunValue();
        StartupRegistrationState startupState = StartupService.GetState();
        List<ShortcutProbe> shortcutProbes = AppDistributionService.Current.IsMicrosoftStore
            ? []
            : GetShortcutProbes(currentExePath, localizationService);

        bool isUacDisabled = uacPolicy.EnableLua == 0;
        bool hasAppCompatIssue = appCompatEntries.Any(entry => ContainsIncompatibleAppCompatToken(entry.Value));
        bool hasStartupIssue =
            IsStartupValueSuspicious(startupValue, currentExePath) ||
            startupState is StartupRegistrationState.PathMismatch or
                StartupRegistrationState.BlockedOrFailed;
        bool hasShortcutIssue = shortcutProbes.Any(probe => probe.HasIssue);
        // Distinct from the generic shortcut issues: this one causes
        // elevation on every launch and is invisible to the registry-based
        // AppCompat scan, so it gets its own remediation text.
        bool hasShortcutRunAsIssue = shortcutProbes.Any(probe => probe.HasRunAsFlag);
        // Named elevation locals: the Decide* helpers below take
        // is-explorer-elevated, and a field review caught an inverted
        // literal (`IsElevated == false`) at one call site — named locals
        // keep every consumer on the same polarity.
        bool isCurrentElevated = currentToken.IsElevated == true;
        bool isExplorerElevated = explorerToken.IsElevated == true;
        bool permissionMismatch = isCurrentElevated && !isExplorerElevated;
        // Reverse asymmetry (Medium app under an elevated desktop): UIPI
        // blocks tray creation and cross-level drag here too, but the fix
        // is on the shell side, not a DeskBox relaunch.
        bool shellAboveApp = !isCurrentElevated && isExplorerElevated;
        FullyElevatedSessionAttribution fullyElevatedAttribution =
            DecideFullyElevatedSessionAttribution(
                isCurrentElevated,
                isExplorerElevated,
                isUacDisabled,
                DetectBuiltInAdminAccount());
        // Diagnostic-facing form of ComputeCanRelaunchUnelevated: "needs
        // relaunch" must mean "a relaunch would actually land unelevated".
        // The historical form (elevated && (!uacDisabled || !explorerLow))
        // returned true on fully elevated desktops with UAC on — lighting
        // the repair button and steering the user into a restart that
        // lands elevated again.
        bool needsRelaunch = ComputeNeedsRelaunchDiagnostic(
            isCurrentElevated,
            isUacDisabled,
            isExplorerElevated);

        DragDropDiagnosticSeverity severity =
            permissionMismatch || shellAboveApp || hasAppCompatIssue || hasShortcutRunAsIssue || hasStartupIssue || hasShortcutIssue
                ? DragDropDiagnosticSeverity.Warning
                : isUacDisabled || fullyElevatedAttribution != FullyElevatedSessionAttribution.None
                    ? DragDropDiagnosticSeverity.Info
                    : DragDropDiagnosticSeverity.Ok;

        DragDropDiagnosticIssue issue;
        string summary;
        string detail;
        // Environment causes outrank local ones, and asymmetry outranks
        // UAC-off: an asymmetric session has a DeskBox-side fix while
        // UAC-off is only the backdrop it happened in.
        DragDropDiagnosticIssue environmentIssue = DecideEnvironmentIssue(
            isUacDisabled,
            permissionMismatch,
            shellAboveApp,
            fullyElevatedAttribution);
        if (environmentIssue == DragDropDiagnosticIssue.PermissionMismatch)
        {
            issue = DragDropDiagnosticIssue.PermissionMismatch;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.PermissionMismatch");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.PermissionMismatch");
        }
        else if (environmentIssue == DragDropDiagnosticIssue.ShellAboveApp)
        {
            issue = DragDropDiagnosticIssue.ShellAboveApp;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.ShellAboveApp");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.ShellAboveApp");
        }
        else if (environmentIssue == DragDropDiagnosticIssue.UacDisabled)
        {
            issue = DragDropDiagnosticIssue.UacDisabled;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.UacDisabled");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.UacDisabled");
        }
        else if (environmentIssue == DragDropDiagnosticIssue.FullyElevatedSession)
        {
            issue = DragDropDiagnosticIssue.FullyElevatedSession;
            bool builtInAdmin =
                fullyElevatedAttribution == FullyElevatedSessionAttribution.BuiltInAdminAccount;
            summary = Text(localizationService, builtInAdmin
                ? "Settings.DragDropPermission.Summary.FullyElevatedAdminAccount"
                : "Settings.DragDropPermission.Summary.FullyElevatedGeneric");
            detail = Text(localizationService, builtInAdmin
                ? "Settings.DragDropPermission.Detail.FullyElevatedAdminAccount"
                : "Settings.DragDropPermission.Detail.FullyElevatedGeneric");
        }
        else if (hasShortcutRunAsIssue)
        {
            issue = DragDropDiagnosticIssue.ShortcutRunAs;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.ShortcutRunAs");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.ShortcutRunAs");
        }
        else if (hasAppCompatIssue)
        {
            issue = DragDropDiagnosticIssue.AppCompatIssue;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.AppCompatIssue");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.AppCompatIssue");
        }
        else if (hasStartupIssue || hasShortcutIssue)
        {
            issue = DragDropDiagnosticIssue.StartupShortcutIssue;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.StartupShortcutIssue");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.StartupShortcutIssue");
        }
        else
        {
            issue = DragDropDiagnosticIssue.None;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.Ok");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.Ok");
        }

        return new DragDropPermissionDiagnostic(
            severity,
            issue,
            summary,
            detail,
            FormatProcessToken("DeskBox", currentToken, localizationService),
            FormatProcessToken("Explorer", explorerToken, localizationService),
            FormatUacPolicy(uacPolicy, localizationService),
            FormatAppCompatStatus(appCompatEntries, localizationService),
            FormatStartupStatus(
                startupState,
                startupValue,
                currentExePath,
                localizationService),
            FormatShortcutStatus(shortcutProbes, localizationService),
            currentToken.IsElevated == true,
            explorerToken.IsElevated == true,
            isUacDisabled,
            hasAppCompatIssue,
            hasStartupIssue,
            hasShortcutIssue,
            needsRelaunch);
    }

    /// <summary>
    /// Localized environment-cause lines for shell-environment conditions
    /// known to break startup surfaces: an elevated desktop running above
    /// this process, and incompatible compatibility layers. Empty when
    /// nothing was diagnosed. The fatal-startup dialog and the degraded
    /// tray notice append this so the fix path reaches the user instead of
    /// living only in the log.
    /// </summary>
    public static string BuildStartupEnvironmentIssueSummary(LocalizationService? localizationService)
    {
        var lines = new List<string>();

        if (GetCurrentProcessTokenSnapshot().IsElevated == false &&
            GetExplorerTokenSnapshot().IsElevated == true)
        {
            lines.Add(Text(localizationService, "Settings.DragDropPermission.Summary.ShellAboveApp"));
        }

        string exePath = GetCurrentExePath();
        if (GetRelevantAppCompatEntries(exePath).Any(
                entry => ContainsIncompatibleAppCompatToken(entry.Value)))
        {
            lines.Add(Text(localizationService, "Settings.DragDropPermission.Summary.AppCompatIssue"));
        }

        if (!AppDistributionService.Current.IsMicrosoftStore &&
            GetKnownShortcutPaths().Any(path =>
                File.Exists(path) && TryGetShortcutRunAsFlag(path)))
        {
            lines.Add(Text(localizationService, "Settings.DragDropPermission.Summary.ShortcutRunAs"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Single source of truth for "is this process elevated".
    /// WindowsPrincipal.IsInRole is false on a UAC filtered token (the
    /// Administrators group is deny-only there) and true on the elevated
    /// token — unlike an Owner-vs-User comparison, which merely reports
    /// whether the *user* sits in the Administrators group (a filtered
    /// token's default owner stays the Administrators SID) and therefore
    /// misfires on every admin-account machine.
    /// </summary>
    private static bool? s_cachedIsCurrentProcessElevated;

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cached per-process wrapper around <see cref="IsCurrentProcessElevated"/>
    /// for UI constructors and pointer hot paths (process token elevation is
    /// immutable for the lifetime of a Windows process).
    /// </summary>
    public static bool IsCurrentProcessElevatedCached() =>
        s_cachedIsCurrentProcessElevated ??= IsCurrentProcessElevated();

    public static StartupElevationAssessment AssessStartupElevation()
    {
        ProcessTokenSnapshot currentToken = GetCurrentProcessTokenSnapshot();
        // The Explorer probe now runs for every launch, not just elevated
        // ones: the ShellAboveApp direction (Medium app under an elevated
        // desktop) decides the tray-degraded notice and the lifeline
        // exemption. A failed probe reads as unelevated, so the worst case
        // is offering the one-click relaunch whose own fallback re-routes
        // through ShellExecute when Explorer is genuinely unavailable.
        ProcessTokenSnapshot explorerToken = GetExplorerTokenSnapshot();
        UacPolicySnapshot uacPolicy = GetUacPolicySnapshot();
        bool isUacDisabled = uacPolicy.EnableLua == 0;
        bool isCurrentElevated = currentToken.IsElevated == true;
        bool isExplorerElevated = explorerToken.IsElevated == true;
        return new StartupElevationAssessment(
            isCurrentElevated,
            isExplorerElevated,
            isUacDisabled,
            ComputeCanRelaunchUnelevated(isUacDisabled, isExplorerElevated),
            DetectBuiltInAdminAccount());
    }

    /// <summary>
    /// Stricter than Diagnose's NeedsRelaunch: the Explorer-parent relaunch
    /// only produces an unelevated instance when Explorer itself runs
    /// unelevated (with UAC on). With UAC off — or an elevated Explorer —
    /// the parent-process handover would spawn another elevated instance,
    /// so the notice must explain instead of offering a restart.
    /// </summary>
    public static bool ComputeCanRelaunchUnelevated(bool isUacDisabled, bool isExplorerElevated)
    {
        return !isUacDisabled && !isExplorerElevated;
    }

    /// <summary>
    /// Diagnostic-facing twin of <see cref="ComputeCanRelaunchUnelevated"/>:
    /// "needs relaunch" must mean "a relaunch would actually land
    /// unelevated". The historical form returned true on fully elevated
    /// desktops with UAC on, lighting the repair button and steering the
    /// user into a pointless restart round-trip.
    /// </summary>
    internal static bool ComputeNeedsRelaunchDiagnostic(
        bool isCurrentProcessElevated,
        bool isUacDisabled,
        bool isExplorerElevated)
    {
        return isCurrentProcessElevated && !isUacDisabled && !isExplorerElevated;
    }

    /// <summary>
    /// Cause attribution for a same-level elevated desktop. Pure so the
    /// priority (UAC-off explanation first, then built-in Administrator,
    /// then generic) stays pinned by contract tests. Only symmetric
    /// elevated sessions attribute; asymmetric ones are None because they
    /// have their own fix machinery.
    /// </summary>
    public static FullyElevatedSessionAttribution DecideFullyElevatedSessionAttribution(
        bool isCurrentProcessElevated,
        bool isExplorerElevated,
        bool isUacDisabled,
        bool isBuiltInAdminAccount)
    {
        if (!isCurrentProcessElevated || !isExplorerElevated)
        {
            return FullyElevatedSessionAttribution.None;
        }

        if (isUacDisabled)
        {
            return FullyElevatedSessionAttribution.UacDisabled;
        }

        return isBuiltInAdminAccount
            ? FullyElevatedSessionAttribution.BuiltInAdminAccount
            : FullyElevatedSessionAttribution.GenericElevatedDesktop;
    }

    /// <summary>
    /// Pure environment-issue selection for the diagnostic card: integrity
    /// asymmetry outranks UAC-off (an asymmetric session has a DeskBox-side
    /// fix while UAC-off is only the backdrop), and the fully-elevated
    /// family ranks below both. Local issues (compat layers, startup,
    /// shortcuts) only surface when the environment is clean.
    /// </summary>
    internal static DragDropDiagnosticIssue DecideEnvironmentIssue(
        bool isUacDisabled,
        bool permissionMismatch,
        bool shellAboveApp,
        FullyElevatedSessionAttribution fullyElevatedAttribution)
    {
        if (permissionMismatch)
        {
            return DragDropDiagnosticIssue.PermissionMismatch;
        }

        if (shellAboveApp)
        {
            return DragDropDiagnosticIssue.ShellAboveApp;
        }

        if (isUacDisabled || fullyElevatedAttribution == FullyElevatedSessionAttribution.UacDisabled)
        {
            return DragDropDiagnosticIssue.UacDisabled;
        }

        return fullyElevatedAttribution is FullyElevatedSessionAttribution.BuiltInAdminAccount
            or FullyElevatedSessionAttribution.GenericElevatedDesktop
            ? DragDropDiagnosticIssue.FullyElevatedSession
            : DragDropDiagnosticIssue.None;
    }

    /// <summary>
    /// The built-in Administrator is the only account whose SID ends in
    /// relative identifier 500 (S-1-5-&lt;authority&gt;-500). The account
    /// name is not a signal — it is frequently renamed, and unrelated
    /// accounts can be named "Administrator" — so the RID is the only
    /// stable identity. Split-parsed rather than EndsWith so e.g. ...-1500
    /// never matches.
    /// </summary>
    internal static bool IsBuiltInAdministratorSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return false;
        }

        // SID components are hyphen-separated (S-1-5-<authority>-500).
        string[] parts = sid.Split('-');
        return parts.Length >= 2 && parts[^1] == "500";
    }

    private static bool DetectBuiltInAdminAccount()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return IsBuiltInAdministratorSid(identity.User?.Value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the AppCompat RUNASADMIN-style layers that keep relaunching
    /// DeskBox elevated. Used by Repair and by the launch gate before an
    /// unelevated relaunch, so the next start is not elevated again.
    /// Failures go to <paramref name="failures"/> when supplied (Repair's
    /// report), otherwise to the log only (launch path).
    /// </summary>
    public static int ClearElevationCompatibilityFlags(List<string>? failures = null)
    {
        string currentExePath = GetCurrentExePath();
        int clearedCount = 0;

        foreach (var entry in GetRelevantAppCompatEntries(currentExePath))
        {
            if (!ContainsIncompatibleAppCompatToken(entry.Value))
            {
                continue;
            }

            try
            {
                // The per-user branch deliberately bypasses the current-user
                // hive shortcut: inside an MSIX container that view is
                // copy-on-write virtualized, so the delete would land in the
                // package-private hive while the real layer survives (and the
                // repair would count it as cleared). HKLM only accepts writes
                // when the process is elevated, which the failure list
                // already covers.
                RegistryKey? key = entry.RootName == PerUserRootName
                    ? OpenPerUserAppCompatLayersKey(writable: true)
                    : Registry.LocalMachine.OpenSubKey(AppCompatLayersKey, writable: true);
                if (key is not null)
                {
                    using (key)
                    {
                        key.DeleteValue(entry.ExePath, throwOnMissingValue: false);
                    }

                    clearedCount++;
                    App.Log($"[DragDropPermission] Removed AppCompat layer root={entry.RootName} path='{entry.ExePath}' value='{entry.Value}'");
                }
            }
            catch (Exception ex)
            {
                if (failures is null)
                {
                    App.Log($"[DragDropPermission] AppCompat clear failed for '{entry.ExePath}': {ex.Message}");
                }
                else
                {
                    failures.Add($"AppCompat {entry.ExePath}: {ex.Message}");
                }
            }
        }

        return clearedCount;
    }

    public static DragDropPermissionRepairResult Repair(SettingsService settingsService)
    {
        string currentExePath = GetCurrentExePath();
        var before = Diagnose();
        List<string> failures = [];
        bool requiresStartupSettings = false;
        int repairedCount = ClearElevationCompatibilityFlags(failures);

        try
        {
            StartupRegistrationState startupStateBefore = StartupService.GetState();
            if (settingsService.Settings.AutoStart ||
                startupStateBefore == StartupRegistrationState.Enabled)
            {
                StartupOperationResult startupResult = StartupService.Enable();
                if (startupResult.State == StartupRegistrationState.Enabled)
                {
                    if (startupStateBefore != StartupRegistrationState.Enabled)
                    {
                        repairedCount++;
                    }
                }
                else if (startupResult.State != StartupRegistrationState.Pending)
                {
                    requiresStartupSettings =
                        startupResult.RequiresSystemSettings;
                    string startupFailure = string.IsNullOrWhiteSpace(
                        startupResult.ErrorMessage)
                        ? $"Startup remained in state {startupResult.State}."
                        : startupResult.ErrorMessage;
                    failures.Add($"Startup: {startupFailure}");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Startup: {ex.Message}");
        }

        if (!AppDistributionService.Current.IsMicrosoftStore)
        {
            foreach (var shortcut in GetKnownShortcutDefinitions())
            {
                try
                {
                    if (!shortcut.EnsureExists && !File.Exists(shortcut.Path))
                    {
                        continue;
                    }

                    CreateOrUpdateShortcut(
                        shortcut.Path,
                        currentExePath,
                        shortcut.Arguments,
                        DeskBoxIconPath);
                    repairedCount++;
                    App.Log($"[DragDropPermission] Rewrote shortcut '{shortcut.Path}' target='{currentExePath}' args='{shortcut.Arguments}'");
                }
                catch (Exception ex)
                {
                    failures.Add($"Shortcut {shortcut.Path}: {ex.Message}");
                }
            }
        }

        string failureMessage = failures.Count == 0 ? string.Empty : string.Join("; ", failures);

        // Same predicate as the diagnostic's NeedsRelaunch: on a fully
        // elevated desktop a restart lands elevated again, so the repair
        // flow must not steer the user into one even when local entries
        // (shortcuts, compat layers) were genuinely fixed.
        bool needsRelaunch = ComputeNeedsRelaunchDiagnostic(
            before.IsCurrentProcessElevated,
            before.IsUacDisabled,
            before.IsExplorerElevated);
        return new DragDropPermissionRepairResult(
            failures.Count == 0,
            needsRelaunch,
            repairedCount,
            failureMessage,
            requiresStartupSettings);
    }

    /// <summary>
    /// Relaunches this executable at Explorer's (standard-user) integrity via
    /// PROC_THREAD_ATTRIBUTE_PARENT_PROCESS (see
    /// <see cref="DeskBox.Platform.ShellUnelevatedLaunch"/>). When
    /// <paramref name="additionalArguments"/> is given it is appended to the
    /// new instance's command line — the launch gate passes
    /// "--await-parent-exit &lt;pid&gt;" (plus "--startup" for unattended
    /// launches) so the new instance waits out the elevated holder of the
    /// single-instance mutex instead of racing it. Falls back to a plain
    /// ShellExecute relaunch when the shell-parented creation is impossible.
    /// </summary>
    public static bool TryRelaunchAsExplorerUser(string additionalArguments = "")
    {
        if (AppDistributionService.Current.IsMicrosoftStore &&
            AppDistributionService.Current.TryGetPackagedAppUserModelId() is { Length: > 0 } aumid)
        {
            if (PackagedUnelevatedRelaunch.TryRelaunchPackagedAppViaExplorer(
                    aumid,
                    additionalArguments,
                    App.Log))
            {
                return true;
            }

            // Secondary Store channel: parent explorer.exe to the desktop shell
            // to activate shell:AppsFolder\<AUMID> with Medium integrity and
            // preserved MSIX package identity when COM ShellWindows automation
            // is unavailable. Guarded by IsShellWindowElevated() == false so an
            // elevated desktop can never enter a relaunch loop.
            if (ShellUnelevatedLaunch.IsShellWindowElevated() == false &&
                PackagedUnelevatedRelaunch.TryFormatAppsFolderTarget(aumid, out string appsFolderTarget))
            {
                string explorerExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe");
                if (ShellUnelevatedLaunch.TryStartAtShellIntegrity(
                        explorerExe,
                        appsFolderTarget,
                        App.Log))
                {
                    return true;
                }
            }

            return false;
        }

        string exePath = GetCurrentExePath();
        if (!File.Exists(exePath))
        {
            return false;
        }

        if (ShellUnelevatedLaunch.TryStartAtShellIntegrity(exePath, additionalArguments, App.Log))
        {
            return true;
        }

        // Secondary Direct channel: delegate ShellExecute to Explorer's hosted
        // IShellDispatch2 object when PROC_THREAD_ATTRIBUTE_PARENT_PROCESS is
        // blocked (e.g. third-party HIPS filtering OpenProcess on explorer.exe),
        // provided Explorer itself runs unelevated.
        if (ShellUnelevatedLaunch.IsShellWindowElevated() == false &&
            PackagedUnelevatedRelaunch.TryShellExecuteViaExplorer(
                exePath,
                additionalArguments,
                Path.GetDirectoryName(exePath),
                App.Log))
        {
            return true;
        }

        // Never fall back to plain in-process ShellExecute while elevated: it
        // inherits the elevated token (re-triggering the elevation gate loop)
        // instead of landing at Explorer's Medium integrity.
        if (IsCurrentProcessElevated())
        {
            return false;
        }

        return TryShellExecute(exePath, additionalArguments);
    }

    internal static string ResolveUserAccountControlSettingsLauncherPath(
        string? systemDirectoryOverride = null)
    {
        string systemDirectory = string.IsNullOrWhiteSpace(systemDirectoryOverride)
            ? Environment.SystemDirectory
            : systemDirectoryOverride;
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            string candidate = Path.Combine(
                systemDirectory,
                "UserAccountControlSettings.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "UserAccountControlSettings.exe";
    }

    public static bool OpenUserAccountControlSettings(IntPtr ownerWindow = default)
    {
        string target = ResolveUserAccountControlSettingsLauncherPath();
        return Win32Helper.OpenFile(ownerWindow, target);
    }

    private static bool TryShellExecute(string exePath, string additionalArguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = additionalArguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
            });
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[DragDropPermission] Relaunch failed: {ex}");
            return false;
        }
    }

    private static string GetCurrentExePath()
    {
        return Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "DeskBox.exe");
    }

    private static bool IsStartupValueSuspicious(string? startupValue, string currentExePath)
    {
        if (string.IsNullOrWhiteSpace(startupValue))
        {
            return false;
        }

        string? path = ExtractExecutablePath(startupValue);
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        return !string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentExePath), StringComparison.OrdinalIgnoreCase) ||
               ContainsIncompatibleAppCompatToken(startupValue);
    }

    private static string? ExtractExecutablePath(string commandLine)
    {
        string trimmed = commandLine.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] == '"')
        {
            int endQuote = trimmed.IndexOf('"', 1);
            return endQuote > 1 ? trimmed[1..endQuote] : null;
        }

        int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            return trimmed[..(exeIndex + 4)];
        }

        int firstSpace = trimmed.IndexOf(' ');
        return firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
    }

    private static List<AppCompatEntry> GetRelevantAppCompatEntries(string currentExePath)
    {
        // Fixed known-install locations only. Deliberately closed channels:
        // the HKCU Run value used to be absorbed here, but any same-user
        // process can rewrite it, which would make an elevated instance
        // delete arbitrary HKLM AppCompat layers; and the historical
        // any-directory "DeskBox.exe" filename fallback matched third-party
        // exes with the same name. Custom install directories keep their
        // stale layers (Repair reports them as unrelated); clearing those is
        // the user's call, not a guessable side effect.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentExePath,
            Path.Combine(AppContext.BaseDirectory, "DeskBox.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "DeskBox",
                "DeskBox.exe")
        };

        var entries = new List<AppCompatEntry>();
        // Both sides of the per-user branch (read and clear) go through
        // HKU\<SID>: under MSIX, a virtualized delete from an earlier Store
        // build can even tombstone the value in the container's private
        // hive, hiding the still-present real layer from HKCU reads here.
        AddAppCompatEntries(
            entries,
            () => OpenPerUserAppCompatLayersKey(writable: false),
            PerUserRootName,
            paths);
        AddAppCompatEntries(
            entries,
            () => Registry.LocalMachine.OpenSubKey(AppCompatLayersKey),
            "HKLM",
            paths);
        return entries;
    }

    private const string PerUserRootName = "HKCU";

    /// <summary>
    /// Opens the current user's AppCompat Layers key through HKU\<SID>
    /// instead of HKCU. Same rationale as the Run entry store: explicit user
    /// identity bypasses the MSIX copy-on-write registry virtualization that
    /// would otherwise swallow Repair's deletes (and mask prior virtualized
    /// deletes from diagnose reads) in packaged builds.
    /// </summary>
    private static RegistryKey? OpenPerUserAppCompatLayersKey(bool writable)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string? sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            return null;
        }

        using RegistryKey users = RegistryKey.OpenBaseKey(
            RegistryHive.Users,
            RegistryView.Default);
        return users.OpenSubKey($@"{sid}\{AppCompatLayersKey}", writable);
    }

    private static void AddAppCompatEntries(
        List<AppCompatEntry> entries,
        Func<RegistryKey?> openKey,
        string rootName,
        HashSet<string> relevantPaths)
    {
        try
        {
            using var key = openKey();
            if (key is null)
            {
                return;
            }

            foreach (string valueName in key.GetValueNames())
            {
                if (!IsRelevantDeskBoxPath(valueName, relevantPaths))
                {
                    continue;
                }

                if (key.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    entries.Add(new AppCompatEntry(rootName, valueName, value));
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DragDropPermission] Failed to read AppCompat {rootName}: {ex.Message}");
        }
    }

    private static bool IsRelevantDeskBoxPath(string valueName, HashSet<string> relevantPaths)
    {
        // Exact full-path matches against the known install locations only;
        // see GetRelevantAppCompatEntries for why the historical filename
        // fallback is gone.
        return relevantPaths.Contains(valueName);
    }

    private static bool ContainsIncompatibleAppCompatToken(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               IncompatibleAppCompatTokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatAppCompatStatus(List<AppCompatEntry> entries, LocalizationService? localizationService)
    {
        if (entries.Count == 0)
        {
            return Text(localizationService, "Settings.DragDropPermission.Status.NoAppCompatFlags");
        }

        return string.Join(Environment.NewLine, entries.Select(entry =>
            $"{entry.RootName}: {entry.Value} ({entry.ExePath})"));
    }

    private static string FormatStartupStatus(
        StartupRegistrationState startupState,
        string? startupValue,
        string currentExePath,
        LocalizationService? localizationService)
    {
        if (startupState == StartupRegistrationState.DisabledByUser)
        {
            return Text(localizationService, "Settings.AutoStart.WindowsDisabled");
        }

        if (startupState == StartupRegistrationState.Pending)
        {
            return Text(localizationService, "Settings.AutoStart.Pending");
        }

        if (string.IsNullOrWhiteSpace(startupValue))
        {
            return Text(localizationService, "Settings.DragDropPermission.Status.StartupDisabled");
        }

        return IsStartupValueSuspicious(startupValue, currentExePath)
            ? Format(localizationService, "Settings.DragDropPermission.Status.Check", startupValue)
            : Format(localizationService, "Settings.DragDropPermission.Status.Normal", startupValue);
    }

    private const uint SldfRunAsUser = 0x0000_2000;

    // Standalone COM reader for a shortcut's "Run as administrator" flag,
    // deliberately independent of the TryReadShortcut backend split: the
    // flag lives in the link header (IShellLinkDataList::GetFlags /
    // SLDF_RUNAS_USER), not in target/arguments, and a .lnk with this bit
    // relaunches DeskBox elevated on every open, which the registry-based
    // AppCompat scan cannot see.
    //
    // Raw vtable slots throughout (see ComVtable): the old ComImport form
    // died twice — the co-class constructor was an InvalidProgramException
    // under NativeAOT (retail acceptance 2026-10-20), and the interface
    // declaration was missing CopyDataBlock, so GetFlags sat on
    // RemoveDataBlock's slot and never returned a flags value even on JIT
    // (real order per ShObjIdl_core.h: AddDataBlock 3, CopyDataBlock 4,
    // RemoveDataBlock 5, GetFlags 6, SetFlags 7).
    //
    // IPersistFile order per objidl.h: GetClassID 3, IsDirty 4, Load 5,
    // Save 6, SaveCompleted 7, GetCurFile 8.
    private static readonly Guid s_clsid_ShellLink =
        new("00021401-0000-0000-C000-000000000046");

    private static readonly Guid s_iid_IPersistFile =
        new("0000010b-0000-0000-C000-000000000046");

    private static readonly Guid s_iid_IShellLinkDataList =
        new("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PersistFileLoadInvoker(
        IntPtr self,
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        uint mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PersistFileSaveInvoker(
        IntPtr self,
        [MarshalAs(UnmanagedType.LPWStr)] string? fileName,
        int remember);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkDataListGetFlagsInvoker(
        IntPtr self, out uint dwFlags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellLinkDataListSetFlagsInvoker(
        IntPtr self, uint dwFlags);

    private const uint StgmReadWrite = 0x0000_0002;

    /// <summary>
    /// Clears <c>SLDF_RUNAS_USER</c> in-place on known DeskBox shortcuts when
    /// set, preserving any custom icon, hotkey, description, or working
    /// directory configured by the user. Safe to run on the silent startup
    /// de-elevation path without recreating missing shortcuts.
    /// </summary>
    public static int ClearShortcutRunAsFlags(List<string>? failures = null)
    {
        if (AppDistributionService.Current.IsMicrosoftStore)
        {
            return 0;
        }

        int clearedCount = 0;
        foreach (string shortcutPath in GetKnownShortcutPaths())
        {
            if (!File.Exists(shortcutPath))
            {
                continue;
            }

            if (TryClearShortcutRunAsFlag(shortcutPath, out bool wasModified, out string? error))
            {
                if (wasModified)
                {
                    clearedCount++;
                    App.Log($"[DragDropPermission] Cleared SLDF_RUNAS_USER in-place on '{shortcutPath}'");
                }
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                if (failures is null)
                {
                    App.Log($"[DragDropPermission] Failed to clear SLDF_RUNAS_USER on '{shortcutPath}': {error}");
                }
                else
                {
                    failures.Add($"Shortcut RunAs {shortcutPath}: {error}");
                }
            }
        }

        return clearedCount;
    }

    internal static bool TryClearShortcutRunAsFlag(
        string shortcutPath,
        out bool wasModified,
        out string? error) =>
        TryUpdateShortcutRunAsFlag(shortcutPath, enableRunAs: false, out wasModified, out error);

    internal static bool TrySetShortcutRunAsFlag(
        string shortcutPath,
        bool enableRunAs,
        out bool wasModified,
        out string? error) =>
        TryUpdateShortcutRunAsFlag(shortcutPath, enableRunAs, out wasModified, out error);

    private static bool TryUpdateShortcutRunAsFlag(
        string shortcutPath,
        bool enableRunAs,
        out bool wasModified,
        out string? error)
    {
        wasModified = false;
        error = null;
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
        {
            return true;
        }

        IntPtr shellLink = IntPtr.Zero;
        try
        {
            Guid clsid = s_clsid_ShellLink;
            Guid persistFileIid = s_iid_IPersistFile;
            if (!ComVtable.TryCreateInProcessInstance(
                    ref clsid,
                    ref persistFileIid,
                    out shellLink))
            {
                error = "CoCreateInstance(CLSID_ShellLink) failed";
                return false;
            }

            int loadHr = ComVtable.Slot<PersistFileLoadInvoker>(shellLink, 5)(
                shellLink,
                shortcutPath,
                StgmReadWrite);
            if (loadHr < 0)
            {
                // Fall back to read mode if share/mode flags were rejected;
                // IPersistFile::Save receives shortcutPath explicitly below.
                loadHr = ComVtable.Slot<PersistFileLoadInvoker>(shellLink, 5)(
                    shellLink,
                    shortcutPath,
                    0);
                if (loadHr < 0)
                {
                    error = $"IPersistFile::Load hr=0x{loadHr:X8}";
                    return false;
                }
            }

            Guid dataListIid = s_iid_IShellLinkDataList;
            int qiHr = ComVtable.QueryInterface(
                shellLink,
                ref dataListIid,
                out IntPtr dataList);
            if (qiHr < 0 || dataList == IntPtr.Zero)
            {
                error = $"QI(IShellLinkDataList) hr=0x{qiHr:X8}";
                return false;
            }

            try
            {
                int getHr = ComVtable.Slot<ShellLinkDataListGetFlagsInvoker>(
                    dataList,
                    6)(dataList, out uint flags);
                if (getHr < 0)
                {
                    error = $"IShellLinkDataList::GetFlags hr=0x{getHr:X8}";
                    return false;
                }

                uint updatedFlags = enableRunAs
                    ? (flags | SldfRunAsUser)
                    : (flags & ~SldfRunAsUser);
                if (updatedFlags == flags)
                {
                    return true;
                }

                int setHr = ComVtable.Slot<ShellLinkDataListSetFlagsInvoker>(
                    dataList,
                    7)(dataList, updatedFlags);
                if (setHr < 0)
                {
                    error = $"IShellLinkDataList::SetFlags hr=0x{setHr:X8}";
                    return false;
                }
            }
            finally
            {
                Marshal.Release(dataList);
            }

            int saveHr = ComVtable.Slot<PersistFileSaveInvoker>(shellLink, 6)(
                shellLink,
                shortcutPath,
                1);
            if (saveHr < 0)
            {
                error = $"IPersistFile::Save hr=0x{saveHr:X8}";
                return false;
            }

            wasModified = true;
            ShortcutHelper.InvalidateStoredMetadataCache(shortcutPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (shellLink != IntPtr.Zero)
            {
                Marshal.Release(shellLink);
            }
        }
    }

    internal static bool TryGetShortcutRunAsFlag(string shortcutPath)
    {
        IntPtr shellLink = IntPtr.Zero;
        try
        {
            Guid clsid = s_clsid_ShellLink;
            Guid persistFileIid = s_iid_IPersistFile;
            if (!ComVtable.TryCreateInProcessInstance(
                    ref clsid,
                    ref persistFileIid,
                    out shellLink))
            {
                return false;
            }

            int loadHr = ComVtable.Slot<PersistFileLoadInvoker>(shellLink, 5)(
                shellLink,
                shortcutPath,
                0);
            if (loadHr < 0)
            {
                return false;
            }

            Guid dataListIid = s_iid_IShellLinkDataList;
            int qiHr = ComVtable.QueryInterface(
                shellLink,
                ref dataListIid,
                out IntPtr dataList);
            if (qiHr < 0 || dataList == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                int flagsHr =
                    ComVtable.Slot<ShellLinkDataListGetFlagsInvoker>(
                        dataList,
                        6)(dataList, out uint flags);
                return flagsHr == 0 && (flags & SldfRunAsUser) != 0;
            }
            finally
            {
                Marshal.Release(dataList);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[DragDropPermission] Run-as flag probe failed for '{shortcutPath}': {ex.Message}");
            return false;
        }
        finally
        {
            if (shellLink != IntPtr.Zero)
            {
                Marshal.Release(shellLink);
            }
        }
    }

    private static List<ShortcutProbe> GetShortcutProbes(string currentExePath, LocalizationService? localizationService)
    {
        var probes = new List<ShortcutProbe>();
        foreach (var shortcut in GetKnownShortcutDefinitions())
        {
            string shortcutPath = shortcut.Path;
            if (!File.Exists(shortcutPath))
            {
                continue;
            }

            bool hasIssue = false;
            string reason = Text(localizationService, "Settings.DragDropPermission.Status.Present");
            bool hasRunAsFlag = false;
            try
            {
                if (TryReadShortcut(shortcutPath, out string? targetPath, out string? arguments))
                {
                    hasIssue =
                        !string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(currentExePath), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(arguments?.Trim() ?? string.Empty, shortcut.Arguments.Trim(), StringComparison.OrdinalIgnoreCase);
                    reason = hasIssue
                        ? Format(localizationService, "Settings.DragDropPermission.Status.Target", $"{targetPath} {arguments}".Trim())
                        : Text(localizationService, "Settings.DragDropPermission.Status.TargetNormal");
                }
                else
                {
                    hasIssue = true;
                    reason = Text(localizationService, "Settings.DragDropPermission.Status.ShortcutTargetUnreadable");
                }

                hasRunAsFlag = TryGetShortcutRunAsFlag(shortcutPath);
                if (hasRunAsFlag)
                {
                    // The registry-based AppCompat scan cannot see this
                    // elevation source; surface it at the probe level too so
                    // the shortcut status line explains itself.
                    hasIssue = true;
                    reason = Text(localizationService, "Settings.DragDropPermission.Status.ShortcutRunAs");
                }
            }
            catch (Exception ex)
            {
                hasIssue = true;
                reason = Format(localizationService, "Settings.DragDropPermission.Status.CheckFailed", ex.Message);
            }

            probes.Add(new ShortcutProbe(shortcutPath, hasIssue, reason, hasRunAsFlag));
        }

        return probes;
    }

    private static string[] GetKnownShortcutPaths()
    {
        return GetKnownShortcutDefinitions().Select(definition => definition.Path).ToArray();
    }

    private static ShortcutDefinition[] GetKnownShortcutDefinitions()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        return
        [
            new ShortcutDefinition(Path.Combine(desktop, $"{AppName}.lnk"), string.Empty, false),
            new ShortcutDefinition(Path.Combine(programs, $"{AppName}.lnk"), string.Empty, true)
        ];
    }

    private static string FormatShortcutStatus(List<ShortcutProbe> probes, LocalizationService? localizationService)
    {
        if (probes.Count == 0)
        {
            return Text(localizationService, "Settings.DragDropPermission.Status.NoShortcuts");
        }

        return string.Join(Environment.NewLine, probes.Select(probe =>
            Format(localizationService,
                probe.HasIssue ? "Settings.DragDropPermission.Status.CheckPath" : "Settings.DragDropPermission.Status.NormalPath",
                probe.Path,
                probe.Reason)));
    }

    private static bool TryReadShortcut(string shortcutPath, out string targetPath, out string arguments)
    {
#if DESKBOX_NATIVE_AOT
        ShortcutNativeCallResult native =
            ShortcutNativeBackend.ReadEffectiveDiagnostic(shortcutPath);
        targetPath = native.Metadata?.TargetPath ?? string.Empty;
        arguments = native.Metadata?.Arguments ?? string.Empty;
        if (!native.Success)
        {
            App.Log(
                $"[DragDropPermission] Rust shortcut read failed for " +
                $"'{shortcutPath}': {native.Failure}; {native.Detail}");
        }

        return native.Success;
#else
        if (ShortcutBackendPolicy.Current == ShortcutBackendMode.Rust)
        {
            ShortcutNativeCallResult native =
                ShortcutNativeBackend.ReadEffectiveDiagnostic(shortcutPath);
            targetPath = native.Metadata?.TargetPath ?? string.Empty;
            arguments = native.Metadata?.Arguments ?? string.Empty;
            if (!native.Success)
            {
                App.Log(
                    $"[DragDropPermission] Explicit Rust shortcut read failed for " +
                    $"'{shortcutPath}': {native.Failure}; {native.Detail}");
            }

            return native.Success;
        }

        return TryReadShortcutWithCSharp(shortcutPath, out targetPath, out arguments);
#endif
    }

#if !DESKBOX_NATIVE_AOT
    internal static bool TryReadShortcutWithCSharp(
        string shortcutPath,
        out string targetPath,
        out string arguments)
    {
        targetPath = string.Empty;
        arguments = string.Empty;

        try
        {
            var shellLink = (IShellLinkW)(object)new ShellLink();
            var persistFile = (System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink;
            persistFile.Load(shortcutPath, 0);

            var pathBuffer = new StringBuilder(260);
            IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<Win32FindDataW>());
            try
            {
                shellLink.GetPath(pathBuffer, pathBuffer.Capacity, data, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }

            var argumentBuffer = new StringBuilder(512);
            shellLink.GetArguments(argumentBuffer, argumentBuffer.Capacity);
            targetPath = pathBuffer.ToString().Trim();
            arguments = argumentBuffer.ToString().Trim();
            return !string.IsNullOrWhiteSpace(targetPath);
        }
        catch (Exception ex)
        {
            App.Log($"[DragDropPermission] Failed to read shortcut '{shortcutPath}': {ex.Message}");
            return false;
        }
    }
#endif

    // Icon of the DeskBox application itself, for shortcuts whose target is
    // DeskBox.exe. Shortcuts to user files must pass null instead so the
    // system renders the target's own icon (see Shell Links docs).
    internal static readonly string DeskBoxIconPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", "deskbox.ico");

    internal static void CreateOrUpdateShortcut(
        string shortcutPath,
        string targetPath,
        string arguments,
        string? iconPath)
    {
        string? directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string workingDirectory = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory;
        string effectiveIconPath = iconPath ?? string.Empty;
#if DESKBOX_NATIVE_AOT
        var metadata = new ShortcutInfo(
            targetPath,
            string.Empty,
            arguments,
            workingDirectory,
            effectiveIconPath,
            0);
        ShortcutNativeWriteCallResult native =
            ShortcutNativeBackend.WriteShortcut(shortcutPath, metadata);
        if (!native.Success)
        {
            App.Log(
                $"[DragDropPermission] Rust shortcut write failed for " +
                $"'{shortcutPath}': {native.Failure}; {native.Detail}");
            throw new InvalidOperationException(
                $"Rust shortcut write failed: {native.Failure}; {native.Detail}");
        }
#else
        if (ShortcutBackendPolicy.Current == ShortcutBackendMode.Rust)
        {
            var metadata = new ShortcutInfo(
                targetPath,
                string.Empty,
                arguments,
                workingDirectory,
                effectiveIconPath,
                0);
            ShortcutNativeWriteCallResult native =
                ShortcutNativeBackend.WriteShortcut(shortcutPath, metadata);
            if (!native.Success)
            {
                App.Log(
                    $"[DragDropPermission] Explicit Rust shortcut write failed for " +
                    $"'{shortcutPath}': {native.Failure}; {native.Detail}");
                throw new InvalidOperationException(
                    $"Rust shortcut write failed: {native.Failure}; {native.Detail}");
            }
        }
        else
        {
            CreateOrUpdateShortcutWithCSharp(
                shortcutPath,
                targetPath,
                arguments,
                workingDirectory,
                iconPath);
        }
#endif

        ShortcutHelper.InvalidateStoredMetadataCache(shortcutPath);
    }

#if !DESKBOX_NATIVE_AOT
    internal static void CreateOrUpdateShortcutWithCSharp(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string? iconPath)
    {
        var shellLink = (IShellLinkW)(object)new ShellLink();
        shellLink.SetPath(targetPath);
        shellLink.SetArguments(arguments);
        shellLink.SetWorkingDirectory(workingDirectory);
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            // No icon location means the system uses the target's own icon
            // plus the shortcut overlay — the Explorer-native default.
            shellLink.SetIconLocation(iconPath, 0);
        }

        var persistFile = (System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink;
        persistFile.Save(shortcutPath, true);
    }
#endif

    private static ProcessTokenSnapshot GetCurrentProcessTokenSnapshot()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            IntPtr currentProcess = DragDropPermissionNativeMethods.GetCurrentProcess();
            return CreateProcessTokenSnapshot(
                Environment.ProcessId,
                "DeskBox",
                principal.IsInRole(WindowsBuiltInRole.Administrator),
                currentProcess);
        }
        catch (Exception ex)
        {
            return ProcessTokenSnapshot.Unknown(Environment.ProcessId, "DeskBox", ex.Message);
        }
    }

    private static ProcessTokenSnapshot GetExplorerTokenSnapshot()
    {
        try
        {
            Process? explorer = Process.GetProcessesByName("explorer").FirstOrDefault();
            if (explorer is null)
            {
                return ProcessTokenSnapshot.Unknown(0, "explorer", "not-running");
            }

            IntPtr processHandle = DragDropPermissionNativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)explorer.Id);
            if (processHandle == IntPtr.Zero)
            {
                return ProcessTokenSnapshot.Unknown(explorer.Id, explorer.ProcessName, $"open-error:{Marshal.GetLastWin32Error()}");
            }

            try
            {
                return CreateProcessTokenSnapshot(explorer.Id, explorer.ProcessName, null, processHandle);
            }
            finally
            {
                DragDropPermissionNativeMethods.CloseHandle(processHandle);
            }
        }
        catch (Exception ex)
        {
            return ProcessTokenSnapshot.Unknown(0, "explorer", ex.Message);
        }
    }

    private static ProcessTokenSnapshot CreateProcessTokenSnapshot(int processId, string processName, bool? isAdminRole, IntPtr processHandle)
    {
        bool? isElevated = TryGetTokenElevation(processHandle, out bool elevated) ? elevated : null;
        string integrity = TryGetIntegrityLevel(processHandle, out string level) ? level : "unknown";
        return new ProcessTokenSnapshot(processId, processName, isAdminRole, isElevated, integrity, null);
    }

    private static string FormatProcessToken(string label, ProcessTokenSnapshot snapshot, LocalizationService? localizationService)
    {
        string elevated = snapshot.IsElevated is null
            ? Text(localizationService, "Settings.DragDropPermission.Status.Unknown")
            : snapshot.IsElevated.Value
                ? Text(localizationService, "Settings.DragDropPermission.Status.Administrator")
                : Text(localizationService, "Settings.DragDropPermission.Status.Standard");
        string adminRole = snapshot.IsAdminRole is null
            ? string.Empty
            : snapshot.IsAdminRole.Value
                ? Text(localizationService, "Settings.DragDropPermission.Status.AdminGroup")
                : Text(localizationService, "Settings.DragDropPermission.Status.NonAdminGroup");
        string error = string.IsNullOrWhiteSpace(snapshot.Error)
            ? string.Empty
            : Format(localizationService, "Settings.DragDropPermission.Status.ErrorSuffix", snapshot.Error);
        string integrity = string.Equals(snapshot.IntegrityLevel, "unknown", StringComparison.OrdinalIgnoreCase)
            ? Text(localizationService, "Settings.DragDropPermission.Status.UnknownValue")
            : snapshot.IntegrityLevel;
        return Format(localizationService, "Settings.DragDropPermission.Status.Process", label, elevated, integrity, adminRole, error);
    }

    private static UacPolicySnapshot GetUacPolicySnapshot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UacPolicyKey);
            return new UacPolicySnapshot(
                ReadIntValue(key, "EnableLUA"),
                ReadIntValue(key, "ConsentPromptBehaviorAdmin"),
                ReadIntValue(key, "PromptOnSecureDesktop"),
                null);
        }
        catch (Exception ex)
        {
            return new UacPolicySnapshot(null, null, null, ex.Message);
        }
    }

    private static int? ReadIntValue(RegistryKey? key, string name)
    {
        object? value = key?.GetValue(name);
        return value switch
        {
            int intValue => intValue,
            string stringValue when int.TryParse(stringValue, out int parsed) => parsed,
            _ => null
        };
    }

    private static string FormatUacPolicy(UacPolicySnapshot policy, LocalizationService? localizationService)
    {
        if (!string.IsNullOrWhiteSpace(policy.Error))
        {
            return Format(localizationService, "Settings.DragDropPermission.Status.ReadFailed", policy.Error);
        }

        string enableLua = policy.EnableLua switch
        {
            0 => Text(localizationService, "Settings.DragDropPermission.Status.UacNeverNotify"),
            1 => Text(localizationService, "Settings.DragDropPermission.Status.UacEnabled"),
            _ => Text(localizationService, "Settings.DragDropPermission.Status.Unknown")
        };

        return Format(localizationService, "Settings.DragDropPermission.Status.UacDetails", enableLua, FormatNullable(policy.ConsentPromptBehaviorAdmin, localizationService), FormatNullable(policy.PromptOnSecureDesktop, localizationService));
    }

    private static string FormatNullable(int? value, LocalizationService? localizationService)
    {
        return value?.ToString() ?? Text(localizationService, "Settings.DragDropPermission.Status.UnknownValue");
    }

    private static string Text(LocalizationService? localizationService, string key)
    {
        return localizationService?.T(key) ?? LocalizationService.DefaultText(key);
    }

    private static string Format(LocalizationService? localizationService, string key, params object[] args)
    {
        return localizationService?.Format(key, args) ?? LocalizationService.DefaultFormat(key, args);
    }

    private static bool TryGetTokenElevation(IntPtr processHandle, out bool isElevated)
    {
        isElevated = false;
        if (!DragDropPermissionNativeMethods.OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            int length = Marshal.SizeOf<DragDropPermissionNativeMethods.TokenElevation>();
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!DragDropPermissionNativeMethods.GetTokenInformation(tokenHandle, DragDropPermissionNativeMethods.TokenInformationClass.TokenElevation, buffer, length, out _))
                {
                    return false;
                }

                var elevation = Marshal.PtrToStructure<DragDropPermissionNativeMethods.TokenElevation>(buffer);
                isElevated = elevation.TokenIsElevated != 0;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            DragDropPermissionNativeMethods.CloseHandle(tokenHandle);
        }
    }

    private static bool TryGetIntegrityLevel(IntPtr processHandle, out string level)
    {
        level = string.Empty;
        if (!DragDropPermissionNativeMethods.OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            _ = DragDropPermissionNativeMethods.GetTokenInformation(tokenHandle, DragDropPermissionNativeMethods.TokenInformationClass.TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
            if (length <= 0)
            {
                return false;
            }

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!DragDropPermissionNativeMethods.GetTokenInformation(tokenHandle, DragDropPermissionNativeMethods.TokenInformationClass.TokenIntegrityLevel, buffer, length, out _))
                {
                    return false;
                }

                var label = Marshal.PtrToStructure<DragDropPermissionNativeMethods.TokenMandatoryLabel>(buffer);
                IntPtr subAuthorityCount = DragDropPermissionNativeMethods.GetSidSubAuthorityCount(label.Label.Sid);
                if (subAuthorityCount == IntPtr.Zero)
                {
                    return false;
                }

                byte count = Marshal.ReadByte(subAuthorityCount);
                if (count == 0)
                {
                    return false;
                }

                IntPtr integrityRidPointer = DragDropPermissionNativeMethods.GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                int integrityRid = Marshal.ReadInt32(integrityRidPointer);
                level = FormatIntegrityLevel(integrityRid);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            DragDropPermissionNativeMethods.CloseHandle(tokenHandle);
        }
    }

    private static string FormatIntegrityLevel(int integrityRid)
    {
        return integrityRid switch
        {
            < SecurityMandatoryLowRid => $"Untrusted(0x{integrityRid:X})",
            < SecurityMandatoryMediumRid => $"Low(0x{integrityRid:X})",
            < SecurityMandatoryHighRid => $"Medium(0x{integrityRid:X})",
            < SecurityMandatorySystemRid => $"High(0x{integrityRid:X})",
            < SecurityMandatoryProtectedProcessRid => $"System(0x{integrityRid:X})",
            _ => $"Protected(0x{integrityRid:X})"
        };
    }

    private sealed record ProcessTokenSnapshot(
        int ProcessId,
        string ProcessName,
        bool? IsAdminRole,
        bool? IsElevated,
        string IntegrityLevel,
        string? Error)
    {
        public static ProcessTokenSnapshot Unknown(int processId, string processName, string error)
        {
            return new ProcessTokenSnapshot(processId, processName, null, null, "unknown", error);
        }
    }

    private sealed record UacPolicySnapshot(
        int? EnableLua,
        int? ConsentPromptBehaviorAdmin,
        int? PromptOnSecureDesktop,
        string? Error);

    private sealed record AppCompatEntry(
        string RootName,
        string ExePath,
        string Value);

    private sealed record ShortcutProbe(
        string Path,
        bool HasIssue,
        string Reason,
        bool HasRunAsFlag = false);

    private sealed record ShortcutDefinition(
        string Path,
        string Arguments,
        bool EnsureExists);

    // Token-integrity and relaunch entry points with their marshaling
    // structures live in DeskBox.Platform.DragDropPermissionNativeMethods.

#if !DESKBOX_NATIVE_AOT
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindDataW
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription(string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory(string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments(string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation(string pszIconPath, int iIcon);
        void SetRelativePath(string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath(string pszFile);
    }
#endif
}
