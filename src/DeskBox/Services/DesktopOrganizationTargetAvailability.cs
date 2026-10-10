using System.ComponentModel;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Availability classification for an auto-organization rule target folder.
/// Every non-available value is treated as recoverable: the rule is skipped
/// at runtime and surfaced to the user, but never disabled on disk.
/// </summary>
public enum DesktopOrganizationTargetAvailability
{
    Available,
    Missing,
    AccessDenied,
    Unreachable
}

/// <summary>
/// Reported once per unavailable target set. WidgetNames drives the
/// notification copy (single widget vs. count); TargetPaths is diagnostic.
/// </summary>
public sealed record DesktopOrganizationTargetUnavailableSummary(
    IReadOnlyList<string> WidgetNames,
    IReadOnlyList<string> TargetPaths);

internal static class DesktopOrganizationTargetProbe
{
    /// <summary>
    /// Classifies a mapped folder path. Directory.Exists alone is not a
    /// discriminator: it returns false for a missing folder, an
    /// access-denied folder, and an offline device alike, so the metadata is
    /// touched after a negative Exists check to surface the real failure
    /// mode and classify it by HRESULT.
    /// </summary>
    public static DesktopOrganizationTargetAvailability Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DesktopOrganizationTargetAvailability.Missing;
        }

        try
        {
            var info = new DirectoryInfo(path);
            if (info.Exists)
            {
                return DesktopOrganizationTargetAvailability.Available;
            }

            _ = info.Attributes;
            return DesktopOrganizationTargetAvailability.Missing;
        }
        catch (Exception ex)
        {
            return ClassifyFailure(ex);
        }
    }

    internal static DesktopOrganizationTargetAvailability ClassifyFailure(
        Exception failure)
    {
        const int ErrorFileNotFound = unchecked((int)0x80070002);
        const int ErrorPathNotFound = unchecked((int)0x80070003);
        const int ErrorAccessDenied = unchecked((int)0x80070005);
        const int ErrorNotReady = unchecked((int)0x80070015);
        const int ErrorBadNetPath = unchecked((int)0x80070035);
        if (failure is Win32Exception win32)
        {
            // Win32Exception keeps the raw code in NativeErrorCode; its
            // HResult does not follow the 0x80070000 | code layout on every
            // runtime, so classify from the native code directly.
            return win32.NativeErrorCode switch
            {
                2 or 3 => DesktopOrganizationTargetAvailability.Missing,
                5 => DesktopOrganizationTargetAvailability.AccessDenied,
                _ => DesktopOrganizationTargetAvailability.Unreachable
            };
        }

        return failure.HResult switch
        {
            ErrorFileNotFound or ErrorPathNotFound =>
                DesktopOrganizationTargetAvailability.Missing,
            ErrorAccessDenied => DesktopOrganizationTargetAvailability.AccessDenied,
            ErrorNotReady or ErrorBadNetPath =>
                DesktopOrganizationTargetAvailability.Unreachable,
            _ => failure switch
            {
                UnauthorizedAccessException =>
                    DesktopOrganizationTargetAvailability.AccessDenied,
                FileNotFoundException or DirectoryNotFoundException =>
                    DesktopOrganizationTargetAvailability.Missing,
                _ => DesktopOrganizationTargetAvailability.Unreachable
            }
        };
    }
}

/// <summary>
/// Startup advisor that pairs the recoverable-skip policy in
/// NormalizeOrganizerSettings with a user-visible warning: when the
/// auto-organization master switch is on but a rule target folder is not
/// usable, the affected widgets are reported once per session so the
/// organization pause is never silent. The probe runs here - off the
/// settings load/save paths - because classifying an offline device can
/// block for the network timeout; normalization never touches the disk.
/// </summary>
internal static class DesktopOrganizationTargetAvailabilityAdvisor
{
    private static readonly HashSet<string> s_warnedWidgetIds =
        new(StringComparer.Ordinal);

    public static void WarnIfUnavailableTargets(
        SettingsService settingsService,
        Action<DesktopOrganizationTargetUnavailableSummary> showWarning,
        Func<string?, DesktopOrganizationTargetAvailability>? probe = null)
    {
        probe ??= DesktopOrganizationTargetProbe.Classify;
        try
        {
            AppSettings settings = settingsService.Settings;
            if (!settings.DesktopAutoOrganizationEnabled)
            {
                return;
            }

            var widgetsById = settings.Widgets
                .Where(widget =>
                    widget.WidgetKind == WidgetKind.File &&
                    !widget.IsDisabled &&
                    !settings.DeletedWidgetIds.Contains(widget.Id))
                .GroupBy(widget => widget.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var unavailable = new List<WidgetConfig>();
            var seenWidgetIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (DesktopOrganizationRule rule in settings.DesktopOrganizationRules)
            {
                if (!rule.IsEnabled ||
                    (rule.CategoryIds.Count == 0 &&
                     rule.SubtypeIds.Count == 0 &&
                     rule.Extensions.Count == 0) ||
                    !widgetsById.TryGetValue(rule.TargetWidgetId, out WidgetConfig? widget) ||
                    !seenWidgetIds.Add(widget.Id))
                {
                    continue;
                }

                // An empty mapped path is a configuration fact (the folder has
                // not been backfilled yet), not a disk observation, so it is
                // classified without consulting the probe.
                DesktopOrganizationTargetAvailability kind =
                    string.IsNullOrWhiteSpace(widget.MappedFolderPath)
                        ? DesktopOrganizationTargetAvailability.Missing
                        : probe(widget.MappedFolderPath);
                if (kind != DesktopOrganizationTargetAvailability.Available)
                {
                    unavailable.Add(widget);
                }
            }

            lock (s_warnedWidgetIds)
            {
                unavailable.RemoveAll(widget => !s_warnedWidgetIds.Add(widget.Id));
            }

            if (unavailable.Count == 0)
            {
                return;
            }

            App.Log(
                "[DesktopAutoOrganization] Auto-organization targets unavailable: " +
                string.Join(
                    ", ",
                    unavailable.Select(widget =>
                        $"'{widget.Name}' ({widget.MappedFolderPath})")));
            showWarning(new DesktopOrganizationTargetUnavailableSummary(
                unavailable.Select(widget => widget.Name).ToList(),
                unavailable
                    .Select(widget => widget.MappedFolderPath ?? string.Empty)
                    .ToList()));
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[DesktopAutoOrganization] Target availability advisory failed: " +
                $"{ex.Message}");
        }
    }

    internal static void ResetForTests()
    {
        lock (s_warnedWidgetIds)
        {
            s_warnedWidgetIds.Clear();
        }
    }
}
