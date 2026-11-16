// ────────────────────────────────────────────────────────────────────
// NumericTypeConverter, centralized numeric type conversion helpers.
// Replaces repeated if-blocks and switch-statements with lookup tables.
// ────────────────────────────────────────────────────────────────────

namespace ScriptLangCore;

internal static class NumericTypeConverter
{
    /// <summary>Fast check: is the boxed value a numeric type?</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNumeric(object? value) =>
        value is byte or sbyte or short or ushort or int or uint or
               long or ulong or float or double or decimal or char;
    /// <summary>Fast check: is the boxed value an integral type (no float/double/decimal)?</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsIntegral(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or char;

    /// <summary>Map CLR type → Func{long → object} for numeric widening from JSON (all integers parsed as long).</summary>
    private static readonly Dictionary<Type, Func<long, object>> FromLong = new()
    {
        [typeof(int)] = v => (int)v,
        [typeof(short)] = v => (short)v,
        [typeof(ushort)] = v => (ushort)v,
        [typeof(uint)] = v => (uint)v,
        [typeof(byte)] = v => (byte)v,
        [typeof(sbyte)] = v => (sbyte)v,
        [typeof(float)] = v => (float)v,
        [typeof(double)] = v => (double)v,
        [typeof(decimal)] = v => (decimal)v,
    };

    /// <summary>Map TokenType → Func{long → object} for deserialization.</summary>
    public static readonly Dictionary<TokenType, Func<long, object>> FromLongByToken = new()
    {
        [TokenType.Int] = v => (int)v,
        [TokenType.Short] = v => (short)v,
        [TokenType.UShort] = v => (ushort)v,
        [TokenType.UInt] = v => (uint)v,
        [TokenType.Byte] = v => (byte)v,
        [TokenType.SByte] = v => (sbyte)v,
        [TokenType.Float] = v => (float)v,
        [TokenType.Double] = v => (double)v,
        [TokenType.Decimal] = v => (decimal)v,
    };

    /// <summary>Widens small integer types to int so arithmetic stays precise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object? WidenSmallInt(object? val) => val switch
    {
        sbyte sb => (int)sb,
        byte b => (int)b,
        short s => (int)s,
        ushort us => (int)us,
        char c => (int)c,
        _ => val
    };

    /// <summary>Convert any numeric type to double for arithmetic.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ConvertToDouble(object value) => value switch
    {
        byte b => b,
        sbyte sb => sb,
        short s => s,
        ushort us => us,
        int i => i,
        uint ui => ui,
        long l => l,
        ulong ul => ul,
        char c => c,
        float f => f,
        double d => d,
        decimal dec => (double)dec,
        _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to double")
    };

    /// <summary>Default values for value types by TokenType.</summary>
    public static readonly IReadOnlyDictionary<TokenType, object> DefaultValueMap = new Dictionary<TokenType, object>
    {
        { TokenType.Byte, (byte)0 }, { TokenType.Short, (short)0 }, { TokenType.Int, 0 },
        { TokenType.Long, 0L }, { TokenType.UInt, (uint)0 }, { TokenType.UShort, (ushort)0 },
        { TokenType.SByte, (sbyte)0 }, { TokenType.ULong, (ulong)0 },
        { TokenType.Float, 0.0f }, { TokenType.Double, 0.0 },
        { TokenType.Decimal, 0.0m }, { TokenType.Bool, false }, { TokenType.Char, '\0' }
    };

    /// <summary>Write any numeric value to Utf8JsonWriter with per-type dispatch (preserves precision).</summary>
    public static void WriteJsonNumber(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case uint ui: writer.WriteNumberValue(ui); break;
            case ulong ul: writer.WriteNumberValue(ul); break;
            case short sh: writer.WriteNumberValue(sh); break;
            case ushort us: writer.WriteNumberValue(us); break;
            case byte by: writer.WriteNumberValue(by); break;
            case sbyte sb: writer.WriteNumberValue(sb); break;
            case float f: writer.WriteNumberValue(f); break;
            case double d: writer.WriteNumberValue(d); break;
            case decimal dec: writer.WriteNumberValue(dec); break;
            default: writer.WriteNumberValue(Convert.ToDouble(value)); break;
        }
    }

    /// <summary>Convert numeric double to specific target type. Throws InvalidCastException on overflow.</summary>
    public static object ConvertNumeric(double value, TokenType targetType)
    {
        try
        {
            return targetType switch
            {
                TokenType.Byte => Convert.ToByte(value),
                TokenType.Short => Convert.ToInt16(value),
                TokenType.Int => Convert.ToInt32(value),
                TokenType.Long => Convert.ToInt64(value),
                TokenType.UInt => Convert.ToUInt32(value),
                TokenType.UShort => Convert.ToUInt16(value),
                TokenType.SByte => Convert.ToSByte(value),
                TokenType.ULong => Convert.ToUInt64(value),
                TokenType.Float => Convert.ToSingle(value),
                TokenType.Double => value,
                TokenType.Decimal => Convert.ToDecimal(value),
                _ => throw new InvalidCastException($"Unsupported numeric target type {targetType}")
            };
        }
        catch (OverflowException)
        {
            throw new InvalidCastException($"Numeric value {value} is out of range for type {TypeInfo.SimpleTypeName(targetType)}.");
        }
    }

    /// <summary>Coerce a numeric value to a CLR type. Returns null on failure.</summary>
    public static object? CoerceToType(object? val, Type targetType)
    {
        if (val == null) return null;

        // Already correct type
        if (targetType.IsInstanceOfType(val)) return val;

        // Long → narrower (JSON parser returns long for all integers)
        if (val is long lVal && FromLong.TryGetValue(targetType, out var fromLong))
            return fromLong(lVal);

        // int → wider (int → double/float/decimal)
        if (val is int iVal)
        {
            if (targetType == typeof(double)) return (double)iVal;
            if (targetType == typeof(float)) return (float)iVal;
            if (targetType == typeof(decimal)) return (decimal)iVal;
        }

        // double → decimal
        if (val is double dVal && targetType == typeof(decimal))
            return (decimal)dVal;

        return null; // can't coerce
    }
}
