using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using DeskBox.Platform;

namespace DeskBox.Services;

internal sealed record WindowsCrashEventEntry(
    DateTimeOffset TimeCreatedUtc,
    string Provider,
    long EventId,
    IReadOnlyDictionary<string, string> Properties);

internal sealed record WindowsCrashEventReport(
    int SchemaVersion,
    string Status,
    DateTimeOffset? WindowStartUtc,
    IReadOnlyList<WindowsCrashEventEntry> Events);

/// <summary>
/// Decides which collected crash events belong in the support package.
/// DeskBox crashes are the direct subject; explorer.exe crashes are the
/// first cause of the logon restart loops (feedback #576) and without them
/// the package cannot attribute "who died first".
/// </summary>
internal static class WindowsCrashEventFilterPolicy
{
    private static readonly string[] MonitoredImageNames =
    [
        "DeskBox.exe",
        "explorer.exe"
    ];

    internal static bool ShouldInclude(IReadOnlyDictionary<string, string> properties)
    {
        foreach ((_, string value) in properties)
        {
            foreach (string imageName in MonitoredImageNames)
            {
                if (EndsWithImageName(value, imageName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool EndsWithImageName(string value, string imageName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string trimmed = value.Trim().Trim('"', '\'');
        return trimmed.EndsWith(imageName, StringComparison.OrdinalIgnoreCase) &&
            (trimmed.Length == imageName.Length ||
                trimmed[^(imageName.Length + 1)] is '\\' or '/');
    }
}

/// <summary>
/// Collects metadata (timestamps, exception codes, faulting modules) for
/// recent crash events from the Application event log. Values are redacted
/// through the diagnostics sanitizer before they ever leave this class.
///
/// The query shells out to the inbox wevtutil: the project deliberately does
/// not reference the Windows Desktop framework, so System.Diagnostics.Eventing
/// is unavailable, and pulling the whole desktop reference set into the
/// NativeAOT publish graph for a diagnostics-only read is not worth it. The
/// process spawn happens once per user-triggered package export.
/// </summary>
internal sealed class WindowsCrashEventCollector
{
    internal const int SchemaVersion = 1;
    internal static readonly TimeSpan LookBackWindow = TimeSpan.FromDays(7);
    internal const int MaximumEvents = 60;
    private const int MaximumScannedEvents = 200;

    // Application Error (1000) carries exception code and faulting module;
    // Application Hang (1002) covers the frozen-explorer variants of restart
    // loops; .NET Runtime (1026) covers managed unhandled exceptions; Windows
    // Error Reporting (1001) adds the bucketed report signature.
    private const string QueryXPath =
        "*[System[Provider[@Name='Application Error' or @Name='Application Hang' or " +
        "@Name='.NET Runtime' or @Name='Windows Error Reporting'] and " +
        "TimeCreated[timediff(@SystemTime) <= 604800000]]]";

    private static readonly string[] WevtutilArguments =
    [
        "qe",
        "Application",
        "/q:" + QueryXPath,
        "/c:" + MaximumScannedEvents.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "/rd:true",
        "/f:Xml",
        "/e:Events"
    ];

    private readonly Func<Task<string>>? _stdoutOverride;

    public WindowsCrashEventCollector()
    {
    }

    /// <summary>Test seam: replaces the wevtutil spawn with a canned payload.</summary>
    internal WindowsCrashEventCollector(Func<Task<string>> stdoutOverride)
    {
        _stdoutOverride = stdoutOverride;
    }

    public async Task<WindowsCrashEventReport> CollectAsync(
        Func<string, string> valueSanitizer,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset windowStartUtc = DateTimeOffset.UtcNow - LookBackWindow;
        try
        {
            string xml = _stdoutOverride is not null
                ? await _stdoutOverride()
                : await RunWevtutilAsync(cancellationToken);
            IReadOnlyList<WindowsCrashEventEntry> events = ParseEvents(xml, valueSanitizer);
            return new WindowsCrashEventReport(
                SchemaVersion,
                events.Count == 0 ? "no-matching-events" : "ok",
                windowStartUtc,
                events);
        }
        catch (Exception ex)
        {
            // The package must still export when event logs are unavailable
            // (locked down machines, stripped images, spawn failures).
            App.Log($"[Diagnostics] Crash event collection unavailable: {ex.Message}");
            return new WindowsCrashEventReport(
                SchemaVersion,
                "unavailable",
                windowStartUtc,
                []);
        }
    }

    internal static IReadOnlyList<WindowsCrashEventEntry> ParseEvents(
        string xml,
        Func<string, string> valueSanitizer)
    {
        var events = new List<WindowsCrashEventEntry>();
        var settings = new XmlReaderSettings
        {
            Async = false,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit
        };

        // Single linear pass with explicit event boundaries. Element content is
        // captured through the text node that follows the element, never by an
        // inline ReadElementContent* call: those advance onto the next sibling,
        // and the following Read() would then skip it entirely (a self-closing
        // TimeCreated right after EventID is exactly that shape).
        DateTimeOffset? created = null;
        string? provider = null;
        long eventId = 0;
        Dictionary<string, string>? properties = null;
        bool insideEventData = false;
        string? pendingDataName = null;
        bool pendingEventId = false;

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        while (reader.Read() && events.Count < MaximumEvents)
        {
            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName == "Event")
            {
                if (created is not null && !string.IsNullOrEmpty(provider))
                {
                    var candidate = new WindowsCrashEventEntry(
                        created.Value,
                        provider,
                        eventId,
                        properties ?? []);
                    if (WindowsCrashEventFilterPolicy.ShouldInclude(candidate.Properties))
                    {
                        events.Add(candidate);
                    }
                }

                created = null;
                provider = null;
                eventId = 0;
                properties = null;
                insideEventData = false;
                pendingDataName = null;
                pendingEventId = false;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Text)
            {
                if (pendingEventId)
                {
                    _ = long.TryParse(reader.Value.Trim(), out eventId);
                    pendingEventId = false;
                }
                else if (pendingDataName is not null)
                {
                    properties ??= [];
                    properties[pendingDataName] = valueSanitizer(reader.Value);
                    pendingDataName = null;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "Event":
                    created = null;
                    provider = null;
                    eventId = 0;
                    properties = null;
                    insideEventData = false;
                    pendingDataName = null;
                    pendingEventId = false;
                    break;

                case "EventData":
                    insideEventData = true;
                    properties ??= [];
                    break;

                case "Data" when insideEventData:
                    pendingDataName = reader.GetAttribute("Name");
                    break;

                case "Provider" when !insideEventData:
                    provider = reader.GetAttribute("Name");
                    break;

                case "EventID" when !insideEventData:
                    pendingEventId = true;
                    break;

                case "TimeCreated" when !insideEventData:
                    // Event-log timestamps carry 100ns precision (9 fractional
                    // digits), which DateTimeOffset.TryParse rejects; XmlConvert
                    // accepts the full range.
                    string? systemTime = reader.GetAttribute("SystemTime");
                    try
                    {
                        created = systemTime is null
                            ? null
                            : XmlConvert.ToDateTimeOffset(systemTime);
                    }
                    catch (FormatException)
                    {
                        // Leave created null; the event is skipped.
                    }

                    break;
            }
        }

        return events;
    }

    internal static string SerializeReport(WindowsCrashEventReport report)
    {
        return JsonSerializer.Serialize(
            report,
            CrashEventJsonContext.Default.WindowsCrashEventReport);
    }

    private async Task<string> RunWevtutilAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "wevtutil.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in WevtutilArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (_stdoutOverride is not null)
        {
            return await _stdoutOverride();
        }

        if (Win32Helper.IsWindowsInSModeLockdown())
        {
            throw new PlatformNotSupportedException("wevtutil is skipped under Windows S Mode lockdown policy.");
        }

        return await RunProcessAsync(startInfo, cancellationToken);
    }

    private static async Task<string> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("wevtutil failed to start.");
        }

        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                $"wevtutil produced no output (exit code {process.ExitCode}).");
        }

        return output;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(WindowsCrashEventReport),
    TypeInfoPropertyName = "WindowsCrashEventReport")]
internal sealed partial class CrashEventJsonContext : JsonSerializerContext
{
}
