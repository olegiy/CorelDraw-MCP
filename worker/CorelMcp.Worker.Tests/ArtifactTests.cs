using System.Text.Json;
using System.Diagnostics;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class ArtifactTests
{
    [Fact]
    public void SmallResultsStayInlineAndLargeResultsBecomePrivateJsonArtifacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CorelMcp_ArtifactTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        List<string> artifacts = [];
        var small = new { count = 2 };
        Assert.Same(small, WorkerArtifacts.PrepareResult(directory, small, artifacts));
        Assert.Empty(artifacts);
        var large = new { text = new string('Я', 700000) };
        var result = WorkerArtifacts.PrepareResult(directory, large, artifacts);
        var summary = JsonSerializer.SerializeToElement(result, Protocol.JsonOptions);
        Assert.True(summary.GetProperty("result_in_artifact").GetBoolean());
        Assert.Single(artifacts);
        Assert.StartsWith(directory + Path.DirectorySeparatorChar, artifacts[0]);
        using var json = JsonDocument.Parse(File.ReadAllText(artifacts[0]));
        Assert.Equal(large.text, json.RootElement.GetProperty("text").GetString());
        Assert.Equal(new FileInfo(artifacts[0]).Length, summary.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public void BoundedWriterStopsBeforeWritingBeyondTheLimit()
    {
        using var output = new MemoryStream();
        using var limited = new LimitedWriteStream(output, 16, true);
        limited.Write(new byte[16]);
        Assert.Equal("ARTIFACT_LIMIT_EXCEEDED", Assert.Throws<OperationFailure>(() => limited.WriteByte(1)).Code);
        Assert.Equal(16, output.Length);
    }

    [Fact]
    public void IoFailureIsNeverConvertedIntoSuccessfulSerialization()
    {
        using var output = new BrokenStorage();
        using var limited = new LimitedWriteStream(output, 1000, true);
        Assert.Throws<IOException>(() => JsonSerializer.Serialize(limited, new { text = "Данные" }));
    }

    [Fact]
    public void InvalidPrivateDirectoriesAreRejectedBeforeCreatingFiles()
    {
        Assert.Throws<OperationFailure>(() => WorkerArtifacts.EnsureDirectory("relative"));
        var path = Path.Combine(Path.GetTempPath(), "CorelMcp_ArtifactTest_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "file");
        Assert.Throws<OperationFailure>(() => WorkerArtifacts.EnsureDirectory(path));
    }

    [Fact]
    public void PrivateArtifactDirectoriesNeverFollowAJunction()
    {
        var root = Path.Combine(Path.GetTempPath(), "CorelMcp_JunctionTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "target"); Directory.CreateDirectory(target);
        var link = Path.Combine(root, "link");
        var script = Path.Combine(root, "create.ps1");
        File.WriteAllText(script, "param([string]$Link,[string]$Target)\n$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path $Link -Target $Target | Out-Null\n");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Link", link, "-Target", target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10000));
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        var error = Assert.Throws<OperationFailure>(() => WorkerArtifacts.EnsureDirectory(link));
        Assert.Equal("ARTIFACT_INVALID_PATH", error.Code);
        Assert.Empty(Directory.GetFiles(target));
    }

    private sealed class BrokenStorage : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Тест: диск заполнен.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Тест: диск заполнен.");
    }
}
