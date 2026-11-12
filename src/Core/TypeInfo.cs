namespace ScriptLangCore;

internal sealed record TypeInfo(
    TokenType BaseType,
    bool IsNullable,
    int ArrayRank,
    int JaggedDepth,
    string? TypeName,
    TypeArgList? GenericArgs = null,
    IReadOnlyList<TypeInfo>? TupleElementTypes = null,
    IReadOnlyList<string>? TupleElementNames = null
)
{
    public bool IsArray => ArrayRank > 0 || JaggedDepth > 0;
    public bool IsScalar => !IsArray;
    public bool IsTupleType => TupleElementTypes is { Count: > 0 };
    private bool IsGeneric => GenericArgs is { Count: > 0 };
    public bool IsGenericParameter => TypeName != null && BaseType == TokenType.Object && !IsArray && !IsNullable && !IsGeneric;
    public static TypeInfo Void => new(TokenType.Void, false, 0, 0, null);

    /// <summary>The return type a LAMBDA that yields a value is DECLARED with, on BOTH engines: the widest
    /// type, so no return coercion runs, and the declaration is the same whichever engine built the lambda.
    /// The declaration is what a script reads back (`print(lambda)` renders its signature), so the tree
    /// walker and the bytecode compiler cannot each keep their own spelling of it. `CoerceTo` accepts null
    /// for an object, so a lambda that returns null is still accepted.</summary>
    public static TypeInfo LambdaValueReturn => new(TokenType.Object, false, 0, 0, null);

    internal static string SimpleTypeName(TokenType t) => TypeSystem.Name(t);

    /// <summary>The type as a script WRITES it, the key the interface registry uses: `IShape`, `I<int>`. ONE
    /// renderer, so an `implements` clause and an `is` test cannot spell one type two ways.
    ///
    /// It renders into a SPAN and allocates nothing itself: the common case writes into a stack buffer and exactly
    /// one string is built at the end, and a deeper type takes the growing-buffer path rather than a second walk.
    /// A caller that already owns a buffer calls FormatSpelling and allocates nothing at all.</summary>
    public static string Spelling(TypeInfo type)
    {
        // A non-generic type is spelled by its own NAME, a string this engine already holds: no buffer, no copy,
        // and no allocation on the `is` path, which asks for a spelling at every interface test.
        if (type.GenericArgs is not { Count: > 0 }) return type.TypeName ?? SimpleTypeName(type.BaseType);
        Span<char> buffer = stackalloc char[StackSpellingChars];
        var written = FormatSpelling(type, buffer);
        if (written >= 0) return new string(buffer[..written]);
        for (var size = StackSpellingChars * 2; ; size *= 2)
        {
            var larger = new char[size];
            var length = FormatSpelling(type, larger);
            if (length >= 0) return new string(larger, 0, length);
        }
    }

    /// <summary>The bound a spelling may take on the STACK before the growing buffer is used, which is the shape the
    /// rest of this engine takes on a hot path: one bounded fast path, and one overflow path for the rare case.</summary>
    private const int StackSpellingChars = 256;

    /// <summary>Writes the spelling into `destination` and returns how many chars it wrote, or -1 when it does not
    /// fit. This is the ONE implementation of the shape; Spelling sizes a single string around it, so the two can
    /// never disagree, and nothing here allocates.</summary>
    public static int FormatSpelling(TypeInfo type, Span<char> destination)
    {
        var name = type.TypeName ?? SimpleTypeName(type.BaseType);
        if (name.Length > destination.Length) return -1;
        name.AsSpan().CopyTo(destination);
        var written = name.Length;
        if (type.GenericArgs is not { Count: > 0 } args) return written;
        if (written >= destination.Length) return -1;
        destination[written++] = '<';
        for (var index = 0; index < args.Count; index++)
        {
            if (index > 0)
            {
                if (written + 2 > destination.Length) return -1;
                destination[written++] = ',';
                destination[written++] = ' ';
            }
            var inner = FormatSpelling(args[index], destination[written..]);
            if (inner < 0) return -1;
            written += inner;
        }
        if (written >= destination.Length) return -1;
        destination[written++] = '>';
        return written;
    }

    /// <summary>The type as a DECLARATION writes it, which is what a script can write back and what `typeof(<a name>)`
    /// reports for a null: the generic arguments (from <see cref="Spelling"/>), then the nullable mark and the array
    /// ranks, which Spelling does not render because it is the INTERFACE key (`I<int>` beside `is I`). Two renderers
    /// existed and neither was the whole name: Spelling answered "List&lt;int&gt;" for `List&lt;int&gt;?` and "int" for
    /// `int[]`, while ToString answered "int[]" but dropped a generic's arguments.</summary>
    public static string DeclaredSpelling(TypeInfo type)
    {
        var name = Spelling(type);
        if (type.IsNullable && !name.EndsWith('?')) name += "?";
        var ranks = type.ArrayRank + type.JaggedDepth;
        for (var i = 0; i < ranks; i++) name += "[]";
        return name;
    }

    public override string ToString()
    {
        if (TupleElementTypes is { Count: > 0 })
            return "(" + string.Join(", ", TupleElementTypes.Select(t => t.ToString())) + ")";
        var name = TypeName ?? SimpleTypeName(BaseType);
        if (IsNullable) name += "?";
        for (var i = 0; i < ArrayRank; i++) name += "[]";
        return name;
    }

    /// <summary>Creates a TypeInfo representing a generic type parameter (e.g. T).</summary>
    public static TypeInfo GenericParameter(string name) =>
        new(TokenType.Object, false, 0, 0, name);

    /// <summary>Substitutes generic type parameters using the given mapping. Returns a new TypeInfo.</summary>
    public TypeInfo Substitute(Dictionary<string, TypeInfo> mapping)
    {
        // If this is a generic parameter, look it up and substitute. `IsGenericParameter` deliberately
        // excludes array and nullable shapes, so `T[]`, `T[][]` and `T?` are recognised by NAME here: the
        // concrete type is substituted and the declared shape is kept (added to the concrete one's own, so a
        // `T[]` whose T is itself `int[]` becomes `int[][]`). Without this, an array of a type parameter
        // stayed "T[]", a name nothing could resolve, so the element type was lost.
        if (TypeName != null && mapping.TryGetValue(TypeName, out var concrete)
            && (IsGenericParameter || IsArray || IsNullable))
        {
            if (!IsArray && !IsNullable) return concrete;
            return concrete with
            {
                ArrayRank = concrete.ArrayRank + ArrayRank,
                JaggedDepth = concrete.JaggedDepth + JaggedDepth,
                IsNullable = IsNullable || concrete.IsNullable,
            };
        }

        // Substitute generic arguments (e.g. Box<T> → Box<int>)
        if (GenericArgs is { Count: > 0 })
        {
            var newArgs = new TypeInfo[GenericArgs.Count];
            for (var i = 0; i < GenericArgs.Count; i++)
                newArgs[i] = GenericArgs[i].Substitute(mapping);
            return this with { GenericArgs = [with(newArgs)] };
        }

        // Substitute type name if it contains generic parameter names
        // (used for class references like "Pair<T,int>")
        if (TypeName == null || mapping.Count <= 0) return this;
        var rewritten = TypeName;
        foreach (var kv in mapping)
        {
            // Replace <T> in the type name with the concrete type name
            var search = $"<{kv.Key}>";
            if (rewritten.Contains(search))
                rewritten = rewritten.Replace(search, $"<{kv.Value.TypeName ?? kv.Value.BaseType.ToString()}>");
        }

        if (rewritten != TypeName)
            return this with { TypeName = rewritten };

        return this;
    }
}
