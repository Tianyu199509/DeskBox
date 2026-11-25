using Microsoft.UI.Dispatching;

namespace DeskBox;

public partial class App
{
    private async Task<bool> TryDispatchShutdownToUiAsync(DispatcherQueue queue)
    {
        int uiEntered = 0;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (Volatile.Read(ref uiEntered) == 0)
            {
                Log("[Shutdown] UI dispatcher did not pick up shutdown within 5s; arming immediate exit backstop.");
                ArmShutdownExitBackstop(TimeSpan.FromMilliseconds(250));
            }
        });

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
        {
            Interlocked.Exchange(ref uiEntered, 1);
            try
            {
                await ShutdownApplicationOnUiCoreAsync();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
        {
            return false;
        }

        await completion.Task;
        return true;
    }
}
