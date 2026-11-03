using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI.Controls;
using DeskBox.Services;
using Microsoft.UI.Xaml;

[assembly: InternalsVisibleTo("DeskBox.Tests")]

namespace DeskBox;

internal static class SettingsControlsAdapter
{
    private static int s_registered;

    [ModuleInitializer]
    internal static void InitializeModule() => EnsureRegistered();

    internal static void EnsureRegistered()
    {
        if (System.Threading.Interlocked.Exchange(ref s_registered, 1) != 0)
        {
            return;
        }

        InfoTip.RegisterExternalCardAdapter(
            static d => d is SettingsCard or SettingsExpander,
            static element => (element as SettingsCard)?.Header as string
                ?? (element as SettingsExpander)?.Header as string,
            static (element, composite) =>
            {
                if (element is SettingsCard card)
                {
                    card.Header = composite;
                }
                else if (element is SettingsExpander expander)
                {
                    expander.Header = composite;
                }
            });

        Localized.RegisterExternalHeaderDescriptionAdapter(
            static (target, value) =>
            {
                switch (target)
                {
                    case SettingsCard settingsCard:
                        settingsCard.Header = InfoTip.TryCreateHeaderContent(settingsCard, value) ?? value;
                        return true;
                    case SettingsExpander settingsExpander:
                        settingsExpander.Header = InfoTip.TryCreateHeaderContent(settingsExpander, value) ?? value;
                        return true;
                    default:
                        return false;
                }
            },
            static (target, value) =>
            {
                switch (target)
                {
                    case SettingsCard settingsCard:
                        settingsCard.Description = value;
                        return true;
                    case SettingsExpander settingsExpander:
                        settingsExpander.Description = value;
                        return true;
                    default:
                        return false;
                }
            });
    }
}
