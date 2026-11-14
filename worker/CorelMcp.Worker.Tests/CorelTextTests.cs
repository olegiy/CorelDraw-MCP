using CorelMcp.Worker.Validation;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class CorelTextTests
{
    [Theory]
    [InlineData("Проверка — Ёж\r\nВторая строка\t123")]
    [InlineData("")]
    [InlineData("中文 العربية Ελληνικά")]
    public void AcceptsSupportedBmpTextWithoutChangingIt(string value)
        => Assert.Equal(value, CorelText.Validate(value).Value);

    [Theory]
    [MemberData(nameof(UnsupportedTexts))]
    public void RefusesUnsupportedTextBeforeAnyComAdapterCanReceiveIt(string value, string code)
    {
        var error = Assert.Throws<TextValidationException>(() => CorelText.Validate(value));
        Assert.Equal(code, error.Code);
        Assert.False(error.Applied);
    }

    public static IEnumerable<object[]> UnsupportedTexts()
    {
        yield return ["🦔", "TEXT_UNSUPPORTED_CHARACTER"];
        yield return ["a\0b", "TEXT_UNSUPPORTED_CHARACTER"];
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void RefusesAnIsolatedSurrogate(int codeUnit)
    {
        var value = "a" + (char)codeUnit + "b";
        Assert.Equal("TEXT_INVALID_UTF16", Assert.Throws<TextValidationException>(() => CorelText.Validate(value)).Code);
    }

    [Fact]
    public void EnforcesTheUtf16LimitAtItsExactBoundary()
    {
        Assert.Equal(100000, CorelText.Validate(new string('Я', 100000)).Value.Length);
        Assert.Equal("TEXT_LIMIT_EXCEEDED", Assert.Throws<TextValidationException>(() => CorelText.Validate(new string('Я', 100001))).Code);
    }
}
