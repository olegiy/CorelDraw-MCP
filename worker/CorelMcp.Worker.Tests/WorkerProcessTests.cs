using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class WorkerProcessTests
{
    [Fact]
    public async Task PublishesReadyAndRejectsLegacyCodeWithoutActivatingCom()
    {
        using var worker = Start();
        using var ready = await Read(worker);
        Assert.Equal("ready", ready.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, ready.RootElement.GetProperty("protocol_version").GetInt32());
        Assert.Equal("x64", ready.RootElement.GetProperty("architecture").GetString());
        Assert.StartsWith(".NET 10.", ready.RootElement.GetProperty("runtime").GetString());
        var id = Guid.NewGuid().ToString();
        await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { job_id = id, tool = "corel_execute_vba", args = new { }, run_directory = "C:\\Test" }));
        await worker.StandardInput.FlushAsync();
        using var result = await Read(worker);
        Assert.Equal("result", result.RootElement.GetProperty("type").GetString());
        Assert.Equal(id, result.RootElement.GetProperty("job_id").GetString());
        Assert.Equal("UNKNOWN_TOOL", result.RootElement.GetProperty("data").GetProperty("error").GetProperty("code").GetString());
        Assert.False(result.RootElement.GetProperty("data").GetProperty("error").GetProperty("applied").GetBoolean());
        worker.StandardInput.Close();
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, worker.ExitCode);
        Assert.Null(await worker.StandardOutput.ReadLineAsync());
    }

    [Fact]
    public async Task InvalidInputExitsBeforeDispatchWithoutPublishingAResult()
    {
        using var worker = Start();
        using var ready = await Read(worker);
        await worker.StandardInput.WriteLineAsync("{\"job_id\":\"ambiguous\",\"job_id\":\"other\"}");
        await worker.StandardInput.FlushAsync();
        worker.StandardInput.Close();
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, worker.ExitCode);
        Assert.Null(await worker.StandardOutput.ReadLineAsync());
        Assert.Contains("PROTOCOL_INVALID", await worker.StandardError.ReadToEndAsync());
    }

    private static Process Start()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "dotnet.exe"));
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CorelMcp.Worker.dll"));
        return Process.Start(start)!;
    }

    private static async Task<JsonDocument> Read(Process process)
    {
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(line);
        return JsonDocument.Parse(line);
    }
}
