namespace DeskBox.Contracts;

internal interface ITodoReminderSession : IAsyncDisposable
{
    void Start();
    void Refresh();
    Task<int> CheckNowAsync(DateTimeOffset now);
}
