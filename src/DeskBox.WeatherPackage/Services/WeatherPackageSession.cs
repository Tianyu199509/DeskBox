namespace DeskBox.WeatherPackage.Services;

internal sealed class WeatherPackageSession : IDisposable
{
    internal string PackageRoot { get; }
    internal string DataRoot { get; }
    internal WeatherHostConnection Host { get; }
    internal PackageLocalization Localization { get; }
    internal WeatherService Weather { get; }
    internal CitySearchService Cities { get; }
    private bool _disposed;
    internal WeatherPackageSession(string packageRoot, string dataRoot, WeatherHostConnection host)
    {
        PackageRoot = packageRoot;
        DataRoot = dataRoot;
        Host = host;
        Localization = new PackageLocalization(packageRoot, host.ReadLocale());
        Weather = new WeatherService(new WeatherCacheStore(Path.Combine(dataRoot, "cache", "weather-cache.json")));
        Cities = new CitySearchService(Weather);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Host.Subscribe(0);
        Weather.Dispose();
    }
}
