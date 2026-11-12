namespace ScriptLangCore;

using System.Buffers;

internal sealed record Lexer(ReadOnlyMemory<char> Source)
{
    [ThreadStatic]
    private static StringBuilder? _sharedBuilder;

    private static StringBuilder GetBuilder()
    {
        var sb = _sharedBuilder ??= new StringBuilder(4096);
        sb.Clear();
        return sb;
    }

    private ReadOnlySpan<char> Src => Source.Span;
    private int Length => Src.Length;

    private int _start;
    private int _current;
    private int _lineNumber = 1;
    private int _lineStart;

    public IEnumerable<Token> GetTokens()
    {
        // Skip UTF-8 BOM (U+FEFF) if present at start
        if (_current == 0 && Length > 0 && Src[0] == '\uFEFF')
            _current = 1;

        while (!IsAtEnd())
        {
            _start = _current;
            var car = Advance();

            switch (car)
            {
                case ' ': case '\r': case '\t': break;
                case '\n':
                    _lineNumber++;
                    _lineStart = _current;
                    break;

                case '(': yield return AddToken(TokenType.LeftParen); break;
                case ')': yield return AddToken(TokenType.RightParen); break;
                case '[': yield return AddToken(TokenType.LeftBracket); break;
                case ']': yield return AddToken(TokenType.RightBracket); break;
                case '{': yield return AddToken(TokenType.LeftBrace); break;
                case '}': yield return AddToken(TokenType.RightBrace); break;
                case ',': yield return AddToken(TokenType.Comma); break;
                case '.': yield return AddToken(Match('.') ? TokenType.DotDot : TokenType.Dot); break;
                case ';': yield return AddToken(TokenType.Semicolon); break;
                case '*':
                    yield return AddToken(Match('=') ? TokenType.StarEqual : TokenType.Star);
                    break;
                case '?':
                    if (Peek() == '?' && _current + 1 < Length && Src[_current + 1] == '=')
                    {
                        Advance(); Advance();
                        yield return AddToken(TokenType.QuestionEqual);
                    }
                    else if (Match('?')) yield return AddToken(TokenType.QuestionQuestion);
                    else if (Match('.')) yield return AddToken(TokenType.QuestionDot);
                    else if (Match('=')) yield return AddToken(TokenType.QuestionEqual);
                    else yield return AddToken(TokenType.Question);
                    break;
                case ':': yield return AddToken(TokenType.Colon); break;

                case '!': yield return AddToken(Match('=') ? TokenType.BangEqual : TokenType.Bang); break;
                case '=':
                    if (Match('='))
                        yield return AddToken(TokenType.EqualEqual);
                    else if (Match('>'))
                        yield return AddToken(TokenType.Arrow);
                    else
                        yield return AddToken(TokenType.Equal);
                    break;

                case '>':
                    if (Match('='))
                        yield return AddToken(TokenType.GreaterEqual);
                    else if (Match('>'))
                        yield return AddToken(Match('=') ? TokenType.RightShiftEqual : TokenType.RightShift);
                    else
                        yield return AddToken(TokenType.Greater);
                    break;
                case '<':
                    if (Match('='))
                        yield return AddToken(TokenType.LessEqual);
                    else if (Match('<'))
                        yield return AddToken(Match('=') ? TokenType.LeftShiftEqual : TokenType.LeftShift);
                    else
                        yield return AddToken(TokenType.Less);
                    break;
                case '%':
                    yield return AddToken(Match('=') ? TokenType.PercentEqual : TokenType.Percent);
                    break;

                case '/':
                    if (Match('/'))
                    {
                        // ONE scan to the line break instead of a Peek/Advance per character: a comment only advances
                        // the line when the `\n` itself is consumed below, so stopping ON it is what the old loop did.
                        var toBreak = Src[_current..].IndexOf('\n');
                        _current = toBreak < 0 ? Length : _current + toBreak;
                        _start = _current;
                    }
                    else if (Match('*'))
                    {
                        ScanBlockComment();
                    }
                    else
                    {
                        yield return AddToken(Match('=') ? TokenType.SlashEqual : TokenType.Slash);
                    }
                    break;

                case '"':
                    if (Peek() == '"' && _current + 1 < Length && Src[_current + 1] == '"')
                    {
                        Advance(); Advance();
                        yield return ScanRawString(ConsumeQuoteRun(3));
                    }
                    else
                    {
                        yield return ScanStringInternal(false);
                    }
                    break;
                case '\'': yield return ScanStringOrChar(); break;

                case '+':
                    if (Match('=')) yield return AddToken(TokenType.PlusEqual);
                    else if (Match('+')) yield return AddToken(TokenType.PlusPlus);
                    else yield return AddToken(TokenType.Plus);
                    break;
                case '-':
                    if (Match('=')) yield return AddToken(TokenType.MinusEqual);
                    else if (Match('-')) yield return AddToken(TokenType.MinusMinus);
                    else yield return AddToken(TokenType.Minus);
                    break;

                case '&':
                    if (Match('='))
                        yield return AddToken(TokenType.AmpersandEqual);
                    else if (Match('&'))
                        yield return AddToken(TokenType.AndAnd);
                    else
                        yield return AddToken(TokenType.Ampersand);
                    break;
                case '|':
                    if (Match('='))
                        yield return AddToken(TokenType.PipeEqual);
                    else if (Match('|'))
                        yield return AddToken(TokenType.OrOr);
                    else
                        yield return AddToken(TokenType.Pipe);
                    break;
                case '^':
                    yield return AddToken(Match('=') ? TokenType.CaretEqual : TokenType.Caret);
                    break;
                case '~':
                    yield return AddToken(TokenType.Tilde);
                    break;

                case '0':
                    if (_current < Length && (Src[_current] == 'b' || Src[_current] == 'B'))
                    {
                        Advance();
                        yield return ScanBinaryLiteral();
                        continue;
                    }
                    if (_current < Length && (Src[_current] == 'x' || Src[_current] == 'X'))
                    {
                        Advance();
                        yield return ScanHexLiteral();
                        continue;
                    }
                    yield return ScanDecimalNumber();
                    continue;

                case '$':
                    {
                        // Count the dollars FIRST: `$"..."`, `$@"..."` (or `@$"..."`), and `$"""..."""` where the count of
                        // `$` is also the number of braces that opens a hole, which is C#'s rule for a raw string that
                        // holds braces (measured on C# itself: a run of 2N braces is refused there).
                        var dollars = 1;
                        while (Peek() == '$') { Advance(); dollars++; }

                        if (dollars == 1 && Peek() == '@' && _current + 1 < Length && Src[_current + 1] == '"')
                        {
                            Advance(); // skip '@'
                            Advance(); // skip '"'
                            yield return ScanInterpolatedString(verbatim: true);
                            break;
                        }
                        if (Peek() == '"')
                        {
                            Advance();
                            if (Peek() == '"' && _current + 1 < Length && Src[_current + 1] == '"')
                            {
                                Advance(); Advance();
                                yield return ScanInterpolatedRawString(dollars, ConsumeQuoteRun(3));
                                break;
                            }
                            if (dollars == 1)
                            {
                                yield return ScanInterpolatedString(verbatim: false);
                                break;
                            }
                            throw Error("Unexpected '$$'. More than one '$' needs a RAW string: write $$\"\"\"...\"\"\".",
                                        _lineNumber, _start - _lineStart + 1);
                        }
                        throw Error("Unexpected '$'. Write $\"...\", $@\"...\" or $\"\"\"...\"\"\" with one '$' per brace a hole needs.",
                                    _lineNumber, _start - _lineStart + 1);
                    }

                case '@':
                    if (Peek() == '"')
                    {
                        Advance();
                        yield return ScanStringInternal(true);
                        break;
                    }
                    if (Peek() == '$')
                    {
                        Advance(); // skip '$'
                        if (Peek() == '"')
                        {
                            Advance(); // skip '"'
                            yield return ScanInterpolatedString(verbatim: true);
                            break;
                        }
                    }
                    if (IsAtEnd())
                        throw Error("Unexpected end of file after '@'.", _lineNumber, _start - _lineStart + 1);
                    if (!IsIdentifierStart(Peek()))
                        throw Error($"Invalid character after '@': '{Peek()}'. Expected an identifier start.", _lineNumber, _start - _lineStart + 1);

                    var idStart = _current;
                    var escapedRest = Src[_current..];
                    var escapedStop = escapedRest.IndexOfAnyExcept(IdentifierPartChars);
                    _current = escapedStop < 0 ? Length : escapedStop + idStart;

                    yield return new Token(TokenType.Identifier, Src[idStart.._current].ToString(), _lineNumber, idStart - _lineStart + 1, WasEscaped: true);
                    break;

                default:
                    if (IsDigit(car))
                    {
                        yield return ScanDecimalNumber();
                        continue;
                    }
                    if (IsIdentifierStart(car))
                    {
                        yield return ScanIdentifier();
                        continue;
                    }
                    // A NON-ASCII CHARACTER SAYS WHY IT IS REFUSED, because the bare sentence reads as an encoding
                    // fault: reported from a live session, a one-line French capture answered "Unexpected character
                    // 'é'" and the reader went looking for a decoding problem. The rule is the one stated here:
                    // `IsIdentifierStart`/`IsIdentifierPart` are ASCII letters, digits and underscore, and no operator
                    // or delimiter is non-ASCII, so such a character is legal only inside a string or a comment.
                    throw Error(car > 0x7F
                        ? $"Unexpected character '{car}': a name holds ASCII letters, digits, underscore or an accented Latin-1 letter, so this character is only valid inside a string or a comment"
                        : $"Unexpected character '{car}'", _lineNumber, _start - _lineStart + 1);
            }
        }

        yield return new Token(TokenType.Eof, string.Empty, _lineNumber, _current - _lineStart + 1);
    }

    private Token ScanBinaryLiteral()
    {
        var builder = GetBuilder();
        while (_current < Length && (IsBinaryDigit(Src[_current]) || Src[_current] == '_'))
        {
            if (Src[_current] != '_')
                builder.Append(Src[_current]);
            _current++;
        }
        if (builder.Length == 0)
            throw Error("Binary literal must contain at least one digit.", _lineNumber, _start - _lineStart + 1);
        try
        {
            var value = Convert.ToInt64(builder.ToString(), 2);
            return new Token(TokenType.Number, value.ToString(), _lineNumber, _start - _lineStart + 1);
        }
        catch (OverflowException)
        {
            var ulongValue = Convert.ToUInt64(builder.ToString(), 2);
            return new Token(TokenType.Number, ulongValue.ToString(), _lineNumber, _start - _lineStart + 1);
        }
    }

    private Token ScanHexLiteral()
    {
        var builder = GetBuilder();
        while (_current < Length && (IsHexDigit(Src[_current]) || Src[_current] == '_'))
        {
            if (Src[_current] != '_')
                builder.Append(Src[_current]);
            _current++;
        }
        if (builder.Length == 0)
            throw Error("Hexadecimal literal must contain at least one digit.", _lineNumber, _start - _lineStart + 1);
        var hex = builder.ToString();
        var value = Convert.ToInt64(hex, 16);
        return new Token(TokenType.Number, value.ToString(), _lineNumber, _start - _lineStart + 1);
    }

    private Token ScanDecimalNumber()
    {
        var start = _start;
        _current = start;

        // Skip underscores to get the raw digit span
        while (_current < Length && (IsDigit(Src[_current]) || Src[_current] == '_'))
            _current++;

        var hasDot = _current < Length && Src[_current] == '.'
                                       && _current + 1 < Length && IsDigit(Src[_current + 1]);

        // Check for scientific notation (e/E)
        var hasExponent = !hasDot && _current < Length && (Src[_current] == 'e' || Src[_current] == 'E');

        if (!hasDot && !hasExponent)
        {
            // Integer fast path: check for suffix first
            var suffixCh = '\0';
            var suffixEnd = _current;
            if (suffixEnd < Length && "fFdDmMlL".Contains(Src[suffixEnd]))
            {
                suffixCh = Src[suffixEnd];
                suffixEnd++;
            }

            // Strip underscores from the integer portion
            var intSpan = StripUnderscores(Src[start.._current]);

            if (suffixCh != '\0')
            {
                _current = suffixEnd;
                var rawSrc = Src[start.._current];
                return suffixCh switch
                {
                    _ => new Token(TokenType.Number, rawSrc.ToString(), _lineNumber, start - _lineStart + 1)
                };
            }

            if (intSpan.Length == 0)
                throw Error("Invalid number format.", _lineNumber, start - _lineStart + 1);

            if (long.TryParse(intSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var longVal))
                return new Token(TokenType.Number, longVal.ToString(), _lineNumber, start - _lineStart + 1);
            // Fallback for overflow
            var fallback = double.Parse(intSpan, CultureInfo.InvariantCulture);
            return new Token(TokenType.Number, fallback.ToString("G", CultureInfo.InvariantCulture), _lineNumber, start - _lineStart + 1);
        }

        // Float path: use builder for the decimal part
        _current = start;
        var builder = GetBuilder();
        while (_current < Length && (IsDigit(Src[_current]) || Src[_current] == '_'))
        {
            if (Src[_current] != '_')
                builder.Append(Src[_current]);
            _current++;
        }
        if (hasDot)
        {
            builder.Append('.');
            _current++;
            while (_current < Length && (IsDigit(Src[_current]) || Src[_current] == '_'))
            {
                if (Src[_current] != '_')
                    builder.Append(Src[_current]);
                _current++;
            }
        }

        // Scientific notation: e/E [+/-] digits
        if (_current < Length && (Src[_current] == 'e' || Src[_current] == 'E'))
        {
            builder.Append(Src[_current]);
            _current++;
            if (_current < Length && (Src[_current] == '+' || Src[_current] == '-'))
            {
                builder.Append(Src[_current]);
                _current++;
            }
            while (_current < Length && (IsDigit(Src[_current]) || Src[_current] == '_'))
            {
                if (Src[_current] != '_')
                    builder.Append(Src[_current]);
                _current++;
            }
        }

        var suffix = '\0';
        if (_current < Length && "fFdDmMlL".Contains(Src[_current]))
        {
            suffix = Src[_current];
            _current++;
        }

        if (suffix != '\0' || hasExponent)
        {
            // Keep raw source text including exponent/suffix so the parser can detect the type
            var rawSrc = Src[start.._current];
            // For numbers with exponent, ensure they parse as double even if the value is round
            if (hasExponent)
            {
                //double d = double.Parse(builder.ToString(), CultureInfo.InvariantCulture);
                return new Token(TokenType.Number, rawSrc.ToString(), _lineNumber, start - _lineStart + 1);
            }

            // suffix switch, regular suffixed numbers
            return suffix switch
            {
                _ => new Token(TokenType.Number, rawSrc.ToString(), _lineNumber, start - _lineStart + 1)
            };
        }

        var raw = builder.ToString();
        var numberVal = double.Parse(raw, CultureInfo.InvariantCulture);
        return new Token(TokenType.Number, numberVal.ToString(CultureInfo.InvariantCulture), _lineNumber, start - _lineStart + 1);
    }

    private void ScanBlockComment()
    {
        while (!IsAtEnd())
        {
            var c = Advance();
            switch (c)
            {
                case '\n':
                    _lineNumber++;
                    _lineStart = _current;
                    break;
                case '*' when Peek() == '/':
                    Advance();
                    _start = _current;
                    return;
            }
        }
        throw Error("Unterminated block comment", _lineNumber, _start - _lineStart + 1);
    }

    private Token AddToken(TokenType type) =>
        new(type, Src[_start.._current].ToString(), _lineNumber, _start - _lineStart + 1);

    private bool IsAtEnd() => _current >= Length;
    private char Advance() => Src[_current++];
    private char Peek() => IsAtEnd() ? '\0' : Src[_current];

    private bool Match(char expected)
    {
        if (IsAtEnd() || Src[_current] != expected) return false;
        _current++;
        return true;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    /// <summary>A character a NAME may START with: ASCII `a-z A-Z`, `_`, or an ACCENTED LATIN-1 letter. The accented range
    /// is the maintainer's call (decision dec-6dc5a61c5750ec4d: "Latin-1 accents only"), so French, German, Spanish,
    /// Portuguese and Nordic names work (`hé`, `café`, `Straße`, `niño`, `ação`) while CJK, Cyrillic and Greek stay out of
    /// the language's names and belong in a string or a comment. The three ranges are the LATIN-1 SUPPLEMENT's letters:
    /// U+00C0..U+00D6, U+00D8..U+00F6 and U+00F8..U+00FF, which skips the two symbols inside them (`×` U+00D7 and
    /// `÷` U+00F7). Names compare with the interpreter's own case rule (Ordinal or OrdinalIgnoreCase), both of which handle
    /// these characters, so no second name set is needed anywhere.</summary>
    private static bool IsIdentifierStart(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_'
        or >= '\u00C0' and <= '\u00D6' or >= '\u00D8' and <= '\u00F6' or >= '\u00F8' and <= '\u00FF';
    private static bool IsHexDigit(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static bool IsBinaryDigit(char c) => c == '0' || c == '1';

    /// <summary>Every character a name may CONTINUE with: the start set above, the ASCII digits, and the `` ` `` this
    /// lexer also allows inside a name (the `@`-escaped and the plain identifier loops both did that by hand). It is one
    /// <see cref="SearchValues{T}"/> so a name is consumed by ONE vectorised scan of the remaining source instead of a
    /// per-character predicate. MEASURED against the build before it, both NativeAOT, interleaved, best of four: a
    /// 22.8 MB file of comments (nearly pure scanning) went 367 ms to 145 ms, a 1.0 MB file of ordinary code went
    /// 232 ms to 225 ms (compilation dominates there), and both wrote a BYTE-IDENTICAL bundle, which is the check that
    /// the token stream did not move.</summary>
    private static readonly SearchValues<char> IdentifierPartChars = CreateIdentifierPartChars();

    private static SearchValues<char> CreateIdentifierPartChars()
    {
        var chars = new char[160];
        var n = 0;
        void Add(char c) => chars[n++] = c;
        for (var c = 'a'; c <= 'z'; c++) Add(c);
        for (var c = 'A'; c <= 'Z'; c++) Add(c);
        for (var c = '0'; c <= '9'; c++) Add(c);
        Add('_');
        Add('`');
        // the LATIN-1 SUPPLEMENT letters, the two symbols inside the ranges excluded (see IsIdentifierStart)
        for (var c = '\u00C0'; c <= '\u00D6'; c++) Add(c);
        for (var c = '\u00D8'; c <= '\u00F6'; c++) Add(c);
        for (var c = '\u00F8'; c <= '\u00FF'; c++) Add(c);
        return SearchValues.Create(chars.AsSpan(0, n));
    }

    /// <summary>
    /// Returns a span with '_' characters removed from the input.
    /// Uses stackalloc for small numbers (≤256 chars), heap otherwise.
    /// </summary>
    private static ReadOnlySpan<char> StripUnderscores(ReadOnlySpan<char> input)
    {
        var firstUnderscore = input.IndexOf('_');
        if (firstUnderscore < 0)
            return input;

        var len = input.Length;
        char[]? rented = null;
        var buf = len <= 256
            ? stackalloc char[len]
            : (rented = ArrayPool<char>.Shared.Rent(len));

        var write = 0;
        for (var i = 0; i < len; i++)
        {
            if (input[i] != '_')
                buf[write++] = input[i];
        }

        var result = buf[..write].ToString();
        if (rented != null)
            ArrayPool<char>.Shared.Return(rented);

        // Return as a string span; we can't return a reference to stackalloc
        return result.AsSpan();
    }

    /// <summary>Decodes the escape an author wrote after a backslash and appends it, for EVERY literal that processes
    /// escapes (a regular string, a single-quoted one, an interpolated one, verbatim or not). ONE implementation, so
    /// the set cannot drift between them, and it is C#'s set: <c>\' \" \\ \0 \a \b \f \n \r \t \v</c>, the fixed-width
    /// <c>\uXXXX</c> and <c>\UXXXXXXXX</c>, and <c>\xH..H</c> with one to four hex digits (greedy, as in C#).
    /// <c>\{</c> and <c>\}</c> are accepted too, because the interpolated scanner did before this helper existed.
    /// A code point above <c>0xFFFF</c> becomes a SURROGATE PAIR, which is what a UTF-16 string needs for
    /// <c>\U0001F600</c>; above <c>0x10FFFF</c> is refused, as it is in C#.</summary>
    private void AppendEscape(StringBuilder builder, char ec)
    {
        switch (ec)
        {
            case '\'': builder.Append('\''); return;
            case '"': builder.Append('"'); return;
            case '\\': builder.Append('\\'); return;
            case '0': builder.Append('\0'); return;
            case 'a': builder.Append('\a'); return;
            case 'b': builder.Append('\b'); return;
            case 'f': builder.Append('\f'); return;
            case 'n': builder.Append('\n'); return;
            case 'r': builder.Append('\r'); return;
            case 't': builder.Append('\t'); return;
            case 'v': builder.Append('\v'); return;
            case '{': builder.Append('{'); return;
            case '}': builder.Append('}'); return;
            case 'u': AppendCodePoint(builder, ReadHexEscape(4, 4, "\\u")); return;
            case 'U': AppendCodePoint(builder, ReadHexEscape(8, 8, "\\U")); return;
            case 'x': AppendCodePoint(builder, ReadHexEscape(1, 4, "\\x")); return;
            default:
                throw Error($"Unknown escape '\\{ec}'", _lineNumber, _start - _lineStart + 1);
        }
    }

    /// <summary>The hex digits of a <c>\u</c>, <c>\U</c> or <c>\x</c> escape. <paramref name="min"/> is what the escape
    /// requires and <paramref name="max"/> what it accepts, so `\u` and `\U` are fixed width and `\x` is greedy.</summary>
    private int ReadHexEscape(int min, int max, string what)
    {
        var value = 0;
        var count = 0;
        while (count < max && !IsAtEnd() && Uri.IsHexDigit(Peek()))
        {
            value = value * 16 + Convert.ToInt32(Peek().ToString(), 16);
            Advance();
            count++;
        }
        if (count < min)
            throw Error($"The escape '{what}' needs {min} hex digit(s), got {count}.", _lineNumber, _start - _lineStart + 1);
        return value;
    }

    private void AppendCodePoint(StringBuilder builder, int codePoint)
    {
        if (codePoint > 0x10FFFF)
            throw Error($"The escape names code point U+{codePoint:X}, past the last one (U+10FFFF).", _lineNumber, _start - _lineStart + 1);
        builder.Append(codePoint <= 0xFFFF ? (char)codePoint : char.ConvertFromUtf32(codePoint));
    }

    private Token ScanStringInternal(bool isVerbatim, char quote = '"')
    {
        var builder = GetBuilder();
        var startLine = _lineNumber;
        var startCol = _start - _lineStart + 1;

        while (true)
        {
            // ONE scan per run: everything up to the next character that MEANS something is appended in a single
            // span copy instead of an Advance/Append per character. A `\r` is content here (only `\n` moves the line),
            // so it stays inside the run. Measured with the identifier and comment scans, both NativeAOT, interleaved,
            // best of four: a 22.8 MB comment file 367 ms to 145 ms, a 1.0 MB code file 232 ms to 225 ms, identical bytes.
            var rest = Src[_current..];
            var stop = isVerbatim ? rest.IndexOfAny(quote, '\n') : rest.IndexOfAny(quote, '\\', '\n');
            if (stop < 0)
            {
                if (rest.Length > 0)
                {
                    builder.Append(rest);
                    _current = Length;
                }
                var unterminated = isVerbatim ? "Unterminated verbatim string" : "Unterminated string";
                throw Error(unterminated, startLine, startCol);
            }
            if (stop > 0)
            {
                builder.Append(rest[..stop]);
                _current += stop;
            }

            var c = Advance();

            if (isVerbatim)
            {
                if (c == quote)
                {
                    if (Peek() == quote)
                    {
                        Advance();
                        builder.Append(quote);
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    if (c == '\n') { _lineNumber++; _lineStart = _current; }
                    builder.Append(c);
                }
            }
            else
            {
                if (c == quote)
                    break;

                if (c == '\\')
                {
                    if (IsAtEnd())
                        throw Error("Unterminated escape sequence.", startLine, startCol);

                    AppendEscape(builder, Advance());
                }
                else
                {
                    if (c == '\n') { _lineNumber++; _lineStart = _current; }
                    builder.Append(c);
                }
            }
        }

        return new Token(TokenType.String, builder.ToString(), startLine, startCol);
    }

    /// <summary>How many quote characters the run starting at the current position holds, the caller naming how many it
    /// has ALREADY consumed (three at both openers here, where the dispatch consumed one and skipped two). It leaves
    /// <c>_current</c> just past the run, which is what both raw-string openers need: C# opens a raw literal with ANY run
    /// of three or more quotes (`""""abc""""` is legal and yields `abc`), and the literal then closes on a run of that
    /// SAME length.</summary>
    private int ConsumeQuoteRun(int alreadyConsumed)
    {
        var count = alreadyConsumed;
        while (!IsAtEnd() && Src[_current] == '"') { Advance(); count++; }
        return count;
    }

    /// <summary>Applies C#'s INDENTATION rule to a multi-line raw literal's content, which is what makes an indented
    /// template possible: when the closing delimiter sits on its own line, the whitespace before it is the literal's
    /// INDENTATION, and it is removed from the start of every content line (a whitespace-only line shorter than it
    /// loses all of its whitespace). The line break before that delimiter goes with it, so the value does not end in
    /// one. A literal whose delimiter does NOT sit on its own line (a one-line raw string, or an odd shape) is returned
    /// untouched, which keeps this engine's leniency for the shapes C# rejects outright.
    /// Measured against the C# compiler: `"""\nabc\n"""` is `abc` (3 characters) where this engine used to keep the
    /// break, and an indented closing delimiter is what lets a template be written at a method's indentation.</summary>
    private static string ApplyRawIndent(string raw)
    {
        var lastBreak = raw.LastIndexOf('\n');
        if (lastBreak < 0) return raw;                          // one line: no indentation concept

        var tail = raw[(lastBreak + 1)..];
        for (var i = 0; i < tail.Length; i++)
            if (tail[i] != ' ' && tail[i] != '\t') return raw;   // the delimiter is not on its own line

        var indent = tail.Length;
        // `body` drops the break AND the indentation, and a CRLF file's `\r` belongs to that break.
        var bodyEnd = lastBreak;
        if (bodyEnd > 0 && raw[bodyEnd - 1] == '\r') bodyEnd--;
        var body = raw[..bodyEnd];
        if (indent == 0) return body;

        var builder = GetBuilder();
        var lineStart = 0;
        while (lineStart <= body.Length)
        {
            var next = body.IndexOf('\n', lineStart);
            var end = next < 0 ? body.Length : next;
            var stop = lineStart;
            var removed = 0;
            while (stop < end && removed < indent && (body[stop] == ' ' || body[stop] == '\t')) { stop++; removed++; }
            var rest = body[stop..end];
            // A whitespace-only line shorter than the indentation loses what it has and nothing else: that is the
            // one case where a strict "remove exactly the indentation" would throw the line away.
            builder.Append(rest);
            if (next < 0) break;
            builder.Append('\n');
            lineStart = next + 1;
        }
        return builder.ToString();
    }

    /// <summary>Scans a raw string literal """...""", content is taken verbatim
    /// (no escape sequences, single/double quotes allowed) and may span multiple lines.
    /// The closing delimiter is the next run of `quotes` quote characters (the width of the OPENING run: three by
    /// default, more when the literal was opened wider so its content could hold quotes). Empty string if the
    /// delimiter is immediately closed.</summary>
    private Token ScanRawString(int quotes)
    {
        var builder = GetBuilder();
        var startLine = _lineNumber;
        var startCol = _start - _lineStart + 1;

        // C# semantics: content starts on the line after the opening delimiter.
        // Skip the first line break (LF or CRLF) after """.
        if (!IsAtEnd())
        {
            if (Peek() == '\r' && _current + 1 < Length && Src[_current + 1] == '\n')
            {
                Advance(); Advance();
                _lineNumber++; _lineStart = _current;
            }
            else if (Peek() == '\n')
            {
                Advance();
                _lineNumber++; _lineStart = _current;
            }
        }

        while (true)
        {
            if (IsAtEnd())
                throw Error("Unterminated raw string literal", startLine, startCol);

            var c = Advance();

            if (c == '"')
            {
                var runCol = _current - 1 - _lineStart + 1;
                var run = ConsumeQuoteRun(1);
                if (run == quotes) break;                     // the closing delimiter
                if (run > quotes)
                    // C# REFUSES THIS (CS8998) and so does the engine, but with the reason in it: a longer run is
                    // ambiguous, and the fix is a wider opening delimiter (for a run inside the content) or a space
                    // or a line break before the closing quotes (for one at the end, which is the shape a reader
                    // usually has: `--tools-dir "%TEMP%/empty-tools"` typed straight into `"""..."""`).
                    throw Error($"a raw string's content cannot hold a run of {run} quote(s): a run of {quotes} closes the literal. "
                              + $"Open it with {run} quote(s) when the run belongs to the content, or put a space (or a line break) "
                              + $"before the closing quotes when the content ends with one",
                                _lineNumber, runCol);
                for (var k = 0; k < run; k++) builder.Append('"');   // shorter than the delimiter: literal content
                continue;
            }

            if (c == '\n') { _lineNumber++; _lineStart = _current; }
            builder.Append(c);
        }

        return new Token(TokenType.String, ApplyRawIndent(builder.ToString()), startLine, startCol);
    }

    /// <summary>Scans an interpolated RAW string (`$"""..."""`, and more dollars when the text holds braces), following
    /// C#'s brace rule as measured on the C# compiler itself: a run of r opening braces is
    ///   * LITERAL when r &lt; N (N being the number of `$`),
    ///   * a HOLE when N &lt;= r &lt; 2N, with the braces LEFT of the hole literal,
    ///   * an ERROR when r &gt;= 2N (C# answers CS9006 there: a literal brace needs MORE dollars rather than a
    ///     doubled one, which is the opposite of the non-raw form, where `{{` is a literal brace).
    /// The first line break after the opening delimiter is skipped, as in every raw string here.
    /// The token carries the text with ONE-brace holes either way, which is the convention the interpolation parser
    /// already reads, so nothing downstream had to learn a second shape.</summary>
    private Token ScanInterpolatedRawString(int dollars, int quotes)
    {
        var builder = GetBuilder();
        var startLine = _lineNumber;
        var startCol = _start - _lineStart + 1;
        var depth = 0;

        // C# semantics: content starts on the line after the opening delimiter.
        if (!IsAtEnd())
        {
            if (Peek() == '\r' && _current + 1 < Length && Src[_current + 1] == '\n')
            {
                Advance(); Advance();
                _lineNumber++; _lineStart = _current;
            }
            else if (Peek() == '\n')
            {
                Advance();
                _lineNumber++; _lineStart = _current;
            }
        }

        while (true)
        {
            if (IsAtEnd())
                throw Error("Unterminated raw interpolated string", startLine, startCol);

            var c = Advance();

            if (c == '"')
            {
                var runCol = _current - 1 - _lineStart + 1;
                var run = ConsumeQuoteRun(1);
                if (run == quotes) break;                        // the closing delimiter
                if (run > quotes)
                    throw Error($"a raw interpolated string's content cannot hold a run of {run} quote(s): a run of {quotes} closes the literal. "
                              + $"Open it with {run} quote(s) when the run belongs to the content, or put a space (or a line break) "
                              + $"before the closing quotes when the content ends with one",
                                _lineNumber, runCol);
                for (var k = 0; k < run; k++) builder.Append('"');
                continue;
            }

            if (c == '{')
            {
                if (depth > 0) { builder.Append(c); continue; }   // inside a hole a brace belongs to the expression
                var braces = 1;
                while (Peek() == '{') { Advance(); braces++; }
                if (braces >= 2 * dollars)
                    throw Error($"a run of {braces} '{{' is refused: with {dollars} dollar(s) a hole opens with {dollars} brace(s), "
                              + $"and a literal brace wants MORE dollars rather than a doubled one (C# refuses a run of {2 * dollars} too).",
                                startLine, startCol);
                if (braces < dollars)
                {
                    for (var k = 0; k < braces; k++) builder.Append("{{");   // fewer than N: literal text
                    continue;
                }
                for (var k = dollars; k < braces; k++) builder.Append("{{");  // the braces LEFT of the hole are literal
                depth = 1;
                builder.Append('{');
                continue;
            }

            if (c == '}')
            {
                var braces = 1;
                while (Peek() == '}') { Advance(); braces++; }
                if (braces >= 2 * dollars)
                    throw Error($"a run of {braces} '}}' is refused: with {dollars} dollar(s) a hole closes with {dollars} brace(s) "
                              + $"(C# refuses a run of {2 * dollars} too).", startLine, startCol);
                if (depth == 0)
                {
                    for (var k = 0; k < braces; k++) builder.Append("}}");   // outside a hole: literal text
                    continue;
                }
                if (braces < dollars)
                {
                    for (var k = 0; k < braces; k++) builder.Append('}');    // the expression's own braces
                    continue;
                }
                depth = 0;
                builder.Append('}');
                for (var k = dollars; k < braces; k++) builder.Append("}}");
                continue;
            }

            if (c == '\n') { _lineNumber++; _lineStart = _current; }
            builder.Append(c);
        }

        return new Token(TokenType.InterpolatedString, ApplyRawIndent(builder.ToString()), startLine, startCol);
    }

    private Token ScanInterpolatedString(bool verbatim)
    {
        var builder = GetBuilder();
        var startLine = _lineNumber;
        var startCol = _start - _lineStart + 1;
        var depth = 0;
        var nestedString = false;   // a VERBATIM hole is "inside a nested literal" between the quotes of a pair

        while (true)
        {
            if (IsAtEnd())
                throw Error("Unterminated interpolated string.", startLine, startCol);

            var c = Advance();

            if (c == '"' && depth == 0)
            {
                if (verbatim && Peek() == '"')
                {
                    // A doubled quote is ONE quote, as in every verbatim string: the token text is re-parsed by the
                    // interpolation splitter, where a lone `"` is an ordinary character, so writing the pair through
                    // leaked them into the value (`class=""row""` where the plain `@"..."` spelling answered
                    // `class="row"`).
                    Advance();
                    builder.Append('"');
                    continue;
                }
                break;
            }

            if (depth > 0 && c == '\'' && verbatim)
            {
                // A char literal is written normally whatever the parent's form, so it is skipped the same way.
                builder.Append(c);
                while (!IsAtEnd())
                {
                    var n = Advance();
                    builder.Append(n);
                    if (n == '\\' && !IsAtEnd()) { builder.Append(Advance()); continue; }
                    if (n == '\'') break;
                    if (n == '\n') { _lineNumber++; _lineStart = _current; }
                }
                continue;
            }

            if (depth > 0 && c == '"' && verbatim)
            {
                // In a VERBATIM parent a quote comes DOUBLED, so each pair toggles "inside a nested literal" and a brace
                // while inside one belongs to the string rather than to the hole: `$@"{f(""a}b"")}"` needs that. The pair
                // is written through as ONE quote, the way every other verbatim quote is (the splitter re-reads this
                // text). A LONE quote is passed through, as it was before this existed; C# would refuse it.
                if (Peek() == '"') { Advance(); builder.Append('"'); nestedString = !nestedString; }
                else builder.Append('"');
                continue;
            }

            if (depth > 0 && (c == '"' || c == '\''))
            {
                // In a NORMAL parent a quote opens a nested literal that runs to its own closing quote, escapes included,
                // and is skipped whole so its braces are not the hole's: `$"{f("a}b")}"` is one hole holding a call.
                var quote = c;
                builder.Append(c);
                while (!IsAtEnd())
                {
                    var n = Advance();
                    if (n == quote) { builder.Append(n); break; }
                    if (n == '\\' && !IsAtEnd()) { builder.Append(n); builder.Append(Advance()); continue; }
                    if (n == '\n') { _lineNumber++; _lineStart = _current; }
                    builder.Append(n);
                }
                continue;
            }

            if (c == '\\' && depth == 0)
            {
                if (verbatim)
                {
                    if (Peek() == '"') { Advance(); builder.Append('"'); }
                    else builder.Append('\\');
                    continue;
                }
                if (IsAtEnd())
                    throw Error("Unterminated escape sequence in interpolated string.", startLine, startCol);
                AppendEscape(builder, Advance());
                continue;
            }

            if (c == '"' && depth > 0 && verbatim && Peek() == '"')
            {
                // Inside a HOLE a quote is doubled too, because the whole literal is verbatim (C#), and the engine
                // un-doubles it before the expression is parsed: `{a == ""x""}` is an expression holding one quote, and
                // `{a == """"}` compares against an EMPTY string.
                Advance();
                builder.Append('"');
                continue;
            }

            if (c == '{')
            {
                if (Peek() == '{')
                {
                    Advance();
                    builder.Append("{{");
                }
                else
                {
                    depth++;
                    builder.Append(c);
                }
                continue;
            }

            if (c == '}')
            {
                if (depth > 0 && nestedString) { builder.Append(c); continue; }   // the string's own brace, not the hole's
                if (depth > 0)
                {
                    depth--;
                    if (depth == 0) nestedString = false;   // a verbatim hole's nested-literal state ends with the hole
                    builder.Append(c);
                    continue;
                }
                if (Peek() == '}')
                {
                    Advance();
                    builder.Append("}}");
                    continue;
                }
                // Single }, literal
                builder.Append(c);
                continue;
            }

            if (c == '\n') { _lineNumber++; _lineStart = _current; }
            builder.Append(c);
        }

        return new Token(TokenType.InterpolatedString, builder.ToString(), startLine, startCol);
    }

    private Token ScanStringOrChar()
    {
        // Peek ahead to determine if this is a char literal (single char) or a string (multi-char)
        var savedPos = _current;
        var line = _lineNumber;
        var col = _start - _lineStart + 1;

        if (IsAtEnd())
            throw Error("Unterminated string or char literal", line, col);

        // Check if we have a char literal: 'x' or '\n', exactly one escaped or raw char
        if (!IsAtEnd())
        {
            var next = Src[_current];
            if (next == '\\')
            {
                // A char literal may carry an escape: `'\n'`, `'\0'`, `'\t'`, `'\r'`, `'\\'`, `'\''`, `'"'`, and
                // `'\u00e9'`. The rule is the EVALUATED length (docs/language states it), so the test is "consume
                // the escape, then a closing quote follows". It used to be "a backslash IMMEDIATELY followed by the
                // quote", which only `'\''` is, so every other escaping literal became a STRING: measured,
                // `typeof('\n')`, `typeof('\0')` and `typeof('\t')` answered `string` where `typeof('a')` answers
                // `char`, which made `IsTruthy`'s `char c => c != '\0'` arm unreachable and `(int)'\n'` refuse.
                // TWO details this branch has to respect, both learned the hard way: `ScanEscape` expects `_current`
                // AT the backslash and consumes it (an `Advance()` first makes it eat the escape LETTER as the
                // introducer and then the closing quote), and it knows only the single-letter escapes, so `\uXXXX`
                // is read here and `\UXXXXXXXX` stays a string (its code point can need two UTF-16 units).
                var letter = _current + 1 < Length ? Src[_current + 1] : '\0';
                if (letter is 'n' or 't' or 'r' or '\\' or '\'' or '"' or '0')
                {
                    if (_current + 2 < Length && Src[_current + 2] == '\'')
                    {
                        var ch = ScanEscape(); // consumes the backslash AND the escape letter
                        Advance();             // the closing quote, which ScanEscape does not consume
                        return new Token(TokenType.CharLiteral, ch.ToString(), line, col);
                    }
                }
                else if (letter is 'u' or 'x' or 'U')
                {
                    // The hex escapes, which `ScanEscape` does not know: `\uXXXX` takes exactly 4 digits,
                    // `\UXXXXXXXX` exactly 8, and `\xH..H` takes ONE TO FOUR and is GREEDY (the rule the string
                    // scanner states, so `\x41B` is U+041B rather than U+0041 followed by a `B`). Each yields one
                    // UTF-16 unit, which is what makes it a char when the closing quote follows; a `\U` value
                    // above 0xFFFF needs a surrogate PAIR, so that literal stays a string.
                    var minDigits = letter is 'x' ? 1 : letter is 'u' ? 4 : 8;
                    var maxDigits = letter is 'U' ? 8 : 4;
                    var digits = 0;
                    var value = 0;
                    while (digits < maxDigits)
                    {
                        var at = _current + 2 + digits;
                        if (at >= Length) break;
                        var d = Src[at];
                        var nibble = d >= '0' && d <= '9' ? d - '0'
                            : d >= 'a' && d <= 'f' ? d - 'a' + 10
                            : d >= 'A' && d <= 'F' ? d - 'A' + 10 : -1;
                        if (nibble < 0) break;
                        value = value * 16 + nibble;
                        digits++;
                    }
                    var afterDigits = _current + 2 + digits;
                    if (digits >= minDigits && afterDigits < Length && Src[afterDigits] == '\'' && value <= 0xFFFF)
                    {
                        _current = afterDigits;
                        Advance(); // the closing quote
                        return new Token(TokenType.CharLiteral, ((char)value).ToString(), line, col);
                    }
                }
                _current = savedPos;
                return ScanStringInternal(false, '\'');
            }
            else if (next != '\'')
            {
                // Regular character, advance
                var ch = Advance();
                if (Peek() == '\'')
                {
                    // Exactly one char + closing quote → char literal
                    Advance();
                    return new Token(TokenType.CharLiteral, ch.ToString(), line, col);
                }
                // More characters follow → it's a string
                _current = savedPos;
                return ScanStringInternal(false, '\'');
            }
        }

        // Empty single-quote pair '' or just ', treat as empty string
        _current = savedPos;
        return ScanStringInternal(false, '\'');
    }

    private char ScanEscape()
    {
        Advance();

        return Advance() switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            '0' => '\0',
            var ec => throw Error($"Unknown escape '\\{ec}'", _lineNumber, _start - _lineStart + 1)
        };
    }

    private Token ScanIdentifier()
    {
        // ONE vectorised scan of the rest of the source: the first character that is not a name character ends the run.
        var rest = Src[_current..];
        var stop = rest.IndexOfAnyExcept(IdentifierPartChars);
        _current = stop < 0 ? Length : _current + stop;

        var text = Src[_start.._current];

        return new Token(KeywordOrIdentifier(text), text.ToString(), _lineNumber, _start - _lineStart + 1);
    }

    private static readonly Dictionary<string, TokenType> Keywords
        = new(StringComparer.Ordinal)
        {
            ["int"] = TokenType.Int,
            ["long"] = TokenType.Long,
            ["short"] = TokenType.Short,
            ["byte"] = TokenType.Byte,
            ["uint"] = TokenType.UInt,
            ["ushort"] = TokenType.UShort,
            ["sbyte"] = TokenType.SByte,
            ["ulong"] = TokenType.ULong,
            ["float"] = TokenType.Float,
            ["double"] = TokenType.Double,
            ["decimal"] = TokenType.Decimal,
            ["bool"] = TokenType.Bool,
            ["char"] = TokenType.Char,
            ["string"] = TokenType.StringType,
            ["object"] = TokenType.Object,
            ["void"] = TokenType.Void,
            ["if"] = TokenType.If,
            ["else"] = TokenType.Else,
            ["while"] = TokenType.While,
            ["in"] = TokenType.In,
            ["return"] = TokenType.Return,
            ["true"] = TokenType.True,
            ["false"] = TokenType.False,
            ["null"] = TokenType.Nil,
            ["not"] = TokenType.Not,
            ["var"] = TokenType.Var,
            ["const"] = TokenType.Const,
            ["for"] = TokenType.For,
            ["break"] = TokenType.Break,
            ["continue"] = TokenType.Continue,
            ["debugger"] = TokenType.Debugger,
            ["class"] = TokenType.Class,
            ["struct"] = TokenType.Struct,
            ["static"] = TokenType.Static,
            ["fun"] = TokenType.Fun,
            ["and"] = TokenType.And,
            ["or"] = TokenType.Or,
            ["super"] = TokenType.Super,
            ["this"] = TokenType.This,
            ["new"] = TokenType.New,
            ["constructor"] = TokenType.Constructor,
            ["extends"] = TokenType.Extends,
            ["import"] = TokenType.Import,
            ["namespace"] = TokenType.Namespace,
            ["is"] = TokenType.Is,
            ["as"] = TokenType.As,
            ["alias"] = TokenType.Alias,
            ["delegate"] = TokenType.Delegate,
            ["event"] = TokenType.Event,
            ["operator"] = TokenType.Operator,
            ["ref"] = TokenType.Ref,
            ["out"] = TokenType.Out,
            ["params"] = TokenType.Params,
            ["throw"] = TokenType.Throw,
            ["try"] = TokenType.Try,
            ["catch"] = TokenType.Catch,
            ["finally"] = TokenType.Finally,
            ["public"] = TokenType.Public,
            ["private"] = TokenType.Private,
            ["protected"] = TokenType.Protected,
            ["internal"] = TokenType.Internal,
            ["base"] = TokenType.Base,
            ["get"] = TokenType.Get,
            ["set"] = TokenType.Set,
            ["init"] = TokenType.Init,
            ["pure"] = TokenType.Pure,
            ["yield"] = TokenType.Yield,
            ["switch"] = TokenType.Switch,
            ["using"] = TokenType.Using,
            ["with"] = TokenType.With,
            ["readonly"] = TokenType.Readonly,
            ["case"] = TokenType.Case,
            ["default"] = TokenType.Default,
            ["when"] = TokenType.When,
        };

    private static readonly Dictionary<string, TokenType>.AlternateLookup<ReadOnlySpan<char>> KeywordsLookup
        = Keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    private static TokenType KeywordOrIdentifier(ReadOnlySpan<char> text)
        => KeywordsLookup.TryGetValue(text, out var type) ? type : TokenType.Identifier;

    private static Exception Error(string message, int line, int column) =>
        new ScriptException(ErrorCode.ParseError, message, line, column);
}
