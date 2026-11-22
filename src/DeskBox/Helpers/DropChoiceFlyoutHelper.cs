using DeskBox.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Helpers;

/// <summary>
/// Shows a right-button drag-and-drop choice <see cref="MenuFlyout"/> and
/// guarantees the completion task resolves even if WinUI ignores <c>ShowAt</c>
/// on a hidden, unloaded, or deactivated island.
/// </summary>
internal static class DropChoiceFlyoutHelper
{
    public static async Task<TChoice> ShowAndWaitAsync<TChoice>(
        MenuFlyout menu,
        FrameworkElement placementTarget,
        Windows.Foundation.Point point,
        TaskCompletionSource<TChoice> choiceSource,
        TChoice cancelValue,
        IntPtr hostWindowHandle = default,
        Action? onOpened = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(placementTarget);
        ArgumentNullException.ThrowIfNull(choiceSource);

        if (cancellationToken.IsCancellationRequested ||
            !placementTarget.IsLoaded ||
            placementTarget.XamlRoot is null ||
            (hostWindowHandle != IntPtr.Zero && !Win32Helper.IsWindowVisible(hostWindowHandle)))
        {
            choiceSource.TrySetResult(cancelValue);
            return await choiceSource.Task;
        }

        bool opened = false;
        void OnMenuOpened(object? sender, object e)
        {
            opened = true;
            onOpened?.Invoke();
        }

        void OnMenuClosed(object? sender, object e)
        {
            choiceSource.TrySetResult(cancelValue);
        }

        void OnTargetUnloaded(object sender, RoutedEventArgs e)
        {
            choiceSource.TrySetResult(cancelValue);
        }

        menu.Opened += OnMenuOpened;
        menu.Closed += OnMenuClosed;
        placementTarget.Unloaded += OnTargetUnloaded;
        using CancellationTokenRegistration tokenRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => choiceSource.TrySetResult(cancelValue))
            : default;
        using var openWatchCts = new CancellationTokenSource();

        try
        {
            try
            {
                menu.ShowAt(placementTarget, point);
            }
            catch
            {
                choiceSource.TrySetResult(cancelValue);
                return await choiceSource.Task;
            }

            DispatcherQueue? dispatcher = placementTarget.DispatcherQueue;
            if (dispatcher is not null)
            {
                _ = VerifyFlyoutOpenedAsync(
                    menu,
                    placementTarget,
                    hostWindowHandle,
                    dispatcher,
                    choiceSource,
                    cancelValue,
                    openWatchCts.Token);
            }
            else if (!opened && !menu.IsOpen)
            {
                choiceSource.TrySetResult(cancelValue);
            }

            return await choiceSource.Task;
        }
        finally
        {
            openWatchCts.Cancel();
            menu.Opened -= OnMenuOpened;
            menu.Closed -= OnMenuClosed;
            placementTarget.Unloaded -= OnTargetUnloaded;
            if (menu.IsOpen)
            {
                try
                {
                    menu.Hide();
                }
                catch
                {
                }
            }
        }
    }

    private static async Task VerifyFlyoutOpenedAsync<TChoice>(
        MenuFlyout menu,
        FrameworkElement placementTarget,
        IntPtr hostWindowHandle,
        DispatcherQueue dispatcher,
        TaskCompletionSource<TChoice> choiceSource,
        TChoice cancelValue,
        CancellationToken watchToken)
    {
        try
        {
            await Task.Delay(180, watchToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (choiceSource.Task.IsCompleted || watchToken.IsCancellationRequested)
        {
            return;
        }

        if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (choiceSource.Task.IsCompleted)
                {
                    return;
                }

                bool hostVisible = hostWindowHandle == IntPtr.Zero ||
                    Win32Helper.IsWindowVisible(hostWindowHandle);
                if (!placementTarget.IsLoaded || !hostVisible || !menu.IsOpen)
                {
                    choiceSource.TrySetResult(cancelValue);
                }
            }))
        {
            choiceSource.TrySetResult(cancelValue);
        }
    }
}
