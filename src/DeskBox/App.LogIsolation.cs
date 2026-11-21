namespace DeskBox;

public partial class App
{
    private static string? s_testLogFilePathOverride;

    internal static string? TestLogFilePathOverride
    {
        get => Volatile.Read(ref s_testLogFilePathOverride);
        set
        {
            Volatile.Write(ref s_testLogFilePathOverride, value);
            Volatile.Write(ref s_logDirectoryEnsured, 0);
        }
    }

    private static string EffectiveLogPath =>
        Volatile.Read(ref s_testLogFilePathOverride) ?? LogPath;
}
