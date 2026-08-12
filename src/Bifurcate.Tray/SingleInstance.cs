using System.Windows;

namespace Bifurcate.Tray;

/// <summary>
/// Keeps one tray icon per signed-in user. A second launch does not open a rival instance, it asks
/// the running one to show its window, which is what someone clicking the shortcut twice expects.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Bifurcate.Tray.Instance";
    private const string ShowEventName = @"Local\Bifurcate.Tray.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequested;
    private readonly CancellationTokenSource _stopping = new();

    private SingleInstance(Mutex mutex, EventWaitHandle showRequested, bool isFirst)
    {
        _mutex = mutex;
        _showRequested = showRequested;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    public static SingleInstance Create()
    {
        Mutex mutex = new(initiallyOwned: true, MutexName, out bool createdNew);
        EventWaitHandle showRequested = new(false, EventResetMode.AutoReset, ShowEventName);
        return new SingleInstance(mutex, showRequested, createdNew);
    }

    public void RequestShow() => _showRequested.Set();

    public void ListenForShowRequests(Action onShowRequested)
    {
        Thread listener = new(() =>
        {
            WaitHandle[] handles = [_showRequested, _stopping.Token.WaitHandle];

            while (WaitHandle.WaitAny(handles) == 0)
            {
                Application.Current?.Dispatcher.Invoke(onShowRequested);
            }
        })
        {
            IsBackground = true,
            Name = "Bifurcate show-request listener",
        };

        listener.Start();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        if (IsFirst) { _mutex.ReleaseMutex(); }
        _mutex.Dispose();
        _showRequested.Dispose();
        _stopping.Dispose();
    }
}
