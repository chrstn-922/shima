namespace Shima.Core.Contracts;

public interface IEventDispatcher
{
    IDisposable Subscribe<T>(Action<T> handler);

    void Publish<T>(T payload);
}
