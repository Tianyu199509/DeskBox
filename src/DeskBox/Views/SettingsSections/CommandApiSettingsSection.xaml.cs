using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// Settings section for the local command API used by DeskBox.Cli and MCP
/// clients: master enable switch (applies at next launch), the per-request
/// read-only switch, and the destructive-commands gate. Reads and writes
/// settings directly through the shared SettingsService.
/// </summary>
public sealed partial class CommandApiSettingsSection : UserControl
{
    private bool _isLoading;

    public CommandApiSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private SettingsService Settings => App.Current.SettingsService;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFromSettings();
    }

    /// <summary>
    /// Re-reads settings and updates the controls. Called when the section
    /// becomes visible and right after any toggle applies, so the audit path
    /// and dependent toggles stay in sync.
    /// </summary>
    public void RefreshFromSettings()
    {
        _isLoading = true;
        try
        {
            var settings = Settings.Settings;
            EnableApiToggle.IsOn = settings.EnableCommandApi;
            ReadOnlyToggle.IsOn = settings.CommandApiReadOnly;
            AllowDestructiveToggle.IsOn = settings.AllowDestructiveCommands;
            ReadOnlyToggle.IsEnabled = settings.EnableCommandApi;
            AllowDestructiveToggle.IsEnabled = settings.EnableCommandApi;
            AuditLogPathText.Text = System.IO.Path.Combine(
                DeskBoxDataPathService.Current.RootPath,
                "CommandApi.audit.log");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void EnableApiToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        // The master switch decides whether the pipe server listens at
        // startup; the change takes effect at the next app start.
        Settings.Settings.EnableCommandApi = EnableApiToggle.IsOn;
        Settings.SaveDebounced();
        ReadOnlyToggle.IsEnabled = EnableApiToggle.IsOn;
        AllowDestructiveToggle.IsEnabled = EnableApiToggle.IsOn;
    }

    private void ReadOnlyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        // Read per request by the command dispatcher: effective immediately,
        // no restart needed.
        Settings.Settings.CommandApiReadOnly = ReadOnlyToggle.IsOn;
        Settings.SaveDebounced();
    }

    private void AllowDestructiveToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        // Read per request by the command dispatcher: effective immediately,
        // no restart needed.
        Settings.Settings.AllowDestructiveCommands = AllowDestructiveToggle.IsOn;
        Settings.SaveDebounced();
    }
}
