using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

public sealed record MusicVolumeSnapshot(
    double SystemVolume,
    double SessionVolume,
    bool HasSessionVolume);

public sealed class MusicVolumeService
{
    public Task<MusicVolumeSnapshot> GetVolumeAsync(string sourceAppUserModelId, string sourceDisplayName)
    {
        return Task.Run(() =>
        {
            double systemVolume = GetSystemMasterVolume();
            double? sessionVolume = TryGetSessionVolume(sourceAppUserModelId, sourceDisplayName);

            return new MusicVolumeSnapshot(
                systemVolume,
                sessionVolume ?? 0.0,
                sessionVolume.HasValue);
        });
    }

    public Task<double> GetSystemMasterVolumeAsync()
    {
        return Task.Run(GetSystemMasterVolume);
    }

    public Task<bool> TrySetSystemMasterVolumeAsync(double volume)
    {
        return Task.Run(() => TrySetSystemMasterVolume(volume));
    }

    public Task<bool> TrySetSessionVolumeAsync(string sourceAppUserModelId, string sourceDisplayName, double volume)
    {
        return Task.Run(() => TrySetSessionVolume(sourceAppUserModelId, sourceDisplayName, volume));
    }

    private static double GetSystemMasterVolume()
    {
        if (TryGetEndpointVolume(out var endpointVolume))
        {
            try
            {
                int hr = endpointVolume.GetMasterVolumeLevelScalar(out float volume);
                if (hr >= 0)
                {
                    return NormalizeVolume(volume);
                }
            }
            finally
            {
                ReleaseComObject(endpointVolume);
            }
        }

        return 0.0;
    }

    private static bool TrySetSystemMasterVolume(double volume)
    {
        if (!TryGetEndpointVolume(out var endpointVolume))
        {
            return false;
        }

        try
        {
            float normalizedVolume = (float)NormalizeVolume(volume);
            Guid eventContext = Guid.Empty;
            return endpointVolume.SetMasterVolumeLevelScalar(normalizedVolume, ref eventContext) >= 0;
        }
        finally
        {
            ReleaseComObject(endpointVolume);
        }
    }

    private static double? TryGetSessionVolume(string sourceAppUserModelId, string sourceDisplayName)
    {
        var session = TryFindSession(sourceAppUserModelId, sourceDisplayName);
        if (session?.Volume is null)
        {
            return null;
        }

        try
        {
            int hr = session.Volume.GetMasterVolume(out float volume);
            return hr >= 0 ? NormalizeVolume(volume) : null;
        }
        finally
        {
            session.Dispose();
        }
    }

    private static bool TrySetSessionVolume(string sourceAppUserModelId, string sourceDisplayName, double volume)
    {
        var session = TryFindSession(sourceAppUserModelId, sourceDisplayName);
        if (session?.Volume is null)
        {
            return false;
        }

        try
        {
            float normalizedVolume = (float)NormalizeVolume(volume);
            Guid eventContext = Guid.Empty;
            return session.Volume.SetMasterVolume(normalizedVolume, ref eventContext) >= 0;
        }
        finally
        {
            session.Dispose();
        }
    }

    private static AudioSessionHandle? TryFindSession(string sourceAppUserModelId, string sourceDisplayName)
    {
        if (!TryGetSessionEnumerator(out var sessionEnumerator))
        {
            return null;
        }

        var fallbackSessions = new List<AudioSessionHandle>();
        try
        {
            if (sessionEnumerator.GetCount(out int count) < 0)
            {
                return null;
            }

            for (int i = 0; i < count; i++)
            {
                if (sessionEnumerator.GetSession(i, out var sessionControl) < 0 || sessionControl is null)
                {
                    continue;
                }

                var session = CreateSessionHandle(sessionControl);
                if (session is null)
                {
                    ReleaseComObject(sessionControl);
                    continue;
                }

                if (IsMatchingSession(session, sourceAppUserModelId, sourceDisplayName))
                {
                    foreach (var fallbackSession in fallbackSessions)
                    {
                        fallbackSession.Dispose();
                    }

                    fallbackSessions.Clear();
                    return session;
                }

                if (session.ProcessId != 0 && !session.IsSystemSounds)
                {
                    fallbackSessions.Add(session);
                }
                else
                {
                    session.Dispose();
                }
            }

            return fallbackSessions.Count == 1 ? fallbackSessions[0] : null;
        }
        finally
        {
            if (fallbackSessions.Count != 1)
            {
                foreach (var fallbackSession in fallbackSessions)
                {
                    fallbackSession.Dispose();
                }
            }

            ReleaseComObject(sessionEnumerator);
        }
    }

    private static AudioSessionHandle? CreateSessionHandle(IAudioSessionControl sessionControl)
    {
        IAudioSessionControl2? sessionControl2 = sessionControl as IAudioSessionControl2;
        ISimpleAudioVolume? volume = sessionControl as ISimpleAudioVolume;
        if (sessionControl2 is null || volume is null)
        {
            return null;
        }

        _ = sessionControl.GetDisplayName(out string displayName);
        _ = sessionControl2.GetSessionIdentifier(out string sessionIdentifier);
        _ = sessionControl2.GetSessionInstanceIdentifier(out string sessionInstanceIdentifier);
        _ = sessionControl2.GetProcessId(out uint processId);
        bool isSystemSounds = sessionControl2.IsSystemSoundsSession() == 0;

        return new AudioSessionHandle(
            sessionControl,
            volume,
            processId,
            GetProcessName(processId),
            displayName,
            sessionIdentifier,
            sessionInstanceIdentifier,
            isSystemSounds);
    }

    private static bool TryGetEndpointVolume(out IAudioEndpointVolume endpointVolume)
    {
        endpointVolume = null!;
        if (!TryGetDefaultRenderDevice(out var device))
        {
            return false;
        }

        try
        {
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object endpointVolumeObject) < 0)
            {
                return false;
            }

            endpointVolume = (IAudioEndpointVolume)endpointVolumeObject;
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[MusicVolume] Failed to activate endpoint volume: {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseComObject(device);
        }
    }

    private static bool TryGetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator)
    {
        sessionEnumerator = null!;
        if (!TryGetDefaultRenderDevice(out var device))
        {
            return false;
        }

        IAudioSessionManager2? sessionManager = null;
        try
        {
            Guid iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object sessionManagerObject) < 0)
            {
                return false;
            }

            sessionManager = (IAudioSessionManager2)sessionManagerObject;
            return sessionManager.GetSessionEnumerator(out sessionEnumerator) >= 0 && sessionEnumerator is not null;
        }
        catch (Exception ex)
        {
            App.Log($"[MusicVolume] Failed to enumerate sessions: {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseComObject(sessionManager);
            ReleaseComObject(device);
        }
    }

    private static bool TryGetDefaultRenderDevice(out IMMDevice device)
    {
        device = null!;
        IMMDeviceEnumerator? deviceEnumerator = null;
        try
        {
            deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            return deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) >= 0 && device is not null;
        }
        catch (Exception ex)
        {
            App.Log($"[MusicVolume] Failed to get default render device: {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseComObject(deviceEnumerator);
        }
    }

    private static bool IsMatchingSession(AudioSessionHandle session, string sourceAppUserModelId, string sourceDisplayName)
    {
        if (session.IsSystemSounds)
        {
            return false;
        }

        string normalizedSourceApp = NormalizeMatchText(sourceAppUserModelId);
        string normalizedSourceDisplayName = NormalizeMatchText(sourceDisplayName);
        string normalizedProcessName = NormalizeMatchText(session.ProcessName);
        string normalizedDisplayName = NormalizeMatchText(session.DisplayName);
        string normalizedSessionIdentifier = NormalizeMatchText(session.SessionIdentifier);
        string normalizedSessionInstanceIdentifier = NormalizeMatchText(session.SessionInstanceIdentifier);

        return ContainsMeaningfulMatch(normalizedSessionIdentifier, normalizedSourceApp) ||
               ContainsMeaningfulMatch(normalizedSessionInstanceIdentifier, normalizedSourceApp) ||
               ContainsMeaningfulMatch(normalizedDisplayName, normalizedSourceDisplayName) ||
               ContainsMeaningfulMatch(normalizedProcessName, normalizedSourceDisplayName) ||
               ContainsMeaningfulMatch(normalizedSourceApp, normalizedProcessName) ||
               ContainsMeaningfulMatch(normalizedSessionIdentifier, normalizedSourceDisplayName);
    }

    private static bool ContainsMeaningfulMatch(string haystack, string needle)
    {
        return needle.Length >= 3 &&
               haystack.Length >= 3 &&
               haystack.Contains(needle, StringComparison.Ordinal);
    }

    private static string NormalizeMatchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static double NormalizeVolume(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, 0.0, 1.0)
            : 0.0;
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    private const uint ClsctxAll = 23;

    private enum EDataFlow
    {
        Render,
        Capture,
        All
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications
    }

    private enum AudioSessionState
    {
        Inactive,
        Active,
        Expired
    }

    private sealed class AudioSessionHandle : IDisposable
    {
        public AudioSessionHandle(
            IAudioSessionControl control,
            ISimpleAudioVolume volume,
            uint processId,
            string processName,
            string displayName,
            string sessionIdentifier,
            string sessionInstanceIdentifier,
            bool isSystemSounds)
        {
            Control = control;
            Volume = volume;
            ProcessId = processId;
            ProcessName = processName;
            DisplayName = displayName;
            SessionIdentifier = sessionIdentifier;
            SessionInstanceIdentifier = sessionInstanceIdentifier;
            IsSystemSounds = isSystemSounds;
        }

        public IAudioSessionControl Control { get; }

        public ISimpleAudioVolume Volume { get; }

        public uint ProcessId { get; }

        public string ProcessName { get; }

        public string DisplayName { get; }

        public string SessionIdentifier { get; }

        public string SessionInstanceIdentifier { get; }

        public bool IsSystemSounds { get; }

        public void Dispose()
        {
            ReleaseComObject(Control);
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float level, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float level);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float level);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(ref Guid eventContext);

        [PreserveSig]
        int VolumeStepDown(ref Guid eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupportMask);

        [PreserveSig]
        int GetVolumeRange(out float min, out float max, out float increment);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

        [PreserveSig]
        int RegisterSessionNotification(IntPtr sessionNotification);

        [PreserveSig]
        int UnregisterSessionNotification(IntPtr sessionNotification);

        [PreserveSig]
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);

        [PreserveSig]
        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl sessionControl);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr notifications);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr notifications);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr notifications);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr notifications);

        [PreserveSig]
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);

        [PreserveSig]
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceIdentifier);

        [PreserveSig]
        int GetProcessId(out uint processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolume(out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
    }
}
