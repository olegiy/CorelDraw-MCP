using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.RegularExpressions;

namespace CorelMcp.Worker;

internal sealed record WorkerRequest(string JobId, string Tool, JsonElement Args, string RunDirectory);
internal sealed record WorkerError(string Code, string Message, string Stage, bool? Applied = null, int? Number = null, string? Source = null);
internal sealed record WorkerResult(bool Ok, object? Result = null, WorkerError? Error = null,
    IReadOnlyDictionary<string, object?>? Cleanup = null, IReadOnlyList<string>? Artifacts = null,
    IReadOnlyList<string>? Warnings = null, IReadOnlyDictionary<string, string>? Versions = null);
internal sealed class WorkerProtocolException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

internal static class Protocol
{
    public const int MaxRecordBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex JobId = new("^(?:[a-f0-9-]{36}|req-[a-f0-9]{64})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Tool = new("^corel_[a-z_]{1,100}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static WorkerRequest ParseRequest(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxRecordBytes) throw new WorkerProtocolException("PROTOCOL_LIMIT", "Запись JSONL превышает 4 МиБ.");
        try
        {
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            ValidateJson(root);
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = root.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            if (!names.SetEquals(["job_id", "tool", "args", "run_directory"])) throw new JsonException();
            var id = root.GetProperty("job_id").GetString();
            var tool = root.GetProperty("tool").GetString();
            var args = root.GetProperty("args");
            var directory = root.GetProperty("run_directory").GetString();
            if (id is null || !JobId.IsMatch(id) || tool is null || !Tool.IsMatch(tool)
                || args.ValueKind != JsonValueKind.Object || directory is null || directory.Length > 32767
                || directory.Contains('\0') || !Path.IsPathFullyQualified(directory)) throw new JsonException();
            return new WorkerRequest(id, tool, args.Clone(), directory);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException)
        {
            throw new WorkerProtocolException("PROTOCOL_INVALID", "Некорректный JSONL-запрос: требуется строгий JSON, точный job_id и абсолютный каталог задания.", error);
        }
    }

    private static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new JsonException("Повтор имени свойства.");
                ValidateString(item.Name);
                ValidateJson(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateJson(item);
        else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new JsonException("Число должно быть конечным.");
        else if (value.ValueKind == JsonValueKind.String) ValidateString(value.GetString()!);
    }

    private static void ValidateString(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
            else throw new JsonException("Некорректный UTF-16.");
        }
    }

    public static async IAsyncEnumerable<WorkerRequest> ReadRequestsAsync(Stream input)
    {
        var chunk = new byte[8192];
        var line = new byte[MaxRecordBytes + 1];
        var size = 0;
        int count;
        while ((count = await input.ReadAsync(chunk.AsMemory()).ConfigureAwait(false)) > 0)
        {
            for (int index = 0; index < count; index++)
            {
                var next = chunk[index];
                if (next == 10)
                {
                    var length = size > 0 && line[size - 1] == 13 ? size - 1 : size;
                    yield return ParseRequest(line.AsMemory(0, length));
                    size = 0;
                }
                else
                {
                    if (size == line.Length) throw new WorkerProtocolException("PROTOCOL_LIMIT", "Запись JSONL превышает 4 МиБ.");
                    line[size++] = next;
                }
            }
        }
        if (size > 0) throw new WorkerProtocolException("PROTOCOL_TRUNCATED", "EOF внутри записи JSONL; запрос не принят.");
    }
    public static void WriteRecord(Stream output, object value)
    {
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        {
            throw new WorkerProtocolException("PROTOCOL_INVALID_OUTPUT", "Результат не представим строгим JSON.", error);
        }
        if (bytes.Length > MaxRecordBytes) throw new WorkerProtocolException("PROTOCOL_LIMIT", "Результат превышает 4 МиБ; требуется артефакт.");
        lock (output)
        {
            output.Write(bytes);
            output.WriteByte(10);
            output.Flush();
        }
    }
}
