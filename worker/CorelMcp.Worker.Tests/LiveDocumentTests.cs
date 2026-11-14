using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace CorelMcp.Worker.Tests;

[CollectionDefinition("CorelLive", DisableParallelization = true)]
public sealed class CorelLiveCollection { }

[Collection("CorelLive")]
public sealed class LiveDocumentTests
{
    [LiveCorelFact]
    public async Task DocumentLifecycleUsesExactIdsRestoresActiveDocumentAndNeverNeedsVba()
    {
        using var worker = new WorkerClient();
        List<string> owned = [];
        try
        {
            var statusReport = await worker.Call("corel_status", new { });
            Assert.True(statusReport.GetProperty("ok").GetBoolean());
            Assert.True(statusReport.GetProperty("cleanup").GetProperty("completed").GetBoolean());
            Assert.StartsWith("27.", statusReport.GetProperty("versions").GetProperty("coreldraw").GetString());
            var status = statusReport.GetProperty("result");
            Assert.False(status.TryGetProperty("vba", out _));
            Assert.Equal("csharp", status.GetProperty("executor").GetProperty("backend").GetString());
            var first = await worker.Ok("corel_create_document", new { request_id = "create-one", width_mm = 100, height_mm = 80 });
            var firstId = first.GetProperty("document_id").GetString()!;
            owned.Add(firstId);
            var second = await worker.Ok("corel_create_document", new { request_id = "create-two", width_mm = 40, height_mm = 50 });
            var secondId = second.GetProperty("document_id").GetString()!;
            owned.Add(secondId);
            Assert.NotEqual(firstId, secondId);
            var inspection = await worker.Ok("corel_inspect_document", new { document_id = firstId, limit = 1 });
            Assert.Equal(firstId, inspection.GetProperty("document").GetProperty("document_id").GetString());
            Assert.Equal(100, inspection.GetProperty("pages")[0].GetProperty("width").GetDouble());
            Assert.Equal(80, inspection.GetProperty("pages")[0].GetProperty("height").GetDouble());
            Assert.False(string.IsNullOrEmpty(inspection.GetProperty("pages")[0].GetProperty("page_id").GetString()));
            Assert.False(string.IsNullOrEmpty(inspection.GetProperty("layers")[0].GetProperty("layer_id").GetString()));
            Assert.Equal(secondId, (await worker.Ok("corel_status", new { })).GetProperty("active_document_id").GetString());
            var metadata = await worker.Ok("corel_describe_api", new { kind = "members", name = "Document", filter = "Save", limit = 10 });
            Assert.NotEmpty(metadata.GetProperty("items").EnumerateArray());
            var path = Path.Combine(worker.Directory, "Макет с пробелами.cdr");
            var saved = await worker.Ok("corel_save_document", new { request_id = "save-one", document_id = firstId, path });
            Assert.True(saved.GetProperty("bytes").GetInt64() > 0);
            var opened = await worker.Ok("corel_open_document", new { request_id = "open-loaded", path });
            Assert.Equal(firstId, opened.GetProperty("document_id").GetString());
            await worker.Ok("corel_close_document", new { request_id = "close-one", document_id = firstId });
            owned.Remove(firstId);
            var stale = await worker.Call("corel_inspect_document", new { document_id = firstId });
            Assert.Equal("STALE_DOCUMENT", stale.GetProperty("error").GetProperty("code").GetString());
            Assert.False(stale.GetProperty("error").GetProperty("applied").GetBoolean());
        }
        finally
        {
            foreach (var id in owned)
                await worker.Ok("corel_close_document", new { request_id = "cleanup-" + Guid.NewGuid().ToString("N"), document_id = id, discard_changes = true });
        }
    }

    private sealed class WorkerClient : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> stderr;
        private bool ready;
        public string Directory { get; }
        public WorkerClient()
        {
            var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            Directory = Path.Combine(repository, "artifacts", "worker-documents", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "dotnet.exe"));
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CorelMcp.Worker.dll"));
            process = Process.Start(start)!;
            stderr = process.StandardError.ReadToEndAsync();
        }
        public async Task<JsonElement> Ok(string tool, object args)
        {
            var response = await Call(tool, args);
            Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText() + "\n" + Directory);
            Assert.True(response.GetProperty("cleanup").GetProperty("completed").GetBoolean(), response.GetRawText());
            return response.GetProperty("result").Clone();
        }
        public async Task<JsonElement> Call(string tool, object args)
        {
            if (!ready) { using var handshake = await Read(); Assert.Equal("ready", handshake.RootElement.GetProperty("type").GetString()); ready = true; }
            var id = Guid.NewGuid().ToString();
            var request = JsonSerializer.Serialize(new { job_id = id, tool, args, run_directory = Directory });
            File.AppendAllText(Path.Combine(Directory, "requests.jsonl"), request + "\n");
            await process.StandardInput.WriteLineAsync(request);
            await process.StandardInput.FlushAsync();
            while (true)
            {
                using var record = await Read();
                Assert.Equal(id, record.RootElement.GetProperty("job_id").GetString());
                if (record.RootElement.GetProperty("type").GetString() == "result") return record.RootElement.GetProperty("data").Clone();
            }
        }
        private async Task<JsonDocument> Read()
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.NotNull(line);
            File.AppendAllText(Path.Combine(Directory, "responses.jsonl"), line + "\n");
            return JsonDocument.Parse(line);
        }
        public void Dispose()
        {
            process.StandardInput.Close();
            if (process.WaitForExit(10000)) File.WriteAllText(Path.Combine(Directory, "stderr.txt"), stderr.GetAwaiter().GetResult());
            else File.WriteAllText(Path.Combine(Directory, "unknown.txt"), "Процесс " + process.Id + " ещё работает. Не повторять операции автоматически.");
            process.Dispose();
        }
    }
}
