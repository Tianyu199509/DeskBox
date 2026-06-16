using System.Diagnostics;
using System.Globalization;

namespace DeskBox.Services;

/// <summary>
/// Lightweight opt-in timing logs for performance baseline work.
/// Enable by setting DESKBOX_PERF_LOG=1 before launching DeskBox.
/// </summary>
public static class PerformanceLogger
{
    public const string EnabledEnvironmentVariable = "DESKBOX_PERF_LOG";

    private static readonly Lazy<bool> s_isEnabled = new(
        () => IsEnabledSetting(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable)));

    public static bool IsEnabled => s_isEnabled.Value;

    public static IDisposable Measure(string operation, string? details = null)
    {
        if (!IsEnabled)
        {
            return EmptyScope.Instance;
        }

        return new Scope(operation, details);
    }

    public static void Mark(string operation, string? details = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        App.Log(FormatMessage(operation, null, details));
    }

    internal static bool IsEnabledSetting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalizedValue = value.Trim();
        return normalizedValue switch
        {
            "1" => true,
            _ when normalizedValue.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            _ when normalizedValue.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
            _ when normalizedValue.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
            _ when normalizedValue.Equals("enabled", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };
    }

    private static string FormatMessage(string operation, double? elapsedMs, string? details)
    {
        string message = elapsedMs.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"[Perf] {operation} elapsedMs={elapsedMs.Value:F1}")
            : $"[Perf] {operation}";

        if (string.IsNullOrWhiteSpace(details))
        {
            return message;
        }

        return $"{message} {details.ReplaceLineEndings(" ")}";
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _operation;
        private readonly string? _details;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public Scope(string operation, string? details)
        {
            _operation = operation;
            _details = details;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();
            App.Log(FormatMessage(_operation, _stopwatch.Elapsed.TotalMilliseconds, _details));
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();

        public void Dispose()
        {
        }
    }
}
