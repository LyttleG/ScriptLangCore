namespace ScriptLangCore;

/// <summary>
/// Optimized readonly list of TypeInfo that stores up to 4 inline values
/// to avoid heap allocations for common generic type parameter counts.
/// </summary>
internal sealed record TypeArgList : IReadOnlyList<TypeInfo>
{
    private readonly TypeInfo? _arg0, _arg1, _arg2, _arg3;
    private readonly TypeInfo[]? _overflow;

    public TypeArgList(IReadOnlyList<TypeInfo> args)
    {
        Count = args.Count;
        switch (args.Count)
        {
            case 0:
                _arg0 = _arg1 = _arg2 = _arg3 = null!;
                break;

            case 1:
                _arg0 = args[0];
                break;

            case 2:
                _arg0 = args[0];
                _arg1 = args[1];
                break;

            case 3:
                _arg0 = args[0];
                _arg1 = args[1];
                _arg2 = args[2];
                break;

            case 4:
                _arg0 = args[0];
                _arg1 = args[1];
                _arg2 = args[2];
                _arg3 = args[3];
                break;

            default:
                _overflow = new TypeInfo[args.Count];
                for (var i = 0; i < args.Count; i++)
                    _overflow[i] = args[i];
                break;
        }
    }

    public int Count { get; }

    public TypeInfo this[int index]
    {
        get
        {
            if ((uint)index >= Count)
                throw new ScriptException("Type argument index out of range.");

            if (_overflow != null)
                return _overflow[index];

            var type = index switch
            {
                0 => _arg0,
                1 => _arg1,
                2 => _arg2,
                3 => _arg3,
                _ => null!, // should never happen due to the range check above
            };

            return type ?? throw new ScriptException("Type argument index out of range.");
        }
    }

    public IEnumerator<TypeInfo> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Optional: static empty instance to avoid allocations for non-generic types
    public static readonly TypeArgList Empty = new([]); // don't modify!
}
