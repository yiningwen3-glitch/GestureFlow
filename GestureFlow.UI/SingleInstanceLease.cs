namespace GestureFlow.UI;

public sealed class SingleInstanceLease : IDisposable
{
    private Mutex? _mutex;

    private SingleInstanceLease(Mutex mutex) => _mutex = mutex;

    public static SingleInstanceLease? TryAcquire(string component)
    {
        var mutex = new Mutex(true, $@"Local\GestureFlow.{component}.MvpWindow", out var created);
        if (created)
            return new(mutex);

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        // The WPF startup and exit callbacks are on the mutex-owning dispatcher thread.
        if (_mutex is not { } mutex)
            return;
        _mutex = null;
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
