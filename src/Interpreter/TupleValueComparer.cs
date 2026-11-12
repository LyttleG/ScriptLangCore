namespace ScriptLangCore;
/// <summary>
/// Script-aware value equality for TUPLES used as container keys/elements ONLY.
/// Distinct tuples with equal element values compare equal; ScriptTuple identity
/// and the script-level == / != operators are untouched (they keep reference
/// semantics). Scalars/strings use engine equality (numeric widening); nested
/// tuples recurse; any other composite value compares by reference identity.
/// </summary>
internal sealed class TupleValueComparer : IEqualityComparer<object?>
{
    public static readonly TupleValueComparer Instance = new();

    public new bool Equals(object? x, object? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;

        if (x is ScriptTuple tx)
        {
            if (y is not ScriptTuple ty || tx.Length != ty.Length) return false;
            for (var i = 0; i < tx.Length; i++)
                if (!Equals(tx[i], ty[i])) return false;
            return true;
        }
        if (y is ScriptTuple) return false; // x isn't a tuple, y is -> can't match

        return Interpreter.AreEqual(x, y);
    }

    public int GetHashCode(object? obj) => HashCore(obj);

    private static int HashCore(object? obj)
    {
        if (obj is null) return 0;
        if (obj is ScriptTuple t)
        {
            var hc = new HashCode();
            for (var i = 0; i < t.Length; i++)
                hc.Add(ElementHash(t[i]));
            return hc.ToHashCode();
        }
        return ElementHash(obj);
    }

    /// <summary>Hash of one element, consistent with Equals: numeric widening means
    /// engine-equal numbers must collide, so numerics canonicalize to decimal when
    /// representable; composites hash by runtime identity.</summary>
    private static int ElementHash(object? v) => v switch
    {
        null => 0,
        ScriptTuple => HashCore(v),
        byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal => CanonicalNumericHash(v),
        Array or IScriptList or IScriptDictionary => RuntimeHelpers.GetHashCode(v),
        _ => v.GetHashCode()
    };

    private static int CanonicalNumericHash(object v) => v switch
    {
        decimal m => m.GetHashCode(),
        byte b => ((decimal)b).GetHashCode(),
        sbyte sb => ((decimal)sb).GetHashCode(),
        short s => ((decimal)s).GetHashCode(),
        ushort us => ((decimal)us).GetHashCode(),
        int i => ((decimal)i).GetHashCode(),
        uint ui => ((decimal)ui).GetHashCode(),
        long l => ((decimal)l).GetHashCode(),
        ulong ul => ((decimal)ul).GetHashCode(),
        float f => InRange(f) ? ((decimal)f).GetHashCode() : f.GetHashCode(),
        double d => InRange(d) ? ((decimal)d).GetHashCode() : d.GetHashCode(),
        _ => v.GetHashCode()
    };

    private static bool InRange(float f) =>
      float.IsFinite(f) && f >= (float)decimal.MinValue && f <= (float)decimal.MaxValue;

    private static bool InRange(double d) =>
        double.IsFinite(d) && d >= (double)decimal.MinValue && d <= (double)decimal.MaxValue;
}