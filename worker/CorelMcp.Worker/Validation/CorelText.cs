namespace CorelMcp.Worker.Validation;

internal sealed class TextValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public bool Applied => false;
}

internal sealed record CorelText
{
    public string Value { get; }
    private CorelText(string value) { Value = value; }
    public static CorelText Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 100000)
            throw new TextValidationException("TEXT_LIMIT_EXCEEDED", "Текст превышает 100000 кодовых единиц UTF-16.");
        for (int index = 0; index < value.Length; index++)
        {
            char item = value[index];
            if (char.IsSurrogate(item))
            {
                if (char.IsHighSurrogate(item) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    throw new TextValidationException("TEXT_UNSUPPORTED_CHARACTER", $"CorelDRAW 27 не поддерживает символ U+{char.ConvertToUtf32(item, value[index + 1]):X} в позиции UTF-16 {index}. Документ не изменён.");
                throw new TextValidationException("TEXT_INVALID_UTF16", $"Одиночный суррогат UTF-16 в позиции {index}. Документ не изменён.");
            }
            if (item == '\0')
                throw new TextValidationException("TEXT_UNSUPPORTED_CHARACTER", $"Нулевой символ в позиции UTF-16 {index} не поддерживается. Документ не изменён.");
        }
        return new CorelText(value);
    }
}
