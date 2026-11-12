namespace ScriptLangCore;

public readonly record struct Token(
    TokenType Type,
    string Literal,
    int LineNumber,
    int Column,
    bool WasEscaped = false
);

public enum TokenType
{
    None, // Default token type, should not be used, but only serves as a placeholder for uninitialized tokens
    LeftParen, RightParen, LeftBracket, RightBracket, LeftBrace, RightBrace,
    Comma, Dot, Minus, Plus, Semicolon, Colon, Slash, Star, Percent,
    Ampersand, Pipe, Caret,
    Tilde,      // ~
    LeftShift,  // <<
    RightShift, // >>
    Bang, BangEqual,
    Equal, EqualEqual,
    Greater, GreaterEqual,
    Less, LessEqual,
    Identifier, String, Number, CharLiteral,
    And, Else, False, For, Fun, If, Nil, Not, Or,
    Return, True, While,
    Var, Const, Ref, Out, Params,
    Foreach, In,
    Break, Continue, Debugger,

    // Base types only
    Int, Long, Short, Byte,
    UInt, UShort, SByte, ULong,
    Float, Double, Decimal,
    Bool,
    Char, StringType, Object,
    DateTime, DateTimeOffset, TimeSpan,

    Assign, Void,

    // Related to classes and instances
    New, Class, This, Constructor, Extends, Super,

    Question, QuestionQuestion, QuestionDot, QuestionEqual,
    AndAnd, OrOr,
    PlusPlus, MinusMinus,
    PlusEqual, MinusEqual,
    StarEqual, SlashEqual, PercentEqual,
    AmpersandEqual, PipeEqual, CaretEqual, LeftShiftEqual, RightShiftEqual,
    DotDot,
    Operator,
    InterpolatedString,
    Try,
    Catch,
    Throw,
    Finally,

    // Lambda
    Arrow,

    // Pure function
    Pure,

    // Module system
    Import, Alias, Namespace,

    // Type testing operators
    Is, As,

    // Delegates and events
    Delegate, Event,

    // Structures
    Struct,
    Static,

    // Access
    Public, Private, Protected, Internal, Base,
    Get, Set, Init,

    Yield,              // yield keyword
    //Coroutine,
    Generator,          // Generator<T> delegate type reference

    // Switch pattern matching
    Switch, Case, Default, When,

    Using,
    Readonly,
    // With expression
    With,

    Eof
}
