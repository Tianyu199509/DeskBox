using System.Security.Principal;
using System.Xml.Linq;

namespace DeskBox.Services;

internal sealed record DirectStartupTaskRegistration(
    string ExecutablePath,
    string Arguments,
    string PrincipalUserId,
    string TriggerUserId,
    string LogonType,
    string RunLevel,
    int Priority,
    bool Enabled,
    string ExecutionTimeLimit,
    string MultipleInstancesPolicy,
    bool StartWhenAvailable,
    bool DisallowStartIfOnBatteries,
    bool StopIfGoingOnBatteries,
    bool RunOnlyIfIdle,
    string TriggerDelay,
    string RestartOnFailureInterval = "",
    int RestartOnFailureCount = 0,
    string TaskName = "")
{
    internal string? Xml { get; init; }

    public string CommandLine =>
        $"\"{ExecutablePath}\" {Arguments}".TrimEnd();

    public bool IsOwnedBy(string executablePath) =>
        DirectStartupTaskBackend.PathsEqual(ExecutablePath, executablePath);
}

internal interface IDirectStartupTaskBackend
{
    string LastError { get; }

    bool ReadFailed { get; }

    DirectStartupTaskRegistration? Read();

    bool HasCurrentUserLegacyTask(string executablePath);

    bool IsPreferred(
        DirectStartupTaskRegistration registration,
        string executablePath);

    bool TryRegister(string executablePath);

    bool TryDelete(string? ownedExecutablePath = null);

    bool TryRestore(DirectStartupTaskRegistration registration);
}

/// <summary>
/// Registers, queries, and deletes the direct-distribution logon startup task
/// directly through Task Scheduler 2.0 COM (<see cref="DirectStartupTaskXmlReader"/>).
/// This stays Native AOT friendly, avoids spawning external CLI helpers or writing
/// temporary XML files, and adds no resident helper process or service to DeskBox.
/// </summary>
internal sealed partial class DirectStartupTaskBackend : IDirectStartupTaskBackend
{
    internal const string LegacyTaskName = "DeskBox User Startup";
    internal const string TaskNamePrefix = LegacyTaskName + "-";
    internal const string StartupArguments =
        "--startup --startup-source=scheduled-task";
    internal const int InteractiveTaskPriority = 4;
    internal const string TaskRestartInterval = "PT1M";
    internal const int TaskRestartCount = 3;
    private static readonly XNamespace TaskNamespace =
        "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public string LastError { get; private set; } = string.Empty;

    public bool ReadFailed { get; private set; }

    public DirectStartupTaskRegistration? Read()
    {
        LastError = string.Empty;
        ReadFailed = false;
        string currentUserSid;
        try
        {
            currentUserSid = GetCurrentUserSid();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ReadFailed = true;
            return null;
        }

        DirectStartupTaskRegistration? currentTask = ReadTask(
            GetTaskName(currentUserSid),
            out string currentError, out bool currentReadFailed);
        if (currentTask is not null)
        {
            return currentTask;
        }
        if (currentReadFailed)
        {
            ReadFailed = true;
            LastError = currentError;
            return null;
        }

        // Read the old fixed-name task only as a migration source. A successful
        // registration always uses the SID-scoped name, so separate Windows
        // users can configure startup independently on an all-users install.
        // On a shared Win10 PC, E_ACCESSDENIED on the legacy task means another
        // user/admin created it — treat that as "not owned by current user" so
        // standard users can still fall back cleanly to HKCU\...\Run.
        DirectStartupTaskRegistration? legacyTask = ReadTask(
            LegacyTaskName,
            out string legacyError,
            out bool legacyReadFailed,
            out bool legacyAccessDenied);
        if (legacyTask is not null &&
            IsCurrentUserRegistration(legacyTask, currentUserSid))
        {
            return legacyTask;
        }

        if (legacyAccessDenied)
        {
            ReadFailed = false;
            LastError = string.Empty;
            return null;
        }

        ReadFailed = legacyReadFailed;
        LastError = legacyReadFailed ? legacyError : string.Empty;
        return null;
    }

    public bool HasCurrentUserLegacyTask(string executablePath)
    {
        try
        {
            string currentUserSid = GetCurrentUserSid();
            DirectStartupTaskRegistration? legacyTask = ReadTask(LegacyTaskName, out _);
            return legacyTask is not null &&
                   IsCurrentUserRegistration(legacyTask, currentUserSid) &&
                   IsOwnedOrCompanionOrOrphaned(legacyTask, executablePath);
        }
        catch
        {
            return false;
        }
    }

    public bool IsPreferred(
        DirectStartupTaskRegistration registration,
        string executablePath)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string currentUserSid = identity.User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        string currentUserName = identity.Name;
        return string.Equals(
                   registration.TaskName,
                   GetTaskName(currentUserSid),
                   StringComparison.OrdinalIgnoreCase) &&
               registration.IsOwnedBy(executablePath) &&
               registration.Enabled &&
               string.Equals(
                   registration.Arguments.Trim(),
                   StartupArguments,
                   StringComparison.OrdinalIgnoreCase) &&
               IsCurrentUserId(
                   registration.PrincipalUserId,
                   currentUserSid,
                   currentUserName) &&
               IsCurrentUserId(
                   registration.TriggerUserId,
                   currentUserSid,
                   currentUserName) &&
               string.Equals(
                   registration.LogonType,
                   "InteractiveToken",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   registration.RunLevel,
                   "LeastPrivilege",
                   StringComparison.OrdinalIgnoreCase) &&
               registration.Priority == InteractiveTaskPriority &&
               string.Equals(
                   registration.ExecutionTimeLimit,
                   "PT0S",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   registration.MultipleInstancesPolicy,
                   "IgnoreNew",
                   StringComparison.OrdinalIgnoreCase) &&
               registration.StartWhenAvailable &&
               !registration.DisallowStartIfOnBatteries &&
               !registration.StopIfGoingOnBatteries &&
               !registration.RunOnlyIfIdle &&
               (string.IsNullOrWhiteSpace(registration.TriggerDelay) ||
                string.Equals(
                    registration.TriggerDelay,
                    "PT0S",
                    StringComparison.OrdinalIgnoreCase)) &&
               IsRestartPolicySatisfied(registration);
    }

    /// <summary>
    /// The restart policy is an enhancement, not a correctness property of the
    /// registration: an absent policy is accepted for compatibility with older
    /// registrations. Values that are present but different stay non-preferred.
    /// </summary>
    private static bool IsRestartPolicySatisfied(
        DirectStartupTaskRegistration registration) =>
        (string.IsNullOrWhiteSpace(registration.RestartOnFailureInterval) &&
            registration.RestartOnFailureCount == 0) ||
        (string.Equals(
             registration.RestartOnFailureInterval,
             TaskRestartInterval,
             StringComparison.OrdinalIgnoreCase) &&
            registration.RestartOnFailureCount == TaskRestartCount);

    public bool TryRegister(string executablePath)
    {
        LastError = string.Empty;
        string currentUserSid;
        try
        {
            currentUserSid = GetCurrentUserSid();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }

        string taskName = GetTaskName(currentUserSid);
        string taskXml = BuildTaskXml(executablePath, currentUserSid);
        TaskXmlSnapshot previousTask = QueryTaskXml(taskName);
        if (!previousTask.Succeeded && !previousTask.IsMissing)
        {
            LastError = $"The previous startup task could not be safely backed up: {previousTask.Error}";
            return false;
        }
        if (previousTask.Succeeded && !IsParseableXml(previousTask.Xml))
        {
            LastError = "The previous startup task returned invalid XML; registration was left unchanged.";
            return false;
        }

        // Refuse to create a SID-scoped twin if the legacy task cannot be
        // inspected (Task Scheduler failure). On a shared PC, E_ACCESSDENIED
        // means another user/admin owns the legacy fixed-name task — allow
        // registering the current user's non-colliding SID-scoped task.
        TaskXmlSnapshot legacySnapshot = QueryTaskXml(LegacyTaskName);
        if (!legacySnapshot.Succeeded && !legacySnapshot.IsMissing && !legacySnapshot.IsAccessDenied)
        {
            LastError = $"The legacy startup task could not be inspected: {legacySnapshot.Error}";
            return false;
        }

        try
        {
            DirectStartupTaskXmlReader.Register(taskName, taskXml);

            DirectStartupTaskRegistration? verified = ReadTask(taskName, out string verificationReadError);
            if (verified is null || !IsPreferred(verified, executablePath))
            {
                string verificationError = string.IsNullOrWhiteSpace(LastError)
                    ? verified is null
                        ? $"The registered task could not be read back: {verificationReadError}"
                        : DescribePreferenceMismatch(verified, executablePath)
                    : LastError;
                LastError = RollbackTaskAfterFailure(taskName, previousTask, verificationError);
                return false;
            }

            if (!TryDeleteOwnedLegacyTask(executablePath, currentUserSid, out string legacyCleanupError))
            {
                // Never deliberately leave both the SID-scoped task and the
                // legacy fixed-name task firing together at logon.
                LastError = RollbackTaskAfterFailure(
                    taskName,
                    previousTask,
                    $"Legacy startup task cleanup failed: {legacyCleanupError}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Task registration failed (0x{ex.HResult:X8}): {ex.Message}";
            return false;
        }
    }

    public bool TryDelete(string? ownedExecutablePath = null)
    {
        LastError = string.Empty;
        string currentUserSid;
        try
        {
            currentUserSid = GetCurrentUserSid();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }

        List<string> failures = [];
        string taskName = GetTaskName(currentUserSid);
        if (!TryDeleteTask(taskName, out string currentError))
        {
            failures.Add($"Current task cleanup failed: {currentError}");
        }

        // Always inspect and clean the legacy fixed-name task as well, even when
        // the SID-scoped task was deleted above, so a lingering twin can never
        // survive disable or mode fallback.
        DirectStartupTaskRegistration? legacyTask = ReadTask(
            LegacyTaskName,
            out string legacyReadError,
            out bool legacyReadFailed,
            out bool legacyAccessDenied);
        if (legacyReadFailed && !legacyAccessDenied)
        {
            failures.Add($"Legacy task query failed: {legacyReadError}");
        }
        else if (!legacyAccessDenied &&
                 legacyTask is not null &&
                 IsCurrentUserRegistration(legacyTask, currentUserSid) &&
                 (string.IsNullOrWhiteSpace(ownedExecutablePath) ||
                  IsOwnedOrCompanionOrOrphaned(legacyTask, ownedExecutablePath)))
        {
            if (!TryDeleteTask(LegacyTaskName, out string legacyDeleteError))
            {
                failures.Add($"Legacy task cleanup failed: {legacyDeleteError}");
            }
        }

        if (failures.Count > 0)
        {
            LastError = string.Join("; ", failures);
            return false;
        }

        return true;
    }

    private static DirectStartupTaskRegistration? ReadTask(
        string taskName,
        out string error) => ReadTask(taskName, out error, out _);

    private static DirectStartupTaskRegistration? ReadTask(
        string taskName,
        out string error,
        out bool readFailed) => ReadTask(taskName, out error, out readFailed, out _);

    private static DirectStartupTaskRegistration? ReadTask(
        string taskName,
        out string error,
        out bool readFailed,
        out bool accessDenied)
    {
        error = string.Empty;
        readFailed = false;
        accessDenied = false;
        TaskXmlSnapshot result = QueryTaskXml(taskName);
        if (!result.Succeeded)
        {
            error = result.Error;
            readFailed = !result.IsMissing;
            accessDenied = result.IsAccessDenied;
            return null;
        }

        if (string.IsNullOrWhiteSpace(result.Xml))
        {
            error = "Task query succeeded but returned no XML.";
            readFailed = true;
            return null;
        }

        try
        {
            return ParseTaskXml(result.Xml) with { TaskName = taskName };
        }
        catch (Exception ex)
        {
            error = $"Task XML could not be parsed: {ex.Message}";
            readFailed = true;
            return null;
        }
    }

    public bool TryRestore(DirectStartupTaskRegistration registration)
    {
        LastError = string.Empty;
        string sid;
        try
        {
            sid = GetCurrentUserSid();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(registration.Xml) || !IsParseableXml(registration.Xml) ||
            !IsCurrentUserRegistration(registration, sid) ||
            (registration.TaskName != GetTaskName(sid) && registration.TaskName != LegacyTaskName))
        {
            LastError = "The previous task is not a valid current-user snapshot.";
            return false;
        }

        try
        {
            DirectStartupTaskXmlReader.Register(registration.TaskName, registration.Xml);
            DirectStartupTaskRegistration? restored = ReadTask(registration.TaskName, out string error);
            if (restored is null || !restored.IsOwnedBy(registration.ExecutablePath) ||
                restored.Enabled != registration.Enabled || restored.Arguments != registration.Arguments)
            {
                LastError = $"Task snapshot restore did not verify: {error}";
                return false;
            }
            // A legacy snapshot can predate the SID-scoped task created by the
            // attempted switch. Restore the old task without leaving its new twin.
            if (registration.TaskName == LegacyTaskName)
            {
                string replacementName = GetTaskName(sid);
                DirectStartupTaskRegistration? replacement = ReadTask(replacementName, out _);
                if (replacement?.IsOwnedBy(registration.ExecutablePath) == true &&
                    !TryDeleteTask(replacementName, out string cleanupError))
                {
                    LastError = cleanupError;
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Task snapshot restore failed (0x{ex.HResult:X8}): {ex.Message}";
            return false;
        }
    }

    private static string RollbackTaskAfterFailure(
        string taskName,
        TaskXmlSnapshot previousTask,
        string failureReason)
    {
        if (previousTask.Succeeded)
        {
            try
            {
                DirectStartupTaskXmlReader.Register(taskName, previousTask.Xml);
                return $"{failureReason} Previous task restored.";
            }
            catch (Exception restoreEx)
            {
                return $"{failureReason} Previous task restore failed (0x{restoreEx.HResult:X8}): {restoreEx.Message}";
            }
        }

        bool removedInvalidTask = TryDeleteTask(taskName, out string cleanupError);
        return removedInvalidTask
            ? failureReason
            : $"{failureReason} Cleanup also failed: {cleanupError}";
    }

    private const int ErrorAccessDeniedHr = unchecked((int)0x80070005);

    private static TaskXmlSnapshot QueryTaskXml(string taskName)
    {
        try
        {
            return new(DirectStartupTaskXmlReader.Read(taskName), string.Empty, false, false);
        }
        catch (Exception ex)
        {
            bool missing = ex.HResult is
                DirectStartupTaskXmlReader.ErrorFileNotFoundHr or
                DirectStartupTaskXmlReader.ErrorPathNotFoundHr;
            bool accessDenied = ex.HResult == ErrorAccessDeniedHr ||
                ex is UnauthorizedAccessException;
            return new(string.Empty, $"Task query failed (0x{ex.HResult:X8}): {ex.Message}", missing, accessDenied);
        }
    }

    private readonly record struct TaskXmlSnapshot(string Xml, string Error, bool IsMissing, bool IsAccessDenied)
    {
        public bool Succeeded => string.IsNullOrEmpty(Error);
    }

    private static bool TryDeleteTask(string taskName, out string error)
    {
        try
        {
            _ = DirectStartupTaskXmlReader.Delete(taskName);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Task delete failed (0x{ex.HResult:X8}): {ex.Message}";
            return false;
        }
    }

    private static bool IsCurrentUserRegistration(
        DirectStartupTaskRegistration registration,
        string currentUserSid)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string currentUserName = identity.Name;
        return IsCurrentUserId(
                   registration.PrincipalUserId,
                   currentUserSid,
                   currentUserName) &&
               IsCurrentUserId(
                   registration.TriggerUserId,
                   currentUserSid,
                   currentUserName);
    }

    private static bool IsOwnedOrCompanionOrOrphaned(
        DirectStartupTaskRegistration task,
        string executablePath) =>
        task.IsOwnedBy(executablePath) ||
        AppExecutablePath.IsSameInstallCompanionExecutable(task.ExecutablePath, executablePath) ||
        (!string.IsNullOrWhiteSpace(task.ExecutablePath) && !File.Exists(task.ExecutablePath));

    private static bool TryDeleteOwnedLegacyTask(
        string executablePath,
        string currentUserSid,
        out string error)
    {
        error = string.Empty;
        DirectStartupTaskRegistration? legacyTask = ReadTask(
            LegacyTaskName,
            out string readError,
            out bool readFailed,
            out bool accessDenied);
        if (accessDenied)
        {
            return true;
        }

        if (readFailed)
        {
            error = readError;
            return false;
        }

        if (legacyTask is not null &&
            IsCurrentUserRegistration(legacyTask, currentUserSid) &&
            IsOwnedOrCompanionOrOrphaned(legacyTask, executablePath))
        {
            return TryDeleteTask(LegacyTaskName, out error);
        }

        return true;
    }

    internal static string GetTaskName(string currentUserSid) =>
        TaskNamePrefix + currentUserSid;

    internal static string BuildTaskXml(
        string executablePath,
        string currentUserSid)
    {
        string fullExecutablePath = Path.GetFullPath(executablePath);
        string taskName = GetTaskName(currentUserSid);
        var document = new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(
                TaskNamespace + "Task",
                new XAttribute("version", "1.4"),
                new XElement(
                    TaskNamespace + "RegistrationInfo",
                    new XElement(TaskNamespace + "Source", "DeskBox"),
                    new XElement(TaskNamespace + "Author", "DeskBox"),
                    new XElement(
                        TaskNamespace + "Description",
                        "Starts DeskBox promptly after this user signs in."),
                    new XElement(TaskNamespace + "URI", $"\\{taskName}")),
                new XElement(
                    TaskNamespace + "Triggers",
                    new XElement(
                        TaskNamespace + "LogonTrigger",
                        new XElement(TaskNamespace + "Enabled", "true"),
                        new XElement(TaskNamespace + "UserId", currentUserSid))),
                new XElement(
                    TaskNamespace + "Principals",
                    new XElement(
                        TaskNamespace + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(TaskNamespace + "UserId", currentUserSid),
                        new XElement(TaskNamespace + "LogonType", "InteractiveToken"),
                        new XElement(TaskNamespace + "RunLevel", "LeastPrivilege"))),
                new XElement(
                    TaskNamespace + "Settings",
                    new XElement(TaskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskNamespace + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskNamespace + "StopIfGoingOnBatteries", "false"),
                    new XElement(TaskNamespace + "AllowHardTerminate", "true"),
                    new XElement(TaskNamespace + "StartWhenAvailable", "true"),
                    new XElement(TaskNamespace + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(TaskNamespace + "RunOnlyIfIdle", "false"),
                    new XElement(TaskNamespace + "AllowStartOnDemand", "true"),
                    new XElement(TaskNamespace + "Enabled", "true"),
                    new XElement(TaskNamespace + "Hidden", "false"),
                    new XElement(TaskNamespace + "WakeToRun", "false"),
                    new XElement(TaskNamespace + "ExecutionTimeLimit", "PT0S"),
                    new XElement(TaskNamespace + "Priority", InteractiveTaskPriority),
                    // Task Scheduler's RestartOnFailure retries only when the
                    // scheduler itself fails to launch the process (e.g., early
                    // logon session or volume readiness race); it never restarts
                    // after a running DeskBox process exits or is terminated.
                    new XElement(
                        TaskNamespace + "RestartOnFailure",
                        new XElement(TaskNamespace + "Interval", TaskRestartInterval),
                        new XElement(TaskNamespace + "Count", TaskRestartCount))),
                new XElement(
                    TaskNamespace + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(
                        TaskNamespace + "Exec",
                        new XElement(TaskNamespace + "Command", fullExecutablePath),
                        new XElement(TaskNamespace + "Arguments", StartupArguments),
                        new XElement(
                            TaskNamespace + "WorkingDirectory",
                            Path.GetDirectoryName(fullExecutablePath) ?? string.Empty)))));

        return $"{document.Declaration}{Environment.NewLine}{document}";
    }

    internal static DirectStartupTaskRegistration ParseTaskXml(string taskXml)
    {
        XDocument document = XDocument.Parse(taskXml, LoadOptions.None);
        XElement root = document.Root ??
            throw new InvalidDataException("The task XML has no root element.");
        XNamespace ns = root.Name.Namespace;
        XElement? trigger = root.Descendants(ns + "LogonTrigger").FirstOrDefault();
        XElement? principal = root.Descendants(ns + "Principal").FirstOrDefault();
        XElement? registrationInfo = root.Element(ns + "RegistrationInfo");
        XElement? settings = root.Element(ns + "Settings");
        XElement? action = root.Descendants(ns + "Exec").FirstOrDefault();

        static string Value(XElement? parent, XNamespace ns, string name) =>
            parent?.Element(ns + name)?.Value?.Trim() ?? string.Empty;
        static bool BooleanValue(
            XElement? parent,
            XNamespace ns,
            string name,
            bool defaultValue = false) =>
            bool.TryParse(Value(parent, ns, name), out bool value)
                ? value
                : defaultValue;

        string runLevel = Value(principal, ns, "RunLevel");
        if (string.IsNullOrWhiteSpace(runLevel))
        {
            // Task Scheduler omits LeastPrivilege when exporting XML because it
            // is the schema default. Normalize that omission before validation.
            runLevel = "LeastPrivilege";
        }

        _ = int.TryParse(
            Value(settings, ns, "Priority"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int priority);
        XElement? restartOnFailure = settings?.Element(ns + "RestartOnFailure");
        _ = int.TryParse(
            Value(restartOnFailure, ns, "Count"),
            out int restartCount);

        return new DirectStartupTaskRegistration(
            Value(action, ns, "Command"),
            Value(action, ns, "Arguments"),
            Value(principal, ns, "UserId"),
            Value(trigger, ns, "UserId"),
            Value(principal, ns, "LogonType"),
            runLevel,
            priority,
            // Enabled=true is also commonly omitted from exported XML.
            BooleanValue(settings, ns, "Enabled", defaultValue: true) &&
                BooleanValue(trigger, ns, "Enabled", defaultValue: true),
            Value(settings, ns, "ExecutionTimeLimit"),
            Value(settings, ns, "MultipleInstancesPolicy"),
            BooleanValue(settings, ns, "StartWhenAvailable"),
            BooleanValue(settings, ns, "DisallowStartIfOnBatteries"),
            BooleanValue(settings, ns, "StopIfGoingOnBatteries"),
            BooleanValue(settings, ns, "RunOnlyIfIdle"),
            Value(trigger, ns, "Delay"),
            Value(restartOnFailure, ns, "Interval"),
            restartCount,
            Value(registrationInfo, ns, "URI").TrimStart('\\')) { Xml = taskXml };
    }

    internal static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(first.Trim().Trim('"'))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second.Trim().Trim('"'))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string DescribePreferenceMismatch(
        DirectStartupTaskRegistration registration,
        string executablePath)
    {
        return
            "The registered task did not match the required least-privilege " +
            $"startup contract: expectedPath='{executablePath}' " +
            $"actualPath='{registration.ExecutablePath}' " +
            $"arguments='{registration.Arguments}' " +
            $"principal='{registration.PrincipalUserId}' " +
            $"trigger='{registration.TriggerUserId}' " +
            $"logonType='{registration.LogonType}' " +
            $"runLevel='{registration.RunLevel}' " +
            $"priority={registration.Priority} enabled={registration.Enabled} " +
            $"executionLimit='{registration.ExecutionTimeLimit}' " +
            $"instances='{registration.MultipleInstancesPolicy}' " +
            $"startWhenAvailable={registration.StartWhenAvailable} " +
            $"disallowBattery={registration.DisallowStartIfOnBatteries} " +
            $"stopOnBattery={registration.StopIfGoingOnBatteries} " +
            $"runOnlyIfIdle={registration.RunOnlyIfIdle} " +
            $"delay='{registration.TriggerDelay}' " +
            $"restartInterval='{registration.RestartOnFailureInterval}' " +
            $"restartCount={registration.RestartOnFailureCount} " +
            $"checks={DescribePreferenceCheckResults(registration, executablePath)}.";
    }

    /// <summary>
    /// Per-check booleans for a failed validation. Values are never included —
    /// only which comparisons failed — so the diagnostic log line survives
    /// sanitization and pinpoints the failing field without another round trip.
    /// </summary>
    internal static string DescribePreferenceCheckResults(
        DirectStartupTaskRegistration registration,
        string executablePath)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string currentUserSid = identity.User?.Value ?? string.Empty;
        string currentUserName = identity.Name;

        bool IsUserId(string? candidate) => IsCurrentUserId(
            candidate ?? string.Empty,
            currentUserSid,
            currentUserName);

        bool triggerDelayOk = string.IsNullOrWhiteSpace(registration.TriggerDelay) ||
            string.Equals(registration.TriggerDelay, "PT0S", StringComparison.OrdinalIgnoreCase);

        return
            $"taskName={string.Equals(registration.TaskName, GetTaskName(currentUserSid), StringComparison.OrdinalIgnoreCase)} " +
            $"path={registration.IsOwnedBy(executablePath)} " +
            $"arguments={string.Equals(registration.Arguments.Trim(), StartupArguments, StringComparison.OrdinalIgnoreCase)} " +
            $"principal={IsUserId(registration.PrincipalUserId)} " +
            $"trigger={IsUserId(registration.TriggerUserId)} " +
            $"logonType={string.Equals(registration.LogonType, "InteractiveToken", StringComparison.OrdinalIgnoreCase)} " +
            $"runLevel={string.Equals(registration.RunLevel, "LeastPrivilege", StringComparison.OrdinalIgnoreCase)} " +
            $"priority={registration.Priority == InteractiveTaskPriority} " +
            $"enabled={registration.Enabled} " +
            $"executionLimit={string.Equals(registration.ExecutionTimeLimit, "PT0S", StringComparison.OrdinalIgnoreCase)} " +
            $"instances={string.Equals(registration.MultipleInstancesPolicy, "IgnoreNew", StringComparison.OrdinalIgnoreCase)} " +
            $"startWhenAvailable={registration.StartWhenAvailable} " +
            $"disallowBattery={!registration.DisallowStartIfOnBatteries} " +
            $"stopOnBattery={!registration.StopIfGoingOnBatteries} " +
            $"runOnlyIfIdle={!registration.RunOnlyIfIdle} " +
            $"delay={triggerDelayOk} " +
            $"restart={IsRestartPolicySatisfied(registration)}";
    }

    internal static bool IsCurrentUserId(
        string candidate,
        string currentUserSid,
        string currentUserName,
        Func<string, string?>? accountToSidResolver = null)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string trimmed = candidate.Trim();
        if (string.Equals(trimmed, currentUserSid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(currentUserName) &&
            string.Equals(trimmed, currentUserName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Task Scheduler on Microsoft Account / Entra ID / renamed profiles may
        // export a normalized account alias (e.g. MicrosoftAccount\..., AzureAD\...,
        // or UPN) that differs textually from WindowsIdentity.Name; resolve to SID.
        string? resolvedSid = (accountToSidResolver ?? TryResolveAccountNameToSid)(trimmed);
        return !string.IsNullOrWhiteSpace(resolvedSid) &&
               string.Equals(resolvedSid, currentUserSid, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryResolveAccountNameToSid(string accountName)
    {
        try
        {
            var account = new NTAccount(accountName);
            return account.Translate(typeof(SecurityIdentifier)).Value;
        }
        catch
        {
            return null;
        }
    }

    private static string GetCurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
    }

    private static bool IsParseableXml(string candidate)
    {
        try
        {
            _ = XDocument.Parse(candidate, LoadOptions.None);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
