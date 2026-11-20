using DeskBox.Helpers;

namespace DeskBox.Services;

public sealed record MusicVolumeSnapshot(
    double SystemVolume,
    double SessionVolume,
    bool HasSessionVolume,
    bool HasSystemVolume = true);

internal interface IMusicVolumeController
{
    Task<MusicVolumeSnapshot> GetVolumeAsync(string sourceAppUserModelId, string sourceDisplayName);
    Task<double> GetSystemMasterVolumeAsync();
    Task<bool> TrySetSystemMasterVolumeAsync(double volume);
    Task<bool> TrySetSessionVolumeAsync(string sourceAppUserModelId, string sourceDisplayName, double volume);
}

public sealed class MusicVolumeService : IMusicVolumeController
{
    public MusicVolumeService()
        : this(MusicVolumeBackendPolicy.Current)
    {
    }

    internal MusicVolumeService(MusicVolumeBackendMode _)
    {
    }

    public Task<MusicVolumeSnapshot> GetVolumeAsync(string sourceAppUserModelId, string sourceDisplayName)
    {
        return Task.Run(() => GetVolume(sourceAppUserModelId, sourceDisplayName));
    }

    public Task<double> GetSystemMasterVolumeAsync()
    {
        return Task.Run(GetSystemMasterVolumeCore);
    }

    public Task<bool> TrySetSystemMasterVolumeAsync(double volume)
    {
        return Task.Run(() => TrySetSystemMasterVolumeCore(volume));
    }

    public Task<bool> TrySetSessionVolumeAsync(string sourceAppUserModelId, string sourceDisplayName, double volume)
    {
        return Task.Run(() => TrySetSessionVolumeCore(
            sourceAppUserModelId,
            sourceDisplayName,
            volume));
    }

    private MusicVolumeSnapshot GetVolume(string sourceAppUserModelId, string sourceDisplayName)
    {
        MusicVolumeNativeCallResult result = MusicVolumeNativeBackend.GetSnapshot(
            sourceAppUserModelId,
            sourceDisplayName);
        if (!result.Success)
        {
            LogNativeFailure("get snapshot", result);
            return new MusicVolumeSnapshot(0.0, 0.0, false, HasSystemVolume: false);
        }

        bool hasSystemVolume = HasNativeSystemVolume(result);
        if (!hasSystemVolume)
        {
            LogNativeFailure("get snapshot system volume", result);
        }

        return new MusicVolumeSnapshot(
            hasSystemVolume ? result.SystemVolume : 0.0,
            result.SessionVolume,
            result.HasSessionVolume,
            HasSystemVolume: hasSystemVolume);
    }

    internal static bool HasNativeSystemVolume(MusicVolumeNativeCallResult result) =>
        result.Success && result.SystemHResult >= 0;

    private double GetSystemMasterVolumeCore()
    {
        MusicVolumeNativeCallResult result = MusicVolumeNativeBackend.GetSystemVolume();
        if (!HasNativeSystemVolume(result))
        {
            LogNativeFailure("get system volume", result);
            return 0.0;
        }

        return result.SystemVolume;
    }

    private bool TrySetSystemMasterVolumeCore(double volume)
    {
        MusicVolumeNativeCallResult result = MusicVolumeNativeBackend.SetSystemVolume(volume);
        if (!result.Success)
        {
            LogNativeFailure("set system volume", result);
        }

        return result.Success;
    }

    private bool TrySetSessionVolumeCore(
        string sourceAppUserModelId,
        string sourceDisplayName,
        double volume)
    {
        MusicVolumeNativeCallResult result = MusicVolumeNativeBackend.SetSessionVolume(
            sourceAppUserModelId,
            sourceDisplayName,
            volume);
        if (!result.Success)
        {
            LogNativeFailure("set session volume", result);
        }

        return result.Success;
    }

    private static void LogNativeFailure(string operation, MusicVolumeNativeCallResult result)
    {
        App.Log(
            $"[MusicVolume] Rust {operation} failed: {result.Failure}; {result.Detail} " +
            $"status={result.Status}, HRESULT=0x{result.OperationHResult:X8}.");
    }

    internal static string GetProcessName(uint processId) =>
        DeskBox.Platform.ProcessImageNameResolver.GetProcessName(processId);
}
