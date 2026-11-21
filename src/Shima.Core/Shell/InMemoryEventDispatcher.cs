using Shima.Core.Contracts;

namespace Shima.Core.Shell;

public sealed class InMemoryEventDispatcher : IEventDispatcher
{
    private readonly object _sync = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_sync)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _handlers[typeof(T)] = list;
            }

            list.Add(handler);
        }

        return new Subscription(() => Unsubscribe(typeof(T), handler));
    }

    public void Publish<T>(T payload)
    {
        Delegate[] snapshot;
        lock (_sync)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list) || list.Count == 0)
            {
                return;
            }

            snapshot = list.ToArray();
        }

        foreach (var entry in snapshot)
        {
            if (entry is Action<T> typedHandler)
            {
                typedHandler(payload);
            }
        }
    }

    private void Unsubscribe(Type eventType, Delegate handler)
    {
        lock (_sync)
        {
            if (_handlers.TryGetValue(eventType, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                {
                    _handlers.Remove(eventType);
                }
            }
        }
    }

    private sealed class Subscription(Action disposeAction) : IDisposable
    {
        private Action? _disposeAction = disposeAction;

        public void Dispose() => Interlocked.Exchange(ref _disposeAction, null)?.Invoke();
    }
}
