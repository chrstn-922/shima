namespace Shima.App;

public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Local\Shima.SingleInstance.Mutex";

    private readonly Mutex _mutex;
    private bool _disposed;

    public SingleInstanceGuard(string mutexName = DefaultMutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        _mutex = new Mutex(initiallyOwned: true, name: mutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    public bool IsPrimaryInstance { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsPrimaryInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Ignore if released from a different shutdown thread.
            }
        }

        _mutex.Dispose();
    }
}
