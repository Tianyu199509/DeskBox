namespace DeskBox.Services;

internal static class StartupLaunchPolicy
{
    private const string StartupArgument = "--startup";
    private const string StartupProtocolUri = "deskbox://startup";
    private const string WakeupProtocolUri = "deskbox://wakeup";

    public static bool IsStartupLaunch(
        IEnumerable<string> processArguments,
        string? activationArguments = null,
        bool isStartupTaskActivation = false)
    {
        return isStartupTaskActivation ||
               processArguments.Any(IsStartupArgument) ||
               ParseActivationArguments(activationArguments).Any(IsStartupArgument);
    }

    private static IEnumerable<string> ParseActivationArguments(string? arguments)
    {
        return string.IsNullOrWhiteSpace(arguments)
            ? []
            : arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsStartupArgument(string argument)
    {
        string normalized = argument.Trim().Trim('"').TrimEnd('/');
        if (string.Equals(normalized, StartupArgument, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) &&
            string.Equals(uri.Scheme, "deskbox", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(uri.Host, "startup", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "wakeup", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
