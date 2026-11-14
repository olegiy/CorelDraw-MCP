using System.Runtime.InteropServices;
using CorelMcp.Worker.Interop;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class CorelConnectionTests
{
    [Fact]
    public void RejectsMtaBeforeActivation()
    {
        var activator = new FakeActivator();
        using var connection = new CorelConnection(activator);
        var error = Assert.Throws<CorelConnectionException>(() => connection.Connect("CorelDRAW.Application.27"));
        Assert.Equal("STA_REQUIRED", error.Code);
        Assert.Equal(0, activator.Calls);
    }

    [Fact]
    public void RejectsWrongVersionAndReleasesItsSession() => OnSta(() =>
    {
        var activator = new FakeActivator { Session = new FakeSession { VersionMajor = 26, Version = "26.0" } };
        using var connection = new CorelConnection(activator);
        var error = Assert.Throws<CorelConnectionException>(() => connection.Connect("CorelDRAW.Application.27"));
        Assert.Equal("UNSUPPORTED_VERSION", error.Code);
        Assert.Equal(1, activator.Session.ReleaseCount);
        Assert.Throws<CorelConnectionException>(() => connection.GetApplication());
    });

    [Fact]
    public void MissingRegistrationIsKnownBeforeDispatch() => OnSta(() =>
    {
        var activator = new FakeActivator { Failure = new COMException("Нет регистрации", unchecked((int)0x80040154)) };
        using var connection = new CorelConnection(activator);
        var error = Assert.Throws<CorelConnectionException>(() => connection.Connect("CorelDRAW.Application.27"));
        Assert.Equal("COREL_NOT_REGISTERED", error.Code);
        Assert.Equal(1, activator.Calls);
    });

    [Fact]
    public void AnotherStaCannotUseOrReleaseTheConnection() => OnSta(() =>
    {
        var activator = new FakeActivator();
        using var connection = new CorelConnection(activator);
        connection.Connect("CorelDRAW.Application.27");
        var sessionId = connection.SessionId;
        OnSta(() =>
        {
            Assert.Equal("THREAD_AFFINITY", Assert.Throws<CorelConnectionException>(() => connection.GetApplication()).Code);
            Assert.Equal("THREAD_AFFINITY", Assert.Throws<CorelConnectionException>(() => connection.Dispose()).Code);
            Assert.Equal("THREAD_AFFINITY", Assert.Throws<CorelConnectionException>(() => connection.Connect("CorelDRAW.Application.27")).Code);
        });
        Assert.Equal(0, activator.Session.ReleaseCount);
        Assert.Equal(sessionId, connection.SessionId);
        connection.Dispose();
        connection.Dispose();
        Assert.Equal(1, activator.Session.ReleaseCount);
    });

    [Fact]
    public void RefusesOtherProgIdsBeforeActivation() => OnSta(() =>
    {
        var activator = new FakeActivator();
        using var connection = new CorelConnection(activator);
        Assert.Equal("UNSUPPORTED_PROGID", Assert.Throws<CorelConnectionException>(() => connection.Connect("CorelDRAW.Application.26")).Code);
        Assert.Equal(0, activator.Calls);
    });

    [Fact]
    public void ConnectionCannotReplayActivationOrReconnectAfterDispose() => OnSta(() =>
    {
        var activator = new FakeActivator();
        var connection = new CorelConnection(activator);
        connection.Connect("CorelDRAW.Application.27");
        Assert.Equal("ALREADY_CONNECTED", Assert.Throws<CorelConnectionException>(() => connection.Connect("CorelDRAW.Application.27")).Code);
        connection.Dispose();
        Assert.Throws<ObjectDisposedException>(() => connection.Connect("CorelDRAW.Application.27"));
        Assert.Equal(1, activator.Calls);
    });

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Тестовый STA-поток не завершился.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class FakeActivator : ICorelActivator
    {
        public int Calls { get; private set; }
        public FakeSession Session { get; init; } = new();
        public Exception? Failure { get; init; }
        public ICorelSession Activate(string progId)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Session;
        }
    }

    private sealed class FakeSession : ICorelSession
    {
        public int VersionMajor { get; init; } = 27;
        public string Version { get; init; } = "27.2.0.135";
        public int ReleaseCount { get; private set; }
        public VGCore.Application Application => throw new NotSupportedException("COM в unit-тесте запрещён.");
        public void Dispose() => ReleaseCount++;
    }
}
