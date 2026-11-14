using System.Text.Json;
using CorelMcp.Worker.Operations;

namespace CorelMcp.Worker;

internal static class WorkerArtifacts
{
    public static void EnsureDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new OperationFailure("ARTIFACT_INVALID_PATH", "Каталог задания должен существовать и иметь абсолютный путь.");
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new OperationFailure("ARTIFACT_INVALID_PATH", "Ссылки и junction в пути артефакта запрещены.");
    }
    public static object? PrepareResult(string directory, object? result, List<string> artifacts)
    {
        if (result is null) return null;
        using var bytes = new MemoryStream();
        using (var bounded = new LimitedWriteStream(bytes, 64 * 1024 * 1024, true)) JsonSerializer.Serialize(bounded, result, Protocol.JsonOptions);
        if (bytes.Length <= 3 * 1024 * 1024) return result;
        EnsureDirectory(directory);
        var part = Path.Combine(Path.GetFullPath(directory), "result-" + Guid.NewGuid().ToString("N") + ".part");
        var destination = Path.ChangeExtension(part, ".json");
        bool owned = false;
        try
        {
            using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                bytes.Position = 0; bytes.CopyTo(file); file.Flush(true);
            }
            File.Move(part, destination);
            artifacts.Add(destination);
            return Dto.Of(("result_in_artifact", true), ("path", destination), ("bytes", bytes.Length), ("media_type", "application/json"));
        }
        finally { if (owned && File.Exists(part)) File.Delete(part); }
    }
}

internal sealed class LimitedWriteStream(Stream inner, long limit, bool leaveOpen = false, string limitCode = "ARTIFACT_LIMIT_EXCEEDED") : Stream
{
    private long written;
    private bool disposed;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.Length > limit - written) throw new OperationFailure(limitCode, "Превышен предельный размер артефакта.");
        inner.Write(buffer); written += buffer.Length;
    }
    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing && !leaveOpen) inner.Dispose();
        disposed = true; base.Dispose(disposing);
    }
}
