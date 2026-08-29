using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DeskBox.Services;

public sealed record NativeAppNotificationAction(
    string Text,
    IReadOnlyDictionary<string, string> Arguments,
    string? InputId = null,
    bool IsContextMenu = false);

public sealed record NativeAppNotificationComboBoxItem(
    string Id,
    string Text);

public sealed record NativeAppNotificationComboBox(
    string Id,
    string Title,
    string SelectedItemId,
    IReadOnlyList<NativeAppNotificationComboBoxItem> Items);

public enum NativeAppNotificationActivationSource
{
    Unknown = 0,
    NotificationInvokedEvent = 1,
    CurrentAppInstance = 2
}

public sealed record NativeAppNotificationActivation(
    string Arguments,
    IReadOnlyDictionary<string, string> UserInput,
    NativeAppNotificationActivationSource Source =
        NativeAppNotificationActivationSource.Unknown,
    DateTimeOffset CapturedAtUtc = default,
    int SourceProcessId = 0,
    string? EnvelopeId = null);

public sealed record NativeAppNotificationOptions(
    string? Tag = null,
    string? Group = null);

public sealed record NativeAppNotificationSnapshot(
    uint Id,
    string Tag,
    string Group,
    string Payload);

public sealed class NativeAppNotificationService : IDisposable
{
    private readonly Action<NativeAppNotificationActivation> _activated;
    private bool _isRegistered;
    private bool _isDisposed;

    public NativeAppNotificationService(Action<string> activated)
        : this(activation => activated(activation.Arguments))
    {
    }

    public NativeAppNotificationService(Action<NativeAppNotificationActivation> activated)
    {
        _activated = activated;
    }

    public bool IsRegistered => _isRegistered;

    public bool Register()
    {
        if (_isDisposed)
        {
            return false;
        }

        if (_isRegistered)
        {
            return true;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _isRegistered = true;
            App.Log("[Notification] Native app notification registered");
            return true;
        }
        catch (Exception ex)
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            App.Log($"[Notification] Native app notification registration failed: {ex}");
            return false;
        }
    }

    public bool TryShow(
        string title,
        string message,
        IReadOnlyDictionary<string, string>? arguments = null,
        IReadOnlyList<NativeAppNotificationAction>? actions = null,
        IReadOnlyList<NativeAppNotificationComboBox>? comboBoxes = null,
        NativeAppNotificationOptions? options = null)
    {
        if (_isDisposed || !Register())
        {
            return false;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message);

            if (!string.IsNullOrWhiteSpace(options?.Group))
            {
                builder.SetGroup(options.Group);
            }

            if (!string.IsNullOrWhiteSpace(options?.Tag))
            {
                builder.SetTag(options.Tag);
            }

            if (arguments is not null)
            {
                foreach (var (key, value) in arguments)
                {
                    builder.AddArgument(key, value);
                }
            }

            if (comboBoxes is not null)
            {
                foreach (var comboBox in comboBoxes)
                {
                    if (string.IsNullOrWhiteSpace(comboBox.Id) ||
                        comboBox.Items.Count == 0)
                    {
                        continue;
                    }

                    var appComboBox = new AppNotificationComboBox(comboBox.Id);
                    if (!string.IsNullOrWhiteSpace(comboBox.Title))
                    {
                        appComboBox.SetTitle(comboBox.Title);
                    }

                    foreach (var item in comboBox.Items)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Id) &&
                            !string.IsNullOrWhiteSpace(item.Text))
                        {
                            appComboBox.AddItem(item.Id, item.Text);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(comboBox.SelectedItemId))
                    {
                        appComboBox.SetSelectedItem(comboBox.SelectedItemId);
                    }

                    builder.AddComboBox(appComboBox);
                }
            }

            if (actions is not null)
            {
                foreach (var action in actions)
                {
                    if (string.IsNullOrWhiteSpace(action.Text))
                    {
                        continue;
                    }

                    var button = new AppNotificationButton(action.Text);
                    foreach (var (key, value) in action.Arguments)
                    {
                        button.AddArgument(key, value);
                    }

                    if (!string.IsNullOrWhiteSpace(action.InputId))
                    {
                        button.SetInputId(action.InputId);
                    }

                    if (action.IsContextMenu)
                    {
                        button.SetContextMenuPlacement();
                    }

                    builder.AddButton(button);
                }
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Notification] Native app notification show failed: {ex}");
            return false;
        }
    }

    public async Task<IReadOnlyList<NativeAppNotificationSnapshot>> GetAllAsync()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(NativeAppNotificationService));
        }

        IList<AppNotification> notifications =
            await AppNotificationManager.Default.GetAllAsync();
        return notifications
            .Select(notification => new NativeAppNotificationSnapshot(
                notification.Id,
                notification.Tag ?? string.Empty,
                notification.Group ?? string.Empty,
                notification.Payload ?? string.Empty))
            .ToArray();
    }

    public async Task RemoveByTagAndGroupAsync(string tag, string group)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(NativeAppNotificationService));
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("A notification tag is required.", nameof(tag));
        }

        if (string.IsNullOrWhiteSpace(group))
        {
            throw new ArgumentException("A notification group is required.", nameof(group));
        }

        await AppNotificationManager.Default.RemoveByTagAndGroupAsync(tag, group);
        App.Log($"[Notification] Removed native app notification tag={tag} group={group}");
    }

    public bool Unregister()
    {
        if (_isDisposed)
        {
            return false;
        }

        if (!_isRegistered)
        {
            return true;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            AppNotificationManager.Default.Unregister();
            _isRegistered = false;
            App.Log("[Notification] Native app notification unregistered");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Notification] Native app notification unregister failed: {ex}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _ = Unregister();
        _isDisposed = true;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var userInput = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in args.UserInput)
        {
            if (!string.IsNullOrWhiteSpace(input.Key))
            {
                userInput[input.Key] = input.Value ?? string.Empty;
            }
        }

        _activated(new NativeAppNotificationActivation(
            args.Argument,
            userInput,
            NativeAppNotificationActivationSource.NotificationInvokedEvent,
            DateTimeOffset.UtcNow,
            Environment.ProcessId));
    }
}
