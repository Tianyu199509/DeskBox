using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace DeskBox;

internal static class DeskBoxMainProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (DeskBoxAgentCliEntry.TryIntercept(args, out int cliExitCode))
        {
            return cliExitCode;
        }

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(_params =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }
}
