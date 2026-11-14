using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class TextTests
{
    [Theory]
    [InlineData("Ёж ёж ЁЖ", "ёж", false, new[] { 0, 3, 6 })]
    [InlineData("Ёж ёж ЁЖ", "ёж", true, new[] { 3 })]
    [InlineData("aaaaa", "aa", true, new[] { 0, 2 })]
    [InlineData("a.*b aXXb", ".*", true, new[] { 1 })]
    [InlineData("Один\rДва", "\r", true, new[] { 4 })]
    public void LiteralSearchUsesOrdinalCaseRulesAndNonOverlappingUtf16Offsets(string text, string query, bool sensitive, int[] positions)
        => Assert.Equal(positions, Text.Occurrences(text, query, sensitive));
}
