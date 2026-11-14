using System.Diagnostics;
using System.Text;
using CorelMcp.Worker.Interop;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class ComMutexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherProcessBlocksMutationAndAbandonmentNeverRunsTheCallback(bool abandon)
    {
        var name = "Local\\CorelMcp_MutexTest_" + Guid.NewGuid().ToString("N");
        using var keeper = new Mutex(false, name);
        var directory = Path.Combine(Path.GetTempPath(), "CorelMcp_MutexTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "holder.ps1");
        File.WriteAllText(script, """
            param([string]$Name, [switch]$Abandon)
            $mutex = New-Object Threading.Mutex($false, $Name)
            if (-not $mutex.WaitOne(0)) { exit 17 }
            [Console]::Out.WriteLine('ACQUIRED')
            [Console]::Out.Flush()
            $null = [Console]::In.ReadLine()
            if ($Abandon) { [Environment]::Exit(0) }
            $mutex.ReleaseMutex()
            $mutex.Dispose()
            """, new UTF8Encoding(true));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var item in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Name", name }) start.ArgumentList.Add(item);
        if (abandon) start.ArgumentList.Add("-Abandon");
        using var child = Process.Start(start)!;
        try
        {
            Assert.Equal("ACQUIRED", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("BUSY", Assert.Throws<ComMutexException>(() => ComOperationMutex.Acquire(name)).Code);
            child.StandardInput.WriteLine();
            child.StandardInput.Flush();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, child.ExitCode);
            bool changed = false;
            if (abandon)
            {
                var error = Assert.Throws<ComMutexException>(() => { using var lease = ComOperationMutex.Acquire(name); changed = true; });
                Assert.Equal("ABANDONED_MUTEX", error.Code);
                Assert.False(changed);
            }
            using var after = ComOperationMutex.Acquire(name);
        }
        finally
        {
            if (!child.HasExited) { child.StandardInput.WriteLine(); child.StandardInput.Flush(); }
        }
    }

    [Fact]
    public void AForeignThreadCannotReleaseTheOwnerMutex()
    {
        var name = "Local\\CorelMcp_MutexTest_" + Guid.NewGuid().ToString("N");
        using var lease = ComOperationMutex.Acquire(name);
        Exception? failure = null;
        var thread = new Thread(() => { try { lease.Dispose(); } catch (Exception error) { failure = error; } });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal("THREAD_AFFINITY", Assert.IsType<ComMutexException>(failure).Code);
    }
}
