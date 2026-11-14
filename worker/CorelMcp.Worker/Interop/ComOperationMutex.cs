namespace CorelMcp.Worker.Interop;

internal sealed class ComMutexException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class ComOperationMutex : IDisposable
{
    public const string Name = "Local\\CorelDrawMcp_v27";
    private readonly int owner = Environment.CurrentManagedThreadId;
    private Mutex? mutex;
    private ComOperationMutex(Mutex mutex) { this.mutex = mutex; }
    public static ComOperationMutex Acquire(string name = Name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException)
            {
                mutex.ReleaseMutex();
                throw new ComMutexException("ABANDONED_MUTEX", "Предыдущий исполнитель оставил mutex. Новая операция не выполнялась; требуется сверка состояния CorelDRAW и незавершённых заданий.");
            }
            if (!acquired) throw new ComMutexException("BUSY", "Другой исполнитель MCP удерживает mutex CorelDRAW.");
            return new ComOperationMutex(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != owner)
            throw new ComMutexException("THREAD_AFFINITY", "Mutex может освободить только получивший его поток.");
        var previous = mutex;
        mutex = null;
        if (previous is null) return;
        try { previous.ReleaseMutex(); } finally { previous.Dispose(); }
    }
}
