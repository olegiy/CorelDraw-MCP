using System.Text;
using CorelMcp.Worker;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class ProtocolTests
{
    private const string Id = "00000000-0000-0000-0000-000000000001";
    private static string Request(string args = "{}") => "{\"job_id\":\"" + Id + "\",\"tool\":\"corel_status\",\"args\":" + args + ",\"run_directory\":\"C:\\\\Test\"}";

    [Fact]
    public void RequestReturnsOwnedArgsAndExactBinding()
    {
        var request = Protocol.ParseRequest(Encoding.UTF8.GetBytes(Request("{\"text\":\"Ёж 🦔\"}")));
        Assert.Equal(Id, request.JobId);
        Assert.Equal("corel_status", request.Tool);
        Assert.Equal("C:\\Test", request.RunDirectory);
        Assert.Equal("Ёж 🦔", request.Args.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"x\":1,\"\\u0078\":2}")]
    [InlineData("{\"nested\":[{\"x\":1,\"x\":2}]}")]
    [InlineData("{\"x\":1e400}")]
    [InlineData("{\"x\":NaN}")]
    public void RejectsAmbiguousOrNonfiniteArgs(string args)
        => Assert.Throws<WorkerProtocolException>(() => Protocol.ParseRequest(Encoding.UTF8.GetBytes(Request(args))));

    [Fact]
    public void RejectsUnknownFieldsInvalidIdsAndNonobjects()
    {
        foreach (var text in new[] { Request()[..^1] + ",\"extra\":1}", Request().Replace(Id, "wrong"), Request("[]"), "null", Request().Replace("C:\\\\Test", "relative") })
            Assert.Throws<WorkerProtocolException>(() => Protocol.ParseRequest(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public async Task ReaderHandlesFragmentedUtf8AndCrlf()
    {
        using var input = new OneByteStream(Encoding.UTF8.GetBytes(Request("{\"text\":\"Ёж 🦔\"}") + "\r\n" + Request() + "\n"));
        var requests = await Collect(input);
        Assert.Equal(2, requests.Count);
        Assert.Equal("Ёж 🦔", requests[0].Args.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReaderRejectsTruncatedEofAndInvalidUtf8()
    {
        await Assert.ThrowsAsync<WorkerProtocolException>(() => Collect(new MemoryStream(Encoding.UTF8.GetBytes(Request()))));
        var input = Encoding.UTF8.GetBytes(Request("{\"x\":\"a\"}") + "\n");
        input[Array.IndexOf(input, (byte)'a', Array.IndexOf(input, (byte)':') + 1)] = 0xff;
        await Assert.ThrowsAsync<WorkerProtocolException>(() => Collect(new MemoryStream(input)));
    }

    [Fact]
    public async Task ReaderEnforcesExactFourMebibyteBoundary()
    {
        var empty = Request("{\"x\":\"\"}");
        var exact = Request("{\"x\":\"" + new string('a', Protocol.MaxRecordBytes - Encoding.UTF8.GetByteCount(empty)) + "\"}");
        Assert.Equal(Protocol.MaxRecordBytes, Encoding.UTF8.GetByteCount(exact));
        Assert.Single(await Collect(new MemoryStream(Encoding.UTF8.GetBytes(exact + "\r\n"))));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => Collect(new MemoryStream(Encoding.UTF8.GetBytes(exact + " \n"))));
    }

    [Fact]
    public void WriterNeverPublishesAnOversizedPartialRecord()
    {
        using var output = new MemoryStream();
        Assert.Throws<WorkerProtocolException>(() => Protocol.WriteRecord(output, new { data = new string('a', Protocol.MaxRecordBytes) }));
        Assert.Equal(0, output.Length);
        Protocol.WriteRecord(output, new { type = "progress", job_id = Id, stage = "connecting" });
        Assert.EndsWith("\n", Encoding.UTF8.GetString(output.ToArray()));
        Assert.DoesNotContain("\uFEFF", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    private static async Task<List<WorkerRequest>> Collect(Stream input)
    {
        List<WorkerRequest> requests = [];
        await foreach (var request in Protocol.ReadRequestsAsync(input)) requests.Add(request);
        return requests;
    }

    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
