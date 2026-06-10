using Microsoft.Win32;

namespace DeskBox.Services;

/// <summary>
/// Manages auto-start on boot via Windows Registry (HKCU\Run).
/// </summary>
public static class StartupService
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DeskBox";

    /// <summary>
    /// Check if DeskBox is registered for auto-start.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            return key?.GetValue(AppName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enable auto-start on boot. Registers the current executable in HKCU\Run.
    /// </summary>
    public static void Enable()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            key?.SetValue(AppName, $"\"{exePath}\" --startup");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupService] Failed to enable startup: {ex.Message}");
        }
    }

    /// <summary>
    /// Disable auto-start on boot. Removes the registry entry.
    /// </summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            key?.DeleteValue(AppName, false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupService] Failed to disable startup: {ex.Message}");
        }
    }

    /// <summary>
    /// Set auto-start to the specified state.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }
}
