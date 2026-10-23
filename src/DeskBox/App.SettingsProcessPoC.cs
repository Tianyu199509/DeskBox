// Phase 0 PoC for the 1.6.0 settings-process split (batch 1): a second
// DeskBox.exe launched with --settings-poc skips every primary-instance
// responsibility (AUMID, activation event, single-instance mutex, startup
// watchdog, elevation gate, DI container) and shows a minimal window, so the
// settings-process floor can be measured on real hardware: spawn-to-shown
// latency, private bytes while open, and full release on close.
// This file is scaffolding only — it carries no product behavior and is
// replaced by the real settings host in batch 3.

namespace DeskBox;

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

public partial class App
{
    internal const string SettingsProcessLaunchArgument = "--settings-poc";

    private static readonly long SettingsPocBaselineTimestamp = Stopwatch.GetTimestamp();

    internal bool IsSettingsProcess => _isSettingsProcess;

    private bool _isSettingsProcess;

    internal static bool IsSettingsProcessLaunch(string[] arguments)
    {
        for (int i = 1; i < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], SettingsProcessLaunchArgument, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void SettingsPocTrace(string marker)
    {
        double elapsedMs =
            (Stopwatch.GetTimestamp() - SettingsPocBaselineTimestamp) * 1000.0 /
            Stopwatch.Frequency;
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "deskbox-settings-poc.log"),
                $"{DateTimeOffset.UtcNow:O} +{elapsedMs:F0}ms pid={Environment.ProcessId} {marker}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Measurement logging must never take the window down.
        }
    }

    private void InitializeSettingsProcess()
    {
        _isSettingsProcess = true;
        SettingsPocTrace("branch-entered");
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
            SettingsPocTrace($"unhandled-exception {eventArgs.Message}");
        SettingsPocTrace("ctor-done");
    }

    private void ShowSettingsProcessPoCWindow()
    {
        var window = new Window
        {
            Title = "DeskBox Settings PoC",
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new TextBlock
        {
            Text = "Settings-process PoC shell",
            FontSize = 20,
        });
        content.Children.Add(new TextBlock
        {
            Text = "This window measures the second-process floor; closing it exits the process.",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
        });
        window.Content = content;
        bool activationTraced = false;
        window.Activated += (_, _) =>
        {
            if (!activationTraced)
            {
                activationTraced = true;
                SettingsPocTrace("window-activated");
            }
        };
        window.Closed += (_, _) =>
        {
            SettingsPocTrace("window-closed");
            Environment.Exit(0);
        };
        window.Activate();
        SettingsPocTrace("onlaunched-done");
    }
}
