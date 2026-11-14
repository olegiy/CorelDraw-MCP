using System.Runtime.InteropServices;

namespace CorelMcp.Worker.Interop;

internal interface ICorelActivator { ICorelSession Activate(string progId); }
internal interface ICorelSession : IDisposable
{
    int VersionMajor { get; }
    string Version { get; }
    VGCore.Application Application { get; }
}

internal sealed class CorelConnectionException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public Exception? CleanupError { get; internal set; }
}

internal sealed class CorelConnection(ICorelActivator activator) : IDisposable
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private ICorelSession? session;
    private bool disposed;
    private bool attempted;
    public string? SessionId { get; private set; }
    public string? Version { get; private set; }

    public CorelConnection() : this(new ComCorelActivator()) { }

    private void AssertOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new CorelConnectionException("THREAD_AFFINITY", "Подключение CorelDRAW принадлежит другому STA-потоку.");
    }

    public void Connect(string progId)
    {
        AssertOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new CorelConnectionException("STA_REQUIRED", "Подключение CorelDRAW разрешено только на STA-потоке.");
        if (progId != "CorelDRAW.Application.27")
            throw new CorelConnectionException("UNSUPPORTED_PROGID", "Ожидался ProgID CorelDRAW.Application.27.");
        if (attempted)
            throw new CorelConnectionException("ALREADY_CONNECTED", "Повторная активация запрещена. Создайте новый сеанс после проверки состояния.");
        attempted = true;
        ICorelSession? candidate = null;
        try
        {
            candidate = activator.Activate(progId);
            if (candidate.VersionMajor != 27)
                throw new CorelConnectionException("UNSUPPORTED_VERSION", $"Ожидалась версия CorelDRAW 27, получена {candidate.Version}.");
            var rawVersion = candidate.Version;
            var version = System.Text.RegularExpressions.Regex.Match(rawVersion, @"\d+(?:\.\d+){1,3}",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
            Version = version.Success ? version.Value : rawVersion;
            session = candidate;
            SessionId = Guid.NewGuid().ToString("N");
        }
        catch (Exception error)
        {
            var failure = error as CorelConnectionException ?? new CorelConnectionException(
                error.HResult == unchecked((int)0x80040154) ? "COREL_NOT_REGISTERED" : "COREL_CONNECT_FAILED",
                error.HResult == unchecked((int)0x80040154) ? "CorelDRAW 27 не зарегистрирован." : "Не удалось подключиться к CorelDRAW 27.", error);
            if (candidate is not null)
            {
                try { candidate.Dispose(); } catch (Exception cleanup) { failure.CleanupError = cleanup; }
            }
            throw failure;
        }
    }

    public VGCore.Application GetApplication()
    {
        AssertOwner();
        ObjectDisposedException.ThrowIf(disposed, this);
        return session?.Application ?? throw new CorelConnectionException("NOT_CONNECTED", "Сеанс CorelDRAW не подключён.");
    }

    public void Dispose()
    {
        AssertOwner();
        if (disposed) return;
        disposed = true;
        var previous = session;
        session = null;
        SessionId = null;
        Version = null;
        previous?.Dispose();
    }
}

internal sealed class ComCorelActivator : ICorelActivator
{
    public ICorelSession Activate(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new CorelConnectionException("COREL_NOT_REGISTERED", "CorelDRAW 27 не зарегистрирован.");
        var value = Activator.CreateInstance(type) ?? throw new CorelConnectionException("COREL_CONNECT_FAILED", "CorelDRAW не вернул COM-объект.");
        try { return new ComCorelSession((VGCore.Application)value); }
        catch { if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); throw; }
    }
}

internal sealed class ComCorelSession(VGCore.Application application) : ICorelSession
{
    private VGCore.Application? value = application;
    public VGCore.Application Application => value ?? throw new ObjectDisposedException(nameof(ComCorelSession));
    public int VersionMajor => Application.VersionMajor;
    public string Version => Application.Version;
    public void Dispose()
    {
        var previous = value;
        value = null;
        if (previous is not null && Marshal.IsComObject(previous)) Marshal.ReleaseComObject(previous);
    }
}
