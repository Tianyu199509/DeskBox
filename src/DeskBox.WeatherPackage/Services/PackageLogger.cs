namespace DeskBox.WeatherPackage.Services;

internal static class PackageLogger
{
    internal static Action<string>? Sink { get; set; }
    public static void Log(string message)
    {
        try { Sink?.Invoke(message); } catch { }
    }
    public static void LogVerbose(string message) => Log(message);
}
