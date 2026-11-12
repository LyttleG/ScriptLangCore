namespace ScriptLangCore;

internal class ScriptException : Exception
{
    public ErrorCode ErrorCode { get; }
    public object? Value { get; }
    public int Line { get; }
    public int Column { get; }

    public ScriptException(ErrorCode code, object? value, int line = 0, int column = 0)
        : base(value?.ToString() ?? "null")
    {
        ErrorCode = code;
        Value = value;
        Line = line;
        Column = column;
    }

    public ScriptException(ErrorCode code, string message, int line = 0, int column = 0)
        : base(message)
    {
        ErrorCode = code;
        Value = message;
        Line = line;
        Column = column;
    }

    // Backward-compatible constructors
    public ScriptException(object? value)
        : this(ErrorCode.Unspecified, value) { }

    public ScriptException(string message)
        : this(ErrorCode.Unspecified, message) { }
}
