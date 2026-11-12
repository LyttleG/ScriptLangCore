namespace ScriptLangCore;

internal sealed record Parser
{
    private readonly List<Token> _tokens;
    private int _current;
    private readonly Dictionary<string, DelegateInfo> _delegates;

    /// <summary>When true, a bare `Identifier => ...` is NOT treated as an arrow lambda, used while
    /// parsing match/switch-expression arm patterns, where `x => body` is a bare-identifier PATTERN
    /// (e.g. `crash_switch.slc` classify: `Function => "Function"`).</summary>
    private bool _suppressBareLambda;

    /// <summary>True while parsing the body of a function declared with the contextual `async` modifier, which is
    /// the ONLY place `await` is the keyword. Contextual on purpose (C# treats it so): outside an async body the
    /// word stays an ordinary identifier, so no existing script breaks. A nested lambda's body saves and clears it,
    /// because a lambda is not async here.</summary>
    private bool _inAsyncBody;
    /// <summary>True while parsing an import PATH, where s introduces the alias rather than a cast.</summary>
    private bool _noAsOperator;

    /// <summary>How deep we are inside a DECLARED body (a function, a method or a lambda). Zero means a file's TOP
    /// LEVEL, and that distinction is what makes the top level an async-capable entry: `await` there is the operator
    /// (the interpreter WAITS on it, because nothing would resume the outermost frame), while a body that is not
    /// declared `async` still refuses it by name.</summary>
    private int _bodyDepth;

    /// <summary>Consumes a contextual `async` modifier in a declaration position. Returns whether it was there.</summary>
    private bool MatchAsync()
    {
        if (Check(TokenType.Identifier) && Peek().Literal == "async")
        {
            _ = Advance();
            return true;
        }
        return false;
    }

    /// <summary>Consumes the contextual `async` modifier on a class or struct MEMBER method, whose shape is
    /// `async <type|void> Name(`. Returns whether it was there. The shape test is load bearing: without it the word
    /// would be read as a TYPE NAME, which is how `class C { async int M() { } }` used to declare a method called
    /// `int` whose return type was `async`.</summary>
    private bool ConsumeMemberAsync()
    {
        if (!Check(TokenType.Identifier) || Peek().Literal != "async") return false;
        var start = _current;
        var typeStart = start + 1;
        if (typeStart >= _tokens.Count) return false;
        int typeEnd;
        if (_tokens[typeStart].Type == TokenType.Void) typeEnd = typeStart + 1;
        else
        {
            _current = typeStart;
            try { typeEnd = SafeParseTypeEnd(); }
            finally { _current = start; }
        }
        if (typeEnd <= 0 || typeEnd + 1 >= _tokens.Count) return false;
        if (_tokens[typeEnd].Type != TokenType.Identifier) return false;
        if (_tokens[typeEnd + 1].Type != TokenType.LeftParen) return false;
        _ = Advance();   // the `async` itself
        return true;
    }

    /// <summary>Refuses the contextual `async` modifier where this POC does not support it yet, naming the shape
    /// that IS supported. Used by the interface and extension member parsers, whose members are not wrapped as
    /// Tasks yet.</summary>
    private void RefuseAsyncMember()
    {
        if (Check(TokenType.Identifier) && Peek().Literal == "async"
            && (PeekAhead(1).Type is TokenType.Void or TokenType.Identifier
                || TypeTokenHelper.IsBaseType(PeekAhead(1).Type)))
        {
            throw Error("'async' is not supported on an interface or extension member yet. Declare an async "
                        + "function and call it from the member, or put the method on a class or a struct.", Peek());
        }
    }

    /// <summary>Refuses `await` where a DECLARATION would read it as a type name (`await x;`), which is how a stray
    /// `await` outside an async function used to pass SILENTLY: it re-declared `x` as a variable of type `await`
    /// and did nothing. Outside an async body `await` is an ordinary identifier (C# treats it so), but it is never a
    /// type name, and the shape refused here is unambiguous: `await`, a name, then `;` or `=`.</summary>
    /// <summary>True when a statement beginning with `await` is the AWAIT OPERATOR rather than a declaration whose type
    /// is named `await`: inside an async body, and at a file's top level, which is this engine's async-capable entry.
    /// The declaration paths ask this so the word is never read as a type name there (measured: without it, a top-level
    /// `await t;` declared a variable of type `await`, so `t` came back null and a duplicate-declaration warning fired).</summary>
    private bool IsAwaitOperatorHere()
        => (_inAsyncBody || _bodyDepth == 0)
           && Peek().Literal == "await" && StartsAnAwaitOperand(PeekAhead(1).Type);
    private void RefuseAwaitAsTypeName()
    {
        // Inside an async body the operator owns the word, so the expression path must have it.
        if (_inAsyncBody || _bodyDepth == 0) return;   // the operator here: the caller skips the declaration attempt
        if (Check(TokenType.Identifier) && Peek().Literal == "await"
            && PeekAhead(1).Type == TokenType.Identifier
            && PeekAhead(2).Type is TokenType.Semicolon or TokenType.Equal)
        {
            throw Error("'await' is only the await operator, and only inside an `async` function: it cannot name a "
                        + "type here. Declare the function `async` if you meant to await a task.", Peek());
        }
    }

    /// <summary>Whether a token can BEGIN the operand of an <c>await</c>, which is how the operator is told apart
    /// from a variable that happens to be named <c>await</c> when the body is NOT async. <c>[</c> is left out on
    /// purpose, so indexing such a variable (<c>await[0]</c>) keeps working.</summary>
    private static bool StartsAnAwaitOperand(TokenType type)
        => type is TokenType.Identifier or TokenType.Number or TokenType.String or TokenType.CharLiteral
            or TokenType.True or TokenType.False or TokenType.Nil
            or TokenType.LeftParen or TokenType.New or TokenType.Minus or TokenType.Bang or TokenType.Tilde;

    private readonly StringComparer _nameComparer;

    /// <summary>The text this parse read, when the caller has it, plus the offset each LINE starts at. Together they
    /// turn a token's (line, column) into a character offset, which is how a PATTERN's own source text is captured
    /// (see <see cref="AttachPatternSourceText"/>): a bundle carries a pattern as its TEXT and re-parses it at load,
    /// because a `Pattern` is an AST fragment of arbitrary expressions and the wire has no form for one. Null when the
    /// caller passed no text (a synthesized or inline parse), and then an exporter REFUSES that body by name.</summary>
    private readonly string? _sourceText;
    private readonly int[]? _lineStarts;

    public Parser(IEnumerable<Token> tokens, IReadOnlyDictionary<string, DelegateInfo>? delegates = null,
        bool caseSensitive = false, string? source = null)
    {
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _nameComparer = comparer;
        _tokens = [.. tokens];
        _delegates = delegates?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, comparer) ?? new Dictionary<string, DelegateInfo>(comparer);
        _sourceText = source;
        if (source != null)
        {
            var starts = new List<int> { 0 };
            for (var i = 0; i < source.Length; i++)
                if (source[i] == '\n') starts.Add(i + 1);
            _lineStarts = [.. starts];
        }
    }

    /// <summary>The character offset a (line, column) pair points at, or -1 when this parse has no text or the pair
    /// is outside it. The lexer counts COLUMNS in characters from the line's first char, so the arithmetic is exact
    /// as long as both sides read the same text, which is why the caller that has the source hands it over.</summary>
    private int OffsetOf(int line, int column)
    {
        if (_sourceText == null || _lineStarts == null) return -1;
        if (line < 1 || line > _lineStarts.Length) return -1;
        var offset = _lineStarts[line - 1] + (column - 1);
        return offset >= 0 && offset <= _sourceText.Length ? offset : -1;
    }

    /// <summary>Records the pattern's SOURCE TEXT on the node it returns: from the token the pattern STARTS at to the
    /// token that FOLLOWS it, which is where the parser stands once the pattern is consumed. This is what makes a
    /// `match` pattern exportable at all (`PatternEngineDescriptor.PatternText`), and it answers the node unchanged
    /// when this parse has no text. The span is derived from (line, column) pairs rather than from a token's
    /// `Literal.Length`, because for a string or a number the Literal is the DECODED value (`"a\nb"` is 6 characters
    /// of source and 3 of value), so only the NEXT token's start is a reliable end.</summary>
    private Pattern AttachPatternSourceText(Pattern pattern, Token startToken)
    {
        var start = OffsetOf(startToken.LineNumber, startToken.Column);
        if (start < 0) return pattern;
        var next = Peek();
        var end = OffsetOf(next.LineNumber, next.Column);
        if (end < start) return pattern;
        return pattern with { SourceText = _sourceText![start..end] };
    }

    /// <summary>The SOURCE TEXT a TOP-LEVEL STATEMENT starting at this token spans, or null when this parse has no
    /// text. The span runs to the token that FOLLOWS the statement, which is where the parser stands once it has been
    /// consumed, and the end comes from that NEXT token's position for the reason the pattern's own helper states: a
    /// token's `Literal` is the DECODED value (`"a\nb"` is 6 characters of source and 3 of value), so only the
    /// following token's start is a reliable end. A statement that ends the file leaves the stream at EOF, whose
    /// position may not be usable, so the slice then runs to the text's end. Attached to EVERY top-level statement,
    /// because two carriers read it: a DECLARATION whose body the compiler did not take (a function, a class, a
    /// struct) and a STATEMENT the compiler could not compile, which a compiled program body hands to the tree.</summary>
    private string? SourceTextOf(Token startToken)
    {
        var start = OffsetOf(startToken.LineNumber, startToken.Column);
        if (start < 0) return null;
        var end = OffsetOf(Peek().LineNumber, Peek().Column);
        if (end < start) end = _sourceText!.Length;
        return _sourceText![start..end];
    }

    /// <summary>Parse a small expression from a string (used by string interpolation).</summary>
    public Expr ParseInlineExpression(string source, int baseLine = 0, int baseCol = 0)
    {
        try
        {
            var lexer = new Lexer(source.AsMemory());
            var subTokens = lexer.GetTokens().ToList();
            var subParser = new Parser(subTokens, _delegates);
            return subParser.Expression();
        }
        catch (ScriptException ex) when (baseLine > 0)
        {
            // Adjust reported location to original script position
            throw new ScriptException(ex.ErrorCode, ex.Message, ex.Line + baseLine - 1, ex.Column + baseCol - 1);
        }
    }

    public IEnumerable<Stmt> GetStatements()
    {
        // File-level `namespace A.B;` wraps every following top-level statement in a
        // NamespaceDeclStmt until the next file-level namespace or EOF (C# semantics).
        string? pendingName = null;
        List<Stmt>? pendingBody = null;

        while (!IsAtEnd())
        {
            if (Peek().Type == TokenType.Semicolon) { _ = Advance(); continue; }

            // Block-form `namespace A.B { … }` is a normal statement (nested inside a
            // pending file-level namespace when one is open).
            if (Check(TokenType.Namespace) && !IsFileLevelNamespace())
            {
                var stmt = NamespaceStatement();
                if (pendingName != null) { pendingBody!.Add(stmt); }
                else yield return stmt;
                continue;
            }

            if (Check(TokenType.Namespace))   // file-level form
            {
                var nameToken = Consume(TokenType.Namespace, "Expect 'namespace'.");
                var name = ParseQualifiedIdentifier();
                _ = Consume(TokenType.Semicolon, "Expect ';' after file-level namespace name.");
                if (pendingName != null)
                {
                    yield return NamespaceDeclStmt.Create(pendingName, pendingName.Split('.'),
                        pendingBody!, nameToken.LineNumber, nameToken.Column);
                }
                pendingName = name;
                pendingBody = new List<Stmt>();
                continue;
            }

            var declStartToken = Peek();
            var next = TopLevel();
            // EVERY top-level statement carries its own source text when this parse has the file's (see
            // SourceTextOf). Attached HERE rather than at each of TopLevel's branches, so there is ONE site and a new
            // branch cannot forget it, and so the two carriers that read it cannot disagree about a span: a
            // DECLARATION whose body the compiler did not take (a function, a class, a struct) and a STATEMENT the
            // compiler could not compile, which a compiled program body hands back to the tree by this text.
            next.SourceText ??= SourceTextOf(declStartToken);
            if (pendingName != null) { pendingBody!.Add(next); }
            else yield return next;
        }

        if (pendingName != null)
            yield return NamespaceDeclStmt.Create(pendingName, pendingName.Split('.'),
                pendingBody!, 0, 0);
    }

    // True when `namespace` is followed by a qualified identifier and then `;`
    // (file-level) rather than `{` (block form). Tokens are already lexed, scan
    // identifier / `.` / `;` ahead of the current position.
    private bool IsFileLevelNamespace()
    {
        var i = _current + 1;                       // past 'namespace'
        var sawId = false;
        while (i < _tokens.Count - 1)
        {
            var t = _tokens[i].Type;
            if (t == TokenType.Identifier) { sawId = true; i++; continue; }
            if (t == TokenType.Dot && sawId) { i++; continue; }
            return t == TokenType.Semicolon && sawId;
        }
        return false;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────
    private bool Match(TokenType t1) { if (Check(t1)) { _ = Advance(); return true; } return false; }
    private bool Match(TokenType t1, TokenType t2) { if (Check(t1) || Check(t2)) { _ = Advance(); return true; } return false; }
    private bool Match(TokenType t1, TokenType t2, TokenType t3) { if (Check(t1) || Check(t2) || Check(t3)) { _ = Advance(); return true; } return false; }
    /// <summary>The fallback for four or more tokens, in C#'s 13 `params ReadOnlySpan` form. The one-, two- and
    /// three-argument overloads above cover the hot spellings (261 call sites, exactly ONE of which passes four), so this
    /// exists to keep one definition rather than to optimise, and the span form means no caller allocates an array for it
    /// (the old `params TokenType[]` also ran a LINQ delegate per call through `types.Any(Check)`).</summary>
    private bool Match(params ReadOnlySpan<TokenType> types)
    {
        foreach (var t in types) { if (Check(t)) { _ = Advance(); return true; } }
        return false;
    }

    private bool Check(TokenType type) => !IsAtEnd() && Peek().Type == type;
    private bool IsAtEnd() => _tokens[_current].Type == TokenType.Eof;
    private Token Peek() => _tokens[_current];
    private Token Previous() => _tokens[_current - 1];
    private Token Advance() { if (!IsAtEnd()) _current++; return _tokens[_current - 1]; }

    private Token Consume(TokenType type, string message) =>
        Check(type) ? Advance() : throw Error($"{message} (expected '{type}', found '{Peek().Type}').\nConsider adding the missing token before this position", Peek());

    private static Exception Error(string message, Token token) =>
        new ScriptException(ErrorCode.ParseError, message, token.LineNumber, token.Column);

    private static void Warn(string message, Token token)
    {
        Logging.Log.Warning("Script", message, token.LineNumber, token.Column);
    }

    private Token PeekAhead(int offset)
    {
        var index = _current + offset;
        return index < _tokens.Count ? _tokens[index] : _tokens[^1];
    }

    private Token ConsumeIdentifier(string message)
        => ConsumeIdentifierCore(message, warnOnReservedKeyword: true);

    /// <summary>Like <see cref="ConsumeIdentifier"/> but WITHOUT the reserved-keyword warning.
    /// Used for member names after '.', a member (native function, namespace member, property)
    /// is a name, not a keyword position, so <code>co.Continue()</code> must not warn about
    /// 'Continue'. Member names are matched case-sensitively by the dispatchers; the lexer's
    /// keyword set is case-insensitive and does not apply here.</summary>
    private Token ConsumeMemberIdentifier(string message)
        => ConsumeIdentifierCore(message, warnOnReservedKeyword: false);

    private Token ConsumeIdentifierCore(string message, bool warnOnReservedKeyword)
    {
        var token = Peek();
        switch (token.Type)
        {
            case TokenType.Identifier:
                _ = Advance();
                return token;

            // get/set/init are soft keywords, allow as identifiers anywhere
            case TokenType.Get:
            case TokenType.Set:
            case TokenType.Init:
                _ = Advance();
                return new Token(TokenType.Identifier, token.Literal, token.LineNumber, token.Column);
        }

        if (!IsReservedKeyword(token.Literal))
            throw Error($"{message} (expected identifier, found '{token.Type}').", token);

        if (warnOnReservedKeyword && !IsReceiverKeyword(token.Literal))
            Logging.Log.Warning("Script", $"Using reserved keyword '{token.Literal}' as an identifier. This may cause confusion.", token.LineNumber, token.Column);
        _ = Advance();
        return token;
    }

    /// <summary>The keywords that NAME a receiver. The engine models them as variables holding one
    /// (`this` is the trailing local of a method or an extension member), so they are legitimate
    /// VALUES, and a misplaced one is refused by the resolver by name ("used outside of a class
    /// method"). The identifier warns below therefore skips them: warning about `this` is noise.
    private static bool IsReceiverKeyword(string literal) => literal is "this" or "base" or "super";

    private static readonly FrozenSet<string> ReservedKeywords = new[]
    {
        "if", "else", "while", "for", "foreach", "in",
        "return", "break", "continue", "var", "const",
        "true", "false", "null", "not", "new", "this", "base",
        "super", "class", "struct", "delegate", "event",
        "import", "as", "alias", "is", "try", "catch",
        "finally", "throw", "fun", "pure", "yield", "params",
        "int", "long", "short", "byte", "float", "double", "decimal",
        "bool", "char", "string", "object", "void",
        "DateTime", "DateTimeOffset", "TimeSpan",
        "Action", "Func", "Predicate", "Comparison", "Converter", "EventHandler"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static bool IsReservedKeyword(string? identifier)
        => !string.IsNullOrEmpty(identifier) && ReservedKeywords.Contains(identifier);

    private Token ConsumeAnyOperatorToken(string message)
    {
        var token = Peek();
        if (token.Type is not (TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash
            or TokenType.Percent or
            TokenType.Less or TokenType.Greater or TokenType.LessEqual or TokenType.GreaterEqual or TokenType.EqualEqual
            or TokenType.BangEqual)) throw Error(message, token);
        _ = Advance(); return token;
    }

    // ─── Top-level & Declarations ─────────────────────────────────────────

    private Stmt TopLevel()
    {
        if (Match(TokenType.Import)) return ImportStatement();
        if (Match(TokenType.Delegate)) return ParseDelegate();

        // Contextual `extension` block: a declaration keyword only where a declaration may start, so
        // the word stays usable as an identifier (same treatment as the contextual `enum` below).
        if (IsInterfaceDeclStart()) return ParseInterfaceDeclaration();
        if (IsExtensionBlockStart()) return ParseExtensionDeclaration(null);

        var typeDecl = TryParseTypeDeclaration();
        if (typeDecl != null) return typeDecl;

        TryConsumeAccessModifier(); // silently ignore access modifiers at top level

        // Contextual enum keyword: accept "enum" as keyword only at declaration start
        if (Peek().Type == TokenType.Identifier && Peek().Literal.Equals("enum", StringComparison.Ordinal))
        {
            _ = Advance();
            return EnumDeclaration(AccessModifier.Private);
        }

        // void function (optionally declared `async`, the C# flavour)
        var voidAsyncPos = _current;
        var isAsyncVoid = MatchAsync();
        if (Peek().Type == TokenType.Void && PeekAhead(1).Type == TokenType.Identifier && PeekAhead(2).Type == TokenType.LeftParen)
        {
            _ = Advance();
            var nameToken = ConsumeIdentifier("Expect function name.");
            // `async void` has its own declaration branch, so the refusal lives here too, not only in the typed
            // path: this engine has ONE untyped Task, an async call always hands back an awaitable Task, and C#'s
            // unawaitable form therefore cannot be expressed, so accepting it would mean something else.
            if (isAsyncVoid)
                throw Error("this engine has no unawaitable `async void`: an async body always hands back a Task here, so write `async Task` (C# reserves `async void` for event handlers).", Peek());
            return FunctionDeclaration(nameToken, TypeInfo.Void, isAsync: isAsyncVoid);
        }
        _current = voidAsyncPos;

        // typed function
        var funcCheckPos = _current;
        var isFunction = false;
        try
        {
            Match(TokenType.Pure);
            MatchAsync();   // the contextual `async` modifier, after `pure` (the engine's modifier order)
            // `await W();` at a top level begins like a TYPED function: `await` as the return type, `W` as the name,
            // `(` as its parameters. The statement dispatch rules the OPERATOR out before it attempts a VARIABLE
            // declaration, but this sighting is a second attempt and needs the same question (measured: without it
            // the statement answered "Expect '{' before function body", expecting a body for `await W()`).
            if (!IsAwaitOperatorHere() && (IsTypeStart(Peek()) || Peek().Type == TokenType.Void
                || IsAtTypedDeclaration(_current, TokenType.LeftParen, TokenType.Less)))
            {
                var typeEnd = SafeParseTypeEnd();
                if (typeEnd >= 0 && typeEnd < _tokens.Count && _tokens[typeEnd].Type == TokenType.Identifier)
                {
                    var nextPos = typeEnd + 1;
                    if (nextPos < _tokens.Count && (_tokens[nextPos].Type == TokenType.LeftParen || _tokens[nextPos].Type == TokenType.Less))
                    {
                        // A type with a SUFFIX (`A? Mk(…)`, `A[] Mk(…)`) is accepted here for the first time, and
                        // `x ? f() : g();` begins identically, so that shape must also show a declaration BODY.
                        var hadSuffix = typeEnd > 1
                            && _tokens[typeEnd - 1].Type is TokenType.Question or TokenType.RightBracket;
                        isFunction = !hadSuffix || DeclarationBodyFollows(nextPos);
                    }
                }
            }
        }
        catch { _current = funcCheckPos; }

        if (isFunction)
        {
            _current = funcCheckPos;
            var isPureFunc = Match(TokenType.Pure);
            var isAsyncFunc = MatchAsync();
            var returnType = ParseType(allowVoid: true);
            var nameToken = ConsumeIdentifier("Expect function name.");
            // C#'s rule for an `async` body's declared type (CS1983): `Task<T>` means the body's result is T, `Task`
            // means it has none, and anything else is refused BY NAME, which is what retires the engine's own
            // `async int`. `async void` is refused as well: this engine has ONE untyped Task, so every async call hands
            // back an awaitable Task and C#'s unawaitable form cannot be expressed, which means accepting it would
            // silently mean something else. C# reserves `async void` for event handlers, which a script lambda serves.
            if (isAsyncFunc)
            {
                if (returnType.BaseType == TokenType.Void)
                    throw Error("this engine has no unawaitable `async void`: an async body always hands back a Task here, so write `async Task` (C# reserves `async void` for event handlers).", nameToken);
                if (returnType.TypeName is { } declaredName && string.Equals(declaredName, "Task", StringComparison.Ordinal))
                    returnType = returnType.GenericArgs is { Count: 1 } asyncArgs ? asyncArgs[0] : new TypeInfo(TokenType.Object, false, 0, 0, null);
                else
                    throw Error("an `async` body must declare a `Task`, a `Task<T>` or `void`, as C# requires (CS1983): write `async Task<int>` for a body that returns an int, or `async Task` for one that returns nothing.", nameToken);
            }
            return FunctionDeclaration(nameToken, returnType, isPure: isPureFunc, isCoroutine: false, isAsync: isAsyncFunc);
        }

        // tuple-typed function: (int i, string s) Name(...)
        _current = funcCheckPos;
        var tupleCheckPos = _current;
        if (Peek().Type == TokenType.Pure) _ = Advance();
        MatchAsync();
        if (Peek().Type == TokenType.LeftParen && IsAtTupleTypeReturn())
        {
            _current = tupleCheckPos;
            var isPureFunc2 = Match(TokenType.Pure);
            var isAsyncFunc2 = MatchAsync();
            var elementNames = ParseTupleElementNames();
            var nameToken2 = ConsumeIdentifier("Expect function name.");
            if (Peek().Type == TokenType.LeftParen)
            {
                var ret2 = FunctionDeclaration(nameToken2, new TypeInfo(TokenType.Object, true, 0, 0, null), isPure: isPureFunc2, isCoroutine: false, isAsync: isAsyncFunc2);
                ret2.TupleElementNames = elementNames;
                return ret2;
            }
            if (Peek().Type == TokenType.Equal)
            {
                // tuple-typed variable declaration: (int c, int d) name = (expr, expr);
                _ = Advance();
                var init = Expression();
                _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
                var decl = VarDeclStmt.Create(nameToken2.Literal, init, new TypeInfo(TokenType.Object, true, 0, 0, null), false, nameToken2.WasEscaped, nameToken2.LineNumber, nameToken2.Column);
                decl.TupleElementNames = elementNames;
                return decl;
            }
            throw Error("Expect '(' after function name or '=' in tuple declaration.", Peek());
        }
        _current = funcCheckPos;

        // var function
        if (Peek().Type == TokenType.Var && PeekAhead(1).Type == TokenType.Identifier && PeekAhead(2).Type == TokenType.LeftParen)
        {
            _ = Advance();
            var nameToken = ConsumeIdentifier("Expect function name.");
            return FunctionDeclaration(nameToken, new TypeInfo(TokenType.Object, true, 0, 0, null));
        }

        // typed variable declaration
        RefuseAwaitAsTypeName();
        var typeDeclPos = _current;
        var isTypeDecl = false;
        TypeInfo? typeDeclType = null;
        Token typeDeclName = default;
        // Guarded so a statement that cannot begin with a type never provokes ParseType's ParseError. Skipping the
        // attempt reaches the outcome the catch produced (isTypeDecl stays false, the position is untouched), minus
        // one first-chance exception per statement.
        if (CouldStartType(Peek()) && !IsAwaitOperatorHere())
        {
            try
            {
                typeDeclType = ParseType(allowVoid: true);
                if (Peek().Type == TokenType.Identifier)
                {
                    typeDeclName = ConsumeIdentifier("Expect variable name.");
                    if (Peek().Type == TokenType.Equal || Peek().Type == TokenType.Semicolon) isTypeDecl = true;
                }
            }
            catch { _current = typeDeclPos; }
        }

        if (isTypeDecl)
        {
            if (Match(TokenType.Equal))
            {
                var initializer = Expression();
                _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
                return VarDeclStmt.Create(typeDeclName.Literal, initializer, typeDeclType, false, typeDeclName.WasEscaped, typeDeclName.LineNumber, typeDeclName.Column);
            }
            _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
            return VarDeclStmt.Create(typeDeclName.Literal, null, typeDeclType, false, typeDeclName.WasEscaped, typeDeclName.LineNumber, typeDeclName.Column);
        }
        _current = typeDeclPos;

        // var declaration
        if (Peek().Type == TokenType.Var)
        {
            var savePos = _current;
            var isVarDecl = false;
            Token varNameToken = default;
            try
            {
                _ = Advance();

                // var (a, b) = expr, tuple destructuring
                if (Check(TokenType.LeftParen))
                {
                    _ = Advance(); // consume '('
                    var destVars = new List<string>();
                    if (!Check(TokenType.RightParen))
                    {
                        do
                        {
                            var dName = ConsumeIdentifier("Expect variable name in destructuring.").Literal;
                            destVars.Add(dName);
                        } while (Match(TokenType.Comma));
                    }
                    _ = Consume(TokenType.RightParen, "Expect ')' after destructuring variables.");
                    _ = Consume(TokenType.Equal, "Expect '=' after destructuring.");
                    var destExpr = Expression();
                    _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");

                    // Build: DestructuringStmt(a, b, source)
                    return DestructuringStmt.Create(destVars, destExpr, null, false, destExpr.Line, destExpr.Column);
                }

                Match(TokenType.Question);
                if (Peek().Type is TokenType.Identifier or TokenType.Get or TokenType.Set or TokenType.Init
                    || (Peek().Type != TokenType.Semicolon && IsReservedKeyword(Peek().Literal)))
                {
                    varNameToken = ConsumeIdentifier("Expect variable name.");
                    if (Peek().Type == TokenType.Equal || Peek().Type == TokenType.Semicolon) isVarDecl = true;
                }
            }
            catch { _current = savePos; }

            if (isVarDecl)
            {
                var varType = new TypeInfo(TokenType.Object, true, 0, 0, null);
                if (Match(TokenType.Equal))
                {
                    var initializer = Expression();
                    // var x = <numeric literal>: keep the literal's type as the declared type so a
                    // later `x = <narrower numeric>` WIDENS instead of silently re-typing the variable
                    // (e.g. `var sum = 0L; sum = 0;` must keep sum long, WIDEN_NUM / VisitAssignExpr).
                    if (initializer is NumberExpr ne
                        && ne.Type.BaseType is TokenType.Long or TokenType.Double or TokenType.Decimal or TokenType.Float)
                        varType = ne.Type;
                    _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
                    return VarDeclStmt.Create(varNameToken.Literal, initializer, varType, false, varNameToken.WasEscaped, varNameToken.LineNumber, varNameToken.Column);
                }
                else
                {
                    _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
                    return VarDeclStmt.Create(varNameToken.Literal, null, varType, false, varNameToken.WasEscaped, varNameToken.LineNumber, varNameToken.Column);
                }
            }
            _current = savePos;
        }

        // const declaration
        if (Peek().Type == TokenType.Const && PeekAhead(1).Type == TokenType.Identifier && PeekAhead(2).Type == TokenType.Equal)
        {
            _ = Advance();
            var nameToken = ConsumeIdentifier("Expect constant name.");
            _ = Consume(TokenType.Equal, "Expect '=' after constant name.");
            var initializer = Expression();
            _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
            return VarDeclStmt.Create(nameToken.Literal, initializer, new TypeInfo(TokenType.Object, false, 0, 0, null), true, nameToken.WasEscaped, nameToken.LineNumber, nameToken.Column);
        }

        // Array destructuring: [a, b] = expr; or [a, *rest] = expr;
        if (!Check(TokenType.LeftBracket)) return Statement();
        {
            var save = _current;
            try
            {
                _ = Advance();
                var destVars = new List<(string Name, bool IsRest)>();
                if (!Check(TokenType.RightBracket))
                {
                    do
                    {
                        var isRest = Match(TokenType.Star);
                        var vName = ConsumeIdentifier("Expect variable name.").Literal;
                        destVars.Add((vName, isRest));
                    } while (Match(TokenType.Comma));
                }
                if (Check(TokenType.RightBracket))
                {
                    _ = Advance();
                    if (Match(TokenType.Equal))
                    {
                        var src = Expression();
                        _ = Consume(TokenType.Semicolon, "Expect ';' after destructuring.");
                        return DestructuringStmt.Create([.. destVars.Select(v => v.Name)], src, null, false, src.Line, src.Column);
                    }
                }
            }
            catch
            {
                // ignored
            }

            _current = save;
        }

        return Statement();
    }

    private Stmt? TryParseTypeDeclaration()
    {
        var save = _current;
        AccessModifier? access = null;
        if (Check(TokenType.Public)) { access = AccessModifier.Public; _ = Advance(); }
        else if (Check(TokenType.Private)) { access = AccessModifier.Private; _ = Advance(); }
        else if (Check(TokenType.Protected)) { access = AccessModifier.Protected; _ = Advance(); }
        else if (Check(TokenType.Internal)) { access = AccessModifier.Internal; _ = Advance(); }

        // `public static class Container`, the C# 14 home for extension blocks. The `static` modifier is
        // consumed only when a class or struct follows, so it cannot swallow a `static` that belongs to
        // something else. A TOP-LEVEL block needs no container at all; a block INSIDE a class requires that
        // class to be static (the C# rule), so the modifier is remembered and checked where the block is parsed.
        var isStaticClass = false;
        if (Check(TokenType.Static) && PeekAhead(1).Type is TokenType.Class or TokenType.Struct)
        {
            isStaticClass = true;
            _ = Advance();
        }

        var isReadonly = Match(TokenType.Readonly);
        // record: contextual keyword, 'record' identifier { ... } behaves as a class with structural equality
        var isRecord = false;
        if (!isReadonly && Check(TokenType.Identifier) && Peek().Literal.Equals("record", StringComparison.Ordinal)
            && PeekAhead(1).Type == TokenType.Identifier)
        {
            _ = Advance();
            isRecord = true;
        }
        if (Check(TokenType.Class)) { _ = Advance(); return ClassDeclaration(access ?? AccessModifier.Private, isRecord: isRecord, isStaticClass: isStaticClass); }
        // record Name { ... }, 'record' already consumed above; the class body follows the name
        if (isRecord)
            return ClassDeclaration(access ?? AccessModifier.Private, isRecord: true, isStaticClass: isStaticClass);
        if (Check(TokenType.Struct)) { _ = Advance(); return StructDeclaration(access ?? AccessModifier.Private, null, false, isReadonly); }
        // Contextual enum: "enum" as an identifier followed by a name
        if (Peek().Type == TokenType.Identifier && Peek().Literal.Equals("enum", StringComparison.Ordinal))
        { _ = Advance(); return EnumDeclaration(access ?? AccessModifier.Public); }

        if (!access.HasValue) return null;
        _current = save; return null;
    }

    private ImportStmt ImportStatement()
    {
        var line = Previous().LineNumber;
        var col = Previous().Column;
        // The path is an EXPRESSION: a plain literal (the common case, unchanged) or anything that yields one,
        // so a deployed script can name a module beside the executable without hard-coding an absolute path
        // (`import Application.Path + "/m.inc" as Z;`). `as` introduces the ALIAS here rather than a cast, which
        // is why the operator is suppressed for the path only: without that, `expression as Z` would parse as a
        // cast and the alias would vanish into the expression.
        var savedNoAs = _noAsOperator;
        _noAsOperator = true;
        Expr pathExpression;
        try { pathExpression = Expression(); }
        finally { _noAsOperator = savedNoAs; }
        string? alias = null;
        if (Match(TokenType.Alias) || Match(TokenType.As)) // both "import x alias ns" and "import x as ns"
            alias = ConsumeIdentifier("Expect alias name.").Literal;
        _ = Consume(TokenType.Semicolon, "Expect ';' after import.");
        return ImportStmt.Create(pathExpression, alias, line, col);
    }

    private Stmt NamespaceStatement()
    {
        var token = Consume(TokenType.Namespace, "Expect 'namespace'.");
        var name = ParseQualifiedIdentifier();      // "A.B", existing helper (Parser.cs:785)
        var segments = name.Split('.');
        if (Match(TokenType.Semicolon))
            throw Error("File-level 'namespace X;' is only allowed at the top level of a file.", token);
        _ = Consume(TokenType.LeftBrace, "Expect '{' after namespace name.");
        // Namespace bodies hold TOP-LEVEL declarations (functions, classes, structs, ...),
        // so parse each body statement with TopLevel(), Block() only accepts statements.
        var body = new List<Stmt>();
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            if (Match(TokenType.Semicolon)) continue;
            body.Add(TopLevel());
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after namespace body.");
        return NamespaceDeclStmt.Create(name, segments, body, token.LineNumber, token.Column);
    }

    private EventDecl ParseEventDecl(AccessModifier? accessOverride = null)
    {
        var access = accessOverride ?? ParseAccessModifier();
        var line = Previous().LineNumber;
        var col = Previous().Column;
        var delegateType = ConsumeIdentifier("Expect delegate type name.").Literal;
        var name = ConsumeIdentifier("Expect event name.");
        _ = Consume(TokenType.Semicolon, "Expect ';' after event declaration.");
        return EventDecl.Create(name.Literal, delegateType, access, line, col);
    }

    private DelegateDecl ParseDelegate(AccessModifier access = AccessModifier.Public)
    {
        var line = Previous().LineNumber;
        var col = Previous().Column;
        TypeInfo returnType;
        if (Peek().Type == TokenType.Void) { _ = Advance(); returnType = TypeInfo.Void; }
        else { returnType = ParseType(allowVoid: true); }
        var name = ConsumeIdentifier("Expect delegate name.");
        _ = Consume(TokenType.LeftParen, "Expect '(' after delegate name.");
        var (parameters, isRefs, isOuts, isParamsList, defaults, tupleElems, isIns) = ParseParameterList();
        if (tupleElems is { Count: > 0 } && tupleElems.Any(e => e is { Count: > 0 }))
            throw Error("Tuple parameters are not supported in this context.", Peek());
        _ = Consume(TokenType.RightParen, "Expect ')' after parameters.");
        _ = Consume(TokenType.Semicolon, "Expect ';' after delegate declaration.");
        var del = DelegateDecl.Create(name.Literal, returnType, parameters, isRefs, isOuts, access, line, col);
        del.IsInParams = isIns;
        return del;
    }

    // ─── Class / Struct ───────────────────────────────────────────────────

    /// <summary>True when the next tokens begin a NESTED type declaration: an optional access modifier, an
    /// optional <c>static</c>, then <c>class</c> or <c>struct</c>. `static` counts only when a type follows it,
    /// the same rule as at the top level, so a member declared `static` is never swallowed by this lookahead.
    /// A nested <c>static class</c> is the container C# gives an extension block, so it has to be declarable;
    /// this is the ONE place the class body and the struct body both ask.</summary>
    private bool IsNestedTypeStart()
    {
        var i = 0;
        if (PeekAhead(i).Type is TokenType.Public or TokenType.Private or TokenType.Protected or TokenType.Internal) i++;
        if (PeekAhead(i).Type == TokenType.Static) i++;
        return PeekAhead(i).Type is TokenType.Class or TokenType.Struct;
    }

    private ClassDeclStmt ClassDeclaration(AccessModifier access, string? outerName = null, bool isRecord = false, bool isStaticClass = false)
    {
        var line = Previous().LineNumber;
        var col = Previous().Column;
        var name = ConsumeIdentifier("Expect class name.");
        var typeParams = ParseTypeParameters();
        string? parentName = null;
        if (Match(TokenType.Extends)) parentName = ParseQualifiedIdentifier();
        // `implements I, J`: a contextual word after the optional base class, so a member or a field named
        // `implements` is unaffected.
        var interfaceNames = new List<string>();
        if (Peek().Type == TokenType.Identifier
            && Peek().Literal.Equals("implements", StringComparison.Ordinal))
        {
            _ = Advance();
            do { interfaceNames.Add(ParseTypeSpelling()); } while (Match(TokenType.Comma));
        }
        var qualifiedName = outerName != null ? $"{outerName}.{name.Literal}" : name.Literal;
        _ = Consume(TokenType.LeftBrace, "Expect '{' before class body.");

        var fields = new List<FieldDecl>();
        var methods = new List<FuncDecl>();
        var constructors = new List<ConstructorDecl>();
        var nestedClasses = new List<ClassDeclStmt>();
        var nestedStructs = new List<StructDeclStmt>();
        var events = new List<EventDecl>(4);
        var operatorMethods = new Dictionary<TokenType, FuncDecl>(4);
        var classDelegates = new List<DelegateDecl>();
        var properties = new List<PropertyDecl>();
        var extensions = new List<ExtensionDeclStmt>();
        FuncDecl? destructor = null;

        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            // A nested type declaration, one decision, shared with the struct body (IsNestedTypeStart). The access
            // modifier and `static` are both optional, and `static` is passed on: a nested `static class` is the
            // container C# gives an extension block, which the block check below then accepts.
            if (IsInterfaceDeclStart())
                throw Error("An interface is declared at the TOP level, not inside a class body: move "
                            + $"'{PeekAhead(1).Literal}' out, and have this class `implements` it.", Peek());

            if (IsNestedTypeStart())
            {
                var nestedAccess = TryConsumeAccessModifier() ?? AccessModifier.Private;
                var nestedStatic = Match(TokenType.Static);
                if (Check(TokenType.Class)) { _ = Advance(); nestedClasses.Add(ClassDeclaration(nestedAccess, qualifiedName, isStaticClass: nestedStatic)); }
                else { _ = Advance(); nestedStructs.Add(StructDeclaration(nestedAccess, qualifiedName)); }
                continue;
            }
            if (IsExtensionBlockStart())
            {
                // C# allows an extension block only in a STATIC class, and this language now says the same: a block
                // inside `class Holder` used to be accepted, with nothing that made it legal. (A TOP-LEVEL block
                // needs no container at all, and that path never reaches this method, so this rejects exactly the
                // container that is not static.)
                if (!isStaticClass)
                    throw Error("An extension block must be declared in a `static class`: declare this block at the top "
                                + "level (a top-level block needs no container), or inside a `static class` - top-level "
                                + "or nested. A container that is not static is not allowed.", Peek());
                extensions.Add(ParseExtensionDeclaration(qualifiedName));
                continue;
            }

            var isStatic = Match(TokenType.Static);
            var memberAccess = AccessModifier.Private;
            var hasModifier = false;
            var mod = TryConsumeAccessModifier();
            if (mod.HasValue) { memberAccess = mod.Value; hasModifier = true; }

            // Allow `static` to appear before or after the access modifier
            if (!isStatic) isStatic = Match(TokenType.Static);

            if (Match(TokenType.Delegate)) { classDelegates.Add(ParseDelegate(memberAccess)); continue; }
            if (Match(TokenType.Event)) { events.Add(ParseEventDecl(memberAccess)); continue; }

            if (Match(TokenType.Var))
            {
                var nullable = Match(TokenType.Question);
                var type = new TypeInfo(TokenType.Object, nullable, 0, 0, null);
                var memberName = ConsumeIdentifier("Expect member name.");
                Expr? initializer = null;
                if (Match(TokenType.Equal))
                {
                    initializer = Expression();
                    // Only warn when initializer is NOT a lambda, var is fine for functions
                    if (!(initializer is LambdaExpr))
                        Warn($"Field uses implicit type 'var'. Consider using explicit type for better clarity.", Previous());
                }
                fields.Add(new FieldDecl(memberAccess, type, memberName.Literal, initializer, isStatic));
                while (Match(TokenType.Comma))
                {
                    var nextName = ConsumeIdentifier("Expect field name.");
                    Expr? nextInit = null;
                    if (Match(TokenType.Equal)) nextInit = Expression();
                    fields.Add(new FieldDecl(memberAccess, type, nextName.Literal, nextInit, isStatic));
                }
                _ = Consume(TokenType.Semicolon, "Expect ';' after field declaration.");
                continue;
            }

            if (Match(TokenType.Constructor))
            {
                if (isStatic)
                {
                    // Static constructors cannot have access modifiers or parameters
                    if (hasModifier) throw Error("Static constructor cannot have an access modifier.", Previous());
                    var ctor = ParseConstructor(null);
                    if (ctor.Params.Count > 0) throw Error("Static constructor cannot have parameters.", Previous());
                    constructors.Add(ctor with { IsStatic = true });
                }
                else
                    constructors.Add(ParseConstructor(hasModifier ? memberAccess : null));
                continue;
            }
            if (Check(TokenType.Tilde)) { destructor = ParseDestructor(qualifiedName); continue; }

            // ClassName() constructor syntax, match class name followed by (
            if (Check(TokenType.Identifier) && Peek().Literal == name.Literal && PeekAhead(1).Type == TokenType.LeftParen)
            {
                _ = Advance();
                constructors.Add(ParseConstructor(hasModifier ? memberAccess : null));
                continue;
            }

            if (IsTypeStart(Peek()) || Peek().Type == TokenType.Void || Peek().Type == TokenType.Identifier || Peek().Type == TokenType.Pure
                || (Check(TokenType.Ref) && IsAtTypedDeclaration(_current + 1, TokenType.LeftParen)))
            {
                var isPure = Match(TokenType.Pure);
                var isAsyncMember = ConsumeMemberAsync();
                // A ref-RETURNING member: `public ref int Field() { return ref this.F; }`. The declared type names what
                // the reference POINTS AT, and the body answers the reference, exactly as a free function's does.
                var isRefReturnMember = Match(TokenType.Ref);
                var type = ParseType(allowVoid: true);
                // C#'s rule for an `async` member, the same one the free-function path applies (CS1983): `Task<T>` means
                // the body's result is T, `Task` means none, and anything else is refused by name; `async void` is
                // refused because this engine's async call always hands back an awaitable Task.
                if (isAsyncMember)
                {
                    if (type.BaseType == TokenType.Void)
                        throw Error("this engine has no unawaitable `async void`: an async body always hands back a Task here, so write `async Task`.", Previous());
                    if (type.TypeName is { } asyncMemberName && string.Equals(asyncMemberName, "Task", StringComparison.Ordinal))
                        type = type.GenericArgs is { Count: 1 } memberArgs ? memberArgs[0] : new TypeInfo(TokenType.Object, false, 0, 0, null);
                    else
                        throw Error("an `async` member must declare a `Task`, a `Task<T>` or `void`, as C# requires (CS1983): write `async Task<int>` for a body that returns an int.", Previous());
                }
                if (Check(TokenType.Operator))
                {
                    var opMethod = ParseOperatorOverloadWithReturnType(type, memberAccess);
                    operatorMethods[opMethod.OperatorToken!.Value] = opMethod;
                    continue;
                }
                var memberName = ConsumeIdentifier("Expect member name.");
                string? contractQualifier = null;
                if (Check(TokenType.Dot) && PeekAhead(1).Type == TokenType.Identifier)
                {
                    // `int IShape.area() { }` - an EXPLICIT implementation, qualified by its contract.
                    contractQualifier = memberName.Literal;
                    _ = Advance();
                    memberName = ConsumeIdentifier("Expect the member name after the contract qualifier.");
                }
                if (Check(TokenType.LeftParen) || Check(TokenType.Less))
                {
                    if (memberName.Literal == name.Literal)
                        throw Error("Method cannot have the same name as the class. Use 'constructor' or '" + name.Literal + "(...)' without a return type to declare a constructor.", memberName);
                    var method = FunctionDeclaration(memberName, type, memberAccess, isPure, isCoroutine: false, isAsync: isAsyncMember, isRefReturn: isRefReturnMember);
                    if (contractQualifier != null) method.Qualifier = contractQualifier;
                    if (!method.IsCoroutine && ContainsYield(method.Body)) method.IsCoroutine = true;
                    method.IsStatic = isStatic;
                    methods.Add(method);
                }
                else if (Check(TokenType.LeftBrace))
                {
                    // Property declaration: `Type Name { get; set; }`
                    var prop = ParsePropertyDeclaration(type, memberName.Literal, memberAccess, isStatic);
                    properties.Add(prop);
                    // Consume optional stray ';' after property declarations
                    if (!Check(TokenType.Semicolon)) continue;
                    var semi = Peek();
                    Logging.Log.Warning("Property", "Unexpected ';' after property declaration.", semi.LineNumber, semi.Column);
                    _ = Advance();
                }
                else
                {
                    var fieldNames = new List<string> { memberName.Literal };
                    var fieldInits = new Dictionary<string, Expr?>(_nameComparer);

                    // Optional initializer on the first field: int x = 1, ...
                    if (Match(TokenType.Equal))
                        fieldInits[memberName.Literal] = Expression();

                    while (Match(TokenType.Comma))
                    {
                        var nextName = ConsumeIdentifier("Expect field name.");
                        fieldNames.Add(nextName.Literal);
                        // Optional per-field initializer: int x = 1, y = 2, z;
                        if (Match(TokenType.Equal))
                            fieldInits[nextName.Literal] = Expression();
                    }
                    _ = Consume(TokenType.Semicolon, "Expect ';' after field declaration.");
                    fields.AddRange(fieldNames.Select(fName
                        => new FieldDecl(memberAccess, type, fName, fieldInits.GetValueOrDefault(fName), isStatic)));
                }
            }
            else if (hasModifier) throw Error("Expected type after access modifier.", Peek());
            else throw Error("Expected class, event, constructor, method, field, or operator declaration.", Peek());
        }

        _ = Consume(TokenType.RightBrace, "Expect '}' after class body.");
        var classStmt = ClassDeclStmt.Create(qualifiedName, parentName, fields, methods, constructors, nestedClasses, events,
            new ReadOnlyDictionary<TokenType, FuncDecl>(operatorMethods), access, line, col, typeParams, destructor, classDelegates, properties, nestedStructs, extensions, interfaceNames);
        classStmt.IsRecord = isRecord;
        return classStmt;
    }

    /// <summary>True when the next tokens start an `extension` block. The word is CONTEXTUAL, like `enum`:
    /// it is a declaration keyword only where a declaration may start, so scripts can still use
    /// `extension` as a plain identifier.</summary>
    /// <summary>A contextual `interface` start, the way `extension` is contextual: the word must be followed by
    /// another identifier, so a script that uses `interface` as a name keeps working.</summary>
    private bool IsInterfaceDeclStart() =>
        Peek().Type == TokenType.Identifier
        && Peek().Literal.Equals("interface", StringComparison.Ordinal)
        && PeekAhead(1).Type == TokenType.Identifier;

    private bool IsExtensionBlockStart() =>
        Peek().Type == TokenType.Identifier
        && Peek().Literal.Equals("extension", StringComparison.Ordinal)
        && PeekAhead(1).Type is TokenType.Less or TokenType.LeftParen;

    /// <summary>Parses `interface Name [extends A, B] { Type Member(args); Type Member(args) { default } }`.
    /// An interface owns no storage, so only methods are accepted, and a member without a body is abstract while
    /// one with a body is a DEFAULT: the implementation a class inherits when it does not write its own.</summary>
    /// <summary>A type as a script WRITES it, which is what the interface registry is keyed by: `IShape`, or
    /// `I<int>` for an instantiated generic contract. TypeInfo.Spelling renders it, so the parser and the engine
    /// cannot spell one type two ways.</summary>
    private string ParseTypeSpelling() => TypeInfo.Spelling(ParseType(allowVoid: false));
    private InterfaceDeclStmt ParseInterfaceDeclaration()
    {
        var start = Advance();   // `interface`
        var name = ConsumeIdentifier("Expect the interface name.");
        var interfaceTypeParams = new List<string>();
        if (Match(TokenType.Less))
        {
            do { interfaceTypeParams.Add(ConsumeIdentifier("Expect a type parameter name.").Literal); }
            while (Match(TokenType.Comma));
            ConsumeGenericClose("Expect '>' after the interface's type parameters.");
        }
        var parents = new List<string>();
        if (Match(TokenType.Extends))
        {
            do { parents.Add(ParseTypeSpelling()); } while (Match(TokenType.Comma));
        }
        _ = Consume(TokenType.LeftBrace, "Expect '{' before the interface body.");

        var members = new List<(string Name, int Arity)>();
        var defaults = new Dictionary<string, FuncDecl>(_nameComparer);
        var interfaceProperties = new List<InterfacePropertySignature>();
        var seen = new HashSet<string>(_nameComparer);
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            if (!IsTypeStart(Peek()) && Peek().Type is not (TokenType.Void or TokenType.Identifier or TokenType.Pure))
                throw Error("Expected a method or a property in the interface body. An interface declares "
                            + "METHODS and PROPERTIES, so a field or a nested type cannot be a member of one, and a " + "property is written with accessors (`{ get; }`) rather than as a field.", Peek());

            RefuseAsyncMember();
            var isPure = Match(TokenType.Pure);
            var returnType = ParseType(allowVoid: true);
            var memberName = ConsumeIdentifier("Expect the interface member's name.");
            string? contractQualifier = null;
            if (Check(TokenType.Dot) && PeekAhead(1).Type == TokenType.Identifier)
            {
                // `string IBase.M() { ... }` - an OVERRIDE of an ancestor's default, named by its contract.
                contractQualifier = memberName.Literal;
                _ = Advance();
                memberName = ConsumeIdentifier("Expect the member name after the contract qualifier.");
            }
            // A PROPERTY the interface pins. The abstract form (`{ get; }`, `{ get; set; }`) says which accessors
            // a member of that name must have; a body (`=> expr;` or `{ get => ...; }`) is a DEFAULT the engine
            // supplies, served through the same miss path a method default uses.
            if (Check(TokenType.Arrow) || Check(TokenType.LeftBrace))
            {
                var propertyName = memberName;
                bool hasGetter = false, hasSetter = false;
                FuncDecl? getterBody = null, setterBody = null;
                if (Match(TokenType.Arrow))
                {
                    hasGetter = true;
                    var expressionBody = new List<Stmt>
                    {
                        ReturnStmt.Create(Expression(), propertyName.LineNumber, propertyName.Column)
                    };
                    _ = Consume(TokenType.Semicolon, "Expect ';' after the property's expression.");
                    getterBody = ParseAccessor(propertyName, "get", returnType, null, expressionBody);
                }
                else
                {
                    _ = Consume(TokenType.LeftBrace, "Expect '{' before the property's accessors.");
                    while (!Check(TokenType.RightBrace) && !IsAtEnd())
                    {
                        if (Match(TokenType.Get))
                        {
                            if (hasGetter) throw Error("Duplicate 'get' accessor.", Previous());
                            hasGetter = true;
                            if (Check(TokenType.Semicolon)) _ = Advance();   // abstract: the implementer provides it
                            else getterBody = ParseAccessorBody(propertyName, "get", returnType);
                        }
                        else if (Match(TokenType.Set))
                        {
                            if (hasSetter) throw Error("Duplicate 'set' accessor.", Previous());
                            hasSetter = true;
                            if (Check(TokenType.Semicolon)) _ = Advance();
                            else
                            {
                                setterBody = ParseAccessorBody(propertyName, "set", returnType);
                                throw Error("A default SETTER is not served yet: this language reaches a property "
                                            + "through its NAME on the value, so a body here could never answer. "
                                            + "Declare `set;` and let the implementing member own the write, or "
                                            + "make the property read-only.", propertyName);
                            }
                        }
                        else throw Error("Expected 'get' or 'set' in the property's accessors.", Peek());
                    }
                    _ = Consume(TokenType.RightBrace, "Expect '}' after the property's accessors.");
                }
                if (!hasGetter && !hasSetter)
                    throw Error("A property needs at least one accessor.", propertyName);
                interfaceProperties.Add(new InterfacePropertySignature(propertyName.Literal, returnType, hasGetter,
                    hasSetter, getterBody, setterBody));
                continue;
            }
            if (!Check(TokenType.LeftParen) && !Check(TokenType.Less))
                throw Error("Expected '(' after the name of a method, or '=>' or '{' for a property: an interface "
                            + "declares methods and properties, and a member may carry a DEFAULT body.", memberName);

            _ = Consume(TokenType.LeftParen, "Expect '(' before the member's parameters.");
            var (parameters, isRefs, isOuts, isParamsList, defaultsList, tupleElems, isIns) = ParseParameterList();
            _ = Consume(TokenType.RightParen, "Expect ')' after the member's parameters.");

            var key = memberName.Literal + "/" + parameters.Count;
            if (!seen.Add(key))
                throw Error($"The interface already declares '{key}'. An interface pins a name and an arity, so a "
                            + "second member of that shape is a duplicate.", memberName);
            members.Add((memberName.Literal, parameters.Count));

            if (Check(TokenType.Semicolon))
            {
                if (contractQualifier != null)
                    throw Error($"An override needs a BODY, so write `{contractQualifier}.{memberName.Literal}(...) "
                                + "{ ... }`. To make an inherited member abstract again, re-declare it UNQUALIFIED "
                                + $"(`{memberName.Literal}(...);`), which is what a plain re-declaration does.", memberName);
                _ = Advance();
                continue;   // abstract: every implementing class must provide it
            }

            IReadOnlyList<Stmt> body;
            if (Match(TokenType.Arrow))
            {
                body = new List<Stmt> { ReturnStmt.Create(Expression(), memberName.LineNumber, memberName.Column) };
                _ = Consume(TokenType.Semicolon, "Expect ';' after the default body's expression.");
            }
            else
            {
                Consume(TokenType.LeftBrace, "Expect ';' for an abstract member, or '=> expr;' or '{ ... }' for a "
                                             + "member with a DEFAULT body.");
                body = Block();
            }
            var defaultBody = FuncDecl.Create(memberName.Literal, parameters, isRefs, isParamsList, body, returnType,
                null, AccessModifier.Public, memberName.LineNumber, memberName.Column, typeParameters: null, isInParams: isIns);
            if (contractQualifier != null) defaultBody.Qualifier = contractQualifier;
            defaults[key] = defaultBody;
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after the interface body.");
        return InterfaceDeclStmt.Create(name.Literal, parents, members, defaults, interfaceProperties, interfaceTypeParams,
            start.LineNumber, start.Column);
    }
    /// <summary>Parses `extension [&lt;T&gt;] (Target) { members }`. `containerName` is the enclosing static
    /// class, or null for a top-level block.</summary>
    private ExtensionDeclStmt ParseExtensionDeclaration(string? containerName)
    {
        var start = Advance(); // `extension`
        var typeParameters = new List<string>();
        if (Match(TokenType.Less))
        {
            do { typeParameters.Add(ConsumeIdentifier("Expect a type parameter name.").Literal); }
            while (Match(TokenType.Comma));
            ConsumeGenericClose("Expect '>' after the extension block's type parameters.");
        }

        _ = Consume(TokenType.LeftParen, "Expect '(' after 'extension' - the extended type goes here.");
        var target = ParseType(allowVoid: false);
        // C# 14 NAMES the receiver (`extension(string s)`); an unnamed one writes just the type. Only an
        // identifier directly before the closing `)` is a name, so nothing else can be swallowed by this.
        string? receiverName = null;
        if (Check(TokenType.Identifier) && _current + 1 < _tokens.Count
            && _tokens[_current + 1].Type == TokenType.RightParen)
            receiverName = Advance().Literal;
        _ = Consume(TokenType.RightParen, "Expect ')' after the extended type.");
        _ = Consume(TokenType.LeftBrace, "Expect '{' to open the extension body.");

        var members = new List<FuncDecl>();
        var properties = new List<ExtensionPropertyDecl>();
        var operators = new List<FuncDecl>();
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            var isStatic = Match(TokenType.Static);
            var access = TryConsumeAccessModifier() ?? AccessModifier.Private;
            if (!isStatic) isStatic = Match(TokenType.Static); // `static` before or after the modifier

            if (!IsTypeStart(Peek()) && Peek().Type is not (TokenType.Void or TokenType.Identifier or TokenType.Pure))
                throw Error("Expected a method declaration in the extension body. Extension members are methods, "
                            + "so each one needs a return type and a body.", Peek());

            var isPureExt = Match(TokenType.Pure);
            var isAsyncExt = ConsumeMemberAsync();
            var returnType = ParseType(allowVoid: true);
            // An OPERATOR has no name to consume, so it is dispatched before the identifier, and it is always
            // static: an extension block adds no receiver for it to sit on.
            if (Check(TokenType.Operator))
            {
                operators.Add(ParseExtensionOperator(returnType, access, isStatic, Previous()));
                continue;
            }
            var memberName = ConsumeIdentifier("Expect the extension member's name.");
            if (!Check(TokenType.LeftParen) && !Check(TokenType.Less))
            {
                // A property: `public int Len => expr;` or `public int Len { get => …; set { … } }`. Anything
                // else after the name is neither, so the message says what is missing rather than guessing.
                if (Check(TokenType.Arrow) || Check(TokenType.LeftBrace))
                {
                    properties.Add(ParseExtensionProperty(memberName, returnType, access, isStatic));
                    continue;
                }
                throw Error("Expected '(' after the extension member's name, or a property body: a member is a "
                            + "method, `Name(args) => …`, or a computed property, `Name => …` / `Name { get => …; "
                            + "set { … } }`.", memberName);
            }
            var method = FunctionDeclaration(memberName, returnType, access, isPureExt, isCoroutine: false, isAsync: isAsyncExt);
            if (!method.IsCoroutine && ContainsYield(method.Body)) method.IsCoroutine = true;
            method.IsStatic = isStatic;
            members.Add(method);
        }

        _ = Consume(TokenType.RightBrace, "Expect '}' after the extension body.");
        return ExtensionDeclStmt.Create(typeParameters, target, members, containerName, start.LineNumber, start.Column,
            receiverName, properties, operators);
    }

    /// <summary>An OPERATOR declared by an extension block: `public static Point operator +(Point a, Point b)`.
    /// It takes BOTH operands, unlike a class operator (whose left operand is the receiver), and it must be
    /// `static` because a block has no receiver to sit on. Only the binary shape is accepted: a one-parameter
    /// declaration is refused here, with that reason, rather than registered as a block that would never fire.</summary>
    private FuncDecl ParseExtensionOperator(TypeInfo returnType, AccessModifier access, bool isStatic, Token typeEnd)
    {
        if (!isStatic)
            throw Error("An extension operator is `static`, because the block adds no receiver for it to sit on. "
                        + "Write `public static " + (returnType.TypeName ?? "T") + " operator ...`.", typeEnd);

        var operatorKeyword = Consume(TokenType.Operator, "Expect 'operator'.");
        var opToken = ConsumeAnyOperatorToken("Expect an operator token (+, -, *, /, %, <, >, <=, >=, ==, !=).");
        _ = Consume(TokenType.LeftParen, "Expect '(' after the operator.");
        var (parameters, isRefs, isOuts, isParamsList, defaults, tupleElems, isIns) = ParseParameterList();
        if (tupleElems is { Count: > 0 } && tupleElems.Any(e => e is { Count: > 0 }))
            throw Error("Tuple parameters are not supported in an operator.", Peek());
        if (parameters.Count != 2)
            throw Error($"An extension operator takes BOTH operands, so `operator {opToken.Literal}` needs two "
                        + "parameters. A unary operator is not supported on an extension block: write a method "
                        + "instead.", Peek());
        if (isRefs.Count > 0 && isRefs.Any(b => b))
            throw Error("Operator parameters cannot be ref or out.", Peek());
        _ = Consume(TokenType.RightParen, "Expect ')' after the operands.");
        _ = Consume(TokenType.LeftBrace, "Expect '{' before the operator's body.");
        var body = Block();
        var declared = FuncDecl.Create($"op_{opToken.Type}", parameters, isRefs, isParamsList, body, returnType,
            opToken.Type, access, operatorKeyword.LineNumber, operatorKeyword.Column, typeParameters: null, isInParams: isIns);
        declared.IsStatic = true;
        declared.HasYield = ContainsYield(body);
        return declared;
    }

    /// <summary>One COMPUTED property of an extension block, from `Name` onwards: `=> expr;` or
    /// `{ get => …; set { … } }`. An accessor with no body (`get;`) needs storage this block cannot have, so it is
    /// refused HERE, at its own token, with the reason and the two bodies that work, a registration-time refusal
    /// would say the same thing from the wrong place.</summary>
    private ExtensionPropertyDecl ParseExtensionProperty(Token name, TypeInfo type, AccessModifier access, bool isStatic)
    {
        if (Match(TokenType.Arrow))
        {
            var body = new List<Stmt> { ReturnStmt.Create(Expression(), name.LineNumber, name.Column) };
            _ = Consume(TokenType.Semicolon, "Expect ';' after the property's expression.");
            var arrowGetter = ParseAccessor(name, "get", type, null, body);
            FinishAccessor(arrowGetter, isStatic);
            return ExtensionPropertyDecl.Create(name.Literal, type, access, isStatic,
                arrowGetter, null, name.LineNumber, name.Column);
        }

        _ = Consume(TokenType.LeftBrace, "Expect '{' before property accessors.");
        FuncDecl? getter = null;
        FuncDecl? setter = null;
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            if (Match(TokenType.Get))
            {
                if (getter != null) throw Error("Duplicate 'get' accessor.", Previous());
                getter = ParseAccessorBody(name, "get", type);
            }
            else if (Match(TokenType.Set))
            {
                if (setter != null) throw Error("Duplicate 'set' accessor.", Previous());
                setter = ParseAccessorBody(name, "set", type);
            }
            else throw Error("Expected a 'get' or 'set' accessor. An extension property has no 'init' accessor: "
                             + "there is no storage to initialize.", Peek());
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after property accessors.");
        if (getter == null && setter == null)
            throw Error("Property must have at least one accessor.", Previous());
        FinishAccessor(getter, isStatic);
        FinishAccessor(setter, isStatic);
        return ExtensionPropertyDecl.Create(name.Literal, type, access, isStatic, getter, setter,
            name.LineNumber, name.Column);
    }

    /// <summary>An accessor's body: `=> expr;` or `{ … }`. `get;` / `set;` is refused with the reason, because an
    /// extension property has no backing field to read or write.</summary>
    private FuncDecl ParseAccessorBody(Token name, string kind, TypeInfo type)
    {
        if (Check(TokenType.Semicolon))
            throw Error($"An extension property cannot be `{kind};`, an extension block adds no storage, so every "
                        + $"accessor needs a body: `{kind} => expr;` or `{kind} {{ … }}`.", Peek());

        IReadOnlyList<Stmt> body;
        if (Match(TokenType.Arrow))
        {
            body = new List<Stmt> { ReturnStmt.Create(Expression(), name.LineNumber, name.Column) };
            _ = Consume(TokenType.Semicolon, "Expect ';' after the accessor's expression.");
        }
        else
        {
            _ = Consume(TokenType.LeftBrace, "Expect '=> expr;' or '{ … }' after the accessor name.");
            body = Block();
        }
        return ParseAccessor(name, kind, type, kind == "set" ? "value" : null, body);
    }

    /// <summary>The accessor as the member path's own FuncDecl: a getter takes no parameters and returns the
    /// property's type, a setter takes one parameter named `value` of that type and returns nothing.</summary>
    private static FuncDecl ParseAccessor(Token name, string kind, TypeInfo type, string? parameterName,
        IReadOnlyList<Stmt> body)
    {
        var parameters = parameterName == null
            ? new List<(TypeInfo Type, string Name)>()
            : [(type, parameterName)];
        var flags = parameters.Select(_ => false).ToList();
        var returns = kind == "get" ? type : new TypeInfo(TokenType.Void, false, name.LineNumber, name.Column, null);
        return FuncDecl.Create(kind + "_" + name.Literal, parameters, flags, [], body, returns, null,
            AccessModifier.Public, name.LineNumber, name.Column, [], flags);
    }

    /// <summary>The accessor carries the PROPERTY's half and its coroutine-ness: the compiler picks a method's
    /// context from `IsStatic`, and a body that yields must be wrapped like a coroutine member rather than run
    /// flat (the same two flags the method path sets on a member).</summary>
    private static void FinishAccessor(FuncDecl? accessor, bool isStatic)
    {
        if (accessor == null) return;
        accessor.IsStatic = isStatic;
        if (!accessor.IsCoroutine && ContainsYield(accessor.Body)) accessor.IsCoroutine = true;
    }

    private PropertyDecl ParsePropertyDeclaration(TypeInfo type, string name, AccessModifier declAccess, bool isStatic)
    {
        _ = Consume(TokenType.LeftBrace, "Expect '{' before property accessors.");
        AccessorDecl? getter = null;
        AccessorDecl? setter = null;

        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            // Optional per-accessor access modifier
            var accAccess = TryConsumeAccessModifier() ?? declAccess;

            if (Match(TokenType.Get))
            {
                if (getter != null) throw Error("Duplicate 'get' accessor.", Previous());
                _ = Consume(TokenType.Semicolon, "Expect ';' after 'get'.");
                getter = new AccessorDecl(accAccess, false);
            }
            else if (Match(TokenType.Set))
            {
                if (setter != null) throw Error("Duplicate 'set' accessor.", Previous());
                _ = Consume(TokenType.Semicolon, "Expect ';' after 'set'.");
                setter = new AccessorDecl(accAccess, false);
            }
            else if (Match(TokenType.Init))
            {
                if (setter != null) throw Error("Duplicate 'init' accessor. 'init' is a flavor of 'set'.", Previous());
                _ = Consume(TokenType.Semicolon, "Expect ';' after 'init'.");
                setter = new AccessorDecl(accAccess, true);
            }
            else throw Error("Expected 'get', 'set', or 'init' accessor.", Peek());
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after property accessors.");
        if (getter == null && setter == null) throw Error("Property must have at least one accessor.", Previous());
        Expr? initializer = null;
        if (!Match(TokenType.Equal))
            return PropertyDecl.Create(name, type, declAccess, isStatic, getter, setter, initializer,
                Peek().LineNumber, Peek().Column);

        initializer = Expression(); _ = Consume(TokenType.Semicolon, "Expect ';' after property initializer.");
        return PropertyDecl.Create(name, type, declAccess, isStatic, getter, setter, initializer,
            Peek().LineNumber, Peek().Column);
    }

    private StructDeclStmt StructDeclaration(AccessModifier access, string? parentName = null, bool isNested = false, bool isReadonly = false)
    {
        var line = Previous().LineNumber;
        var col = Previous().Column;
        var name = ConsumeIdentifier("Expect struct name.");
        var qualifiedName = parentName != null ? $"{parentName}.{name.Literal}" : name.Literal;
        var typeParams = ParseTypeParameters();
        // `implements I, J`: contextual, so a field or a member named `implements` is unaffected.
        var structInterfaces = new List<string>();
        if (Peek().Type == TokenType.Identifier
            && Peek().Literal.Equals("implements", StringComparison.Ordinal))
        {
            _ = Advance();
            do { structInterfaces.Add(ParseTypeSpelling()); } while (Match(TokenType.Comma));
        }
        _ = Consume(TokenType.LeftBrace, "Expect '{' before struct fields.");
        var fields = new List<FieldDecl>();
        var methods = new List<FuncDecl>();
        var constructors = new List<ConstructorDecl>();
        var operatorMethods = new Dictionary<TokenType, FuncDecl>(4);
        var properties = new List<PropertyDecl>();

        // Nested class/struct tracking for structs
        var nestedClasses = new List<ClassDeclStmt>();
        var nestedStructs = new List<StructDeclStmt>();

        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            var isStatic = Match(TokenType.Static);
            var memberAccess = AccessModifier.Private;
            var hasAccMod = false;
            var mod3 = TryConsumeAccessModifier();
            if (mod3.HasValue) { memberAccess = mod3.Value; hasAccMod = true; }
            if (!isStatic) isStatic = Match(TokenType.Static);

            if (Match(TokenType.Constructor)) { constructors.Add(ParseConstructor(hasAccMod ? memberAccess : null)); continue; }

            // ClassName() constructor syntax for structs
            if (Check(TokenType.Identifier) && Peek().Literal == name.Literal && PeekAhead(1).Type == TokenType.LeftParen)
            {
                _ = Advance();
                constructors.Add(ParseConstructor(hasAccMod ? memberAccess : null));
                continue;
            }

            // Nested class/struct inside a struct, the same single decision the class body uses, so `public static
            // class Inner` is declarable in here too. A nested CLASS keeps taking the struct's own name as its outer
            // name (as it always did here); a nested struct keeps the qualified name.
            if (IsInterfaceDeclStart())
                throw Error("An interface is declared at the TOP level, not inside a class body: move "
                            + $"'{PeekAhead(1).Literal}' out, and have this class `implements` it.", Peek());

            if (IsNestedTypeStart())
            {
                var nestedAccess = TryConsumeAccessModifier() ?? AccessModifier.Private;
                var nestedStatic = Match(TokenType.Static);
                if (Check(TokenType.Class)) { _ = Advance(); nestedClasses.Add(ClassDeclaration(nestedAccess, name.Literal, isStaticClass: nestedStatic)); }
                else { _ = Advance(); nestedStructs.Add(StructDeclaration(nestedAccess, qualifiedName)); }
                continue;
            }

            if (Match(TokenType.Var))
            {
                var nullable = Match(TokenType.Question);
                var type = new TypeInfo(TokenType.Object, nullable, 0, 0, null);
                var memberName = ConsumeIdentifier("Expect member name.");
                Expr? initializer = null;
                if (Match(TokenType.Equal))
                {
                    initializer = Expression();
                    if (!(initializer is LambdaExpr))
                        Warn($"Field uses implicit type 'var'. Consider using explicit type for better clarity.", Previous());
                }
                fields.Add(new FieldDecl(memberAccess, type, memberName.Literal, initializer, isStatic));
                while (Match(TokenType.Comma))
                {
                    var nextName = ConsumeIdentifier("Expect field name.");
                    Expr? nextInit = null;
                    if (Match(TokenType.Equal)) nextInit = Expression();
                    fields.Add(new FieldDecl(memberAccess, type, nextName.Literal, nextInit));
                }
                _ = Consume(TokenType.Semicolon, "Expect ';' after field declaration.");
                continue;
            }

            if (!IsTypeStart(Peek()) && Peek().Type != TokenType.Void && Peek().Type != TokenType.Identifier &&
                Peek().Type != TokenType.Pure && !(Check(TokenType.Ref) && IsAtTypedDeclaration(_current + 1, TokenType.LeftParen)))
                throw Error("Expected class member, method, or constructor.", Peek());
            {
                var isPure = Match(TokenType.Pure);
                var isAsyncMember = ConsumeMemberAsync();
                var isRefReturnMember = Match(TokenType.Ref);
                var type = ParseType(allowVoid: true);
                // C#'s rule for an `async` member, the same one the free-function path applies (CS1983): `Task<T>` means
                // the body's result is T, `Task` means none, and anything else is refused by name; `async void` is
                // refused because this engine's async call always hands back an awaitable Task.
                if (isAsyncMember)
                {
                    if (type.BaseType == TokenType.Void)
                        throw Error("this engine has no unawaitable `async void`: an async body always hands back a Task here, so write `async Task`.", Previous());
                    if (type.TypeName is { } asyncMemberName && string.Equals(asyncMemberName, "Task", StringComparison.Ordinal))
                        type = type.GenericArgs is { Count: 1 } memberArgs ? memberArgs[0] : new TypeInfo(TokenType.Object, false, 0, 0, null);
                    else
                        throw Error("an `async` member must declare a `Task`, a `Task<T>` or `void`, as C# requires (CS1983): write `async Task<int>` for a body that returns an int.", Previous());
                }
                if (Check(TokenType.Operator))
                {
                    var opMethod = ParseOperatorOverloadWithReturnType(type, memberAccess);
                    operatorMethods[opMethod.OperatorToken!.Value] = opMethod;
                    continue;
                }
                var memberName = ConsumeIdentifier("Expect member name.");
                string? contractQualifier = null;
                if (Check(TokenType.Dot) && PeekAhead(1).Type == TokenType.Identifier)
                {
                    // `int IShape.area() { }` - an EXPLICIT implementation, qualified by its contract.
                    contractQualifier = memberName.Literal;
                    _ = Advance();
                    memberName = ConsumeIdentifier("Expect the member name after the contract qualifier.");
                }
                if (type is { BaseType: TokenType.Object, TypeName: null, GenericArgs: null })
                    Warn($"Field '{memberName.Literal}' uses implicit type 'var'. Consider using explicit type for better clarity.", memberName);
                if (Check(TokenType.LeftParen))
                {
                    if (memberName.Literal == name.Literal)
                        throw Error("Method cannot have the same name as the struct. Use 'constructor' or '" + name.Literal + "(...)' without a return type to declare a constructor.", memberName);
                    var method = FunctionDeclaration(memberName, type, memberAccess, isPure, isCoroutine: false, isAsync: isAsyncMember, isRefReturn: isRefReturnMember);
                    if (contractQualifier != null) method.Qualifier = contractQualifier;
                    if (!method.IsCoroutine && ContainsYield(method.Body)) method.IsCoroutine = true;
                    method.IsStatic = isStatic;
                    methods.Add(method);
                }
                else if (Check(TokenType.LeftBrace))
                {
                    var prop = ParsePropertyDeclaration(type, memberName.Literal, memberAccess, false);
                    properties.Add(prop);
                    if (Check(TokenType.Semicolon))
                        Warn("Unexpected ';' after property declaration.", Peek());
                }
                else
                {
                    var fieldNames = new List<string> { memberName.Literal };
                    while (Match(TokenType.Comma))
                    {
                        var nextName = ConsumeIdentifier("Expect field name.");
                        fieldNames.Add(nextName.Literal);
                    }
                    Expr? initializer = null;
                    if (Match(TokenType.Equal)) initializer = Expression();
                    _ = Consume(TokenType.Semicolon, "Expect ';' after field.");
                    fields.AddRange(fieldNames.Select(fName => new FieldDecl(memberAccess, type, fName, initializer, isStatic)));
                }
            }
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after struct body.");
        var ret = StructDeclStmt.Create(qualifiedName, fields, access, line, col, typeParams);
        ret.Methods = methods;
        ret.Interfaces = structInterfaces;
        ret.Constructors = constructors;
        ret.Properties = properties;
        ret.NestedClasses = nestedClasses;
        ret.NestedStructs = nestedStructs;
        ret.IsReadOnly = isReadonly;
        if (operatorMethods.Count > 0)
            ret.OperatorMethods = new ReadOnlyDictionary<TokenType, FuncDecl>(operatorMethods);
        return ret;
    }

    private Stmt EnumDeclaration(AccessModifier access)
    {
        var line = Previous().LineNumber;
        var col = Previous().Column;
        var name = ConsumeIdentifier("Expect enum name.");
        _ = Consume(TokenType.LeftBrace, "Expect '{' after enum name.");
        var members = new List<(string Name, Expr? Init)>();
        if (!Check(TokenType.RightBrace))
        {
            do
            {
                var memberName = ConsumeIdentifier("Expect enum member name.");
                Expr? init = null;
                if (Match(TokenType.Equal))
                    init = Expression();
                members.Add((memberName.Literal, init));
            } while (Match(TokenType.Comma));
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after enum body.");
        return EnumDeclStmt.Create(name.Literal, members, access, line, col);
    }

    private string ParseQualifiedIdentifier()
    {
        var builder = new StringBuilder();
        builder.Append(ConsumeIdentifier("Expect identifier.").Literal);
        while (Match(TokenType.Dot))
            builder.Append('.').Append(ConsumeMemberIdentifier("Expect identifier after '.'.").Literal);
        return builder.ToString();
    }

    /// <summary>Scans forward from _current to find the end of a type, without consuming tokens. Returns the index after the type, or -1.</summary>
    private int SafeParseTypeEnd()
    {
        var pos = _current;
        if (pos >= _tokens.Count) return -1;
        var t = _tokens[pos].Type;

        // Base type keyword
        if (TypeTokenHelper.IsBaseType(t) || t == TokenType.Void)
        {
            pos++;
            // optional ?
            if (pos < _tokens.Count && _tokens[pos].Type == TokenType.Question) pos++;
            // optional [] or [,]
            while (pos < _tokens.Count && _tokens[pos].Type == TokenType.LeftBracket)
            {
                pos++;
                if (pos < _tokens.Count && _tokens[pos].Type == TokenType.Comma) { while (pos < _tokens.Count && _tokens[pos].Type == TokenType.Comma) pos++; }
                if (pos < _tokens.Count && _tokens[pos].Type == TokenType.RightBracket) pos++;
                else return -1;
            }
            return pos;
        }

        // Identifier (could be a type name, or could be a function name being detected)
        if (t == TokenType.Identifier)
        {
            pos++; // consume identifier
            // optional .Identifier
            while (pos < _tokens.Count && _tokens[pos].Type == TokenType.Dot)
            {
                pos++;
                if (pos < _tokens.Count && _tokens[pos].Type == TokenType.Identifier) pos++;
                else return -1;
            }
            var afterParts = pos;
            // Optional generic args '<' Type (',' Type)* '>' (nested, Predicate<T>,
            // Dictionary<object, List<int>>) and array suffixes ('[]'/'[,]', repeated), mirroring
            // ParseType, so 'T[] Filter', 'List<T> Filter', 'Dictionary<K,V> Filter' are detected
            // as function declarations. LOOKAHEAD ONLY: on ANY failure mid-consume we roll back to
            // the pre-generic/pre-bracket position and return the plain-identifier position
            // (matching the prior behavior). The token at the returned position must be the
            // FUNCTION NAME identifier for the caller at L220-224 to detect the declaration.
            if (pos < _tokens.Count && _tokens[pos].Type == TokenType.Less)
            {
                var genericEnd = TrySkipGenericArgs(pos, out _);
                if (genericEnd > pos) pos = genericEnd;
                else return afterParts;
            }
            else
            {
                // optional ?
                if (pos < _tokens.Count && _tokens[pos].Type == TokenType.Question) pos++;
                // optional [] or [,], repeated
                while (pos < _tokens.Count && _tokens[pos].Type == TokenType.LeftBracket)
                {
                    pos++;
                    if (pos < _tokens.Count && _tokens[pos].Type == TokenType.Comma)
                        while (pos < _tokens.Count && _tokens[pos].Type == TokenType.Comma) pos++;
                    if (pos < _tokens.Count && _tokens[pos].Type == TokenType.RightBracket) pos++;
                    else return afterParts;
                }
            }
            return pos;
        }

        return -1;
    }

    /// <summary>Lookahead-only: skips a full type (base keyword, or identifier with optional .parts,
    /// optional nested generic args, optional '?', optional '[]'/'[,]' suffixes) starting at token
    /// index pos. Returns the index after the type, or -1 on failure. <paramref name="carryOver"/>
    /// receives 1 when a collapsed '>>' (RightShift) supplied this type's closing '>', meaning the
    /// enclosing generic level is already closed too.</summary>
    private int SkipTypeTokens(int pos, out int carryOver)
    {
        var count = _tokens.Count;
        carryOver = 0;
        if (pos >= count) return -1;
        var t = _tokens[pos].Type;
        // A TUPLE type where a type is expected: `Task<(int a, int b)>`, `((int, int) p, ...)`, `(string s, int i)[]`.
        // Skipped as a BALANCED paren group plus the same `?`/array suffixes every other type takes, so the
        // declaration detection recognises `Task<(int a, int b)> Name(` as a typed declaration. Measured: without
        // this, `async Task<(int a, int b)>` was a ParseError ("Expect ';' after expression"), because the
        // speculative scan could not see past the tuple and the line was then read as an expression.
        if (t == TokenType.LeftParen)
        {
            var tupleDepth = 0;
            while (pos < count)
            {
                var tupleToken = _tokens[pos].Type;
                if (tupleToken == TokenType.LeftParen) tupleDepth++;
                else if (tupleToken == TokenType.RightParen)
                {
                    tupleDepth--;
                    if (tupleDepth == 0) { pos++; break; }
                }
                pos++;
            }
            if (tupleDepth != 0) return -1;
            if (pos < count && _tokens[pos].Type == TokenType.Question) pos++;
            while (pos < count && _tokens[pos].Type == TokenType.LeftBracket)
            {
                pos++;
                if (pos < count && _tokens[pos].Type == TokenType.Comma)
                    while (pos < count && _tokens[pos].Type == TokenType.Comma) pos++;
                if (pos < count && _tokens[pos].Type == TokenType.RightBracket) pos++;
                else return -1;
            }
            return pos;
        }

        // Base type keyword        // Base type keyword
        if (TypeTokenHelper.IsBaseType(t) || t == TokenType.Void)
        {
            pos++;
            // optional ?
            if (pos < count && _tokens[pos].Type == TokenType.Question) pos++;
            // optional [] or [,]
            while (pos < count && _tokens[pos].Type == TokenType.LeftBracket)
            {
                pos++;
                if (pos < count && _tokens[pos].Type == TokenType.Comma)
                    while (pos < count && _tokens[pos].Type == TokenType.Comma) pos++;
                if (pos < count && _tokens[pos].Type == TokenType.RightBracket) pos++;
                else return -1;
            }
            return pos;
        }

        // Identifier type
        if (t != TokenType.Identifier) return -1;
        pos++;
        while (pos < count && _tokens[pos].Type == TokenType.Dot)
        {
            pos++;
            if (pos < count && _tokens[pos].Type == TokenType.Identifier) pos++;
            else return -1;
        }

        // optional generic args
        if (pos < count && _tokens[pos].Type == TokenType.Less)
        {
            var end = TrySkipGenericArgs(pos, out carryOver);
            if (end < 0) return -1;
            pos = end;
        }

        // optional ?
        if (pos < count && _tokens[pos].Type == TokenType.Question) pos++;
        // optional [] or [,], repeated
        while (pos < count && _tokens[pos].Type == TokenType.LeftBracket)
        {
            pos++;
            if (pos < count && _tokens[pos].Type == TokenType.Comma)
                while (pos < count && _tokens[pos].Type == TokenType.Comma) pos++;
            if (pos < count && _tokens[pos].Type == TokenType.RightBracket) pos++;
            else return -1;
        }
        return pos;
    }

    /// <summary>Lookahead-only: skips a generic-argument list '<' Type (',' Type)* '>' starting at the
    /// '<' token. Returns the index after the closing '>', or -1 on failure. <paramref name="carryOver"/>
    /// receives 1 when the close was a collapsed '>>' (RightShift), the second '>' then also closes the
    /// enclosing generic level.</summary>
    private int TrySkipGenericArgs(int pos, out int carryOver)
    {
        var count = _tokens.Count;
        carryOver = 0;
        if (pos >= count || _tokens[pos].Type != TokenType.Less) return -1;
        pos++; // consume '<'
        while (true)
        {
            var argEnd = SkipTypeTokens(pos, out carryOver);
            if (argEnd < 0) return -1;
            pos = argEnd;
            if (pos < count && _tokens[pos].Type == TokenType.Comma) { pos++; continue; }
            break;
        }

        // Close this generic level: a carried-over '>' from a nested '>>' has priority.
        if (carryOver > 0)
            carryOver = 0; // the '>>' already supplied this level's closing '>'
        else if (pos < count && _tokens[pos].Type == TokenType.Greater)
            pos++;
        else if (pos < count && _tokens[pos].Type == TokenType.RightShift)
        {
            pos++;
            carryOver = 1; // second '>' of the '>>' closes the enclosing generic level
        }
        else
            return -1;
        return pos;
    }

    /// <summary>Check if the token at lookahead offset is a valid type start (for disambiguating generics).</summary>
    private bool LookAheadIsType(int offset)
    {
        var pos = _current + offset;
        if (pos >= _tokens.Count) return false;
        var t = _tokens[pos];
        if (TypeTokenHelper.IsBaseType(t.Type)) return true;
        // A TUPLE type: `(…)` followed by `>` and then `(` is a typed call on a tuple,
        // Deserialize<(int i, Iterable it)>(…). Only that exact shape counts. Accepting a bare `(` would
        // swallow every comparison whose right side is parenthesized (`a < (b + c)`), because this lookahead
        // is what decides whether `<` opens a type-argument list or is the less-than operator.
        if (t.Type == TokenType.LeftParen)
        {
            var depth = 0;
            for (var i = pos; i < _tokens.Count; i++)
            {
                if (_tokens[i].Type == TokenType.LeftParen) depth++;
                else if (_tokens[i].Type == TokenType.RightParen && --depth == 0)
                    return i + 2 < _tokens.Count
                        && _tokens[i + 1].Type == TokenType.Greater
                        && _tokens[i + 2].Type == TokenType.LeftParen;
            }
            return false;
        }
        if (t.Type != TokenType.Identifier) return false;
        // Check if followed by > or ,, typical generic argument pattern
        var next = pos + 1;
        if (next >= _tokens.Count) return true;
        var nextType = _tokens[next].Type;
        // ...or by '<', which is a NESTED generic: in `Deserialize<List<int>>(...)` the token after
        // `List` is '<'. Leaving that out made a single nested type argument look like "not a type", so
        // the call was not recognised as typed at all, the callee was then evaluated as a property and
        // reported "Property 'X' not found in namespace". A nested argument in SECOND position escaped it,
        // because the first argument's lookahead is what decides.
        return nextType is TokenType.Greater or TokenType.Comma or TokenType.Question
            || nextType == TokenType.Less;
    }

    /// <summary>Parse a destructor: ~ClassName() { body }</summary>
    private FuncDecl ParseDestructor(string className)
    {
        var line = Previous().LineNumber;
        var col = Previous().Column;
        _ = Advance(); // consume ~
        var nameToken = ConsumeIdentifier("Expect class name after '~'.");
        var expectedName = className.Contains('.') ? className.Split('.')[^1] : className;
        if (!_nameComparer.Equals(nameToken.Literal, expectedName))
            throw Error($"Destructor name must match class name '{expectedName}'.", nameToken);
        _ = Consume(TokenType.LeftParen, "Expect '(' after destructor name.");
        _ = Consume(TokenType.RightParen, "Expect ')' after '()'.");
        _ = Consume(TokenType.LeftBrace, "Expect '{' before destructor body.");
        var body = Block();
        var ret = FuncDecl.Create($"~{className}", [], [], [], body,
            TypeInfo.Void, null, AccessModifier.Public, line, col);
        ret.HasYield = ContainsYield(body);
        return ret;
    }

    private List<string>? ParseTypeParameters()
    {
        if (!Match(TokenType.Less)) return null;
        var parameters = new List<string>();
        do
        {
            parameters.Add(ConsumeIdentifier("Expect type parameter name.").Literal);
        } while (Match(TokenType.Comma));
        ConsumeGenericClose("Expect '>' after type parameters.");
        return parameters;
    }

    private ConstructorDecl ParseConstructor(AccessModifier? defaultAccess = null)
    {
        var access = defaultAccess ?? TryConsumeAccessModifier() ?? AccessModifier.Private;
        var line = Previous().LineNumber;
        var col = Previous().Column;
        _ = Consume(TokenType.LeftParen, "Expect '(' after constructor.");
        var (parameters, isRefs, isOuts, isParamsList, defaults, tupleElems, isIns) = ParseParameterList();
        if (tupleElems is { Count: > 0 } && tupleElems.Any(e => e is { Count: > 0 }))
            throw Error("Tuple parameters are not supported in this context.", Peek());
        _ = Consume(TokenType.RightParen, "Expect ')' after parameters.");
        _ = Consume(TokenType.LeftBrace, "Expect '{' before constructor body.");
        var body = Block();
        // The out-param list was PARSED (ParseParameterList hands it back) and then dropped here, so a constructor
        // declared `(out int a)` carried no out flag at all: `new C(out x)` was bound by value and the callee's
        // assignment vanished into a local (measured: the caller's `x` stayed 0, silently, and the argument cell
        // built at the call site was unwrapped). Every other declaration site passes the list; this one did not.
        var ctorDecl = new ConstructorDecl(parameters, isRefs, body, access, line, col, isOuts, IsParams: isParamsList);
        ctorDecl.IsInParams = isIns;
        RefuseInParamWrites("<constructor>", parameters, isIns, body, line, col);
        return ctorDecl;
    }

    /// <summary>Consumes and returns an access modifier token, or null if none found.</summary>
    private AccessModifier? TryConsumeAccessModifier()
    {
        if (Match(TokenType.Public)) return AccessModifier.Public;
        if (Match(TokenType.Private)) return AccessModifier.Private;
        if (Match(TokenType.Protected)) return AccessModifier.Protected;
        if (Match(TokenType.Internal)) return AccessModifier.Internal;
        return null;
    }

    private AccessModifier ParseAccessModifier()
        => TryConsumeAccessModifier() ?? AccessModifier.Public;

    private FuncDecl ParseOperatorOverloadWithReturnType(TypeInfo returnType, AccessModifier access)
    {
        var line = Previous().LineNumber;
        var col = Previous().Column;
        _ = Consume(TokenType.Operator, "Expect 'operator' keyword.");
        var opToken = ConsumeAnyOperatorToken("Expect operator token (+, -, *, /, %, <, >, <=, >=, ==, !=).");
        _ = Consume(TokenType.LeftParen, "Expect '(' after operator.");
        var (parameters, isRefs, isOuts, isParamsList, defaults, tupleElems, isIns) = ParseParameterList();
        if (tupleElems is { Count: > 0 } && tupleElems.Any(e => e is { Count: > 0 }))
            throw Error("Tuple parameters are not supported in this context.", Peek());
        if (parameters.Count != 1) throw Error("Operator method must have exactly one parameter.", Peek());
        if (isRefs.Count > 0 && isRefs[0]) throw Error("Operator method parameters cannot be 'ref'.", Peek());
        _ = Consume(TokenType.RightParen, "Expect ')' after parameters.");
        _ = Consume(TokenType.LeftBrace, "Expect '{' before operator body.");
        var body = Block();
        var ret = FuncDecl.Create($"op_{opToken.Type}", parameters, isRefs, isParamsList, body, returnType, opToken.Type, access, line, col, typeParameters: null, isInParams: isIns);
        ret.IsPure = false; ret.IsCoroutine = false;
        ret.HasYield = ContainsYield(body);
        return ret;
    }

    // ─── Function / Lambda ────────────────────────────────────────────────

    private FuncDecl FunctionDeclaration(Token name, TypeInfo? explicitReturnType = null,
        AccessModifier access = AccessModifier.Private, bool isPure = false, bool isCoroutine = false,
        IReadOnlyList<string>? typeParameters = null, bool isAsync = false, bool isRefReturn = false)
    {
        var returnType = explicitReturnType ?? ParseType(allowVoid: true);
        // `pure` memoises a RETURN VALUE, and an async call returns a Task instead, so the cache would be written on
        // every call and never read (the read sits after the IsAsync branch in InvokeScriptFunction). Refused rather
        // than ignored: as it stood the flag was inert AND grew a memo entry per call.
        if (isAsync && isPure)
            throw Error("'pure' has no meaning on an 'async' function: it memoises a return value, and an async "
                        + "call returns a Task. Drop one of the two modifiers.", name);
        // Tuple return type: (int i, string s) → element names recorded on the function
        IReadOnlyList<string>? tupleElementNames = null;
        if (explicitReturnType == null && Check(TokenType.LeftParen) && IsAtTupleTypeReturn())
        {
            tupleElementNames = ParseTupleElementNames();
            returnType = new TypeInfo(TokenType.Object, true, 0, 0, null);
        }
        typeParameters ??= ParseTypeParameters();
        _ = Consume(TokenType.LeftParen, "Expect '(' after function name.");
        var (parameters, isRefs, isOuts, isParamsList, defaults, tupleElems, isIns) = ParseParameterList();
        _ = Consume(TokenType.RightParen, "Expect ')' after parameters.");

        IReadOnlyList<Stmt> body;
        bool autoCoroutine;

        // `await` is the keyword ONLY inside a body declared `async`, which is where this flag is set: a nested
        // lambda's body clears it again (a lambda is not async in this POC), so the two never leak into each other.
        var savedInAsyncBody = _inAsyncBody;
        _inAsyncBody = isAsync;
        _bodyDepth = _bodyDepth + 1;

        if (Match(TokenType.Arrow))
        {
            var bodyExpr = Expression();
            _ = Consume(TokenType.Semicolon, "Expect ';' after expression body.");
            body = new List<Stmt> { ReturnStmt.Create(bodyExpr, name.LineNumber, name.Column) };
            autoCoroutine = ContainsYieldInExpr(bodyExpr);
        }
        else
        {
            _ = Consume(TokenType.LeftBrace, "Expect '{' before function body.");
            body = Block();
            autoCoroutine = ContainsYield(body);
        }

        // Tuple parameters: prepend the per-param destructure prologue to the body.
        var prologue = BuildTupleParamPrologue(parameters, tupleElems, name.LineNumber, name.Column);
        if (prologue.Count > 0) body = [.. prologue, .. body];

        _inAsyncBody = savedInAsyncBody;
        _bodyDepth = _bodyDepth - 1;

        var ret = FuncDecl.Create(name.Literal, parameters, isRefs, isParamsList, body, returnType, null, access, name.LineNumber, name.Column, typeParameters, isOuts, isIns);
        ret.IsPure = isPure;
        ret.IsRefReturn = isRefReturn;
        ret.IsAsync = isAsync;
        // An `async` body suspends like a coroutine (that is what lets `await` yield to the pump) while NOT being a
        // value-yield generator: its call path hands back a Task, see Interpreter's IsAsync branch.
        ret.IsCoroutine = isCoroutine || autoCoroutine || isAsync;
        ret.HasYield = autoCoroutine;   // body contains a YieldExpr, same ContainsYield pass
        if (tupleElementNames is { Count: > 0 }) ret.TupleElementNames = tupleElementNames;
        ret.TupleParamNames = tupleElems;
        ret.DefaultValues = DefaultsOrNull(defaults);
        RefuseInParamWrites(name.Literal, parameters, isIns, body, name.LineNumber, name.Column);
        return ret;
    }

    /// <summary>The read-only rule for an `in` parameter, enforced WHERE THE DECLARATION IS, which is C# rule R004
    /// ("Cannot assign to an in parameter") and needs no runtime guard: a body that writes the name, passes it by
    /// `ref`/`out`, or takes a `ref` local over it, is refused with the reason and the fix. The walk covers the
    /// statements a body can nest (block, if, while, do, for, foreach, try, using, switch) and the two assignment
    /// forms, increment/decrement, and a ref/out argument or ref-local target naming the parameter.
    /// </summary>
    /// <summary>The statements a lambda body holds, for the read-only walk: a block body is itself, and an
    /// expression body becomes one synthetic statement so the SAME walker sees its assignments.
    /// </summary>
    private static IReadOnlyList<Stmt> LambdaBodyStmts(Expr? bodyExpr, IReadOnlyList<Stmt>? bodyStmts)
        => bodyStmts ?? (bodyExpr != null ? [ExprStmt.Create(bodyExpr, bodyExpr.Line, bodyExpr.Column)] : []);

    private void RefuseInParamWrites(string name, IReadOnlyList<(TypeInfo Type, string Name)> parameters,
        IReadOnlyList<bool> isIns, IReadOnlyList<Stmt> body, int line, int col)
    {
        if (isIns.Count == 0 || !isIns.Any(f => f)) return;
        var readOnly = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < parameters.Count && i < isIns.Count; i++)
            if (isIns[i]) readOnly.Add(parameters[i].Name);
        if (readOnly.Count == 0) return;

        void Refuse(string targetName)
        {
            throw Error($"Cannot assign to '{targetName}': it is an 'in' parameter, and an 'in' parameter is read-only. " +
                        $"Declare it `ref {targetName}` to write it back, or drop the keyword to get an ordinary parameter.", Peek());
        }

        void WalkExpr(Expr e)
        {
            switch (e)
            {
                case AssignExpr a: if (readOnly.Contains(a.Name)) Refuse(a.Name); WalkExpr(a.Value); break;
                case CompoundAssignExpr c: if (NamesReadOnly(c.Target)) Refuse(TargetName(c.Target)); WalkExpr(c.Value); break;
                case IncrementDecrementExpr idc: if (NamesReadOnly(idc.Target)) Refuse(TargetName(idc.Target)); break;
                case CallExpr c2:
                    for (var i = 0; i < c2.Arguments.Count; i++)
                    {
                        var flagged = (c2.IsRefArgs != null && i < c2.IsRefArgs.Count && c2.IsRefArgs[i])
                                      || (c2.IsOutArgs != null && i < c2.IsOutArgs.Count && c2.IsOutArgs[i]);
                        if (flagged && c2.Arguments[i] is VariableExpr rv && readOnly.Contains(rv.Name))
                            throw Error($"Cannot pass the 'in' parameter '{rv.Name}' by ref or out: an 'in' parameter is " +
                                        "read-only, so declare it `ref` to let the callee write it back.", Peek());
                        // A `ref` LOCAL over the name (`ref int r = ref p;`) writes through it, so it is refused too.
                        if (!flagged && c2.Callee is VariableExpr helper && helper.Name == "__ref_name__"
                            && c2.Arguments.Count > 0 && c2.Arguments[0] is StringExpr sTarget && readOnly.Contains(sTarget.Value))
                            throw Error($"Cannot take a reference to the 'in' parameter '{sTarget.Value}': it is read-only. " +
                                        "Declare it `ref` to take a reference to it.", Peek());
                        WalkExpr(c2.Arguments[i]);
                    }
                    WalkExpr(c2.Callee);
                    break;
                case BinaryExpr b: WalkExpr(b.Left); WalkExpr(b.Right); break;
                case UnaryExpr u: WalkExpr(u.Right); break;
                case ListLiteralExpr l: foreach (var el in l.Elements) WalkExpr(el); break;
                case PropertyAccessExpr p: WalkExpr(p.Target); break;
                case ArrayAccessExpr aa: WalkExpr(aa.Array); foreach (var ix in aa.Indices) WalkExpr(ix); break;
            }

            bool NamesReadOnly(Expr target) => target is VariableExpr v && readOnly.Contains(v.Name);
            string TargetName(Expr target) => target is VariableExpr v ? v.Name : "?";
        }

        void WalkStmt(Stmt s)
        {
            switch (s)
            {
                case BlockStmt b: foreach (var inner in b.Statements) WalkStmt(inner); break;
                case VarDeclStmt vd: if (vd.Initializer != null) WalkExpr(vd.Initializer); break;
                case ExprStmt es: WalkExpr(es.Expression); break;
                case ReturnStmt rs: if (rs.Value != null) WalkExpr(rs.Value); break;
                case IfStmt ifs:
                    WalkExpr(ifs.Condition);
                    foreach (var inner in ifs.ThenBranch) WalkStmt(inner);
                    foreach (var inner in ifs.ElseBranch) WalkStmt(inner);
                    break;
                case WhileStmt ws: WalkExpr(ws.Condition); foreach (var inner in ws.Body) WalkStmt(inner); break;
                case ForStmt fs:
                    if (fs.Init != null) WalkStmt(fs.Init);
                    if (fs.Condition != null) WalkExpr(fs.Condition);
                    if (fs.Increment != null) WalkExpr(fs.Increment);
                    foreach (var inner in fs.Body) WalkStmt(inner);
                    break;
                case ForeachStmt fe: WalkExpr(fe.Collection); foreach (var inner in fe.Body) WalkStmt(inner); break;
                case TryStmt ts:
                    WalkStmt(ts.Body);
                    foreach (var c in ts.Catches) WalkStmt(c.Body);
                    if (ts.Finally != null) WalkStmt(ts.Finally);
                    break;
                case UsingStmt us: WalkExpr(us.Initializer); WalkStmt(us.Body); break;
                case SwitchStmt sw:
                    WalkExpr(sw.Subject);
                    foreach (var c in sw.Cases) foreach (var inner in c.Body) WalkStmt(inner);
                    break;
            }
        }

        foreach (var s in body) WalkStmt(s);
    }

    /// <summary>The defaults a declaration carries, or null when it carries none. ONE rule, shared by a named
    /// function and by a lambda (they are the same question asked of the same parse): a list of all-null entries is
    /// not stored, so <c>DefaultValues == null</c> keeps meaning "this declaration has no defaults", which is what
    /// every arity rule in the engine reads.</summary>
    private static IReadOnlyList<Expr?>? DefaultsOrNull(List<Expr?> defaults)
        => defaults.Count > 0 && defaults.Any(d => d != null) ? defaults : null;

    /// <summary>True when the token stream at _current is a tuple type annotation:
    /// '(' <type> <name> ',' ..., i.e. parenthesized return type, not an expression.</summary>
    private bool IsAtTupleTypeReturn()
    {
        var pos = _current + 1; // skip '('
        var items = 0;
        while (pos < _tokens.Count)
        {
            var t = _tokens[pos].Type;
            if (t == TokenType.RightParen) return items >= 1;
            if (t == TokenType.Comma) { items++; pos++; continue; }
            // type token
            if (t == TokenType.Identifier || TypeTokenHelper.IsBaseType(t) || t is TokenType.String or TokenType.Object or TokenType.Bool or TokenType.Char)
            { pos++; continue; }
            // '?' after type
            if (t == TokenType.Question) { pos++; continue; }
            return false;
        }
        return false;
    }

    /// <summary>Parses "(int i, string s)" and returns the element names ["i", "s"]. Element names
    /// are optional, when ANY element is unnamed ((int, int)), returns null (the tuple type
    /// carries no names).</summary>
    private IReadOnlyList<string>? ParseTupleElementNames()
    {
        var names = new List<string>();
        var sawUnnamed = false;
        _ = Consume(TokenType.LeftParen, "Expect '(' in tuple return type.");
        do
        {
            // parse one element type (may include ?), then an optional name
            _ = ParseType();
            if (Match(TokenType.Question)) { /* nullable marker */ }
            if (Check(TokenType.Identifier) && !IsReservedKeyword(Peek().Literal))
                names.Add(ConsumeIdentifier("Expect element name in tuple type.").Literal);
            else
                sawUnnamed = true;   // unnamed element, the tuple type carries no names
        } while (Match(TokenType.Comma));
        _ = Consume(TokenType.RightParen, "Expect ')' after tuple type.");
        return sawUnnamed ? null : names;
    }

    /// <summary>Parses `(elem, …)` element specs for the shared positional pattern. Elements are
    /// typed (`Type name?`, `Type`, or `_`) and, when `allowLiterals` is true (the `is (…)` form),
    /// also literal constants (`"x"`, 10, -1, true, false, null) and a bare `_` discard.
    /// Nested parentheses are rejected (v1).</summary>
    /// <summary>Converts a numeric token literal (with optional f/d/m/l/u suffix and sign) into
    /// its runtime value for a literal pattern element. Mirrors the expression parser's suffix
    /// handling; falls back to the raw text when the shape is unsupported.</summary>
    private static object ParsePatternNumberLiteral(string raw, bool negate)
    {
        var body = raw;
        while (body.Length > 1 && !char.IsDigit(body[^1]) && body[^1] != '.')
            body = body[..^1];   // strip numeric suffix letters (f/d/m/l/u)
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (long.TryParse(body, System.Globalization.NumberStyles.Integer, inv, out var lv))
            return negate ? -lv : lv;
        if (ulong.TryParse(body, System.Globalization.NumberStyles.Integer, inv, out var uv))
            return negate ? 0UL : uv;
        if (decimal.TryParse(body, System.Globalization.NumberStyles.Number, inv, out var mv))
            return negate ? -mv : mv;
        if (double.TryParse(body, System.Globalization.NumberStyles.Float, inv, out var dv))
            return negate ? -dv : dv;
        return raw;
    }

    private IReadOnlyList<TupleElementPattern> ParseTupleElementPatterns(bool allowLiterals)
    {
        var elements = new List<TupleElementPattern>();
        _ = Consume(TokenType.LeftParen, "Expect '(' in tuple pattern.");
        do
        {
            if (allowLiterals && Check(TokenType.LeftParen))
                throw Error("Nested tuple patterns are not supported.", Peek());

            if (allowLiterals && Check(TokenType.CharLiteral))
            {
                var ct = Consume(TokenType.CharLiteral, "Expect char literal in tuple pattern.");
                elements.Add(new TupleElementPattern
                {
                    Kind = TupleElementKind.Literal,
                    LiteralValue = ct.Literal.Length > 0 ? ct.Literal[0] : '\0'
                });
            }
            else if (allowLiterals && Check(TokenType.String))
            {
                var st = Consume(TokenType.String, "Expect string literal in tuple pattern.");
                elements.Add(new TupleElementPattern { Kind = TupleElementKind.Literal, LiteralValue = st.Literal });
            }
            else if (allowLiterals && (Check(TokenType.Number)
                || (Check(TokenType.Minus) && PeekAhead(1).Type == TokenType.Number)))
            {
                var neg = Match(TokenType.Minus);
                var num = Consume(TokenType.Number, "Expect number literal in tuple pattern.");
                elements.Add(new TupleElementPattern
                {
                    Kind = TupleElementKind.Literal,
                    LiteralValue = ParsePatternNumberLiteral(num.Literal ?? "", neg)
                });
            }
            else if (allowLiterals && (Check(TokenType.True) || Check(TokenType.False)))
            {
                var bl = Check(TokenType.True) ? TokenType.True : TokenType.False;
                _ = Advance();
                elements.Add(new TupleElementPattern
                {
                    Kind = TupleElementKind.Literal,
                    LiteralValue = bl == TokenType.True
                });
            }
            else if (allowLiterals && Match(TokenType.Nil))
            {
                elements.Add(new TupleElementPattern { Kind = TupleElementKind.Literal, LiteralValue = null });
            }
            else if (allowLiterals && Check(TokenType.Identifier) && Peek().Literal == "_")
            {
                _ = Advance();
                elements.Add(new TupleElementPattern { Kind = TupleElementKind.Discard });
            }
            else
            {
                var type = ParseType(allowVoid: false);
                if (Check(TokenType.Identifier) && !IsReservedKeyword(Peek().Literal)
                    && Peek().Literal != "_")
                {
                    var name = ConsumeIdentifier("Expect element name in tuple pattern.").Literal;
                    elements.Add(new TupleElementPattern { Kind = TupleElementKind.TypedVar, Type = type, Name = name });
                }
                else
                {
                    if (Check(TokenType.Identifier) && Peek().Literal == "_") _ = Advance();  // typed discard
                    elements.Add(new TupleElementPattern { Kind = TupleElementKind.TypeOnly, Type = type });
                }
            }
        } while (Match(TokenType.Comma));
        _ = Consume(TokenType.RightParen, "Expect ')' after tuple pattern.");
        return elements;
    }

    /// <summary>True when "(" <type> <name> ... ")" is immediately followed by 'fun'
    ///, i.e. a lambda with a declared tuple return type. Works whether _current is
    /// on the '(' or on the first type token (the cast branch already consumed '(').</summary>
    private bool IsTupleTypeThenFun()
    {
        var pos = _current;
        var orig = _current;
        try
        {
            if (_current >= _tokens.Count) return false;
            // If we're already past '(', step back for IsAtTupleTypeReturn's '(_current+1' convention
            if (_tokens[pos].Type != TokenType.LeftParen && pos > 0 && _tokens[pos - 1].Type == TokenType.LeftParen)
                pos--;
            _current = pos;
            if (!IsAtTupleTypeReturn()) return false;
            // scan past the tuple type to check for 'fun'
            while (pos < _tokens.Count && _tokens[pos].Type != TokenType.RightParen) pos++;
            return pos + 1 < _tokens.Count && _tokens[pos + 1].Type == TokenType.Fun;
        }
        catch { return false; }
        finally
        {
            _current = orig; // caller owns the cursor, restore unconditionally
        }
    }

    private LambdaExpr ParseLambda(Token funToken, IReadOnlyList<string?>? tupleElementNames = null, bool isAsync = false)
    {
        _ = Consume(TokenType.LeftParen, "Expect '(' after 'fun'.");
        var (parameters, isRefs, isOuts, isParamsList, defaults, tupleElems, isIns) = ParseParameterList();
        _ = Consume(TokenType.RightParen, "Expect ')' after parameters.");

        Expr? bodyExpr = null;
        IReadOnlyList<Stmt>? bodyStmts = null;
        bool isCoroutine;

        // An `async fun` body may `await`, so `_inAsyncBody` is set around it exactly as FunctionDeclaration does
        // for a named async function; a nested lambda that is not async clears it again on its own way in.
        var savedInAsyncBody = _inAsyncBody;
        _inAsyncBody = isAsync;
        _bodyDepth = _bodyDepth + 1;
        try
        {
            if (Match(TokenType.Arrow))
            {
                if (Peek().Type == TokenType.LeftBrace)
                    throw Error("Cannot use '=>' with block body '{ ... }'.\nUse 'fun () { ... }' for block body or 'fun () => expr' for expression body.", Peek());
                bodyExpr = Expression();
                isCoroutine = ContainsYieldInExpr(bodyExpr);
            }
            else if (Peek().Type == TokenType.LeftBrace)
            {
                _ = Advance();
                bodyStmts = Block();
                isCoroutine = ContainsYield(bodyStmts);
            }
            else throw Error("Expected '=>' or '{' after lambda parameters.", Peek());
        }
        finally { _inAsyncBody = savedInAsyncBody; _bodyDepth = _bodyDepth - 1; }

        // Tuple parameters: prepend the per-param destructure prologue to the body
        // (the arrow form becomes an explicit return when a prologue is needed).
        var prologue = BuildTupleParamPrologue(parameters, tupleElems, funToken.LineNumber, funToken.Column);
        if (prologue.Count > 0)
        {
            if (bodyExpr != null)
            {
                bodyStmts = [.. prologue, ReturnStmt.Create(bodyExpr, funToken.LineNumber, funToken.Column)];
                bodyExpr = null;
            }
            else if (bodyStmts != null)
            {
                bodyStmts = [.. prologue, .. bodyStmts];
            }
        }

        var ret = LambdaExpr.Create(parameters, isRefs, isParamsList, isOuts, bodyExpr, bodyStmts, funToken.LineNumber, funToken.Column);
        ret.IsInParams = isIns;
        RefuseInParamWrites("<lambda>", parameters, isIns, LambdaBodyStmts(bodyExpr, bodyStmts), funToken.LineNumber, funToken.Column);
        // A lambda's parameter DEFAULTS travel with it, exactly as a named function's do (ONE rule,
        // DefaultsOrNull): the arity every call site reads comes from the DECLARATION the two engines build out of
        // this node, so dropping the list here is what refused `Lm("x")` on a two-parameter lambda.
        ret.DefaultValues = DefaultsOrNull(defaults);
        ret.IsCoroutine = isCoroutine;
        ret.IsAsync = isAsync;
        ret.TupleElementNames = tupleElementNames;
        ret.TupleParamNames = tupleElems;
        ret.CapturedNames = FreeVariableAnalyzer.Analyze(ret);
        return ret;
    }

    private Expr ParseArrowLambda(Token lparen)
    {
        // lparen is already consumed, parse params and body
        var (parameters, isRefs, isOuts, isParamsList, defaults, tupleElems, isIns) = ParseParameterList();
        _ = Consume(TokenType.RightParen, "Expect ')' after lambda parameters.");

        Expr? bodyExpr = null;
        IReadOnlyList<Stmt>? bodyStmts = null;
        bool isCoroutine;

        if (Match(TokenType.Arrow))
        {
            if (Check(TokenType.LeftBrace))
            {
                // (x) => { block }, arrow with block body
                _ = Advance();
                bodyStmts = Block();
                isCoroutine = ContainsYield(bodyStmts);
            }
            else
            {
                // (x) => expr, arrow with expression body
                bodyExpr = Expression();
                isCoroutine = ContainsYieldInExpr(bodyExpr);
            }
        }
        else throw Error("Expect '=>' after lambda parameters.", Peek());

        // Tuple parameters: prepend the per-param destructure prologue to the body
        // (the arrow form becomes an explicit return when a prologue is needed).
        var prologue = BuildTupleParamPrologue(parameters, tupleElems, lparen.LineNumber, lparen.Column);
        if (prologue.Count > 0)
        {
            if (bodyExpr != null)
            {
                bodyStmts = [.. prologue, ReturnStmt.Create(bodyExpr, lparen.LineNumber, lparen.Column)];
                bodyExpr = null;
            }
            else if (bodyStmts != null)
            {
                bodyStmts = [.. prologue, .. bodyStmts];
            }
        }

        var ret = LambdaExpr.Create(parameters, isRefs, isParamsList, isOuts, bodyExpr, bodyStmts, lparen.LineNumber, lparen.Column);
        ret.IsInParams = isIns;
        RefuseInParamWrites("<lambda>", parameters, isIns, LambdaBodyStmts(bodyExpr, bodyStmts), lparen.LineNumber, lparen.Column);
        ret.DefaultValues = DefaultsOrNull(defaults);
        ret.IsCoroutine = isCoroutine;
        ret.TupleParamNames = tupleElems;
        ret.CapturedNames = FreeVariableAnalyzer.Analyze(ret);
        return ret;
    }

    private Expr ParseInterpolatedString()
    {
        var t = Previous();
        var raw = t.Literal;
        var line = t.LineNumber;
        var col = t.Column;

        // Walk the raw content, splitting on {expr} holes
        var parts = new List<Expr>();
        var literalBuilder = new StringBuilder();
        var i = 0;

        while (i < raw.Length)
        {
            var c = raw[i];

            switch (c)
            {
                // Check for doubled {{ (escaped brace, emitted by lexer as "{{")
                case '{' when i + 1 < raw.Length && raw[i + 1] == '{':
                    literalBuilder.Append('{');
                    i += 2;
                    continue;
                // Flush accumulated literal
                case '{':
                    {
                        if (literalBuilder.Length > 0)
                        {
                            parts.Add(StringExpr.Create(literalBuilder.ToString(), line, col));
                            literalBuilder.Clear();
                        }
                        // Capture expression until matching }
                        var exprBuilder = new StringBuilder();
                        var formatBuilder = new StringBuilder();
                        var depth = 1;
                        var parenDepth = 0;
                        var bracketDepth = 0;
                        var inFormat = false;
                        var inAlign = false;
                        var alignBuilder = new StringBuilder();

                        // Whatever is being collected right now: the expression, the alignment or the format. ONE
                        // place decides, so a character's destination cannot drift between the cases below.
                        void AppendHoleChar(char ch)
                        {
                            if (inAlign) alignBuilder.Append(ch);
                            else if (inFormat) formatBuilder.Append(ch);
                            else exprBuilder.Append(ch);
                        }
                        i++;
                        while (i < raw.Length && depth > 0)
                        {
                            var ec = raw[i];
                            switch (ec)
                            {
                                // Skip string literals so : inside them isn't mistaken for format specifier
                                case '\'' or '"' when depth == 1:
                                    {
                                        var quote = ec;
                                        exprBuilder.Append(ec);
                                        i++;
                                        while (i < raw.Length && raw[i] != quote)
                                        {
                                            if (raw[i] == '\\') { exprBuilder.Append(raw[i]); i++; if (i < raw.Length) { exprBuilder.Append(raw[i]); i++; } continue; }
                                            exprBuilder.Append(raw[i]);
                                            i++;
                                        }
                                        if (i < raw.Length) { exprBuilder.Append(raw[i]); i++; }
                                        continue;
                                    }
                                case '{':
                                    {
                                        depth++; if (depth > 1) exprBuilder.Append(ec);
                                        break;
                                    }
                                case '}':
                                    {
                                        depth--;
                                        if (depth > 0) exprBuilder.Append(ec);
                                        break;
                                    }
                                case '(':
                                    exprBuilder.Append(ec); parenDepth++;
                                    break;
                                case ')':
                                    exprBuilder.Append(ec); parenDepth--;
                                    break;
                                // Brackets are tracked for the same reason parentheses are: a comma or a colon INSIDE
                                // them belongs to the expression, not to an alignment or a format specifier. Measured
                                // before they were tracked: `$"{[1, 2]}"` (and any hole holding an index or a list)
                                // was cut in half at the comma, and the crash came out of the expression parser as
                                // "Expect ']' after list literal elements".
                                case '[':
                                    AppendHoleChar(ec); bracketDepth++;
                                    break;
                                case ']':
                                    AppendHoleChar(ec); bracketDepth--;
                                    break;
                                case '?' when depth == 1 && parenDepth == 0 && bracketDepth == 0 && !inFormat
                                              && !(i + 1 < raw.Length && (raw[i + 1] == '?' || raw[i + 1] == '.')):
                                    // A top-level `?` here is a TERNARY, and C# refuses one in a hole for the same reason
                                    // this splitter cannot express it: the `:` that follows is read as the start of a
                                    // format specifier (`{expr:format}`). Measured with `dotnet run` on C# itself: a bare
                                    // `$"{ok ? "y" : "n"}"` does not compile. Name the fix rather than letting the
                                    // expression parser complain about a colon several tokens later.
                                    throw Error("a ternary in an interpolation hole needs parentheses: write {(a ? b : c)}. "
                                              + "The ':' after the expression starts a format specifier, as it does in C#.", t);
                                case ',' when depth == 1 && !inFormat && !inAlign && parenDepth == 0 && bracketDepth == 0:
                                    // An ALIGNMENT follows (C#-style: {expr,width} or {expr,width:format}). It was
                                    // silently dropped before: `{x,5}` left the `,5` to the expression parser, which read
                                    // `x` and stopped, so the width never reached the value. Parentheses AND brackets are
                                    // excluded, because a comma inside them belongs to the expression (a call's
                                    // arguments, a list literal, an index).
                                    inAlign = true;
                                    break;
                                case ':' when depth == 1 && !inFormat && parenDepth == 0 && bracketDepth == 0:
                                    // Format specifier follows (C#-style: {expr:format}), possibly after an alignment.
                                    inFormat = true;
                                    inAlign = false;
                                    break;
                                default:
                                    AppendHoleChar(ec);
                                    break;
                            }

                            i++;
                        }
                        if (depth != 0)
                            throw new ScriptException(ErrorCode.ParseError, "Unclosed '{' in interpolated string expression.", line, col);

                        var exprText = exprBuilder.ToString().Trim();
                        var formatText = formatBuilder.ToString().Trim();
                        var alignText = alignBuilder.ToString().Trim();

                        // An ALIGNMENT wraps the value (or the formatted text) in the engine's one padding helper, so a
                        // width that is not a whole number is refused with the engine's message instead of by a parse of
                        // the template, and C#'s order holds: the format applies first, then the padding.
                        Expr holePart;
                        if (formatText.Length > 0)
                        {
                            // Transform {expr:format} into (expr).ToString("format")
                            var innerExpr = ParseInlineExpression(exprText, line, col);
                            var formatStr = StringExpr.Create(formatText, line, col);
                            holePart = MethodCallExpr.Create(innerExpr, "ToString", [formatStr], [false], line, col,
                                isOutArgs: []);   // non-null, the VM compiler's IsRefArgs/IsOutArgs guards assume a real list
                        }
                        else
                        {
                            holePart = ParseInlineExpression(exprText, line, col);
                        }
                        if (alignText.Length > 0)
                        {
                            holePart = CallExpr.Create(
                                VariableExpr.Create(Interpreter.InterpPadHelperName, line, col),
                                [holePart, StringExpr.Create(alignText, line, col)], [false, false], line, col,
                                isOutArgs: [false, false]);
                        }
                        parts.Add(holePart);
                        continue;
                    }
                // Check for doubled }} (escaped brace)
                case '}' when i + 1 < raw.Length && raw[i + 1] == '}':
                    literalBuilder.Append('}');
                    i += 2;
                    continue;
                default:
                    literalBuilder.Append(c);
                    i++;
                    break;
            }
        }

        // Flush remaining literal
        if (literalBuilder.Length > 0)
            parts.Add(StringExpr.Create(literalBuilder.ToString(), line, col));

        // Chain with BinaryExpr(Plus, ...)
        if (parts.Count == 0)
            return StringExpr.Create("", line, col);

        var result = parts[0];
        // A template is a STRING, so a chain that begins with an EXPRESSION gets an empty literal in front of it.
        // Without it `$"{42}"` WAS the int 42, `$"{n}".Length` an error, `$"{n}" + 1` the number 43, and
        // `$"{a}{b}"` the SUM of the two, because a template whose parts are all expressions returned its first
        // part as-is. The leading "" makes the + chain string concatenation, which also routes every part through
        // the one display rule. A template that starts with text is untouched (its chain is already a string).
        if (result is not StringExpr)
            result = BinaryExpr.Create(StringExpr.Create("", line, col),
                new Token(TokenType.Plus, "+", line, col), result, line, col);
        for (var p = 1; p < parts.Count; p++)
        {
            var op = new Token(TokenType.Plus, "+", line, col);
            result = BinaryExpr.Create(result, op, parts[p], line, col);
        }
        return result;
    }

    // ─── Type Parsing ─────────────────────────────────────────────────────

    private bool IsTypeStart(Token token)
    {
        if (TypeTokenHelper.IsBaseType(token.Type)) return true;
        if (token.Type != TokenType.Identifier) return false;
        var literal = token.Literal.AsSpan();
        if (literal.Equals("print", StringComparison.Ordinal)
            || literal.Equals("pause", StringComparison.Ordinal)
            || literal.Equals("sleep", StringComparison.Ordinal))
            return false;
        var next = _current + 1;
        if (next >= _tokens.Count) return false;
        var nextToken = _tokens[next];
        return nextToken.Type is TokenType.Identifier or TokenType.Less or TokenType.LeftBracket;
    }

    /// <summary>True when a TYPE-led DECLARATION starts at <paramref name="pos"/>: a type (scanned by
    /// <see cref="SafeParseTypeEnd"/>, so a base type, a qualified name, `?`, generic arguments and `[]` are all
    /// part of it) followed by the NAME being declared, followed by one of <paramref name="afterName"/>.
    ///
    /// The NAME plus that follower is what makes the shape unambiguous, and it is why this can accept the `?` that
    /// a bare <see cref="IsTypeStart"/> must not: `x ? y : z` scans as the type `x?` and the name `y`, then meets
    /// `:`, which no declaration follower admits, so a ternary is not taken for a declaration.
    ///
    /// Measured before this existed: `A? x` / `A[] x` (a bare-identifier type with a SUFFIX) was refused as a block
    /// local, a `for` init, a `foreach` variable and a parameter, while `ParseType` itself handles both shapes and
    /// the same declaration worked at the top level, as a field, and for a base or generic type, because each of
    /// those sites asked a predicate that required the token after an identifier to be `Identifier` or `Less`.</summary>
    private bool IsAtTypedDeclaration(int pos, params TokenType[] afterName)
    {
        if (pos < 0 || pos >= _tokens.Count) return false;
        var save = _current;
        _current = pos;
        var typeEnd = SafeParseTypeEnd();
        _current = save;
        if (typeEnd < 0 || typeEnd >= _tokens.Count) return false;
        if (_tokens[typeEnd].Type != TokenType.Identifier) return false;     // the declared NAME
        var after = typeEnd + 1;
        if (after >= _tokens.Count) return afterName.Length == 0;
        var t = _tokens[after].Type;
        for (var i = 0; i < afterName.Length; i++)
            if (afterName[i] == t) return true;
        return false;
    }

    /// <summary>True when a call-like tail at <paramref name="pos"/> is the head of a DECLARATION: a balanced
    /// parameter list (after an optional generic list) followed by a BODY marker (`{` or `=>`).
    ///
    /// This is what keeps a type with a SUFFIX from swallowing a ternary: `x ? f() : g();` starts exactly like the
    /// declaration `x? f(...)`, and the `:` after the call is what tells them apart. A shape already accepted by
    /// <see cref="IsTypeStart"/> does not need this check, so only the newly-taken suffix shapes ask for it and no
    /// existing answer can change.</summary>
    private bool DeclarationBodyFollows(int pos)
    {
        if (pos >= _tokens.Count) return false;
        if (_tokens[pos].Type == TokenType.Less)
        {
            var genericEnd = TrySkipGenericArgs(pos, out _);   // `<T>`, a GENERIC PARAMETER list, not a type
            if (genericEnd <= pos) return false;
            pos = genericEnd;
        }
        if (pos >= _tokens.Count || _tokens[pos].Type != TokenType.LeftParen) return false;
        var depth = 0;
        for (; pos < _tokens.Count; pos++)
        {
            var t = _tokens[pos].Type;
            if (t == TokenType.LeftParen) depth++;
            else if (t == TokenType.RightParen && --depth == 0)
            {
                var after = pos + 1;
                return after < _tokens.Count && _tokens[after].Type is TokenType.LeftBrace or TokenType.Arrow;
            }
        }
        return false;
    }

    /// <summary>Could a TYPE begin at this token? This mirrors what <see cref="ParseType"/> accepts as its FIRST
    /// token, so a caller can decide whether to attempt a type parse without provoking the ParseError that parse
    /// raises for anything else (it throws "Expected a type"): measured, every `if`, `for`, `while`, `return` and
    /// `try` statement reached the typed-variable speculation, threw there and was caught, which is one first-chance
    /// exception per statement.
    ///
    /// It is deliberately NOT <see cref="IsTypeStart"/>: that answers a different question (does a TYPED DECLARATION
    /// start here, so it also looks at the name that follows and wants Identifier/Less/LeftBracket after it), and it
    /// rejects the `?` of a nullable custom type, so guarding with it would skip a declaration like `Foo? v = null;`.</summary>
    private static bool CouldStartType(Token token)
        => TypeTokenHelper.IsBaseType(token.Type)
           || token.Type is TokenType.Void or TokenType.Class or TokenType.Struct
           || token.Type is TokenType.LeftParen or TokenType.Identifier;

    private TypeInfo ParseType(bool allowVoid = true)
    {
        var startToken = Peek();
        if (TypeTokenHelper.IsBaseType(startToken.Type) || (allowVoid && startToken.Type == TokenType.Void))
        {
            var baseType = startToken.Type; _ = Advance();
            var isNullable = Match(TokenType.Question);
            int arrayRank = 0, jaggedDepth = 0;
            while (Check(TokenType.LeftBracket) && !Check(TokenType.Number))
            {
                _ = Advance();
                if (Check(TokenType.Comma))
                {
                    arrayRank = 1;
                    while (Match(TokenType.Comma)) arrayRank++;
                    _ = Consume(TokenType.RightBracket, "Expect ']' after array dimensions.");
                }
                else { _ = Consume(TokenType.RightBracket, "Expect ']' after array specifier."); jaggedDepth++; }
            }
            return new TypeInfo(baseType, isNullable, arrayRank, jaggedDepth, null);
        }

        // `class` / `struct` are declaration keywords (case-insensitive), so in a TYPE
        // position they never arrive as identifiers, map them to the Class / Struct
        // contract type names, which is what is/as match on. (typeof reports the DECLARED
        // name of the instance instead, "Animal", not "Class".)
        if (startToken.Type is TokenType.Class or TokenType.Struct)
        {
            var kwName = startToken.Type == TokenType.Class ? "Class" : "Struct";
            _ = Advance();
            var kwNullable = Match(TokenType.Question);
            int kwRank = 0, kwJag = 0;
            while (Check(TokenType.LeftBracket) && !Check(TokenType.Number))
            {
                _ = Advance();
                if (Check(TokenType.Comma))
                {
                    kwRank = 1;
                    while (Match(TokenType.Comma)) kwRank++;
                    _ = Consume(TokenType.RightBracket, "Expect ']' after array dimensions.");
                }
                else { _ = Consume(TokenType.RightBracket, "Expect ']' after array specifier."); kwJag++; }
            }
            return new TypeInfo(TokenType.Object, kwNullable, kwRank, kwJag, kwName);
        }

        if (Check(TokenType.LeftParen))
            return ParseTupleTypeAnnotation();

        if (startToken.Type != TokenType.Identifier) throw Error("Expected a type", startToken);
        {
            var identifier = ParseQualifiedIdentifier();
            if (Peek().Type == TokenType.Less)
            {
                _ = Advance();
                if (identifier is "Action" or "Func" or "Predicate" or "Comparison" or "Converter" or "EventHandler")
                    return ParseGenericTypeInstantiation(identifier);

                var args = new List<TypeInfo>();
                do { args.Add(ParseType(allowVoid: false)); } while (Match(TokenType.Comma));
                ConsumeGenericClose("Expect '>' after generic type arguments.");
                var isNullable = Match(TokenType.Question);
                int arrayRank = 0, jaggedDepth = 0;
                while (Check(TokenType.LeftBracket) && !Check(TokenType.Number))
                {
                    _ = Advance();
                    if (Check(TokenType.Comma))
                    {
                        arrayRank = 1;
                        while (Match(TokenType.Comma)) arrayRank++;
                        _ = Consume(TokenType.RightBracket, "Expect ']' after array dimensions.");
                    }
                    else { _ = Consume(TokenType.RightBracket, "Expect ']' after array specifier."); jaggedDepth++; }
                }
                return new TypeInfo(TokenType.Object, isNullable, arrayRank, jaggedDepth, identifier, new TypeArgList(args));
            }

            var nullable = Match(TokenType.Question);
            int arrRank = 0, jagDepth = 0;
            while (Check(TokenType.LeftBracket) && !Check(TokenType.Number))
            {
                _ = Advance();
                if (Check(TokenType.Comma))
                {
                    arrRank = 1;
                    while (Match(TokenType.Comma)) arrRank++;
                    _ = Consume(TokenType.RightBracket, "Expect ']' after array dimensions.");
                }
                else { _ = Consume(TokenType.RightBracket, "Expect ']' after array specifier."); jagDepth++; }
            }
            return new TypeInfo(TokenType.Object, nullable, arrRank, jagDepth, identifier);
        }

    }

    /// <summary>Tuple type used as a generic type argument: <c>List&lt;(string s, int i, bool b)&gt;</c>,
    /// or as an array element type: <c>new (string s, int i)[] {{ ... }}</c>. Element types are retained
    /// (element names are consumed) and enforced by CoerceTo when values enter typed containers.</summary>
    private TypeInfo ParseTupleTypeAnnotation()
    {
        _ = Consume(TokenType.LeftParen, "Expect '(' in tuple type.");
        var elems = new List<TypeInfo>();
        var names = new List<string>();
        if (Check(TokenType.RightParen))
            throw Error("Tuple type must have at least one element.", Peek());
        do
        {
            var et = ParseType(allowVoid: false);
            if (Check(TokenType.Identifier) && !IsReservedKeyword(Peek().Literal))
                names.Add(ConsumeIdentifier("Expect element name in tuple type.").Literal);
            else
                names.Add(default!);
            elems.Add(et);
        } while (Match(TokenType.Comma));
        _ = Consume(TokenType.RightParen, "Expect ')' after tuple type.");
        return new TypeInfo(TokenType.Object, false, 0, 0, null, null, elems, names);
    }

    private TypeInfo ParseGenericTypeInstantiation(string delegateBaseName)
    {
        _ = Previous();
        var typeArgs = new List<TypeInfo>();
        do { typeArgs.Add(ParseType(allowVoid: false)); } while (Match(TokenType.Comma));
        ConsumeGenericClose("Expect '>' after generic type arguments.");

        var typeArgNames = string.Join(",", typeArgs.Select(t => t.TypeName ?? t.BaseType.ToString()));
        var concreteName = $"{delegateBaseName}<{typeArgNames}>";

        if (_delegates.TryGetValue(concreteName, out _))
            return new TypeInfo(TokenType.Object, IsNullable: true, ArrayRank: 0, JaggedDepth: 0, TypeName: concreteName, GenericArgs: new TypeArgList(typeArgs));

        var concreteDelegate = delegateBaseName switch
        {
            "Action" => CreateConcreteActionDelegate(concreteName, typeArgs),
            "Func" => CreateConcreteFuncDelegate(concreteName, typeArgs),
            "Predicate" => CreateConcretePredicateDelegate(concreteName, typeArgs),
            "Comparison" => CreateConcreteComparisonDelegate(concreteName, typeArgs),
            "Converter" => CreateConcreteConverterDelegate(concreteName, typeArgs),
            _ => null
        };

        if (concreteDelegate != null) _delegates[concreteName] = concreteDelegate;
        return new TypeInfo(TokenType.Object, IsNullable: true, ArrayRank: 0, JaggedDepth: 0, TypeName: concreteName, GenericArgs: new TypeArgList(typeArgs));
    }

    private static DelegateInfo CreateConcreteActionDelegate(string name, List<TypeInfo> typeArgs)
    {
        var parameters = typeArgs.Select((t, i) => (t, $"arg{i}")).ToArray();
        return new DelegateInfo(name, TypeInfo.Void, parameters, [.. Enumerable.Repeat(false, parameters.Length)], AccessModifier.Public, 0, 0);
    }

    private DelegateInfo CreateConcreteFuncDelegate(string name, List<TypeInfo> typeArgs)
    {
        if (typeArgs.Count < 1) throw Error("Func<TResult> requires at least one type argument (the return type).", Previous());
        var returnType = typeArgs[^1];
        var parameters = typeArgs.Take(typeArgs.Count - 1).Select((t, i) => (t, $"arg{i}")).ToArray();
        return new DelegateInfo(name, returnType, parameters, [.. Enumerable.Repeat(false, parameters.Length)], AccessModifier.Public, 0, 0);
    }

    private DelegateInfo CreateConcretePredicateDelegate(string name, List<TypeInfo> typeArgs)
    {
        if (typeArgs.Count != 1) throw Error("Predicate<T> requires exactly one type argument.", Previous());
        return new DelegateInfo(name, new TypeInfo(TokenType.Bool, false, 0, 0, null),
            [(typeArgs[0], "arg")], [false], AccessModifier.Public, 0, 0);
    }

    private DelegateInfo CreateConcreteComparisonDelegate(string name, List<TypeInfo> typeArgs)
    {
        if (typeArgs.Count != 1) throw Error("Comparison<T> requires exactly one type argument.", Previous());
        var t = typeArgs[0];
        return new DelegateInfo(name, new TypeInfo(TokenType.Int, false, 0, 0, null),
            [(t, "x"), (t, "y")], [false, false], AccessModifier.Public, 0, 0);
    }

    private DelegateInfo CreateConcreteConverterDelegate(string name, List<TypeInfo> typeArgs)
    {
        return typeArgs.Count != 2
            ? throw Error("Converter<TInput, TOutput> requires exactly two type arguments.", Previous())
            : new DelegateInfo(name, typeArgs[1], [(typeArgs[0], "input")], [false], AccessModifier.Public, 0, 0);
    }

    private TypeInfo ParseElementTypeForArray()
    {
        var startToken = Peek();
        if (TypeTokenHelper.IsBaseType(startToken.Type))
        {
            _ = Advance();
            // A nullable ELEMENT is legal (`int?[] a = new int?[2]`), so the marker is consumed and RECORDED here:
            // without it the `?` reached ParseArrayTail, which asked for `[` and answered "Expected a type"
            // instead (measured by assets/language/probes/probe-nullable.slc, 2026-10-06).
            return new TypeInfo(startToken.Type, Match(TokenType.Question), 0, 0, null);
        }

        if (startToken.Type != TokenType.Identifier) throw Error("Expected a type for array element.", startToken);
        var name = ParseQualifiedIdentifier();
        var elementNullable = Match(TokenType.Question);
        if (Peek().Type != TokenType.Less) return new TypeInfo(TokenType.Object, elementNullable, 0, 0, name);
        _ = Advance();
        var args = new List<TypeInfo>();
        do { args.Add(ParseType(allowVoid: false)); } while (Match(TokenType.Comma));
        ConsumeGenericClose("Expect '>' after generic type arguments.");
        return new TypeInfo(TokenType.Object, elementNullable, 0, 0, name, new TypeArgList(args));
    }

    private (List<(TypeInfo Type, string Name)> Params, List<bool> IsRef, List<bool> IsOut,
        List<bool> IsParams, List<Expr?> Defaults, List<IReadOnlyList<string>?> TupleElements, List<bool> IsIn)
        ParseParameterList()
    {
        var parameters = new List<(TypeInfo, string)>();
        var isRefs = new List<bool>();
        var isOuts = new List<bool>();
        var isIns = new List<bool>();
        var isParamsList = new List<bool>();
        var defaults = new List<Expr?>();
        var tupleElements = new List<IReadOnlyList<string>?>();
        if (!Check(TokenType.RightParen))
        {
            do
            {
                var isParams = Match(TokenType.Params);
                var isRef = Match(TokenType.Ref);
                var isOut = !isRef && Match(TokenType.Out);
                // `in` is a read-only, by-value parameter (C# 7.2). It cannot combine with the other two, whose whole
                // point is that the callee WRITES the caller storage.
                var isIn = !isRef && !isOut && Match(TokenType.In);
                if (isRef && isOut) throw Error("Parameter cannot be both 'ref' and 'out'.", Peek());

                // Tuple parameter: "(" <type> <name> {, <type> <name>} ")" [paramName]
                IReadOnlyList<string>? tupleElems = null;
                if (Check(TokenType.LeftParen) && (IsTypeStart(PeekAhead(1))
            || IsAtTypedDeclaration(_current + 1, TokenType.Comma, TokenType.RightParen, TokenType.Equal)))
                {
                    tupleElems = ParseTupleElementNames();
                    if (Check(TokenType.Identifier) && !IsReservedKeyword(Peek().Literal))
                    {
                        // named form: ((int n, int m) p), the name binds the whole tuple
                        var pName = ConsumeIdentifier("Expect parameter name.");
                        parameters.Add((new TypeInfo(TokenType.Object, true, 0, 0, null), pName.Literal));
                        tupleElements.Add(tupleElems);
                    }
                    else if (tupleElems is { Count: 1 })
                    {
                        // single-element unnamed tuple param ((int n)), the element name binds
                        // directly (called with a bare scalar: fibPair((10))), so no prologue
                        parameters.Add((new TypeInfo(TokenType.Object, true, 0, 0, null), tupleElems[0]));
                        tupleElements.Add(null);
                    }
                    else
                    {
                        // unnamed form: ((int n, int m)), elements become direct locals via
                        // a synthesized param + prologue destructure
                        var synth = NextSynthesizedTupleName(parameters);
                        parameters.Add((new TypeInfo(TokenType.Object, true, 0, 0, null), synth));
                        tupleElements.Add(tupleElems);
                    }
                }
                else
                {
                    var pType = IsTypeStart(Peek()) || IsAtTypedDeclaration(_current, TokenType.Comma, TokenType.RightParen, TokenType.Equal)
                        ? ParseType(allowVoid: false)
                        : new TypeInfo(TokenType.Object, IsNullable: true, ArrayRank: 0, JaggedDepth: 0, null);
                    var pName = ConsumeIdentifier("Expect parameter name.");
                    parameters.Add((pType, pName.Literal));
                    tupleElements.Add(null);
                }
                Expr? defaultVal = null;
                if (Match(TokenType.Equal)) defaultVal = Expression();
                isRefs.Add(isRef);
                isOuts.Add(isOut);
                isIns.Add(isIn);
                isParamsList.Add(isParams);
                defaults.Add(defaultVal);
            } while (Match(TokenType.Comma));
        }

        // Validate: params can only appear on the last parameter
        for (var i = 0; i < isParamsList.Count - 1; i++)
        {
            if (isParamsList[i])
                throw Error("'params' must be the last parameter.", Peek());
        }

        // Validate: default values must be at the end (no non-default after default, except params)
        var sawDefault = false;
        for (var i = 0; i < defaults.Count; i++)
        {
            if (defaults[i] != null)
                sawDefault = true;
            else if (sawDefault && !isParamsList[i])
                throw Error("Non-default parameter cannot follow a default parameter.", Peek());
        }

        return (parameters, isRefs, isOuts, isParamsList, defaults, tupleElements, isIns);
    }

    /// <summary>Unique synthesized name for an unnamed tuple parameter, skips any name already
    /// used in the parameter list to avoid collisions.</summary>
    private string NextSynthesizedTupleName(List<(TypeInfo Type, string Name)> parameters)
    {
        var used = new HashSet<string>(parameters.Select(p => p.Name), _nameComparer);
        var i = 0;
        string name;
        do { name = $"__tup{i}"; i++; } while (used.Contains(name));
        return name;
    }

    /// <summary>For each unnamed tuple parameter, build `var (n, m) = __tupN;` destructure
    /// statements (the synthesized param holds the tuple; the destructure reuses the existing
    /// tree/VM/coroutine machinery). Returns statements to PREPEND to the body.
    /// Single-element unnamed params ((int n)) bind directly in ParseParameterList and carry a
    /// null entry here, so they never reach this prologue.</summary>
    private static List<Stmt> BuildTupleParamPrologue(
        List<(TypeInfo Type, string Name)> parameters, List<IReadOnlyList<string>?> tupleElems,
        int line, int col)
    {
        var prologue = new List<Stmt>();
        if (tupleElems == null) return prologue;
        for (var i = 0; i < parameters.Count; i++)
        {
            var elems = tupleElems[i];
            if (elems is not { Count: > 1 } || !parameters[i].Name.StartsWith("__tup", StringComparison.Ordinal))
                continue;   // named form / single-element form: no destructure prologue
            prologue.Add(DestructuringStmt.Create(elems,
                VariableExpr.Create(parameters[i].Name, line, col), null, false, line, col));
        }
        return prologue;
    }

    // ── DRY helper: parse a single call-site argument (handles ref/out) ──
    private (Expr Expr, bool IsRef, bool IsOut, string? Name) ParseCallArg()
    {
        var isRef = Match(TokenType.Ref);
        var isOut = !isRef && Match(TokenType.Out);
        // The `in` keyword at a CALL is optional in C# (passing by value already satisfies an `in` parameter), so it is
        // consumed and forgotten: the position travels as a plain argument. It is accepted here so a reader who writes
        // `P(in y)` is not told the keyword is a name.
        if (!isRef && !isOut) _ = Match(TokenType.In);

        // Named argument: identifier ':' expression (but not '::' or ':' of ternary/type)
        string? argName = null;
        if (!isRef && !isOut && Check(TokenType.Identifier) && PeekAhead(1).Type == TokenType.Colon)
        {
            var nameTok = Advance();
            argName = nameTok.Literal;
            _ = Advance(); // consume ':'
        }

        if (!isOut || !Match(TokenType.Var)) return (Expression(), isRef, isOut, argName);
        // out var <ident> or out var _
        var name = ConsumeIdentifier("Expect variable name after 'out var'.");
        if (name.Literal == "_")
            return (DiscardExpr.Create(name.LineNumber, name.Column), false, true, null);
        // out var x, inline declaration
        return (VariableExpr.Create(name.Literal, name.LineNumber, name.Column), false, true, argName);

    }

    // ─── Statements ───────────────────────────────────────────────────────

    /// <summary>
    /// Two statements used to fail several tokens after the real mistake, with a message about a MISSING SEMICOLON, and
    /// both are replaced here rather than in the grammar. Measured: <c>var out = 1;</c> answered
    /// "Expect ';' after expression (found 'Out')" and ALSO warned that <c>var</c> was a reserved keyword, blaming the
    /// innocent token, while <c>var @out = 1;</c> works; <c>{a: 1};</c> answered "Expect ';' after expression (found
    /// 'Colon')", while <c>var d = { a: 1 };</c> and <c>({ a: 1 });</c> both work.
    /// <para>
    /// This CANNOT refuse anything the parse accepts: the wrapper calls <see cref="StatementCore"/> unchanged and only
    /// replaces the error it throws, so the 31 keywords that are tolerated as names today (<c>var class = 5;</c> is a
    /// pinned path, it warns and works) keep answering exactly as they did.
    /// </para>
    /// </summary>
    private Stmt Statement()
    {
        var afterVar = Check(TokenType.Var) ? PeekAhead(1) : default;
        var nameIsKeyword = afterVar.Type != TokenType.Identifier && afterVar.Type != TokenType.LeftParen
            && afterVar.Type != TokenType.Eof && !string.IsNullOrEmpty(afterVar.Literal)
            && char.IsLetter(afterVar.Literal[0]);
        var looksLikeDictionary = Check(TokenType.LeftBrace)
            && (PeekAhead(1).Type is TokenType.Identifier or TokenType.String or TokenType.CharLiteral
                or TokenType.Number or TokenType.LeftBracket)
            && PeekAhead(2).Type == TokenType.Colon;

        if (!nameIsKeyword && !looksLikeDictionary)
            return StatementCore();

        try
        {
            return StatementCore();
        }
        catch (ScriptException ex) when (ex.ErrorCode == ErrorCode.ParseError)
        {
            if (nameIsKeyword)
                throw Error($"'{afterVar.Literal}' is a keyword, so it cannot be a name here: write '@{afterVar.Literal}' to keep that name, or choose another one.", afterVar);
            throw Error("A statement cannot be a dictionary literal: a '{' at the start of a statement opens a BLOCK, and 'a: 1' is not a statement. Put the literal in a declaration or in parentheses, as in  var d = { a: 1 };  or  ({ a: 1 });", Peek());
        }
    }

    private Stmt StatementCore()
    {
        while (Match(TokenType.Semicolon)) { }
        if (Match(TokenType.Debugger))
        {
            var token = Previous();
            _ = Consume(TokenType.Semicolon, "Expect ';' after 'debugger'.");
            return DebuggerStmt.Create(token.LineNumber, token.Column);
        }
        if (Check(TokenType.Else)) throw Error("Unexpected 'else'. 'else' must follow an 'if' statement.", Peek());
        // A ref-RETURNING declaration, `ref int Slot() { ... }`: its call answers a REFERENCE (C# ref returns), which a
        // `return ref <target>` supplies. It has to be recognised BEFORE the ref-local arm, whose follower is `=`.
        if (Check(TokenType.Ref) && IsAtTypedDeclaration(_current + 1, TokenType.LeftParen))
        {
            _ = Advance();                                        // ref
            var refReturnType = ParseType(allowVoid: false);
            var refFuncName = ConsumeIdentifier("Expect the function name after `ref <type>`.");
            return FunctionDeclaration(refFuncName, refReturnType, isRefReturn: true);
        }
        // `ref int r = ref x;` is a DECLARATION with an explicit type, the shape C# requires for a ref local that wants
        // one. It is a normal `VarDeclStmt` whose initializer is the cell expression (`var r = ref x;` reaches the same
        // place through the expression arm), so nothing downstream has to know a ref local exists.
        if (Check(TokenType.Ref) && IsAtTypedDeclaration(_current + 1, TokenType.Equal))
        {
            var refToken = Advance();
            var refType = ParseType(allowVoid: false);
            var refName = ConsumeIdentifier("Expect variable name.");
            _ = Consume(TokenType.Equal, "Expect '=' after a reference declaration.");
            var refInit = Expression();
            _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
            // The written type is DOCUMENTATION: the local holds the CELL, whose value type is the target's, so declaring
            // it as `object` keeps every engine path (the tree's declaration coercion and the VM's global store) from
            // converting the cell itself. `typeof(r)` still answers what the reference points at, because a read
            // dereferences it.
            _ = refType;
            var refDeclType = new TypeInfo(TokenType.Object, true, 0, 0, null);
            return VarDeclStmt.Create(refName.Literal, refInit, refDeclType, false, refName.WasEscaped,
                refToken.LineNumber, refToken.Column);
        }
        if (Match(TokenType.Import)) return ImportStatement();
        if (Check(TokenType.Namespace)) return NamespaceStatement();
        if (Match(TokenType.If)) return IfStatement();
        if (Match(TokenType.While)) return WhileStatement();
        if (Peek().Type == TokenType.Identifier && Peek().Literal.Equals("do", StringComparison.Ordinal))
        {
            _ = Advance(); // consume 'do'
            var doToken = Previous();
            var body = Statement();
            // Ensure 'while' follows (it's a keyword token)
            _ = Consume(TokenType.While, "Expect 'while' after 'do' body.");
            _ = Consume(TokenType.LeftParen, "Expect '(' after 'while'.");
            var condition = Expression();
            _ = Consume(TokenType.RightParen, "Expect ')' after condition.");
            _ = Consume(TokenType.Semicolon, "Expect ';' after do-while.");
            // Transform: do { body; } while(cond); → { body; while(cond) { body; } }
            var loopBody = new List<Stmt>();
            if (body is BlockStmt block)
                loopBody.AddRange(block.Statements);
            else
                loopBody.Add(body);
            var whileBody = WhileStmt.Create(condition, loopBody, doToken.LineNumber, doToken.Column);
            var wrapper = new List<Stmt>();
            if (body is BlockStmt b)
                wrapper.AddRange(b.Statements);
            else
                wrapper.Add(body);
            wrapper.Add(whileBody);
            return BlockStmt.Create(wrapper, doToken.LineNumber, doToken.Column);
        }
        if (Peek().Type == TokenType.Identifier && Peek().Literal.Equals("while", StringComparison.Ordinal))
        {
            _ = Advance();
            return WhileStatement();
        }
        // `await foreach` (C#'s ASYNC STREAM). `foreach` does NOT start an await OPERAND, so without this arm the
        // two tokens were read as a name `await` followed by a call named `foreach`: measured, the parser warned
        // "Using reserved keyword 'var' as an identifier" and then failed with "Expect ')' after arguments". The
        // combination is only legal as a STATEMENT, which is why the arm sits here rather than in the expression
        // path. Inside a non-async body the expression path's own refusal answers, naming the fix ("Declare the
        // function `async` to await a task here").
        if (Check(TokenType.Identifier) && Peek().Literal == "await"
            && (_inAsyncBody || _bodyDepth == 0)
            && PeekAhead(1).Type == TokenType.Using)
        {
            // `await using`, C#'s awaited disposal: the same parse shape as the `await foreach` arm above.
            _ = Advance();   // consume `await`
            _ = Match(TokenType.Using);
            return UsingStatement(isAwait: true);
        }
        if (Check(TokenType.Identifier) && Peek().Literal == "await"
            && (_inAsyncBody || _bodyDepth == 0)
            && (PeekAhead(1).Type == TokenType.Foreach
                || (PeekAhead(1).Type == TokenType.Identifier && PeekAhead(1).Literal == "foreach")))
        {
            _ = Advance();   // consume `await`
            if (!Match(TokenType.Foreach)) _ = Advance();   // the identifier spelling of `foreach`
            return ForeachStatement(isAwait: true);
        }
        if (Match(TokenType.Foreach) || (Check(TokenType.Identifier) && Peek().Literal == "foreach"))
        {
            if (!Match(TokenType.Foreach)) _ = Advance(); // consume the identifier as if it were a keyword
            return ForeachStatement();
        }
        if (Check(TokenType.Return) && PeekAhead(1).Type == TokenType.Dot)
        {
            // return.Value expression, not a return statement
            var retToken = Advance(); // consume 'return'
            _ = Advance(); // consume '.'
            _ = Consume(TokenType.Identifier, "Expect 'Value' after 'return.'.");
            var rv = ReturnValueExpr.Create(retToken.LineNumber, retToken.Column);

            // Parse optional assignment: return.Value = expr;
            if (Match(TokenType.Equal))
            {
                var eqToken = Previous();
                var value = Expression();
                var assign = PropertyAssignExpr.Create(rv, "Value", value, eqToken.LineNumber, eqToken.Column);
                _ = Consume(TokenType.Semicolon, "Expect ';' after expression.");
                return ExprStmt.Create(assign, eqToken.LineNumber, eqToken.Column);
            }

            var stmt = ExprStmt.Create(rv, retToken.LineNumber, retToken.Column);
            _ = Consume(TokenType.Semicolon, "Expect ';' after expression.");
            return stmt;
        }
        if (Match(TokenType.Return)) return ReturnStatement();
        if (Match(TokenType.For)) return ForStatement();
        if (Match(TokenType.Switch)) return SwitchStatement();
        if (Match(TokenType.Break)) return BreakStatement();
        if (Match(TokenType.Continue)) return ContinueStatement();
        if (Match(TokenType.LeftBrace)) return BlockStmt.Create(Block(), Previous().LineNumber, Previous().Column);
        if (Match(TokenType.Try)) return TryStatement();
        if (Match(TokenType.Using)) return UsingStatement();
        if (Match(TokenType.Throw)) return ThrowStatement();
        return ExpressionStatement();
    }

    private TryStmt TryStatement()
    {
        var tryToken = Previous();
        _ = Consume(TokenType.LeftBrace, "Expect '{' after 'try'.");
        var body = Block();
        var catches = new List<CatchClause>();
        // A clause that catches EVERY thrown value makes every clause after it unreachable, and that is refused
        // here rather than left to run dead, the way C# refuses a `catch` after a catch-all. Two spellings do it,
        // and both are measured: `catch { }` / `catch (var ex)` (an untyped Object clause below), and
        // `catch (ErrorInfo ex)`, because a thrown value is ALWAYS wrapped in an ErrorInfo, whatever was thrown
        // (`throw 42` is caught by `catch (ErrorInfo ex)` and by `catch (var ex)`, and by nothing else).
        var seenCatchAll = false;

        while (Match(TokenType.Catch))
        {
            var catchToken = Previous();
            List<Stmt>? catchBody;
            if (Check(TokenType.LeftBrace))
            {
                if (seenCatchAll) RefuseUnreachableCatch(catchToken);
                _ = Consume(TokenType.LeftBrace, "Expect '{' before catch body.");
                catchBody = Block();
                catches.Add(new CatchClause(new TypeInfo(TokenType.Object, true, 0, 0, null), "",
                    BlockStmt.Create(catchBody, catchToken.LineNumber, catchToken.Column), catchToken.LineNumber, catchToken.Column));
                seenCatchAll = true;
                continue;
            }
            _ = Consume(TokenType.LeftParen, "Expect '(' after 'catch'.");
            TypeInfo exceptionType;
            string varName;
            if (Match(TokenType.Var))
            {
                if (seenCatchAll) RefuseUnreachableCatch(catchToken);
                exceptionType = new TypeInfo(TokenType.Object, true, 0, 0, null);
                varName = ConsumeIdentifier("Expect exception variable name.").Literal;
            }
            else
            {
                exceptionType = ParseType(allowVoid: false);
                varName = Check(TokenType.Identifier) ? ConsumeIdentifier("Expect exception variable name.").Literal : "";
                // A SINGLE IDENTIFIER IS THE VARIABLE, not a type: `catch (e)` means what `catch (var e)` means, an
                // untyped clause that binds `e`. Parsed as a TYPE it became a clause typed `e`, which (a) bound no
                // variable, so `e.Message` was "Undefined variable 'e'", (b) matched nothing, and (c) refused a
                // `--no-source` bundle at LOAD with "type 'e' referenced by the bytecode is not available in this
                // environment" (measured: form_run_focus_test, tabcontrol_wrapper_test). The one name kept as a TYPE is
                // `ErrorInfo`, the wrapper a thrown value is always carried in, so `catch (ErrorInfo)` still names the
                // type it names. Corpus check before the change: 780 catch clauses, every one of them
                // `catch (var x)`, `catch (Type x)` or `catch { }`, except the two `catch (e)` this fixes.
                if (varName.Length == 0 && exceptionType is { TypeName: { Length: > 0 } bareName, IsArray: false }
                    && !string.Equals(bareName, "ErrorInfo", StringComparison.Ordinal))
                {
                    varName = bareName;
                    exceptionType = new TypeInfo(TokenType.Object, true, 0, 0, null);
                }
            }
            _ = Consume(TokenType.RightParen, "Expect ')' after catch variable.");
            _ = Consume(TokenType.LeftBrace, "Expect '{' before catch body.");
            catchBody = Block();
            if (seenCatchAll) RefuseUnreachableCatch(catchToken);
            catches.Add(new CatchClause(exceptionType, varName,
                BlockStmt.Create(catchBody, catchToken.LineNumber, catchToken.Column), catchToken.LineNumber, catchToken.Column));
            if (IsCatchAll(exceptionType)) seenCatchAll = true;
        }

        Stmt? finallyBlock = null;
        if (!Match(TokenType.Finally))
            return TryStmt.Create(BlockStmt.Create(body, tryToken.LineNumber, tryToken.Column), catches, finallyBlock,
                tryToken.LineNumber, tryToken.Column);
        var finallyToken = Previous();
        _ = Consume(TokenType.LeftBrace, "Expect '{' after 'finally'.");
        finallyBlock = BlockStmt.Create(Block(), finallyToken.LineNumber, finallyToken.Column);

        return TryStmt.Create(BlockStmt.Create(body, tryToken.LineNumber, tryToken.Column), catches, finallyBlock, tryToken.LineNumber, tryToken.Column);
    }

    /// <summary>True when a catch clause catches EVERY thrown value: `catch { }` and `catch (var ex)` parse to an
    /// untyped `object` clause, and `catch (ErrorInfo ex)` names the wrapper a thrown value is always carried in.
    /// Measured: a thrown 42, string, bool and null each go to `catch (ErrorInfo ex)` and to `catch (var ex)`, and
    /// to nothing else, so a clause after either can never run.</summary>
    private static bool IsCatchAll(TypeInfo t) =>
        (t.BaseType == TokenType.Object && t.TypeName == null && !t.IsArray)
        || (t.TypeName is { } n && n.Equals("ErrorInfo", StringComparison.OrdinalIgnoreCase));

    /// <summary>The refusal for a clause that can never run, naming what to write instead, because the reader's
    /// next keystroke is what a refusal owes them.</summary>
    private void RefuseUnreachableCatch(Token catchToken)
        => throw Error("This catch clause can never run: an earlier clause catches every thrown value, because a "
            + "thrown value is always an ErrorInfo, so `catch (var ex)` and `catch (ErrorInfo ex)` match all of "
            + "them. Remove this clause, or make the earlier one narrower.", catchToken);

    private ThrowStmt ThrowStatement()
    {
        var throwToken = Previous();
        Expr? expr = null;
        if (!Check(TokenType.Semicolon)) expr = Expression();
        _ = Consume(TokenType.Semicolon, "Expect ';' after throw.");
        return ThrowStmt.Create(expr ?? NullExpr.Create(throwToken.LineNumber, throwToken.Column), throwToken.LineNumber, throwToken.Column);
    }

    private IfStmt IfStatement()
    {
        var ifToken = Previous();
        _ = Consume(TokenType.LeftParen, "Expect '(' after 'if'.");
        var condition = Expression();
        _ = Consume(TokenType.RightParen, "Expect ')' after condition.");
        var then = ParseBranchBody();
        var els = Match(TokenType.Else) ? ParseBranchBody() : [];
        return IfStmt.Create(condition, then, els, ifToken.LineNumber, ifToken.Column);
    }

    private List<Stmt> ParseBranchBody()
    {
        return Match(TokenType.LeftBrace) ? Block() : [Statement()];
    }

    private WhileStmt WhileStatement()
    {
        var whileToken = Previous();
        _ = Consume(TokenType.LeftParen, "Expect '(' after 'while'.");
        var condition = Expression();
        _ = Consume(TokenType.RightParen, "Expect ')' after condition.");
        var whileBody = Match(TokenType.LeftBrace) ? Block() : [Statement()];
        return WhileStmt.Create(condition, whileBody, whileToken.LineNumber, whileToken.Column);
    }

    private ForeachStmt ForeachStatement(bool isAwait = false)
    {
        var foreachToken = Previous();
        _ = Consume(TokenType.LeftParen, "Expect '(' after 'foreach'.");
        TypeInfo? loopVarType;
        if (Match(TokenType.Var)) loopVarType = null;
        else if (IsTypeStart(Peek()) || IsAtTypedDeclaration(_current, TokenType.In)) loopVarType = ParseType();
        else throw Error("Expected a type or 'var'", Peek());
        // FP: foreach (var (a, b) in ...), destructure each item into the names
        IReadOnlyList<string>? loopVarNames = null;
        string varName;
        if (Check(TokenType.LeftParen))
        {
            _ = Advance(); // '('
            var names = new List<string>();
            do { names.Add(ConsumeIdentifier("Expect loop variable name.").Literal); }
            while (Match(TokenType.Comma));
            _ = Consume(TokenType.RightParen, "Expect ')' after loop variable names.");
            if (names.Count == 0)
                throw Error("Expect at least one loop variable name.", Peek());
            loopVarNames = names;
            varName = "";
        }
        else
        {
            varName = ConsumeIdentifier("Expect loop variable name.").Literal;
        }
        _ = Consume(TokenType.In, "Expect 'in' after variable name.");
        var collection = Expression();
        _ = Consume(TokenType.RightParen, "Expect ')' after collection.");
        var foreachBody = Match(TokenType.LeftBrace) ? Block() : [Statement()];
        return ForeachStmt.Create(varName, loopVarType, collection, foreachBody, foreachToken.LineNumber, foreachToken.Column, loopVarNames, isAwait);
    }

    private ReturnStmt ReturnStatement()
    {
        var returnToken = Previous();
        Expr? value = null;
        if (Peek().Type != TokenType.Semicolon) value = Expression();
        _ = Consume(TokenType.Semicolon, "Expect ';' after return.");
        return ReturnStmt.Create(value, returnToken.LineNumber, returnToken.Column);
    }


    /// <summary>Structural equality between two constant patterns (literal value + declared type,
    /// so `1` and `1` collide while `1` and `1L` do not).</summary>
    private static bool SameConstantPattern(Pattern a, Pattern b) => (a, b) switch
    {
        (ConstantPattern { Expr: NumberExpr x }, ConstantPattern { Expr: NumberExpr y }) => x.Value == y.Value && x.Type == y.Type,
        (ConstantPattern { Expr: StringExpr x }, ConstantPattern { Expr: StringExpr y }) => x.Value == y.Value,
        (ConstantPattern { Expr: BoolExpr x }, ConstantPattern { Expr: BoolExpr y }) => x.Value == y.Value,
        (ConstantPattern { Expr: CharExpr x }, ConstantPattern { Expr: CharExpr y }) => x.Value == y.Value,
        (ConstantPattern { Expr: NullExpr }, ConstantPattern { Expr: NullExpr }) => true,
        _ => false
    };

    /// <summary>Warnings for arms that cannot fire and (optionally) a switch with no wildcard.
    /// Warnings only, behaviour never changes.</summary>
    private void WarnArmDiagnostics(IEnumerable<Pattern> patterns, bool warnNonExhaustive, Token owner)
    {
        var seenWildcard = false;
        var constants = new List<Pattern>();
        foreach (var pattern in patterns)
        {
            var token = new Token(TokenType.Identifier, "", pattern.Line, pattern.Column);
            if (seenWildcard)
            {
                Warn("Unreachable pattern: an earlier arm already matches everything.", token);
                continue;
            }
            if (pattern is DiscardPattern)
            {
                seenWildcard = true;
                continue;
            }
            if (pattern is ConstantPattern)
            {
                if (constants.Any(c => SameConstantPattern(c, pattern)))
                    Warn("Duplicate pattern: this constant arm can never be reached.", token);
                else constants.Add(pattern);
            }
        }
        if (warnNonExhaustive && !seenWildcard)
            Warn("This 'switch' has no wildcard '_' or 'default' arm - non-exhaustive switch.", owner);
    }

    private SwitchStmt SwitchStatement()
    {
        var start = Previous(); // 'switch' keyword
        _ = Consume(TokenType.LeftParen, "Expect '(' after 'switch'.");
        var subject = Expression();
        _ = Consume(TokenType.RightParen, "Expect ')' after switch expression.");
        _ = Consume(TokenType.LeftBrace, "Expect '{' after switch header.");

        var cases = new List<CaseClause>();
        var hasDefault = false;

        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            var patterns = new List<Pattern>();
            // Consume first 'case' or 'default'
            if (!Match(TokenType.Case) && !Check(TokenType.Default))
                throw Error("Expect 'case' or 'default'.", Peek());
            do
            {
                var pattern = Match(TokenType.Default)
                    ? new DiscardPattern { Line = Previous().LineNumber, Column = Previous().Column }
                    : ParsePattern();

                if (pattern is DiscardPattern)
                {
                    if (hasDefault)
                        throw Error("Duplicate 'default' case in switch.", Previous());
                    hasDefault = true;
                }

                patterns.Add(pattern);
            } while (Match(TokenType.Case));

            Expr? guard = null;
            if (Match(TokenType.When)) guard = Expression();

            if (Match(TokenType.Arrow))
            {
                var value = Expression();
                _ = Consume(TokenType.Semicolon, "Expect ';' after switch expression arm.");
                var body = new List<Stmt> { ReturnStmt.Create(value, value.Line, value.Column) };
                cases.Add(new CaseClause(patterns, guard, body));
            }
            else if (Check(TokenType.LeftBrace))
            {
                _ = Advance();
                var body = Block();
                cases.Add(new CaseClause(patterns, guard, body));
            }
            else
            {
                // Traditional body: case N: ... break;
                // Support multi-label: case 1: case 2: case 3: body
                _ = Consume(TokenType.Colon, "Expect ':' after case pattern.");
                while (Check(TokenType.Case))
                {
                    _ = Advance(); // consume 'case'
                    Pattern nextPattern;
                    if (Check(TokenType.Default))
                    {
                        _ = Advance();
                        nextPattern = new DiscardPattern { Line = Previous().LineNumber, Column = Previous().Column };
                        if (hasDefault) throw Error("Duplicate 'default' case in switch.", Previous());
                        hasDefault = true;
                    }
                    else
                        nextPattern = ParsePattern();
                    patterns.Add(nextPattern);
                    _ = Consume(TokenType.Colon, "Expect ':' after case pattern.");
                }
                var body = new List<Stmt>();
                while (!Check(TokenType.RightBrace) && !IsAtEnd())
                {
                    if (Check(TokenType.Case) || Check(TokenType.Default))
                        break;
                    body.Add(Statement());
                }
                cases.Add(new CaseClause(patterns, guard, body));
            }
        }

        _ = Consume(TokenType.RightBrace, "Expect '}' after switch body.");
        WarnArmDiagnostics(cases.SelectMany(c => c.Patterns), warnNonExhaustive: true, start);
        return SwitchStmt.Create(subject, cases, start.LineNumber, start.Column);
    }

    private Stmt UsingStatement(bool isAwait = false)
    {
        var start = Previous();

        // C#-style `using var x = expr;`, no parentheses, no block; disposed at scope exit.
        if (Check(TokenType.Var))
        {
            _ = Advance(); // consume 'var'
            var varName = ConsumeIdentifier("Expect variable name.").Literal;
            _ = Consume(TokenType.Equal, "Expect '=' after variable name.");
            var initializer = Expression();
            _ = Consume(TokenType.Semicolon, "Expect ';' after using declaration.");
            return UsingDeclStmt.Create(varName, initializer, start.LineNumber, start.Column, isAwait);
        }

        _ = Consume(TokenType.LeftParen, "Expect '(' after 'using'.");
        // `using (expression)` / `await using (expression)`, C#'s form for a resource that is already a
        // VARIABLE. It binds no name of its own, so the declaration below is skipped and the expression is
        // bound to an internal name nothing can collide with (the tree defines it in the using's own scope).
        if (!Check(TokenType.Var) && !(Check(TokenType.Identifier) && PeekAhead(1).Type == TokenType.Equal))
        {
            var expression = Expression();
            _ = Consume(TokenType.RightParen, "Expect ')' after the using expression.");
            var body0 = Statement();
            return UsingStmt.Create($"__using{++_usingExprCounter}", expression, body0, start.LineNumber, start.Column, isAwait);
        }
        if (Match(TokenType.Var)) { } // skip optional 'var' keyword
        var varName2 = ConsumeIdentifier("Expect variable name.").Literal;
        _ = Consume(TokenType.Equal, "Expect '=' after variable name.");
        var initializer2 = Expression();
        _ = Consume(TokenType.RightParen, "Expect ')' after initializer.");
        var body = Statement();
        return UsingStmt.Create(varName2, initializer2, body, start.LineNumber, start.Column, isAwait);
    }

    /// <summary>Names the internal binding of a `using (expression)`, which binds no user-visible name.</summary>
    private int _usingExprCounter;

    /// <summary>True when the '(' at the current position opens a TUPLE pattern (a comma at
    /// paren depth 1 before the matching ')'), as opposed to a grouped pattern.</summary>
    private bool IsTuplePatternAhead()
    {
        var depth = 0;
        for (var i = _current; i < _tokens.Count; i++)
        {
            var t = _tokens[i].Type;
            if (t == TokenType.LeftParen) depth++;
            else if (t == TokenType.RightParen)
            {
                depth--;
                if (depth <= 0) return false;
            }
            else if (t == TokenType.Comma && depth == 1) return true;
            else if (depth == 1 && t is TokenType.Arrow or TokenType.When) return false;
        }
        return false;
    }

    /// <summary>Element binding names of a pattern (parser-side; used for the logical binding rules).</summary>
    private static List<string> PatternBindings(Pattern pattern)
    {
        var names = new List<string>();
        switch (pattern)
        {
            case VarPattern vp: names.Add(vp.BindingName); break;
            case TypePattern tp when tp.BindingName != null: names.Add(tp.BindingName); break;
            case TupleArmPattern tap:
                foreach (var el in tap.Elements)
                    if (el.Name != null) names.Add(el.Name);
                break;
            case GroupPattern gp: names.AddRange(PatternBindings(gp.Inner)); break;
            case PropertyPattern pp:
                foreach (var (_, memberPattern) in pp.Members)
                    names.AddRange(PatternBindings(memberPattern));
                break;
            case ListPattern lp:
                foreach (var element in lp.Elements) names.AddRange(PatternBindings(element));
                if (lp.Slice?.CaptureName != null) names.Add(lp.Slice.CaptureName);
                break;
            case LogicalPattern { Kind: LogicalKind.And } ap: names.AddRange(PatternBindings(ap.Left)); names.AddRange(PatternBindings(ap.Right!)); break;
            case LogicalPattern { Kind: LogicalKind.Or } op: names.AddRange(PatternBindings(op.Left)); break;
            case LogicalPattern: break;   // `not` discards its bindings
        }
        return names;
    }

    /// <summary>True while parsing the pattern on the right of `is`, where tuple patterns may
    /// contain literal elements (`v is ("bird", 10, false)`).</summary>
    private bool _allowTupleLiteralsInPattern;

    /// <summary>True while parsing a `match` arm pattern, bare identifiers are always-match
    /// variable patterns there, which is the pre-unification behaviour.</summary>
    private bool _inMatchArmPattern;

    private Pattern ParsePattern()
    {
        var patternStart = Peek();
        return ParsePatternTail(ParseNotPattern(), patternStart);
    }

    /// <summary>The COMBINATOR loop of a pattern, given the first operand already parsed. Factored out so an `is`
    /// expression can hand it a first operand of its own: `x is not null and not string` reads as
    /// <c>(not null) and (not string)</c> (C#'s precedence, where `not` binds tighter than `and`), so the `is` handler
    /// builds the leading <c>not</c> itself and this loop continues from there instead of wrapping the WHOLE combination
    /// the way <c>Not(ParsePattern())</c> would.</summary>
    private Pattern ParsePatternTail(Pattern left, Token patternStart)
    {
        while (Check(TokenType.And) || Check(TokenType.Or))
        {
            var op = Advance();
            var right = ParseNotPattern();
            var isAnd = op.Type == TokenType.And;
            if (isAnd)
            {
                // `and` binds the union of both sides, a repeated name would be ambiguous.
                var names = PatternBindings(left);
                names.AddRange(PatternBindings(right));
                var dup = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
                if (dup != null)
                    throw Error($"Duplicate binding '{dup.Key}' in pattern.", op);
            }
            else
            {
                // `or` alternatives must bind the same names, or the true branch could see a
                // name that only one alternative defines.
                var l = PatternBindings(left).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var r = PatternBindings(right).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (!l.SequenceEqual(r, StringComparer.Ordinal))
                    throw Error("Both sides of 'or' must bind the same names.", op);
            }
            left = new LogicalPattern(isAnd ? LogicalKind.And : LogicalKind.Or, left, right)
            { Line = op.LineNumber, Column = op.Column };
        }
        return AttachPatternSourceText(left, patternStart);
    }

    /// <summary>Parses ONE pattern from a standalone text, which is how a bundle rebuilds the pattern a
    /// <c>PatternEngineDescriptor</c> carries: the compiler wrote the pattern's own source (Pattern.SourceText) and
    /// the loader hands it back here, so a pattern means exactly what this parser says it means on both sides. Text
    /// the parser does not consume is ignored, because a re-parse is handed the pattern's span alone, and a text that
    /// does not parse raises this parser's own refusal.</summary>
    internal static Pattern ParsePatternFromText(string text)
    {
        var parser = new Parser(new Lexer(text.AsMemory()).GetTokens());
        // THE SAME FLAG A SOURCE PARSE SETS around its `is <pattern>`: a TUPLE pattern (`(7, 8)`) is allowed only
        // there, because outside one a leading `(` reads as a type or a cast. A bundle re-parses the pattern from the
        // text it carries, so without this a stripped bundle refused the whole blob with "Expected a type" for a
        // subject the file parses fine (measured on `o is (7, 8)` compiled to a bundle).
        parser._allowTupleLiteralsInPattern = true;
        return parser.ParsePattern();
    }

    private Pattern ParseNotPattern()
    {
        if (Check(TokenType.Not))
        {
            var notToken = Advance();
            var inner = ParseNotPattern();
            return new LogicalPattern(LogicalKind.Not, inner, null)
            { Line = notToken.LineNumber, Column = notToken.Column };
        }
        return ParsePrimaryPattern();
    }

    private Pattern ParsePrimaryPattern()
    {
        var start = Peek();

        // Tuple pattern in a pattern position: (Type1 name1, Type2 name2), matched positionally.
        // A parenthesized form WITHOUT a top-level comma is a grouped pattern instead.
        if (Check(TokenType.LeftParen))
        {
            if (IsTuplePatternAhead())
            {
                var specs = ParseTupleElementPatterns(allowLiterals: _allowTupleLiteralsInPattern);
                return new TupleArmPattern(specs) { Line = start.LineNumber, Column = start.Column };
            }
            var open = Advance();
            var inner = ParsePattern();
            _ = Consume(TokenType.RightParen, "Expect ')' after grouped pattern.");
            return new GroupPattern(inner) { Line = open.LineNumber, Column = open.Column };
        }

        // List pattern: [ a, b ], [ a, .. ], [ .., z ], [ a, .., z ], [ a, .. var rest ]
        if (Check(TokenType.LeftBracket))
        {
            var open = Advance();
            var elements = new List<Pattern>();
            SliceSpec? slice = null;
            while (!Check(TokenType.RightBracket) && !IsAtEnd())
            {
                if (Check(TokenType.DotDot))
                {
                    var dots = Advance();
                    if (slice != null)
                        throw Error("A list pattern may contain at most one slice '..'.", dots);
                    string? capture = null;
                    if (Match(TokenType.Var))
                        capture = ConsumeIdentifier("Expect slice capture name after '.. var'.").Literal;
                    else if (Check(TokenType.Identifier) && Peek().Literal == "_")
                        _ = Advance();      // `.. _`, explicit discard: matches like bare `..`, binds nothing
                    slice = new SliceSpec(elements.Count, capture, dots.LineNumber, dots.Column);
                }
                else elements.Add(ParsePattern());
                if (!Check(TokenType.RightBracket))
                    _ = Consume(TokenType.Comma, "Expect ',' between list-pattern elements.");
            }
            _ = Consume(TokenType.RightBracket, "Expect ']' after list pattern.");
            if (elements.Count == 0 && slice == null)
                throw Error("List pattern must contain at least one element.", open);
            return new ListPattern(elements, slice) { Line = open.LineNumber, Column = open.Column };
        }

        // Bare identifier in a MATCH arm = always-match variable pattern (bind the subject),
        // as before the shared grammar: `5 match { five => "yes", _ => "no" }` takes the first arm.
        // `LeftBrace` is in the exclusion list because `Shape { Name: "square" }` is a TYPE-qualified property pattern
        // (a bare name followed by an arm body could never mean "bind the subject", so nothing depended on it).
        if (_inMatchArmPattern && Check(TokenType.Identifier) && Peek().Literal != "_"
            && !IsReservedKeyword(Peek().Literal)
            && PeekAhead(1).Type is not (TokenType.Identifier or TokenType.Less or TokenType.LeftBracket
                or TokenType.Dot or TokenType.Question or TokenType.LeftBrace))
        {
            var bare = Advance();
            return new VarPattern(bare.Literal) { Line = bare.LineNumber, Column = bare.Column };
        }

        // Property pattern: { Member: pattern, ... }
        if (Check(TokenType.LeftBrace))
            return ParsePropertyPatternBody();

        if (start.Type is TokenType.Less or TokenType.Greater or TokenType.LessEqual or TokenType.GreaterEqual)
        {
            _ = Advance();
            var right = Expression();
            return new RelationalPattern(start.Type, right) { Line = start.LineNumber, Column = start.Column };
        }

        if (Match(TokenType.Var))
        {
            var name = ConsumeIdentifier("Expect variable name after 'var'.").Literal;
            return new VarPattern(name) { Line = start.LineNumber, Column = start.Column };
        }

        // Discard: case _:
        if (Check(TokenType.Identifier) && Peek().Literal == "_")
        {
            _ = Advance();
            return new DiscardPattern { Line = Previous().LineNumber, Column = Previous().Column };
        }

        // Special: 'delegate' keyword token refers to System.Delegate type
        if (start.Type == TokenType.Delegate)
        {
            _ = Advance();
            var dt = new TypeInfo(TokenType.Object, false, 0, 0, "Delegate");
            if (!Check(TokenType.Identifier) || Peek().Literal == "when" || Peek().Type == TokenType.Colon
                || Peek().Type == TokenType.Arrow || Peek().Type == TokenType.Case ||
                Peek().Type == TokenType.Default)
                return new TypePattern(dt, null) { Line = start.LineNumber, Column = start.Column };
            var b = ConsumeIdentifier("Expect variable name.").Literal;
            return new TypePattern(dt, b) { Line = start.LineNumber, Column = start.Column };
        }

        // Dotted identifier chain in a pattern position = member-access CONSTANT
        // (e.g. `case HttpStatus.NotFound:`, `HttpStatus.NotFound => ...`). Qualified TYPE
        // patterns are not part of the grammar, and sending the chain to the type branch made
        // enum-member arms silently never match.
        if (Check(TokenType.Identifier) && PeekAhead(1).Type == TokenType.Dot)
        {
            var memberExpr = Expression();
            return new ConstantPattern(memberExpr) { Line = start.LineNumber, Column = start.Column };
        }

        if (IsTypeStart(start) || start.Type == TokenType.Void || start.Type == TokenType.Identifier)
        {
            var type = ParseType(allowVoid: false);
            if (!Check(TokenType.Identifier) || Peek().Literal == "when" || Peek().Type == TokenType.Colon
                || Peek().Type == TokenType.Arrow || Peek().Type == TokenType.Case ||
                Peek().Type == TokenType.Default)
            {
                // `Shape { Name: "square" }`: a TYPE test AND a property test, which is ONE pattern, so the two engines
                // need nothing new for it (the And case is what `is int and > 3` already runs, on both).
                if (Check(TokenType.LeftBrace))
                    return new LogicalPattern(LogicalKind.And,
                        new TypePattern(type, null) { Line = start.LineNumber, Column = start.Column },
                        ParsePropertyPatternBody())
                    { Line = start.LineNumber, Column = start.Column };
                return new TypePattern(type, null) { Line = start.LineNumber, Column = start.Column };
            }
            var bindingName = ConsumeIdentifier("Expect variable name.").Literal;
            return new TypePattern(type, bindingName) { Line = start.LineNumber, Column = start.Column };
        }

        if (Match(TokenType.Identifier) && Previous().Literal == "_")
            return new DiscardPattern { Line = start.LineNumber, Column = start.Column };

        var value = Expression();
        return new ConstantPattern(value) { Line = start.LineNumber, Column = start.Column };
    }

    /// <summary>Parses a property pattern's braces alone: <c>{ Member: pattern, ... }</c>. One implementation, used by the
    /// primary-pattern arm and by the `is` handler's TYPE-qualified form, so the two cannot disagree about what a member
    /// list is.</summary>
    private Pattern ParsePropertyPatternBody()
    {
        var open = Peek();
        _ = Advance();   // '{'
        var members = new List<(string Name, Pattern Pattern)>();
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            string name;
            if (Check(TokenType.String)) name = Advance().Literal;
            else name = ConsumeIdentifier("Expect member name in property pattern.").Literal;
            _ = Consume(TokenType.Colon, "Expect ':' after member name in property pattern.");
            members.Add((name, ParsePattern()));
            if (!Check(TokenType.RightBrace))
                _ = Consume(TokenType.Comma, "Expect ',' between property-pattern members.");
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after property pattern.");
        if (members.Count == 0)
            throw Error("Property pattern must list at least one member.", open);
        return new PropertyPattern(members) { Line = open.LineNumber, Column = open.Column };
    }

    /// <summary>Parses a TYPE-qualified property pattern from its TYPE, which the caller has already read:
    /// <c>Shape { Name: "square" }</c> is a type test AND a property test, so it is ONE <c>And</c> pattern, and the
    /// ordinary combinators and the source text a bundle carries come from the same route every other pattern takes.
    /// <c>is not Shape { ... }</c> reaches the same emitter through the primary-pattern arm, which is why both spellings
    /// mean one thing.</summary>
    private Pattern ParseQualifiedPattern(TypeInfo type, Token isToken, Token patternStart)
    {
        var savedLiterals = _allowTupleLiteralsInPattern;
        _allowTupleLiteralsInPattern = true;
        try
        {
            var qualified = new LogicalPattern(LogicalKind.And,
                new TypePattern(type, null) { Line = isToken.LineNumber, Column = isToken.Column },
                ParsePropertyPatternBody())
            { Line = isToken.LineNumber, Column = isToken.Column };
            return ParsePatternTail(qualified, patternStart);
        }
        finally { _allowTupleLiteralsInPattern = savedLiterals; }
    }

    private ForStmt ForStatement()
    {
        var forToken = Previous();
        _ = Consume(TokenType.LeftParen, "Expect '(' after 'for'.");
        Stmt? init = null;
        if (!Check(TokenType.Semicolon))
        {
            var isVar = Match(TokenType.Var);
            if (isVar || IsTypeStart(Peek()) || IsAtTypedDeclaration(_current, TokenType.Equal, TokenType.Semicolon))
            {
                var type = isVar ? new TypeInfo(TokenType.Object, true, 0, 0, null) : ParseType();
                var nameToken = ConsumeIdentifier("Expect variable name.");
                Expr? initializer = null;
                if (Match(TokenType.Equal)) initializer = Expression();
                init = VarDeclStmt.Create(nameToken.Literal, initializer, type, false, nameToken.WasEscaped, nameToken.LineNumber, nameToken.Column);
            }
            else
            {
                var expr = Expression();
                init = ExprStmt.Create(expr, expr.Line, expr.Column);
            }
        }
        _ = Consume(TokenType.Semicolon, "Expect ';' after loop init.");
        Expr condition = BoolExpr.Create(true, forToken.LineNumber, forToken.Column);
        if (!Check(TokenType.Semicolon)) condition = Expression();
        _ = Consume(TokenType.Semicolon, "Expect ';' after loop condition.");
        Expr? increment = null;
        if (!Check(TokenType.RightParen)) increment = Expression();
        _ = Consume(TokenType.RightParen, "Expect ')' after loop increment.");
        var body = new List<Stmt>();
        if (Match(TokenType.LeftBrace))
            body.AddRange(Block());
        else
            body.Add(Statement());
        return ForStmt.Create(init, condition, increment, body, forToken.LineNumber, forToken.Column);
    }

    private BreakStmt BreakStatement()
    {
        var breakToken = Previous();
        _ = Consume(TokenType.Semicolon, "Expect ';' after break.");
        return BreakStmt.Create(breakToken.LineNumber, breakToken.Column);
    }

    private ContinueStmt ContinueStatement()
    {
        var continueToken = Previous();
        _ = Consume(TokenType.Semicolon, "Expect ';' after continue.");
        return ContinueStmt.Create(continueToken.LineNumber, continueToken.Column);
    }

    private List<Stmt> Block()
    {
        var stmts = new List<Stmt>();
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            bool IsAtLocalFunctionDeclaration()
            {
                // Inside an async body a leading `await` is the OPERATOR, never a type name: `await Fails();` has
                // the exact shape of a typed local function (return type `await`, name `Fails`, then `(`), and
                // reading it that way refused a plain await of a CALL with "Expect '{' before function body".
                // The SAME predicate the statement dispatch and the typed-function sighting use: the operator is the
                // operator inside an async body AND at a TOP LEVEL, its blocks included. This line only asked
                // `_inAsyncBody`, so a bare `await Mark();` inside a top-level `try`, `if` or `while` was read as a typed
                // local function (`await` as the return type, `Mark` as its name) and refused with "Expect '{' before
                // function body", leaving a DECLARATION behind that neither engine evaluated (measured).
                if (IsAwaitOperatorHere()) return false;
                // A PURE LOOKAHEAD: it moves `_current` to parse the type, so it MUST put the position back. It did
                // not, which silently dropped the modifier the caller then consumed: measured, a local `async`
                // function parsed as a PLAIN one, so its `await` was an ordinary identifier and the body failed
                // with "Expect ';' after expression". A local `pure` was dropped the same way.
                var start = _current;
                try
                {
                    // [pure] [async] void name (  |  [pure] [async] var name (  |  [pure] [async] <type> name (  |  ...
                    var pos = _current;
                    if (Peek().Type == TokenType.Pure) pos++;
                    if (pos < _tokens.Count && _tokens[pos].Type == TokenType.Identifier && _tokens[pos].Literal == "async") pos++;
                    if (pos >= _tokens.Count) return false;
                    var t0 = _tokens[pos].Type;
                    if (t0 == TokenType.Void || t0 == TokenType.Var) return IsNameThenParen(pos + 1);
                    if (t0 == TokenType.LeftParen && IsAtTupleTypeReturnAt(pos)) return IsNameThenParen(TupleTypeEndAt(pos) + 1);
                    if (!IsTypeStart(_tokens[pos])
                        && !IsAtTypedDeclaration(pos, TokenType.LeftParen, TokenType.Less)) return false;
                    _current = pos;
                    var typeEnd = SafeParseTypeEnd();
                    _current = pos;
                    if (typeEnd < 0 || !IsNameThenParen(typeEnd)) return false;
                    // Same rule as the top-level sighting: a SUFFIXED type (`A? Mk(…)`) has to show a body, because a
                    // ternary begins the same way.
                    var hadSuffix = typeEnd > 1
                        && _tokens[typeEnd - 1].Type is TokenType.Question or TokenType.RightBracket;
                    return !hadSuffix || DeclarationBodyFollows(typeEnd + 1);
                }
                finally { _current = start; }
            }

            /// <summary>Like IsAtTupleTypeReturn but anchored at an absolute token index
            /// (the '(' position) instead of the current parse position.</summary>
            bool IsAtTupleTypeReturnAt(int lparenPos)
            {
                var pos = lparenPos + 1;
                var items = 0;
                while (pos < _tokens.Count)
                {
                    var t = _tokens[pos].Type;
                    if (t == TokenType.RightParen) return items >= 1;
                    if (t == TokenType.Comma) { items++; pos++; continue; }
                    if (t == TokenType.Identifier || TypeTokenHelper.IsBaseType(t) || t is TokenType.String or TokenType.Object or TokenType.Bool or TokenType.Char)
                    { pos++; continue; }
                    if (t == TokenType.Question) { pos++; continue; }
                    return false;
                }
                return false;
            }

            /// <summary>Returns the index of the ')' closing the tuple type at lparenPos, or -1.</summary>
            int TupleTypeEndAt(int lparenPos)
            {
                var pos = lparenPos + 1;
                while (pos < _tokens.Count)
                {
                    if (_tokens[pos].Type == TokenType.RightParen) return pos;
                    pos++;
                }
                return -1;
            }

            bool IsNameThenParen(int pos)
            {
                if (pos >= _tokens.Count || _tokens[pos].Type != TokenType.Identifier) return false;
                return pos + 1 < _tokens.Count && _tokens[pos + 1].Type == TokenType.LeftParen;
            }

            // "(type n, ...) name =", tuple-typed variable declaration (not a function)
            bool IsTupleVarDeclAhead()
            {
                var end = TupleTypeEndAt(_current);
                if (end < 0 || end + 2 >= _tokens.Count) return false;
                if (_tokens[end + 1].Type != TokenType.Identifier) return false;
                return _tokens[end + 2].Type == TokenType.Equal;
            }

            bool IsAtDeclaration()
            {
                // The operator first: `await faulted;` has the exact shape of a typed declaration (`await` as the type,
                // `faulted` as the name, then `;`), so a BLOCK read it as a DECLARATION and evaluated nothing: no
                // suspend, no throw, the catch never ran, and the variable looked untouched because the declaration
                // was block-local. The CALL spelling was unaffected only because `(` does not match the `;`/`=` the
                // declaration shape needs (measured, and the same predicate the other sights use).
                if (IsAwaitOperatorHere()) return false;
                var token = Peek();
                if (token.Type is TokenType.Const or TokenType.Var) return true;
                // Inside an async body `await` is the OPERATOR, never a type name: without this, `await d;` was
                // read as a declaration of `d` of type `await` (Identifier Identifier Semicolon), so the
                // statement never suspended and the body compiled as if the await were not there (measured).
                if (_inAsyncBody && token.Type == TokenType.Identifier && token.Literal == "await") return false;
                if (TypeTokenHelper.IsBaseType(token.Type) && token.Type != TokenType.Void) return true;
                if (token.Type == TokenType.Void) return true;
                // A bare-identifier type, with or without a suffix (`A a`, `A? a`, `A[] a`, `List<T> a`): the
                // DECLARATION shape decides, so `x ? y : z;` is not taken for one.
                if (token.Type != TokenType.Identifier) return false;
                return IsAtTypedDeclaration(_current, TokenType.Equal, TokenType.Semicolon);
            }

            // Tuple-typed variable declaration: (int x, int y) r = (expr, expr);
            if (Peek().Type == TokenType.LeftParen && IsAtTupleTypeReturn() && IsTupleVarDeclAhead())
            {
                var elementNames = ParseTupleElementNames();
                var tNameTok = ConsumeIdentifier("Expect variable name.");
                if (!Match(TokenType.Equal))
                    throw Error("Expect '=' after tuple declaration name.", Peek());
                var tInit = Expression();
                _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
                var tDecl = VarDeclStmt.Create(tNameTok.Literal, tInit, new TypeInfo(TokenType.Object, true, 0, 0, null), false, tNameTok.WasEscaped, tNameTok.LineNumber, tNameTok.Column);
                tDecl.TupleElementNames = elementNames;
                stmts.Add(tDecl);
                continue;
            }

            // Local (nested) function declaration: [pure] [async] type name ( ... or void name ( ... or var name (
            if (IsAtLocalFunctionDeclaration())
            {
                // A NESTED declaration carries its own source text too, taken with the same helper the top-level
                // attach uses. It is what lets a bundle carry a body the compiler does not take HERE: measured on
                // `_coro_nested_recursion`, a coroutine that calls its OWN name and is declared inside a block is
                // compiled nowhere (its recursive call cannot be attributed at that position), so without the text
                // the strip had to refuse the whole file by name. Both local-function branches share this span.
                var localDeclStart = Peek();
                var isPureFunc = Match(TokenType.Pure);
                var isAsyncFunc = MatchAsync();
                TypeInfo returnType;
                if (Peek().Type == TokenType.Void) { _ = Advance(); returnType = TypeInfo.Void; }
                else if (Peek().Type == TokenType.Var)
                {
                    _ = Advance();
                    returnType = new TypeInfo(TokenType.Object, Match(TokenType.Question), 0, 0, null);
                }
                else if (Peek().Type == TokenType.LeftParen && IsAtTupleTypeReturn())
                {
                    // Tuple-typed local function: (int a, string b) name(...)
                    // or tuple-typed variable declaration: (int x, int y) r = (expr, expr);
                    var elementNames = ParseTupleElementNames();
                    var nameTok3 = ConsumeIdentifier("Expect function name.");
                    if (Peek().Type == TokenType.LeftParen)
                    {
                        var fnRet2 = FunctionDeclaration(nameTok3, new TypeInfo(TokenType.Object, true, 0, 0, null), isPure: isPureFunc, isCoroutine: false, isAsync: isAsyncFunc);
                        fnRet2.TupleElementNames = elementNames;
                        fnRet2.SourceText ??= SourceTextOf(localDeclStart);
                        stmts.Add(fnRet2);
                        continue;
                    }
                    if (Peek().Type == TokenType.Equal)
                    {
                        _ = Advance();
                        var tInit = Expression();
                        _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
                        var tDecl = VarDeclStmt.Create(nameTok3.Literal, tInit, new TypeInfo(TokenType.Object, true, 0, 0, null), false, nameTok3.WasEscaped, nameTok3.LineNumber, nameTok3.Column);
                        tDecl.TupleElementNames = elementNames;
                        stmts.Add(tDecl);
                        continue;
                    }
                    throw Error("Expect '(' after function name or '=' in tuple declaration.", Peek());
                }
                else returnType = ParseType(allowVoid: true);
                var fnName = ConsumeIdentifier("Expect function name.");
                var localFn = FunctionDeclaration(fnName, returnType, isPure: isPureFunc, isCoroutine: false, isAsync: isAsyncFunc);
                localFn.SourceText ??= SourceTextOf(localDeclStart);
                stmts.Add(localFn);
                continue;
            }

            RefuseAwaitAsTypeName();
            if (IsAtDeclaration())
            {
                var isConst = Match(TokenType.Const);
                var isVar = !isConst && Match(TokenType.Var);
                TypeInfo type;
                if (isVar) type = new TypeInfo(TokenType.Object, Match(TokenType.Question), 0, 0, null);
                else if (isConst) type = new TypeInfo(TokenType.Object, false, 0, 0, null);
                else type = ParseType(allowVoid: true);

                // var (a, b) = expr, tuple destructuring in function bodies (mirror top-level path)
                if (isVar && Check(TokenType.LeftParen))
                {
                    _ = Advance();
                    var destVars = new List<string>();
                    if (!Check(TokenType.RightParen))
                    {
                        do
                        {
                            destVars.Add(ConsumeIdentifier("Expect variable name in destructuring.").Literal);
                        } while (Match(TokenType.Comma));
                    }
                    _ = Consume(TokenType.RightParen, "Expect ')' after destructuring variables.");
                    _ = Consume(TokenType.Equal, "Expect '=' after destructuring.");
                    var destExpr = Expression();
                    _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
                    stmts.Add(DestructuringStmt.Create(destVars, destExpr, null, false, destExpr.Line, destExpr.Column));
                    continue;
                }

                var nameToken = ConsumeIdentifier("Expect variable name.");
                Expr? initializer = null;
                if (Match(TokenType.Equal)) initializer = Expression();
                else if (isConst) throw Error($"Const variable '{nameToken.Literal}' must have an initializer.", nameToken);
                _ = Consume(TokenType.Semicolon, "Expect ';' after declaration.");
                stmts.Add(VarDeclStmt.Create(nameToken.Literal, initializer, type, isConst, nameToken.WasEscaped, nameToken.LineNumber, nameToken.Column));
            }
            else
            {
                stmts.Add(Statement());
            }
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after block.");
        return stmts;
    }

    private ExprStmt ExpressionStatement()
    {
        if (Check(TokenType.Else)) throw Error("Unexpected 'else'. Did you forget braces around the 'if' body?", Peek());
        var expr = Expression();
        _ = Consume(TokenType.Semicolon, "Expect ';' after expression.");
        return ExprStmt.Create(expr, expr.Line, expr.Column);
    }

    // ─── Expression Parsing (Pratt-style) ────────────────────────────────

    private Expr Expression() => Assignment();

    /// <summary>Builds the CELL expression a `ref &lt;target&gt;` names, by the target's SHAPE: a bare name goes to the
    /// engine's `__ref_name__` (which resolves whatever the name is bound to, local or global, at run time, because the
    /// parser cannot know), a member to `__ref_field__`, and an element to `__ref_elem__`. The two latter helpers are
    /// the ones a ref ARGUMENT already travels through, so a reference taken here and one passed as `F(ref h.F)` reach
    /// the same storage. Anything else has no storage to point at and is a parse error rather than a silent copy.
    /// </summary>
    private Expr RefTargetExpression(Token refToken)
    {
        var target = Unary();
        var line = refToken.LineNumber;
        var col = refToken.Column;
        var noRefs = new bool[0];
        switch (target)
        {
            case VariableExpr v:
                return MarkRef(CallExpr.Create(
                    VariableExpr.Create(Interpreter.RefNameHelperName, line, col),
                    [StringExpr.Create(v.Name, v.Line, v.Column),
                     NumberExpr.Create(line.ToString(), new TypeInfo(TokenType.Int, false, 0, 0, null), line, col),
                     NumberExpr.Create(col.ToString(), new TypeInfo(TokenType.Int, false, 0, 0, null), line, col)],
                    RefFlags(3), line, col));
            case PropertyAccessExpr p:
                return MarkRef(CallExpr.Create(
                    VariableExpr.Create(Interpreter.RefFieldHelperName, line, col),
                    [p.Target,
                     StringExpr.Create(p.PropertyName, p.Line, p.Column),
                     BoolExpr.Create(false, line, col),
                     NumberExpr.Create(line.ToString(), new TypeInfo(TokenType.Int, false, 0, 0, null), line, col),
                     NumberExpr.Create(col.ToString(), new TypeInfo(TokenType.Int, false, 0, 0, null), line, col)],
                    RefFlags(5), line, col));
            case ArrayAccessExpr a when a.Indices.Count == 1 && a.Indices[0] is not RangeExpr:
                return MarkRef(CallExpr.Create(
                    VariableExpr.Create(Interpreter.RefElemHelperName, line, col),
                    [a.Array,
                     a.Indices[0],
                     BoolExpr.Create(false, line, col),
                     NumberExpr.Create(line.ToString(), new TypeInfo(TokenType.Int, false, 0, 0, null), line, col),
                     NumberExpr.Create(col.ToString(), new TypeInfo(TokenType.Int, false, 0, 0, null), line, col)],
                    RefFlags(5), line, col));
            case CallExpr or MethodCallExpr:
                // `ref F(x)` / `ref b.Field()`: a REF-RETURNING call answers the reference itself.
                MarkRefTarget(target);
                return target;
            default:
                throw Error("A reference must name a variable, a field, an element or a ref-returning call.", new Token(TokenType.Ref, "ref", line, col));
        }
    }

    /// <summary>`n` positions, none of them passed with a keyword: the argument flags a call needs.</summary>
    private static IReadOnlyList<bool> RefFlags(int n)
    {
        var flags = new bool[n];
        return flags;
    }

    /// <summary>`F(x) = 7;` on a ref-RETURNING call: the call is asked for as a REFERENCE (WantsRefResult) and the
    /// assignment writes through it. A call to an ordinary function in that position is refused at RUN time, naming what
    /// it needs, because which declaration the callee is only becomes known then.
    /// </summary>
    private static Expr RefReturnAssign(Expr call, Expr value, Token eqToken)
    {
        MarkRefTarget(call);
        return RefAssignExpr.Create(call, value, eqToken.LineNumber, eqToken.Column);
    }

    /// <summary>Marks a call (a bare one or a member one) as answering a REFERENCE, so it is not dereferenced.</summary>
    private static void MarkRefTarget(Expr target)
    {
        switch (target)
        {
            case CallExpr c: c.WantsRefResult = true; break;
            case MethodCallExpr mc: mc.WantsRefResult = true; break;
        }
    }

    /// <summary>Marks a call that ANSWERS a reference (the three `__ref_*` helpers a `ref <target>` desugars to), so the
    /// call expression does not dereference its own result: the caller wants the cell.</summary>
    private static Expr MarkRef(CallExpr call)
    {
        call.WantsRefResult = true;
        return call;
    }

    private Expr Assignment()
    {
        // `ref <target>` is an EXPRESSION (C# writes it `ref x`, `ref a[i]`, `ref h.F`), and it answers the CELL over
        // that storage: `var r = ref x;` binds `r` to it, and a read dereferences it while an assignment writes
        // through, because a local that HOLDS a cell is already dereferenced on read and written through on store on
        // BOTH engines (that is how a ref parameter works). Parsing it at the expression level means every declaration
        // arm gets the `var r = ref x;` form at once, instead of one arm at a time.
        if (Match(TokenType.Ref)) return RefTargetExpression(Previous());
        var expr = Conditional();
        var compoundOp = CheckCompoundAssign();
        if (compoundOp != null)
        {
            _ = Advance();
            var value = Assignment();
            return (expr switch
            {
                VariableExpr varExpr => CompoundAssignExpr.Create(varExpr, compoundOp.GetValueOrDefault(), value, compoundOp.Value.LineNumber, compoundOp.Value.Column),
                ArrayAccessExpr arrAccess => CompoundAssignExpr.Create(arrAccess, compoundOp.GetValueOrDefault(), value, compoundOp.Value.LineNumber, compoundOp.Value.Column),
                PropertyAccessExpr propAccess => CompoundAssignExpr.Create(propAccess, compoundOp.GetValueOrDefault(), value, compoundOp.Value.LineNumber, compoundOp.Value.Column),
                _ => throw Error("Invalid assignment target.", compoundOp.GetValueOrDefault())
            });
        }

        if (!Match(TokenType.Equal)) return expr;
        {
            var eqToken = Previous();
            var value = Assignment();
            return expr switch
            {
                VariableExpr varExpr => AssignExpr.Create(varExpr.Name, value, eqToken.LineNumber, eqToken.Column),
                ArrayAccessExpr arrAccess => ArrayAssignExpr.Create(arrAccess.Array, arrAccess.Indices, value, eqToken.LineNumber, eqToken.Column),
                PropertyAccessExpr propAccess => PropertyAssignExpr.Create(propAccess.Target, propAccess.PropertyName, value, eqToken.LineNumber, eqToken.Column),
                ReturnValueExpr retVal => PropertyAssignExpr.Create(retVal, "Value", value, eqToken.LineNumber, eqToken.Column),
                CallExpr or MethodCallExpr => RefReturnAssign(expr, value, eqToken),
                _ => throw Error("Invalid assignment target.", eqToken)
            };
        }
    }

    /// <summary>Consumes a generic-type closing '>'. When the lexer collapsed '>>' into RightShift
    /// (nested generics), consumes it and injects a synthetic '>' so the outer type level closes too.</summary>
    private void ConsumeGenericClose(string message)
    {
        if (Check(TokenType.Greater))
        {
            _ = Advance();
            return;
        }

        if (!Check(TokenType.RightShift)) throw Error(message, Peek());
        var t = Advance();
        _tokens.Insert(_current, new Token(TokenType.Greater, ">", t.LineNumber, t.Column));
    }

    private Token? CheckCompoundAssign()
    {
        var tok = Peek();
        if (tok.Type is TokenType.PlusEqual or TokenType.MinusEqual or TokenType.StarEqual or TokenType.SlashEqual or TokenType.PercentEqual
            or TokenType.AmpersandEqual or TokenType.PipeEqual or TokenType.CaretEqual or TokenType.LeftShiftEqual or TokenType.RightShiftEqual
            or TokenType.QuestionEqual)
            return tok;
        return null;
    }

    private Expr Conditional()
    {
        var expr = NullCoalescing();
        if (!Match(TokenType.Question)) return expr;
        var thenExpr = Expression();
        _ = Consume(TokenType.Colon, "Expect ':' in ternary.");
        var elseExpr = Conditional();
        expr = ConditionalExpr.Create(expr, thenExpr, elseExpr, expr.Line, expr.Column);
        return expr;
    }

    private Expr NullCoalescing()
    {
        var expr = LogicalOr();
        while (Match(TokenType.QuestionQuestion))
        {
            var op = Previous(); expr = NullCoalescingExpr.Create(expr, LogicalOr(), op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr LogicalOr()
    {
        var expr = LogicalAnd();
        while (Match(TokenType.OrOr))
        {
            var op = Previous(); expr = LogicalExpr.Create(expr, op, LogicalAnd(), op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr LogicalAnd()
    {
        var expr = BitwiseOr();
        while (Match(TokenType.AndAnd))
        {
            var op = Previous(); expr = LogicalExpr.Create(expr, op, Equality(), op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr BitwiseOr()
    {
        var expr = BitwiseXor();
        while (Match(TokenType.Pipe))
        {
            var op = Previous(); expr = BinaryExpr.Create(expr, op, BitwiseXor(), op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr BitwiseXor()
    {
        var expr = BitwiseAnd();
        while (Match(TokenType.Caret))
        {
            var op = Previous(); expr = BinaryExpr.Create(expr, op, BitwiseAnd(), op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr BitwiseAnd()
    {
        var expr = Equality();
        while (Match(TokenType.Ampersand))
        {
            var op = Previous(); expr = BinaryExpr.Create(expr, op, Equality(), op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr Equality()
    {
        var expr = Relational();
        while (Match(TokenType.EqualEqual, TokenType.BangEqual))
        {
            var op = Previous(); expr = BinaryExpr.Create(expr, op, Relational(), op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr Relational()
    {
        var expr = TypeTest();
        while (Match(TokenType.Less, TokenType.LessEqual, TokenType.Greater, TokenType.GreaterEqual))
        {
            var op = Previous();
            expr = BinaryExpr.Create(expr, op, TypeTest(), op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr TypeTest()
    {
        var expr = Shift();
        if (Match(TokenType.Is))
        {
            var isToken = Previous();
            // The pattern's own span starts here, at `not` when there is one: see ParsePatternTail.
            var patternStart = Peek();
            // is not null, negated null check. A COMBINATOR after `null` (`x is null or int`) takes the pattern route
            // below instead, where `null` is an ordinary constant pattern, so this dedicated node survives only for the
            // plain check (measured: it is the shape the VM's IS_NULL and the tree's IsNullExpr are written for).
            var negate = Match(TokenType.Not);
            if (Check(TokenType.Nil) && PeekAhead(1).Type is not (TokenType.And or TokenType.Or))
            {
                _ = Advance();
                var isn = IsNullExpr.Create(expr, isToken.LineNumber, isToken.Column);
                if (!negate) return isn;
                return UnaryExpr.Create(new Token(TokenType.Bang, "!", isToken.LineNumber, isToken.Column), isn, isToken.LineNumber, isToken.Column);
            }
            // Classic type check / typed binding: an identifier (or base type) starts a TYPE,
            // including qualified names such as `PCL.Point` or `Class`, EXCEPT when a logical
            // operator follows (`x is int and > 3` composes patterns).
            var afterStart = PeekAhead(1).Type;
            var afterBinding = PeekAhead(1).Type == TokenType.Identifier ? PeekAhead(2).Type : afterStart;
            if (!negate && (IsTypeStart(Peek()) || Peek().Type == TokenType.Identifier
                || Peek().Type is TokenType.Class or TokenType.Struct)
                && afterStart is not (TokenType.And or TokenType.Or)
                && afterBinding is not (TokenType.And or TokenType.Or))
            {
                var type = ParseType(allowVoid: false);
                // `Shape { Name: "square" }`: the type QUALIFIES a property pattern, so it is one pattern, and the rest
                // of it (a following `and`, the source text a bundle carries) is the ordinary pattern route.
                if (Check(TokenType.LeftBrace))
                    return IsPatternExpr.CreatePattern(expr,
                        ParseQualifiedPattern(type, isToken, patternStart), isToken.LineNumber, isToken.Column);
                if (!Check(TokenType.Identifier) || IsReservedKeyword(Peek().Literal))
                    return IsExpr.Create(expr, type, isToken.LineNumber, isToken.Column);
                var varName = ConsumeIdentifier("Expect pattern variable name.").Literal;
                return IsPatternExpr.Create(expr, type, varName, isToken.LineNumber, isToken.Column);
            }

            // Otherwise everything after `is` is a pattern: constants, relational, logical,
            // property, list, grouping, and, inside `is` only, tuple patterns with literals.
            {
                var savedLiterals = _allowTupleLiteralsInPattern;
                _allowTupleLiteralsInPattern = true;
                Pattern pattern;
                try
                {
                    // `not` binds tighter than `and`/`or` (C#'s precedence), so it applies to the FIRST operand and the
                    // combinator loop continues from there: `x is not null and not string` is `(not null) and (not string)`.
                    var first = ParseNotPattern();
                    if (negate)
                        first = new LogicalPattern(LogicalKind.Not, first, null)
                        { Line = isToken.LineNumber, Column = isToken.Column };
                    pattern = ParsePatternTail(first, patternStart);
                }
                finally { _allowTupleLiteralsInPattern = savedLiterals; }
                return IsPatternExpr.CreatePattern(expr, pattern, isToken.LineNumber, isToken.Column);
            }
        }

        if (_noAsOperator) return expr;
        if (!Match(TokenType.As)) return expr;
        var asToken = Previous();
        return AsExpr.Create(expr, ParseType(allowVoid: false), asToken.LineNumber, asToken.Column);
    }

    private Expr Shift()
    {
        var expr = Range();
        while (true)
        {
            if (Match(TokenType.LeftShift))
            {
                var op = Previous();
                expr = BinaryExpr.Create(expr, op, Range(), op.LineNumber, op.Column);
            }
            else if (Match(TokenType.RightShift))
            {
                var op = Previous();
                expr = BinaryExpr.Create(expr, op, Range(), op.LineNumber, op.Column);
            }
            else break;
        }
        return expr;
    }

    private Expr Range()
    {
        // Handle open-ended left: [..5] or [..]
        if (Match(TokenType.DotDot))
        {
            var op = Previous();
            // Check if right side exists (not ] or , or ) or end)
            if (Check(TokenType.RightBracket) || Check(TokenType.Comma) || Check(TokenType.RightParen) || IsAtEnd())
                return RangeExpr.Create(null, null, op.LineNumber, op.Column);
            var to = Range();
            return RangeExpr.Create(null, to, op.LineNumber, op.Column);
        }

        var expr = Additive();
        if (!Match(TokenType.DotDot)) return expr;
        {
            var op = Previous();
            // Check if right side exists
            if (!Check(TokenType.RightBracket) && !Check(TokenType.Comma) && !Check(TokenType.RightParen) && !IsAtEnd())
            {
                var to = Range();
                expr = RangeExpr.Create(expr, to, op.LineNumber, op.Column);
            }
            else
                expr = RangeExpr.Create(expr, null, op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr Additive()
    {
        var expr = Multiplicative();
        while (Match(TokenType.Plus, TokenType.Minus))
        {
            var op = Previous(); expr = BinaryExpr.Create(expr, op, Multiplicative(), op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr Multiplicative()
    {
        var expr = Unary();
        while (Match(TokenType.Star, TokenType.Slash, TokenType.Percent))
        {
            var op = Previous(); expr = BinaryExpr.Create(expr, op, Unary(), op.LineNumber, op.Column);
        }
        return expr;
    }

    private Expr Unary()
    {
        // Contextual `await` (C# flavour): the operator inside an `async` body AND at a file's TOP LEVEL, which is
        // this engine's default async-capable entry (an `async Main` runs ordinary code too, so allowing `await`
        // there costs nothing); an ordinary identifier anywhere else, which is exactly how C# treats it and what keeps
        // a script's own `await` name working. The refusal below is for the mistake that rule invites: `await X`
        // written in a function that was not declared `async`. Without it the word is an identifier and the reader
        // gets a bare "Expect ';' ... found 'Identifier'" several tokens later (measured on
        // `return await Task.DelayAsync(1);`).
        if (!_inAsyncBody && _bodyDepth > 0 && Check(TokenType.Identifier) && Peek().Literal == "await"
            && StartsAnAwaitOperand(PeekAhead(1).Type))
        {
            throw Error("'await' is only the await operator, and only inside an `async` function. "
                        + "Declare the function `async` to await a task here.", Peek());
        }
        // At the TOP LEVEL the operand test decides, exactly as the refusal above uses it: `await Task.X()` is the
        // operator, while `print(await)` and `var await = 5;` keep their name, which is the rule the docs state.
        if ((_inAsyncBody || (_bodyDepth == 0 && StartsAnAwaitOperand(PeekAhead(1).Type)))
            && Check(TokenType.Identifier) && Peek().Literal == "await")
        {
            var awaitToken = Advance();
            return AwaitExpr.Create(Unary(), awaitToken.LineNumber, awaitToken.Column);
        }
        if (Match(TokenType.Caret))
        {
            // ^expr, index from end
            var caretOp = Previous();
            return IndexExpr.Create(Unary(), true, caretOp.LineNumber, caretOp.Column);
        }
        if (Match(TokenType.Minus, TokenType.Bang, TokenType.Tilde))
        {
            var op = Previous();
            return UnaryExpr.Create(op, Unary(), op.LineNumber, op.Column);
        }

        if (!Match(TokenType.PlusPlus, TokenType.MinusMinus)) return Primary();
        {
            var op = Previous();
            return IncrementDecrementExpr.Create(Unary(), op, true, op.LineNumber, op.Column);
        }
    }

    // ─── Primary Expressions ─────────────────────────────────────────────

    private Expr Primary()
    {
        Expr expr;

        // FP: prefix match expression, `match (1, 2) { case (int a, int b) => a + b, _ => 0 }`.
        // `match` is a soft keyword; when it STARTS an expression and is followed by a subject and
        // '{', parse the subject and finish through ParseMatchBody (which consumes '{' + arms).
        // Otherwise fall through to the normal identifier/call handling (e.g. a variable or
        // function actually named "match").
        if (Check(TokenType.Identifier) && Peek().Literal.Equals("match", StringComparison.Ordinal)
            && PeekAhead(1).Type != TokenType.LeftBrace)
        {
            var saved = _current;
            try
            {
                _ = Advance(); // consume 'match'
                var matchToken = Previous();
                var subject = Expression();
                if (Check(TokenType.LeftBrace))
                    return ParseMatchBody(subject, matchToken);
            }
            catch { /* not a prefix match, fall through to normal parsing */ }
            _current = saved;
        }

        // Arrow lambda: (args) => expr  or  () => expr  or  (x, y) => { block }
        if (Check(TokenType.LeftParen))
        {
            // Fast check for untyped multi-param lambda: (ident, ident, ...) =>
            if (PeekAhead(1).Type == TokenType.Identifier && PeekAhead(2).Type == TokenType.Comma)
            {
                var saved = _current;
                try
                {
                    _ = Advance(); // consume (
                    var lparen = Previous();
                    var paramNames = new List<string>();
                    if (paramNames == null) throw new ArgumentNullException(nameof(paramNames));
                    // Consume comma-separated identifiers
                    while (Check(TokenType.Identifier))
                    {
                        paramNames.Add(ConsumeIdentifier("Expect param name.").Literal);
                        if (!Match(TokenType.Comma)) break;
                    }
                    if (Match(TokenType.RightParen) && Check(TokenType.Arrow))
                    {
                        _current = saved;
                        _ = Advance(); // consume (
                        return ParseArrowLambda(lparen);
                    }
                }
                catch
                {
                    // ignored
                }

                _current = saved;
            }

            // Zero-param or typed-param lambda: () =>  or  (int x) =>  or  ((int n, int m)) =>
            if (PeekAhead(1).Type == TokenType.RightParen || IsTypeStart(PeekAhead(1))
                || (PeekAhead(1).Type == TokenType.LeftParen && IsTypeStart(PeekAhead(2))))
            {
                var saved = _current;
                try
                {
                    _ = Advance(); // consume (
                    var lparen = Previous();
                    var (paramsList, isRefsList, isOutsList, isParamsList, defaultsList, tupleElems, isInsList) = ParseParameterList();
                    if (Match(TokenType.RightParen) && Check(TokenType.Arrow))
                    {
                        _current = saved;
                        _ = Advance(); // consume (
                        return ParseArrowLambda(lparen);
                    }
                }
                catch
                {
                    // ignored
                }

                _current = saved;
            }
        }

        // Cast: (Type)expr
        if (Match(TokenType.LeftParen))
        {
            var lparen = Previous();
            if (IsTypeStart(Peek()) || Peek().Type == TokenType.Identifier || Peek().Type == TokenType.Void)
            {
                var savedPosition = _current;
                try
                {
                    var type = ParseType(allowVoid: false);
                    if (Match(TokenType.RightParen))
                    {
                        expr = CastExpr.Create(type, Unary(), lparen.LineNumber, lparen.Column);
                        goto process_trailing;
                    }
                }
                catch
                {
                    // ignored
                }

                _current = savedPosition;
            }

            // Declared tuple return type on a lambda: (int i, string s) fun(...) { ... }
            if (IsTupleTypeThenFun())
            {
                // _current is on the first type token (the '(' was consumed), rewind for ParseTupleElementNames
                if (_current > 0 && _tokens[_current - 1].Type == TokenType.LeftParen)
                    _current--;
                var names = ParseTupleElementNames();
                if (!Match(TokenType.Fun))
                    throw Error("Expect 'fun' after tuple return type.", Peek());
                var t = Previous();
                expr = ParseLambda(t, names);
                goto process_trailing;
            }

            expr = Expression();

            // Tuple literal: (a, b, c)
            if (Check(TokenType.Comma))
            {
                var items = new List<Expr> { expr };
                while (Match(TokenType.Comma))
                {
                    if (Check(TokenType.RightParen)) break;
                    items.Add(Expression());
                }
                _ = Consume(TokenType.RightParen, "Expect ')' after tuple.");
                expr = TupleExpr.Create(items, lparen.LineNumber, lparen.Column);
                goto process_trailing;
            }

            _ = Consume(TokenType.RightParen, "Expect ')' after expression.");
            // Single-param arrow lambda: (x) => expr  or  (x) => { block }
            if (Check(TokenType.Arrow))
            {
                // Consume => and parse the body, wrapping (x) as a lambda
                if (!(expr is VariableExpr ve))
                    throw Error("Expected a single parameter name before '=>'.", Peek());
                _ = Advance(); // consume =>
                Expr? bodyExpr = null;
                IReadOnlyList<Stmt>? bodyStmts = null;
                bool isCoroutine;
                if (Check(TokenType.LeftBrace))
                {
                    _ = Advance();
                    bodyStmts = Block();
                    isCoroutine = ContainsYield(bodyStmts);
                }
                else
                {
                    bodyExpr = Expression();
                    isCoroutine = ContainsYieldInExpr(bodyExpr);
                }
                var paramType = new TypeInfo(TokenType.Object, true, 0, 0, null);
                var parameters = new List<(TypeInfo, string)> { (paramType, ve.Name) };
                var ret = LambdaExpr.Create(parameters, [false], [false], [false], bodyExpr, bodyStmts, lparen.LineNumber, lparen.Column);
                ret.IsCoroutine = isCoroutine;
                ret.CapturedNames = FreeVariableAnalyzer.Analyze(ret);
                return ret;
            }
            expr.Line = lparen.LineNumber; expr.Column = lparen.Column;
            goto process_trailing;
        }

        if (Match(TokenType.Number))
        {
            var token = Previous();
            var rawValue = token.Literal;
            string value;
            TypeInfo type;

            // Detect numeric suffix (f/F/d/D/m/M/l/L)
            if (rawValue.Length > 1)
            {
                var last = rawValue[^1];
                switch (last)
                {
                    // Check for common type suffixes
                    case 'f':
                    case 'F':
                        value = rawValue[..^1];
                        type = new TypeInfo(TokenType.Float, false, 0, 0, null);
                        break;

                    case 'd':
                    case 'D':
                        value = rawValue[..^1];
                        type = new TypeInfo(TokenType.Double, false, 0, 0, null);
                        break;

                    case 'm':
                    case 'M':
                        value = rawValue[..^1];
                        type = new TypeInfo(TokenType.Decimal, false, 0, 0, null);
                        break;

                    case 'l':
                    case 'L':
                        value = rawValue[..^1];
                        type = new TypeInfo(TokenType.Long, false, 0, 0, null);
                        break;

                    case 'u':
                    case 'U':
                        value = rawValue[..^1];
                        type = new TypeInfo(TokenType.ULong, false, 0, 0, null);
                        break;

                    default:
                        {
                            value = rawValue;
                            if (long.TryParse(value, out _)) type = new TypeInfo(TokenType.Int, false, 0, 0, null);
                            else if (ulong.TryParse(value, out _)) type = new TypeInfo(TokenType.Long, false, 0, 0, null);
                            else type = new TypeInfo(TokenType.Double, false, 0, 0, null);
                            break;
                        }
                }
            }
            else
            {
                value = rawValue;
                if (long.TryParse(value, out _)) type = new TypeInfo(TokenType.Int, false, 0, 0, null);
                else if (ulong.TryParse(value, out _)) type = new TypeInfo(TokenType.Long, false, 0, 0, null);
                else type = new TypeInfo(TokenType.Double, false, 0, 0, null);
            }

            expr = NumberExpr.Create(value, type, token.LineNumber, token.Column);
        }
        else if (Match(TokenType.True)) { var t = Previous(); expr = BoolExpr.Create(true, t.LineNumber, t.Column); }
        else if (Match(TokenType.False)) { var t = Previous(); expr = BoolExpr.Create(false, t.LineNumber, t.Column); }
        else if (Match(TokenType.Nil)) { var t = Previous(); expr = NullExpr.Create(t.LineNumber, t.Column); }
        else if (Match(TokenType.String)) { var t = Previous(); expr = StringExpr.Create(t.Literal, t.LineNumber, t.Column); }
        else if (Match(TokenType.InterpolatedString)) { expr = ParseInterpolatedString(); }
        else if (Match(TokenType.CharLiteral)) { var t = Previous(); expr = CharExpr.Create(t.Literal[0], t.LineNumber, t.Column); }
        else if (Check(TokenType.Identifier) && Peek().Literal == "async" && PeekAhead(1).Type == TokenType.Fun)
        {
            // Contextual `async` before a lambda: `async fun(...) { ... }` / `async fun(...) => expr`. The word is
            // consumed here so the lambda is built with the async flag, which is what makes `await` legal inside it.
            _ = Advance();                 // the `async`
            var asyncFun = Advance();      // the `fun`
            expr = ParseLambda(asyncFun, isAsync: true);
        }
        else if (Match(TokenType.Fun)) { var t = Previous(); expr = ParseLambda(t); }
        else if (Match(TokenType.New)) { var newToken = Previous(); expr = ParseNewExpression(newToken); }
        else if (Match(TokenType.This)) { var t = Previous(); expr = ThisExpr.Create(t.LineNumber, t.Column); }
        else if (Match(TokenType.Super)) { var t = Previous(); expr = SuperExpr.Create(t.LineNumber, t.Column); }
        else if (Match(TokenType.Base)) { var t = Previous(); expr = BaseExpr.Create(t.LineNumber, t.Column); }
        else if (Check(TokenType.Return) && PeekAhead(1).Type == TokenType.Dot)
        {
            var ret = Advance();  // consume 'return'
            _ = Advance(); // consume '.'
            _ = Consume(TokenType.Identifier, "Expect 'Value' after 'return.'.");
            expr = ReturnValueExpr.Create(ret.LineNumber, ret.Column);
        }
        else if (Check(TokenType.Identifier) && PeekAhead(1).Type == TokenType.Arrow && !_suppressBareLambda)
        {
            // Bare single-parameter arrow lambda (C#-style): `x => expr` or `x => { ... }`.
            // Untyped param, mirrors the `(x) =>` wrap above (paramType Object/nullable, refs/outs [false]).
            // Suppressed while parsing match-arm patterns via _suppressBareLambda so `v => v * 2` stays a pattern.
            var name = Advance();   // identifier (the single param)
            _ = Advance();              // consume =>
            Expr? bodyExpr = null;
            IReadOnlyList<Stmt>? bodyStmts = null;
            bool isCoroutine;
            if (Check(TokenType.LeftBrace))
            {
                _ = Advance();
                bodyStmts = Block();
                isCoroutine = ContainsYield(bodyStmts);
            }
            else
            {
                bodyExpr = Expression();
                isCoroutine = ContainsYieldInExpr(bodyExpr);
            }
            var paramType = new TypeInfo(TokenType.Object, true, 0, 0, null);
            var parameters = new List<(TypeInfo, string)> { (paramType, name.Literal) };
            var ret = LambdaExpr.Create(parameters, [false], [false], [false], bodyExpr, bodyStmts, name.LineNumber, name.Column);
            ret.IsCoroutine = isCoroutine;
            ret.CapturedNames = FreeVariableAnalyzer.Analyze(ret);
            expr = ret;
            goto process_trailing;
        }
        else if (Match(TokenType.Identifier) || Match(TokenType.Get) || Match(TokenType.Set) || Match(TokenType.Init))
        {
            var t = Previous();
            expr = VariableExpr.Create(t.Literal, t.LineNumber, t.Column);
        }
        else if (Match(TokenType.Yield))
        {
            var yieldToken = Previous();
            Expr? value = null;
            if (!Check(TokenType.Semicolon) && !Check(TokenType.RightBrace) && !Check(TokenType.RightParen) && !IsAtEnd())
                value = Expression();
            expr = YieldExpr.Create(value, yieldToken.LineNumber, yieldToken.Column);
        }
        else if (TypeTokenHelper.IsBaseType(Peek().Type)) { var t = Advance(); expr = VariableExpr.Create(t.Literal, t.LineNumber, t.Column); }
        else if (Match(TokenType.LeftBracket)) { expr = ParseListLiteral(Previous()); }
        else if (Match(TokenType.LeftBrace))
        {
            // A CHAR key is a legal key too: a one-character single-quoted literal is a CharLiteral token, so
            // `{'a': 1}` used to fall into the "did you mean a dictionary literal" refusal while writing exactly
            // that (measured, pre-existing: `{'abc': 1}` worked because a longer literal is a String token).
            if (Check(TokenType.RightBrace) || Check(TokenType.Identifier) || Check(TokenType.StringType) || Check(TokenType.String) || Check(TokenType.CharLiteral) || Check(TokenType.LeftBracket) || Check(TokenType.Number))
                expr = ParseDictLiteral(Previous());
            else
                throw Error("Unexpected '{'. Did you mean a dictionary literal like {key: value}?", Previous());
        }
        else if (IsReservedKeyword(Peek().Literal))
        {
            // Hard keyword used as a variable reference, accept with a warning so
            // `var class = 5; print(class);` behaves consistently with the declaration.
            // (Placed after all keyword statement/expr branches: yield, new, super, etc.)
            var t = Advance();
            if (!IsReceiverKeyword(t.Literal))
                Logging.Log.Warning("Script", $"Using reserved keyword '{t.Literal}' as an identifier. This may cause confusion.", t.LineNumber, t.Column);
            expr = VariableExpr.Create(t.Literal, t.LineNumber, t.Column);
        }
        else throw Error("Unexpected token", Peek());

    process_trailing:
        while (true)
        {
            // Generic call: Identifier < Type (, Type)* > ( args )
            // The receiver is bound ONCE here (`genericReceiver`) so the three arms below never cast and never
            // assert non-null: `expr` is reassigned inside this block (to a CallExpr, a TypeRefExpr), and a
            // `(VariableExpr)expr!` after that would be a claim about the CURRENT value of `expr` rather than
            // about the value the branch was entered with.
            if (expr is VariableExpr genericReceiver && Check(TokenType.Less) && LookAheadIsType(1))
            {
                var typeArgs = new List<TypeInfo>();
                _ = Advance(); // consume <
                do { typeArgs.Add(ParseType(allowVoid: false)); } while (Match(TokenType.Comma));
                ConsumeGenericClose("Expect '>' after generic type arguments.");
                // Not a call: `Name<Type>.Member` is a TYPE reference used as an expression - the
                // receiver of a static extension member. Build it and let the trailing loop below pick
                // up the `.Member` that follows.
                if (Check(TokenType.Dot))
                {
                    expr = TypeRefExpr.Create(genericReceiver.Name, typeArgs, genericReceiver.Line, genericReceiver.Column);
                    continue;
                }
                // Now expect ( for the call arguments
                if (Check(TokenType.LeftParen))
                {
                    var lparen = Advance();
                    var args = new List<Expr>();
                    var argRefs = new List<bool>();
                    var argOuts = new List<bool>();
                    var argNames = new List<string?>();
                    if (!Check(TokenType.RightParen))
                    {
                        do
                        {
                            var (argExpr, isRef, isOut, argName) = ParseCallArg();
                            args.Add(argExpr);
                            argRefs.Add(isRef);
                            argOuts.Add(isOut);
                            argNames.Add(argName);
                        } while (Match(TokenType.Comma));
                    }
                    _ = Consume(TokenType.RightParen, "Expect ')' after arguments.");
                    expr = CallExpr.Create(expr, args, argRefs, lparen.LineNumber, lparen.Column, typeArgs, argOuts, argNames);
                    continue;
                }
                // NEITHER `.` NOR `(`: the name with its type arguments IS a type value, which is what
                // `typeof(Dictionary<string, object>)` and `var t = List<int>;` ask for. Before this the
                // arguments were consumed and then DROPPED (the branch just broke out of the trailing loop), so
                // only the bare name was evaluated and the generic spelling vanished (measured: a silent
                // `Undefined variable 'Dictionary'`). A comparison never reaches this branch (`LookAheadIsType`
                // refuses the `a < b && c > d` shape), and the one that would (`a < b > c`) is a ParseError.
                expr = TypeRefExpr.Create(genericReceiver.Name, typeArgs, genericReceiver.Line, genericReceiver.Column);
                continue;
            }

            if (Check(TokenType.LeftParen))
            {
                var lparen = Advance();
                var args = new List<Expr>();
                var argRefs = new List<bool>();
                var argOuts = new List<bool>();
                var argNames = new List<string?>();
                if (!Check(TokenType.RightParen))
                {
                    do
                    {
                        var (argExpr, isRef, isOut, argName) = ParseCallArg();
                        args.Add(argExpr);
                        argRefs.Add(isRef);
                        argOuts.Add(isOut);
                        argNames.Add(argName);
                    } while (Match(TokenType.Comma));
                }
                _ = Consume(TokenType.RightParen, "Expect ')' after arguments.");
                // `nameof(x)` is C#'s operator, rewritten HERE, at parse time, into the string it names: the
                // argument is never evaluated (which is C#'s whole point), and one rewrite covers the top level,
                // a function body, a VM-compiled body and a --no-source bundle, with no new AST node and no
                // compiler work. It yields the LAST IDENTIFIER AS WRITTEN, with no type arguments, so
                // `nameof(List<int>)` and `nameof(List<string?>)` are both "List".
                if (expr is VariableExpr nameofCallee && nameofCallee.Name == "nameof")
                {
                    // What C# REFUSES is refused here, with a reason: an arity other than one, an argument that
                    // is not a NAME (a literal, an expression, a call), and a keyword type on its own, which has
                    // no identifier to give (CS8081: write `nameof(String)`). With those refused the value-based
                    // builtin this used to reach is unreachable for the C# spelling.
                    if (args.Count != 1)
                        throw Error($"nameof takes exactly one argument, got {args.Count}.", lparen);
                    var named = args[0] switch
                    {
                        VariableExpr v => v.Name,
                        PropertyAccessExpr p => p.PropertyName,
                        TypeRefExpr t => t.Name,
                        _ => null,
                    };
                    if (named == null)
                        throw Error("nameof expects a NAME: a variable, a member or a type.", lparen);
                    var isKeywordType = named is "int" or "long" or "short" or "byte" or "sbyte" or "uint" or "ulong"
                        or "ushort" or "float" or "double" or "decimal" or "bool" or "char" or "string" or "object";
                    if (isKeywordType)
                        throw Error($"nameof cannot name the keyword type '{named}': write its type name, as in nameof(String).", lparen);
                    expr = StringExpr.Create(named, lparen.LineNumber, lparen.Column);
                    continue;
                }

                // `typeof(<a NAME>)` HANDS THE NAME ALONG, because a VALUE cannot say what a NULL is: `long? db;` has
                // no runtime type, so the value-based builtin could only answer "null" while the declaration says
                // "long?", which is the answer a reader can act on. One rewrite at parse time therefore covers the
                // top level, a function body, a VM-compiled body and a --no-source bundle. Anything that is not a
                // bare name (`typeof(this.Field)`, `typeof(42)`, `typeof(null)`) keeps the value-based builtin, and
                // so does `typeof` used as a VALUE (`var f = typeof;`).
                if (expr is VariableExpr typeofCallee && typeofCallee.Name == "typeof" && args.Count == 1
                    && args[0] is VariableExpr typeofArg)
                {
                    expr = CallExpr.Create(
                        VariableExpr.Create(Interpreter.TypeofNamedHelperName, lparen.LineNumber, lparen.Column),
                        [StringExpr.Create(typeofArg.Name, lparen.LineNumber, lparen.Column), typeofArg],
                        [], lparen.LineNumber, lparen.Column, isOutArgs: []);
                    continue;
                }

                expr = CallExpr.Create(expr, args, argRefs, lparen.LineNumber, lparen.Column, isOutArgs: argOuts, argNames: argNames);
                continue;
            }
            if (Check(TokenType.LeftBracket))
            {
                var lbracket = Advance();
                var indices = new List<Expr>();
                while (!Check(TokenType.RightBracket) && !IsAtEnd())
                {
                    indices.Add(Expression());
                    if (!Check(TokenType.RightBracket)) _ = Consume(TokenType.Comma, "Expect ',' or ']' after index.");
                }
                _ = Consume(TokenType.RightBracket, "Expect ']' after index list.");
                expr = ArrayAccessExpr.Create(expr, indices, lbracket.LineNumber, lbracket.Column);
                continue;
            }
            if (Match(TokenType.Dot))
            {
                var dot = Previous();
                var member = ConsumeMemberIdentifier("Expect identifier after '.'.");
                IReadOnlyList<TypeInfo>? typeArgs = null;
                if (Check(TokenType.Less) && LookAheadIsType(1))
                {
                    var ta = new List<TypeInfo>();
                    _ = Advance(); // consume <
                    do { ta.Add(ParseType(allowVoid: false)); } while (Match(TokenType.Comma));
                    ConsumeGenericClose("Expect '>' after generic type arguments.");
                    typeArgs = ta;
                }
                if (Check(TokenType.LeftParen))
                {
                    _ = Advance();
                    var args = new List<Expr>();
                    var argRefs = new List<bool>();
                    var argOuts = new List<bool>();
                    var argNames = new List<string?>();
                    if (!Check(TokenType.RightParen))
                    {
                        do
                        {
                            var (argExpr, isRef, isOut, argName) = ParseCallArg();
                            args.Add(argExpr);
                            argRefs.Add(isRef);
                            argOuts.Add(isOut);
                            argNames.Add(argName);
                        } while (Match(TokenType.Comma));
                    }
                    _ = Consume(TokenType.RightParen, "Expect ')' after arguments.");
                    expr = MethodCallExpr.Create(expr, member.Literal, args, argRefs, dot.LineNumber, dot.Column, typeArgs, argOuts, argNames);
                }
                else
                {
                    expr = PropertyAccessExpr.Create(expr, member.Literal, dot.LineNumber, dot.Column);
                }
                continue;
            }
            if (Match(TokenType.QuestionDot))
            {
                var qDot = Previous();
                var member = ConsumeIdentifier("Expect identifier after '?.'.");
                IReadOnlyList<TypeInfo>? typeArgs = null;
                if (Check(TokenType.Less) && LookAheadIsType(1))
                {
                    var ta = new List<TypeInfo>();
                    _ = Advance();
                    do { ta.Add(ParseType(allowVoid: false)); } while (Match(TokenType.Comma));
                    ConsumeGenericClose("Expect '>' after generic type arguments.");
                    typeArgs = ta;
                }
                // Build the inner access: a.b
                Expr innerAccess;
                if (Check(TokenType.LeftParen))
                {
                    _ = Advance();
                    var args = new List<Expr>();
                    var argRefs = new List<bool>();
                    var argOuts = new List<bool>();
                    var argNames = new List<string?>();
                    if (!Check(TokenType.RightParen))
                    {
                        do
                        {
                            var (argExpr, isRef, isOut, argName) = ParseCallArg();
                            args.Add(argExpr);
                            argRefs.Add(isRef);
                            argOuts.Add(isOut);
                            argNames.Add(argName);
                        } while (Match(TokenType.Comma));
                    }
                    _ = Consume(TokenType.RightParen, "Expect ')' after arguments.");
                    innerAccess = MethodCallExpr.Create(expr, member.Literal, args, argRefs, qDot.LineNumber, qDot.Column, typeArgs, argOuts, argNames);
                }
                else
                {
                    innerAccess = PropertyAccessExpr.Create(expr, member.Literal, qDot.LineNumber, qDot.Column);
                }
                // Transform a?.b into a != null ? a.b : null
                var nullCheck = BinaryExpr.Create(
                    expr, new Token(TokenType.BangEqual, null!, qDot.LineNumber, qDot.Column),
                    NullExpr.Create(qDot.LineNumber, qDot.Column),
                    qDot.LineNumber, qDot.Column);
                expr = ConditionalExpr.Create(nullCheck, innerAccess, NullExpr.Create(qDot.LineNumber, qDot.Column), qDot.LineNumber, qDot.Column);
                continue;
            }
            if (Match(TokenType.PlusPlus) || Match(TokenType.MinusMinus))
            {
                var op = Previous();
                expr = IncrementDecrementExpr.Create(expr, op, false, op.LineNumber, op.Column);
                continue;
            }
            // Match expression: expr match { arms }
            if (Peek().Type == TokenType.Identifier && Peek().Literal.Equals("match", StringComparison.Ordinal)
                && PeekAhead(1).Type == TokenType.LeftBrace)
            {
                expr = ParseMatch(expr);
                continue;
            }
            // Switch expression: expr switch { arms }
            if (Peek().Type == TokenType.Switch && PeekAhead(1).Type == TokenType.LeftBrace)
            {
                expr = ParseSwitchExpr(expr);
                continue;
            }
            // With expression: expr with { field = value, ... }
            if (Match(TokenType.With))
            {
                var withToken = Previous();
                _ = Consume(TokenType.LeftBrace, "Expect '{' after 'with'.");
                var fields = new List<(string, Expr)>();
                while (!Check(TokenType.RightBrace) && !IsAtEnd())
                {
                    var fname = ConsumeIdentifier("Expect field name.").Literal;
                    _ = Consume(TokenType.Equal, "Expect '=' in with field.");
                    fields.Add((fname, Expression()));
                    if (!Match(TokenType.Comma)) break;
                }
                _ = Consume(TokenType.RightBrace, "Expect '}' after with fields.");
                expr = WithExpr.Create(expr, fields, withToken.LineNumber, withToken.Column);
                continue;
            }
            break;
        }
        return expr;
    }

    // ─── New expression ─────────────────────────────────────────────────

    private Expr ParseNewExpression(Token newToken)
    {
        if (Match(TokenType.Struct))
        {
            var structExpr = ParseNewStruct();
            if (Match(TokenType.LeftBrace))
                return ParseObjectInitializer(structExpr);
            return structExpr;
        }

        Expr result;

        // ── new class ClassName(args) { ... } syntax sugar ──
        if (Match(TokenType.Class))
        {
            var className = ConsumeIdentifier("Expect class name after 'class'.");
            var typeInfo = new TypeInfo(TokenType.Object, false, 0, 0, className.Literal);
            var ctorExpr = VariableExpr.Create(className.Literal, newToken.LineNumber, newToken.Column);
            IReadOnlyList<Expr>? args = null;
            List<bool>? argRefs = null;
            List<bool>? argOuts = null;
            List<string?>? argNames = null;
            if (Match(TokenType.LeftParen))
            {
                var argList = new List<Expr>();
                argRefs = [];
                argOuts = [];
                argNames = [];
                if (!Check(TokenType.RightParen))
                {
                    do
                    {
                        var (argExpr, isRef, isOut, argName) = ParseCallArg();
                        argList.Add(argExpr); argRefs.Add(isRef);
                        argOuts.Add(isOut);
                        argNames.Add(argName);
                    } while (Match(TokenType.Comma));
                }
                _ = Consume(TokenType.RightParen, "Expect ')' after arguments.");
                args = argList;
            }
            result = NewInstanceExpr.Create(ctorExpr, args, typeInfo, newToken.LineNumber, newToken.Column, isRefArgs: argRefs, isOutArgs: argOuts, argNames: argNames);
        }
        else if (Check(TokenType.LeftParen))
        {
            // new (string s, int i)[] { ... }, tuple-typed array creation
            var tupleBase = ParseTupleTypeAnnotation();
            return ParseArrayTail(tupleBase, newToken);
        }
        else if (IsTypeCreation())
        {
            var typeInfo = ParseType(allowVoid: false);
            RefuseNullableNewType(typeInfo, newToken);
            var constructorExpr = VariableExpr.Create(typeInfo.TypeName ?? typeInfo.BaseType.ToString(), newToken.LineNumber, newToken.Column);
            IReadOnlyList<Expr>? args = null;
            List<bool>? argRefs = null;
            List<bool>? argOuts = null;
            List<string?>? argNames = null;

            if (Match(TokenType.LeftParen))
            {
                var argList = new List<Expr>();
                argRefs = [];
                argOuts = [];
                argNames = [];
                if (!Check(TokenType.RightParen))
                {
                    do
                    {
                        var (argExpr, isRef, isOut, argName) = ParseCallArg();
                        argList.Add(argExpr);
                        argRefs.Add(isRef);
                        argOuts.Add(isOut);
                        argNames.Add(argName);
                    } while (Match(TokenType.Comma));
                }
                _ = Consume(TokenType.RightParen, "Expect ')' after arguments.");
                args = argList;
            }
            result = NewInstanceExpr.Create(constructorExpr, args, typeInfo, newToken.LineNumber, newToken.Column, isRefArgs: argRefs, isOutArgs: argOuts, argNames: argNames);
        }
        else if (IsArrayCreation())
        {
            try { return ParseNewArrayExpr(newToken); }
            catch
            {
                var typeInfo = ParseType(allowVoid: false);
                var constructorExpr = VariableExpr.Create(typeInfo.TypeName ?? typeInfo.BaseType.ToString(), newToken.LineNumber, newToken.Column);
                IReadOnlyList<Expr>? args = null;
                List<bool>? catchArgRefs = null;
                List<bool>? catchArgOuts = null;
                List<string?>? catchArgNames = null;
                if (Match(TokenType.LeftParen))
                {
                    var argList = new List<Expr>();
                    catchArgRefs = [];
                    catchArgOuts = [];
                    catchArgNames = [];
                    if (!Check(TokenType.RightParen))
                    {
                        do
                        {
                            var (argExpr, isRef, isOut, argName) = ParseCallArg();
                            argList.Add(argExpr);
                            catchArgRefs.Add(isRef);
                            catchArgOuts.Add(isOut);
                            catchArgNames.Add(argName);
                        } while (Match(TokenType.Comma));
                    }
                    _ = Consume(TokenType.RightParen, "Expect ')' after arguments.");
                    args = argList;
                }
                result = NewInstanceExpr.Create(constructorExpr, args, typeInfo, newToken.LineNumber, newToken.Column, isRefArgs: catchArgRefs, isOutArgs: catchArgOuts, argNames: catchArgNames);
            }
        }
        else
        {
            var ctorExpr = Expression();
            IReadOnlyList<Expr>? ctorArgs = null;
            List<bool>? ctorArgRefs = null;
            List<bool>? ctorArgOuts = null;
            List<string?>? ctorArgNames = null;
            if (Match(TokenType.LeftParen))
            {
                var argList = new List<Expr>();
                ctorArgRefs = [];
                ctorArgOuts = [];
                ctorArgNames = [];
                if (!Check(TokenType.RightParen))
                {
                    do
                    {
                        var (argExpr, isRef, isOut, argName) = ParseCallArg();
                        argList.Add(argExpr);
                        ctorArgRefs.Add(isRef);
                        ctorArgOuts.Add(isOut);
                        ctorArgNames.Add(argName);
                    } while (Match(TokenType.Comma));
                }
                _ = Consume(TokenType.RightParen, "Expect ')' after arguments.");
                ctorArgs = argList;
            }
            result = NewInstanceExpr.Create(ctorExpr, ctorArgs, null, newToken.LineNumber, newToken.Column, isRefArgs: ctorArgRefs, isOutArgs: ctorArgOuts, argNames: ctorArgNames);
        }

        // ── Object initializer: new Foo { Prop1 = val1, Prop2 = val2 } ──
        if (Match(TokenType.LeftBrace))
            return ParseObjectInitializer(result);

        return result;
    }

    private ObjectInitExpr ParseObjectInitializer(Expr instanceExpr)
    {
        var inits = new List<(string Name, Expr Value)>();
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            var propName = ConsumeIdentifier("Expect property name in object initializer.");
            _ = Consume(TokenType.Equal, "Expect '=' after property name.");
            var value = Expression();
            inits.Add((propName.Literal, value));
            if (!Check(TokenType.RightBrace))
                _ = Consume(TokenType.Comma, "Expect ',' or '}' after property initializer.");
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after object initializer.");
        return ObjectInitExpr.Create(instanceExpr, inits, instanceExpr.Line, instanceExpr.Column);
    }

    /// <summary>`new` cannot construct a NULLABLE type, and this says so in the language's own words instead of
    /// letting the `?` become the conditional operator. Measured 2026-10-06 (reported by the maintainer as a
    /// message too generic for a case the language is right to refuse): `new object?()` reported a bare
    /// "Unexpected token" at the `)`, because the parser read `new object` and then took the `?` for a ternary
    /// whose then-branch was `()`; the report named neither the construct nor the fix. `T?` means "this value
    /// MAY be null", which is not a type to build, so the refusal is correct and only the wording was unusable.
    /// Reached from the type-creation branch, which IsTypeCreation() routes here by accepting the `?` before it
    /// requires the `(`.</summary>
    private void RefuseNullableNewType(TypeInfo ti, Token newToken)
    {
        if (!ti.IsNullable) return;
        // The name as a SCRIPT writes it, generic arguments included, because the advice has to be something the
        // reader can type back: a bare name would answer `new List<int>?()` with "write `new List()`", which is a
        // different error. A nested argument keeps its own marker (an inner `int?` is written "int?"), while the
        // OUTER one is dropped here and added once below, or `int?` came out as `int??` (measured 2026-10-06).
        static string Written(TypeInfo t)
        {
            var n = t.TypeName ?? TypeTokenHelper.ScriptNameOf(t);
            if (t.GenericArgs is { Count: > 0 })
                n += "<" + string.Join(", ", t.GenericArgs.Select(Written)) + ">";
            return n;
        }
        var plain = Written(ti);
        if (plain.EndsWith('?')) plain = plain[..^1];
        throw Error($"`new` cannot construct a nullable type: `{plain}?` is a value that MAY be null, not a type to "
            + $"build. Write `new {plain}()` and, if you want a nullable variable, declare it as `{plain}? v = null;`.",
            newToken);
    }

    private bool IsTypeCreation()
    {
        var save = _current;
        try
        {
            if (!IsTypeStart(Peek()) && Peek().Type != TokenType.Identifier) return false;
            if (IsTypeStart(Peek())) _ = Advance();
            else if (Peek().Type == TokenType.Identifier)
            {
                _ = Advance();
                while (Match(TokenType.Dot))
                {
                    if (!Check(TokenType.Identifier)) return false;
                    _ = Advance();
                }
            }
            else return false;

            if (Match(TokenType.Less))
            {
                var genericDepth = 1;
                while (genericDepth > 0 && !IsAtEnd())
                {
                    if (Match(TokenType.LeftShift)) genericDepth += 2;   // << = two <
                    else if (Match(TokenType.RightShift)) genericDepth -= 2; // >> = two >
                    else if (Match(TokenType.Less)) genericDepth++;
                    else if (Match(TokenType.Greater)) genericDepth--;
                    else _ = Advance();
                }
                if (genericDepth != 0) return false;
            }

            while (Match(TokenType.LeftBracket))
            {
                var bracketDepth = 1;
                while (bracketDepth > 0 && !IsAtEnd())
                {
                    if (Match(TokenType.LeftBracket)) bracketDepth++;
                    else if (Match(TokenType.RightBracket)) bracketDepth--;
                    else _ = Advance();
                }
                if (bracketDepth != 0) return false;
            }

            // A nullable marker is part of the TYPE, so `new Foo?()` reaches the creation branch and is refused
            // THERE by name (RefuseNullableNewType) rather than falling through to the conditional operator,
            // whose then-branch would be the `()` and whose report was a bare "Unexpected token" at the `)`.
            Match(TokenType.Question);

            return Check(TokenType.LeftParen);
        }
        finally { _current = save; }
    }

    private bool IsArrayCreation()
    {
        var save = _current;
        try
        {
            if (!IsTypeStart(Peek()) && Peek().Type != TokenType.Identifier) return false;
            if (IsTypeStart(Peek())) _ = Advance();
            else if (Peek().Type == TokenType.Identifier)
            {
                _ = Advance();
                while (Match(TokenType.Dot)) { if (!Check(TokenType.Identifier)) return false; _ = Advance(); }
            }
            else return false;

            // The same nullable marker one SPELLING over: `new int?[2]` builds an array OF a nullable, and without
            // this the `?` became the conditional operator again ("Expect ':' in ternary", measured by
            // assets/language/probes/probe-nullable.slc, 2026-10-06). Placed before the `<` test so a generic element type
            // (`new List<int>?[2]`) accepts it too.
            Match(TokenType.Question);

            if (!Match(TokenType.Less)) return Check(TokenType.LeftBracket);
            var genericDepth = 1;
            while (genericDepth > 0 && !IsAtEnd())
            {
                if (Match(TokenType.Less)) genericDepth++;
                else if (Match(TokenType.Greater)) genericDepth--;
                else _ = Advance();
            }
            if (genericDepth != 0) return false;

            return Check(TokenType.LeftBracket);
        }
        finally { _current = save; }
    }

    private NewStructExpr ParseNewStruct()
    {
        var structToken = Previous();
        var name = ConsumeIdentifier("Expect struct name.");
        IReadOnlyList<Expr>? fieldValues = null;
        if (!Match(TokenType.LeftParen))
            return NewStructExpr.Create(name.Literal, fieldValues, structToken.LineNumber, structToken.Column);
        var values = new List<Expr>();
        if (!Check(TokenType.RightParen)) { do { values.Add(Expression()); } while (Match(TokenType.Comma)); }
        _ = Consume(TokenType.RightParen, "Expect ')' after values.");
        fieldValues = values;
        return NewStructExpr.Create(name.Literal, fieldValues, structToken.LineNumber, structToken.Column);
    }

    private NewArrayExpr ParseNewArrayExpr(Token newToken)
    {
        return ParseArrayTail(ParseElementTypeForArray(), newToken);
    }

    private NewArrayExpr ParseArrayTail(TypeInfo baseType, Token newToken)
    {
        if (!string.IsNullOrEmpty(baseType.TypeName) && baseType.GenericArgs is { Count: > 0 } && Check(TokenType.LeftBracket))
        {
            var tempPos = _current;
            var hasNumber = false;
            try { if (Match(TokenType.LeftBracket) && Peek().Type == TokenType.Number) hasNumber = true; }
            finally { _current = tempPos; }
            if (hasNumber)
                Warn($"Array creation with generic delegate type '{baseType.TypeName}[...]' may not work as expected.\n" +
                     $"Use 'new List<{baseType.TypeName}>()' or array initializer syntax 'new {baseType.TypeName}[] {{ ... }}' instead.", newToken);
        }

        var jaggedDepth = 0;
        IReadOnlyList<Expr>? sizes = null;
        if (!Match(TokenType.LeftBracket)) throw Error("Expected '[' after type in array creation.", newToken);
        if (!Check(TokenType.RightBracket))
        {
            var sizeList = new List<Expr> { Expression() };
            _ = Consume(TokenType.RightBracket, "Expect ']' after array size.");
            sizes = sizeList;
        }
        else _ = Consume(TokenType.RightBracket, "Expect ']'.");

        while (Match(TokenType.LeftBracket)) { _ = Consume(TokenType.RightBracket, "Expect ']' after empty bracket pair."); jaggedDepth++; }

        IReadOnlyList<Expr>? initializer = null;
        if (Match(TokenType.LeftBrace))
        {
            var initList = new List<Expr>();
            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                initList.Add(Expression());
                if (!Check(TokenType.RightBrace)) _ = Consume(TokenType.Comma, "Expect ',' or '}' after array initializer.");
            }
            _ = Consume(TokenType.RightBrace, "Expect '}' after array initializer.");
            initializer = initList;
        }

        if (sizes == null && initializer == null)
            throw Error("Array creation must specify either size or initializer.", newToken);

        var elemType = new TypeInfo(baseType.BaseType, baseType.IsNullable, 0, jaggedDepth, baseType.TypeName, baseType.GenericArgs, baseType.TupleElementTypes, baseType.TupleElementNames);

        return NewArrayExpr.Create(elemType, sizes, initializer, newToken.LineNumber, newToken.Column);
    }

    // ─── List literal: [expr, expr, ...] ─────────────────────────────────

    private Expr ParseListLiteral(Token lbracket)
    {
        var elements = new List<Expr>();
        if (!Check(TokenType.RightBracket))
        {
            do { elements.Add(Expression()); }
            while (Match(TokenType.Comma));
        }

        // Check for comprehension pattern: [expr for var in iterable (if cond)?]
        if (Match(TokenType.For))
        {
            var firstElem = elements.Count > 0 ? elements[0] : null;
            if (firstElem == null)
                throw Error("Expected expression before 'for' in comprehension.", lbracket);

            var iterVar = ConsumeIdentifier("Expect iteration variable name after 'for'.").Literal;
            _ = Consume(TokenType.In, "Expect 'in' after iteration variable.");
            var collection = Expression();

            Expr? filter = null;
            if (Match(TokenType.If))
                filter = Expression();

            _ = Consume(TokenType.RightBracket, "Expect ']' after comprehension.");
            return ComprehensionExpr.Create(firstElem, iterVar, collection, filter, lbracket.LineNumber, lbracket.Column);
        }

        _ = Consume(TokenType.RightBracket, "Expect ']' after list literal elements.");

        var elemType = InferListElementType(elements);
        return ListLiteralExpr.Create(elements.AsReadOnly(), elemType, lbracket.LineNumber, lbracket.Column);
    }

    private Expr ParseDictLiteral(Token lbrace)
    {
        var entries = new List<(Expr Key, Expr Value)>();
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            Expr key;
            if (Check(TokenType.Identifier))
            {
                var idToken = Advance();
                key = StringExpr.Create(idToken.Literal, idToken.LineNumber, idToken.Column);
            }
            else
                key = Expression();
            _ = Consume(TokenType.Colon, "Expect ':' after dictionary key.");
            var value = Expression();
            entries.Add((key, value));
            if (!Match(TokenType.Comma)) break;
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after dictionary literal.");
        return DictLiteralExpr.Create(entries.AsReadOnly(), lbrace.LineNumber, lbrace.Column);
    }

    /// <summary>Infers the common element type for a list literal from its elements.
    /// Returns object? when elements are absent or have mixed types.</summary>
    private static TypeInfo InferListElementType(List<Expr> elements)
    {
        if (elements.Count == 0)
            return new TypeInfo(TokenType.Object, true, 0, 0, null);

        // Scan each element to determine a common type
        TypeInfo? common = null;
        foreach (var t in elements.Select(GetExprBaseType).OfType<TypeInfo>())
        {
            if (common == null) { common = t; continue; }

            // Promote if needed: int → long → double
            if (common.BaseType == t.BaseType) continue;
            if (IsNumeric(common) && IsNumeric(t))
                common = PromoteNumeric(common, t);
            else
                return new TypeInfo(TokenType.Object, true, 0, 0, null);
        }
        return common ?? new TypeInfo(TokenType.Object, true, 0, 0, null);
    }

    private Expr ParseSwitchExpr(Expr subject)
    {
        var start = Previous(); // 'switch' keyword
        _ = Advance(); // consume 'switch'
        _ = Advance(); // consume '{'
        var arms = new List<SwitchArm>();
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            Pattern pattern;
            // Peek, don't consume, Match would eat Delegate/ClassInfo/etc.
            if (Check(TokenType.Identifier) && Peek().Literal == "_")
            {
                _ = Advance();
                pattern = new DiscardPattern { Line = Previous().LineNumber, Column = Previous().Column };
            }
            else
                pattern = ParsePattern();
            Expr? guard = null;
            if (Match(TokenType.When)) guard = Expression();
            _ = Consume(TokenType.Arrow, "Expect '=>' after pattern in switch arm.");
            var value = Expression();
            arms.Add(new SwitchArm(pattern, guard, value));
            if (!Check(TokenType.RightBrace))
                _ = Consume(TokenType.Comma, "Expect ',' between switch arms.");
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after switch arms.");
        WarnArmDiagnostics(arms.Select(a => a.Pattern), warnNonExhaustive: true, start);
        return SwitchExpr.Create(subject, arms, start.LineNumber, start.Column);
    }

    private Expr ParseMatch(Expr subject)
    {
        _ = Advance(); // consume 'match'
        var matchToken = Previous();
        return ParseMatchBody(subject, matchToken);
    }

    /// <summary>FP: shared match-arm parsing, consumes '{' + arms + '}'. Used by the postfix form
    /// (`subject match { ... }`, via ParseMatch) and the prefix form (`match (subject) { ... }`,
    /// from Primary, where the subject is already parsed and _current is on '{').</summary>
    private Expr ParseMatchBody(Expr subject, Token matchToken)
    {
        _ = Advance(); // consume '{'
        var arms = new List<SwitchArm>();
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            // Optional `case` keyword (prefix form); the grammar is the same as `switch`.
            Match(TokenType.Case);
            // Suppress bare-lambda parsing so a bare-identifier arm pattern (`Function => "Function"`)
            // is NOT mistaken for an arrow lambda. Cleared right after the pattern is parsed.
            _suppressBareLambda = true;
            var savedInArm = _inMatchArmPattern;
            _inMatchArmPattern = true;
            Pattern pattern;
            try { pattern = ParsePattern(); }
            finally { _suppressBareLambda = false; _inMatchArmPattern = savedInArm; }
            Expr? guard = null;
            if (Match(TokenType.When)) guard = Expression();
            _ = Consume(TokenType.Arrow, "Expect '=>' after pattern in match arm.");
            var value = Expression();
            arms.Add(new SwitchArm(pattern, guard, value));
            if (!Check(TokenType.RightBrace))
                _ = Consume(TokenType.Comma, "Expect ',' between match arms.");
        }
        _ = Consume(TokenType.RightBrace, "Expect '}' after match arms.");

        if (arms.Count == 0)
            throw Error("Match expression must have at least one arm.", matchToken);

        WarnArmDiagnostics(arms.Select(a => a.Pattern), warnNonExhaustive: false, matchToken);
        return arms[^1].Pattern is not DiscardPattern
            ? throw Error("Last match arm must be a wildcard '_'.", matchToken)
            : MatchExpr.Create(subject, arms, matchToken.LineNumber, matchToken.Column);
    }

    private static TypeInfo? GetExprBaseType(Expr expr)
    {
        return expr switch
        {
            NumberExpr n => n.Type,
            StringExpr => new TypeInfo(TokenType.StringType, false, 0, 0, null),
            BoolExpr => new TypeInfo(TokenType.Bool, false, 0, 0, null),
            CharExpr => new TypeInfo(TokenType.Char, false, 0, 0, null),
            NullExpr => null,
            DictLiteralExpr => new TypeInfo(TokenType.Object, true, 0, 0, "Dictionary<object, object?>"),
            _ => new TypeInfo(TokenType.Object, true, 0, 0, null)
        };
    }

    private static bool IsNumeric(TypeInfo t)
        => t.BaseType is TokenType.Int or TokenType.Long or TokenType.Double or TokenType.Float;

    private static TypeInfo PromoteNumeric(TypeInfo a, TypeInfo b)
    {
        return Rank(a) >= Rank(b) ? a : b;

        static int Rank(TypeInfo t) =>
            t.BaseType switch
            {
                TokenType.Int => 0,
                TokenType.Long => 1,
                TokenType.Float => 2,
                TokenType.Double => 3,
                _ => -1
            };
    }

    // ─── Yield detection ─────────────────────────────────────────────────

    private static bool ContainsYield(IReadOnlyList<Stmt>? statements)
        => statements != null && statements.Any(ContainsYieldInStmt);

    private static bool ContainsYieldInStmt(Stmt stmt)
        => stmt switch
        {
            ExprStmt { Expression: YieldExpr } => true,
            ReturnStmt { Value: YieldExpr } => true,
            ReturnStmt { Value: not null } ret => ContainsYieldInExpr(ret.Value),
            ExprStmt es => ContainsYieldInExpr(es.Expression),
            BlockStmt block => ContainsYield(block.Statements),
            IfStmt ifs => ContainsYield(ifs.ThenBranch) || ContainsYield(ifs.ElseBranch),
            WhileStmt ws => ContainsYield(ws.Body),
            ForStmt fs => ContainsYield(fs.Body),
            ForeachStmt fes => ContainsYield(fes.Body),
            VarDeclStmt { Initializer: not null } vds => ContainsYieldInExpr(vds.Initializer),
            _ => false
        };

    /// <summary>True when this subtree contains a <c>yield</c>. A CHAIN is left-nested, so recursing per operator
    /// costs one NATIVE frame each and a deep chain ends the process (measured: 20,000 added terms killed the
    /// shipped binary with a stack overflow, in THIS walk, reached from <c>FunctionDeclaration</c>). The spines are
    /// therefore walked with a LOOP; every other shape still recurses through <see cref="ContainsYieldInExprBody"/>,
    /// whose arms are unchanged. The answer is identical, because the predicate is one OR and the loop only changes
    /// the order in which the same nodes are visited.</summary>
    internal static bool ContainsYieldInExpr(Expr expr)
    {
        while (true)
        {
            if (expr is BinaryExpr be) { if (ContainsYieldInExpr(be.Right)) return true; expr = be.Left; continue; }
            if (expr is LogicalExpr le) { if (ContainsYieldInExpr(le.Right)) return true; expr = le.Left; continue; }
            break;
        }
        return ContainsYieldInExprBody(expr);
    }

    private static bool ContainsYieldInExprBody(Expr expr)
        => expr switch
        {
            YieldExpr => true,
            AwaitExpr aw => ContainsYieldInExpr(aw.Operand),
            BinaryExpr b => ContainsYieldInExpr(b.Left) || ContainsYieldInExpr(b.Right),
            LogicalExpr le => ContainsYieldInExpr(le.Left) || ContainsYieldInExpr(le.Right),
            UnaryExpr u => ContainsYieldInExpr(u.Right),
            CallExpr c => ContainsYieldInExpr(c.Callee) || c.Arguments.Any(ContainsYieldInExpr),
            MethodCallExpr mc => ContainsYieldInExpr(mc.Receiver) || mc.Arguments.Any(ContainsYieldInExpr),
            ConditionalExpr cond => ContainsYieldInExpr(cond.Condition) || ContainsYieldInExpr(cond.ThenExpr) || ContainsYieldInExpr(cond.ElseExpr),
            AssignExpr a => ContainsYieldInExpr(a.Value),
            CompoundAssignExpr ca => ContainsYieldInExpr(ca.Target) || ContainsYieldInExpr(ca.Value),
            IncrementDecrementExpr ide => ContainsYieldInExpr(ide.Target),
            LambdaExpr l => l.BodyExpr != null ? ContainsYieldInExpr(l.BodyExpr) : l.BodyStmts != null && ContainsYield(l.BodyStmts),
            TupleExpr t => t.Items.Any(ContainsYieldInExpr),
            ListLiteralExpr ll => ll.Elements.Any(ContainsYieldInExpr),
            DictLiteralExpr dl => dl.Entries.Any(e => ContainsYieldInExpr(e.Key) || ContainsYieldInExpr(e.Value)),
            ArrayAccessExpr aa => ContainsYieldInExpr(aa.Array) || aa.Indices.Any(ContainsYieldInExpr),
            ArrayAssignExpr aas => ContainsYieldInExpr(aas.Array) || aas.Indices.Any(ContainsYieldInExpr) || ContainsYieldInExpr(aas.Value),
            PropertyAccessExpr pa => ContainsYieldInExpr(pa.Target),
            PropertyAssignExpr pas => ContainsYieldInExpr(pas.Target) || ContainsYieldInExpr(pas.Value),
            RangeExpr r => (r.From != null && ContainsYieldInExpr(r.From)) || (r.To != null && ContainsYieldInExpr(r.To)),
            NullCoalescingExpr nc => ContainsYieldInExpr(nc.Left) || ContainsYieldInExpr(nc.Right),
            CastExpr cast => ContainsYieldInExpr(cast.Expression),
            IsExpr ise => ContainsYieldInExpr(ise.Left),
            AsExpr ase => ContainsYieldInExpr(ase.Left),
            IsNullExpr inle => ContainsYieldInExpr(inle.Left),
            IsPatternExpr ipe => ContainsYieldInExpr(ipe.Left),
            IndexExpr ie => ContainsYieldInExpr(ie.Value),
            NewInstanceExpr nie => (nie.Arguments != null && nie.Arguments.Any(ContainsYieldInExpr)) || ContainsYieldInExpr(nie.ConstructorExpr),
            NewArrayExpr nae => (nae.Sizes != null && nae.Sizes.Any(ContainsYieldInExpr)) || (nae.Initializer != null && nae.Initializer.Any(ContainsYieldInExpr)),
            NewStructExpr nse => nse.FieldValues != null && nse.FieldValues.Any(ContainsYieldInExpr),
            ComprehensionExpr cmp => ContainsYieldInExpr(cmp.ElementExpr) || ContainsYieldInExpr(cmp.Collection) || (cmp.Filter != null && ContainsYieldInExpr(cmp.Filter)),
            SwitchExpr sex => ContainsYieldInExpr(sex.Subject) || sex.Arms.Any(a => (a.Guard != null && ContainsYieldInExpr(a.Guard)) || ContainsYieldInExpr(a.Value)),
            MatchExpr mx => ContainsYieldInExpr(mx.Subject) || mx.Arms.Any(arm => (arm.Guard != null && ContainsYieldInExpr(arm.Guard)) || ContainsYieldInExpr(arm.Value)),
            ObjectInitExpr oie => ContainsYieldInExpr(oie.Inner) || oie.Initializers.Any(i => ContainsYieldInExpr(i.Value)),
            WithExpr we => ContainsYieldInExpr(we.Base) || we.Fields.Any(f => ContainsYieldInExpr(f.Value)),
            _ => false
        };
}