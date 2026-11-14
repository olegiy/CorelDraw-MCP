using System.Runtime.InteropServices;

namespace CorelMcp.Worker.Interop;

// RCW aliases are tracked once. Every release stays on the owning STA thread.
internal sealed class ComScope : IDisposable
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly ComOwnership ownership;
    private readonly HashSet<object> tracked = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> ordered = [];
    private bool disposed;

    public ComScope() : this(new ComOwnership()) { }
    internal ComScope(ComOwnership ownership) { this.ownership = ownership; }

    public T Track<T>(T value) where T : class
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("COM-ссылка принадлежит другому потоку.");
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Marshal.IsComObject(value) && tracked.Add(value)) { ordered.Add(value); ownership.Acquire(value); }
        return value;
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Освобождение COM разрешено только на исходном потоке.");
        if (disposed) return;
        disposed = true;
        List<Exception> failures = [];
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            try { ownership.Release(ordered[i]); } catch (Exception error) { failures.Add(error); }
        }
        ordered.Clear();
        tracked.Clear();
        if (failures.Count > 0) throw new AggregateException("Не удалось освободить часть COM-ссылок.", failures);
    }

    public static long Identity(object value)
    {
        var pointer = Marshal.GetIUnknownForObject(value);
        try { return pointer.ToInt64(); } finally { Marshal.Release(pointer); }
    }
}

internal sealed class ComOwnership
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly Dictionary<object, int> leases = new(ReferenceEqualityComparer.Instance);
    public ComScope CreateScope() => new(this);
    public void Acquire(object value)
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("COM-владение принадлежит другому потоку.");
        leases[value] = leases.GetValueOrDefault(value) + 1;
    }
    public void Release(object value)
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("COM-владение принадлежит другому потоку.");
        var count = leases[value] - 1;
        if (count > 0) leases[value] = count;
        else { leases.Remove(value); Marshal.ReleaseComObject(value); }
    }
}
