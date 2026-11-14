using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class LiveCorelFactAttribute : FactAttribute
{
    public LiveCorelFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CORELDRAW_WORKER_LIVE") != "1")
            Skip = "Нужен явный CORELDRAW_WORKER_LIVE=1; тест создаёт только два временных документа.";
    }
}

public sealed class LiveCorelTheoryAttribute : TheoryAttribute
{
    public LiveCorelTheoryAttribute()
    {
        if(Environment.GetEnvironmentVariable("CORELDRAW_WORKER_LIVE")!="1")
            Skip="Нужен явный CORELDRAW_WORKER_LIVE=1; используются только собственные временные документы.";
    }
}

[Collection("CorelLive")]
public sealed class LiveSmokeTests
{
    [LiveCorelFact]
    public void TypedNet10WorkerCreatesTwoTemporaryDrawingsAndRestoresUserState()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var directory = Path.Combine(repository, "artifacts", "worker-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var worker = Path.Combine(AppContext.BaseDirectory, "CorelMcp.Worker.dll");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(worker);
        start.ArgumentList.Add("--live-smoke");
        start.ArgumentList.Add("--run-directory");
        start.ArgumentList.Add(directory);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // A timeout never kills CorelDRAW or retries the operation. Keep diagnostics for manual inspection.
        if (!process.WaitForExit(60000))
            throw new Xunit.Sdk.XunitException($"Исход live smoke неизвестен; PID {process.Id}, диагностика: {directory}. Не повторять автоматически.");
        File.WriteAllText(Path.Combine(directory, "stdout.txt"), stdout.GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(directory, "stderr.txt"), stderr.GetAwaiter().GetResult());
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "smoke.json")));
        var root = report.RootElement;
        Assert.True(process.ExitCode == 0, $"Live gate не пройден; отчёт: {directory}\n{root.GetRawText()}");
        Assert.True(root.GetProperty("success").GetBoolean(), directory);
        Assert.True(root.GetProperty("cleanup_completed").GetBoolean(), directory);
        Assert.True(root.GetProperty("user_documents_unchanged").GetBoolean(), directory);
        Assert.True(root.GetProperty("active_document_restored").GetBoolean(), directory);
        Assert.True(root.GetProperty("control_document_unchanged").GetBoolean(), directory);
        Assert.True(root.GetProperty("unsupported_text_rejected_before_mutation").GetBoolean(), directory);
        Assert.Equal(2, root.GetProperty("created_documents").GetInt32());
        foreach (var item in root.GetProperty("documents").EnumerateArray())
        {
            Assert.True(item.GetProperty("rectangle_static_id").GetInt32() > 0);
            Assert.True(item.GetProperty("text_static_id").GetInt32() > 0);
            Assert.Equal("Проверка CorelDRAW 27 — Ёж", item.GetProperty("text").GetString());
            var png = File.ReadAllBytes(item.GetProperty("preview_path").GetString()!);
            Assert.True(png.Length > 8);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        }
    }
}
