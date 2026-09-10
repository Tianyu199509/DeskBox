namespace DeskBox.MusicPackage.Services;

public sealed record MusicVolumeSnapshot(double SystemVolume, double SessionVolume, bool HasSessionVolume);

public sealed class MusicVolumeService
{
    private static readonly Lazy<RustMusicVolumeModule?> Backend = new(() =>
    {
        var module = RustMusicVolumeModule.Load(out string detail);
        PackageLog.Write(module is null ? detail : $"[MusicPackage] Rust volume backend: {module.ModulePath}");
        return module;
    });
    internal static string? BackendPath => Backend.Value?.ModulePath;
    public Task<MusicVolumeSnapshot> GetVolumeAsync(string app, string name) => Task.Run(() =>
    {
        var result = Backend.Value?.GetMusicVolumeSnapshot(app, name);
        PackageLog.Write(result?.Success == true
            ? $"[MusicPackage] volume-read success system={result.SystemVolume:F4} session={result.SessionVolume:F4} hasSession={result.HasSessionVolume}"
            : $"[MusicPackage] volume-read failed: {result?.Detail ?? "backend unavailable"}");
        return result?.Success == true
            ? new MusicVolumeSnapshot(result.SystemVolume, result.SessionVolume, result.HasSessionVolume)
            : new MusicVolumeSnapshot(0, 0, false);
    });
    public Task<double> GetSystemMasterVolumeAsync() => Task.Run(() =>
    {
        var result = Backend.Value?.GetSystemMusicVolume();
        PackageLog.Write(result?.Success == true
            ? $"[MusicPackage] system-volume-read success value={result.SystemVolume:F4}"
            : $"[MusicPackage] system-volume-read failed: {result?.Detail ?? "backend unavailable"}");
        return result?.Success == true ? result.SystemVolume : 0;
    });
    public Task<bool> TrySetSystemMasterVolumeAsync(double value) => Task.Run(() =>
        Backend.Value?.SetSystemMusicVolume(Normalize(value)).Success == true);
    public Task<bool> TrySetSessionVolumeAsync(string app, string name, double value) => Task.Run(() =>
        Backend.Value?.SetSessionMusicVolume(app, name, Normalize(value)).Success == true);
    private static double Normalize(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
}
