namespace SimpleVoiceChat;

internal sealed class ControllerLifecycle
{
    private const int Created = 0;
    private const int Started = 1;
    private const int Disposed = 2;

    private int state;

    public bool IsStarted => Volatile.Read(ref state) == Started;

    public bool TryStart(object owner)
    {
        while (true)
        {
            int current = Volatile.Read(ref state);
            if (current == Disposed)
            {
                throw new ObjectDisposedException(owner.GetType().Name);
            }
            if (current == Started)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref state, Started, Created) == Created)
            {
                return true;
            }
        }
    }

    public bool TryDispose()
    {
        return Interlocked.Exchange(ref state, Disposed) != Disposed;
    }
}
