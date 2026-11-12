using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>The logical types, as the mapping between an Avro base type and the SCRIPT type that stands for it.
    /// A logical type says the bytes are still the base type's (the spec is explicit: "serialization is always that
    /// of the base type"), so this class converts a value to and from that base representation and lets
    /// <see cref="AvroValueMap"/> write it — the encoding itself is never restated here.
    ///
    /// The three the spec gives no C# mapping for are REFUSED BY NAME rather than silently handled as their base
    /// type: a `big-decimal` read as plain bytes would hand a script a byte array where it asked for a number, and
    /// the script would have no way to know its value had been left in a form it cannot read.</summary>
    internal static class AvroLogicalTypes
    {
        /// <summary>The three-field record a `duration` decodes to, in the spec's order.</summary>
        private static readonly string[] DurationFields = ["months", "days", "milliseconds"];

        /// <summary>The instant every timestamp is counted from, which is also a `date`'s day 0.</summary>
        private static readonly DateTime EpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The same instant as a WALL CLOCK, which is what `local-timestamp-*` counts: no zone is applied
        /// on the way in or out, so the same number means the same local reading everywhere.</summary>
        private static readonly DateTime EpochLocal = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        private const long MillisecondsPerDay = 86_400_000;
        private const long MicrosecondsPerDay = 86_400_000_000;

        // ── script value → the base type's representation ──

        /// <summary>Writes a value whose schema carries a logical type, encoding the BASE type it stands for.
        /// <paramref name="caseSensitive"/> is the interpreter's case flag, which `duration` passes to the
        /// three-member lookup so its fields follow the same rule every other field name in this engine does.</summary>
        internal static void Write(AvroBinaryWriter writer, AvroSchema schema, object? value, string member,
            bool caseSensitive)
        {
            var logical = schema.LogicalType!;
            switch (logical)
            {
                case "decimal":
                {
                    var unscaled = ToUnscaled(value, schema, member);
                    var bytes = unscaled.ToByteArray(isUnsigned: false, isBigEndian: true);
                    if (schema.Kind == AvroKind.Fixed)
                    {
                        // A fixed decimal is EXACTLY the declared width, sign-extended: the spec's layout is a
                        // two's-complement integer in `size` bytes, so a shorter value is padded on the LEFT.
                        if (bytes.Length > schema.FixedSize)
                            throw new ScriptException(
                                $"{member}: decimal {Describe(value)} needs {bytes.Length} bytes, but the fixed " +
                                $"declares {schema.FixedSize}. Precision {schema.Precision} is the most this " +
                                "schema can hold, so the value is out of its declared range.");
                        writer.WriteFixed(PadLeft(bytes, schema.FixedSize, (byte)(unscaled.Sign < 0 ? 0xFF : 0x00)));
                        break;
                    }
                    writer.WriteBytes(bytes);
                    break;
                }

                case "uuid":
                {
                    var guid = ToGuid(value, member);
                    if (schema.Kind == AvroKind.Fixed)
                    {
                        // 1.12 allows uuid over fixed(16). The layout is RFC 4122 (the same order as the canonical
                        // string), which is BIG-endian for the first three fields — not Guid.ToByteArray's mixed
                        // order, so the writing is asked for explicitly.
                        Span<byte> sixteen = stackalloc byte[16];
                        guid.TryWriteBytes(sixteen, bigEndian: true, out _);
                        writer.WriteFixed(sixteen);
                        break;
                    }
                    // A uuid over `string` is the canonical 8-4-4-4-12 form, lowercase, which is what "D" gives.
                    writer.WriteString(guid.ToString("D"));
                    break;
                }

                case "date":
                {
                    // A date is a 32-bit day count and a DateTime spans about 3.6 million days, so the count can
                    // never overflow: no range check is needed, and one would be dead code.
                    writer.WriteInt((ToDateTime(value, member, utc: true).Date - EpochUtc.Date).Days);
                    break;
                }

                case "time-millis":
                {
                    // SUB-MILLISECOND TICKS ARE TRUNCATED, and that is the TYPE's declared resolution rather than a
                    // loss this engine chose: a script TimeSpan counts 100ns ticks, while time-millis promises
                    // milliseconds, so the extra precision has nowhere to go. (A decimal is different: its SCALE is
                    // a promise about the value itself, which is why ToUnscaled refuses a value it would round.)
                    var span = ToTimeSpan(value, member);
                    var millis = span.Ticks / TimeSpan.TicksPerMillisecond;
                    if (millis < 0 || millis >= MillisecondsPerDay)
                        throw new ScriptException(
                            $"{member}: time-millis is {millis} ms, which is outside one day " +
                            $"(0..{MillisecondsPerDay - 1}). A time of day is not a duration, so it cannot be " +
                            "negative or wrap past midnight.");
                    writer.WriteInt((int)millis);
                    break;
                }

                case "time-micros":
                {
                    var span = ToTimeSpan(value, member);
                    var micros = span.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
                    if (micros < 0 || micros >= MicrosecondsPerDay)
                        throw new ScriptException(
                            $"{member}: time-micros is {micros} µs, which is outside one day " +
                            $"(0..{MicrosecondsPerDay - 1}). A time of day is not a duration, so it cannot be " +
                            "negative or wrap past midnight.");
                    writer.WriteLong(micros);
                    break;
                }

                case "timestamp-millis":
                {
                    var utc = ToDateTime(value, member, utc: true);
                    writer.WriteLong((utc - EpochUtc).Ticks / TimeSpan.TicksPerMillisecond);
                    break;
                }

                case "timestamp-micros":
                {
                    var utc = ToDateTime(value, member, utc: true);
                    writer.WriteLong((utc - EpochUtc).Ticks / (TimeSpan.TicksPerMillisecond / 1000));
                    break;
                }

                case "local-timestamp-millis":
                {
                    // NO ZONE IS APPLIED: the wall clock the value reads IS the number, which is the whole
                    // difference between this and timestamp-millis.
                    var local = ToDateTime(value, member, utc: false);
                    writer.WriteLong((local - EpochLocal).Ticks / TimeSpan.TicksPerMillisecond);
                    break;
                }

                case "local-timestamp-micros":
                {
                    var local = ToDateTime(value, member, utc: false);
                    writer.WriteLong((local - EpochLocal).Ticks / (TimeSpan.TicksPerMillisecond / 1000));
                    break;
                }

                case "duration":
                {
                    // Called unconditionally so `why` is ALWAYS assigned: a `value is null ||` short-circuit
                    // would skip the call and leave the reason unset exactly when it is needed for the refusal.
                    var ok = TryDuration(value, caseSensitive, out var months, out var days, out var millis,
                        out var why);
                    if (!ok)
                        throw new ScriptException(
                            $"{member}: duration takes a record with \"months\", \"days\" and \"milliseconds\" " +
                            $"whole numbers, not {AvroValueMap.DescribeValue(value)}. {why}");
                    Span<byte> twelve = stackalloc byte[12];
                    BinaryPrimitives.WriteInt32LittleEndian(twelve, months);
                    BinaryPrimitives.WriteInt32LittleEndian(twelve[4..], days);
                    BinaryPrimitives.WriteInt32LittleEndian(twelve[8..], millis);
                    writer.WriteFixed(twelve);
                    break;
                }

                default:
                    throw Unsupported(logical, member);
            }
        }

        // ── the base type's representation → script value ──

        /// <summary>Decodes a value whose schema carries a logical type, answering the SCRIPT type it stands for.</summary>
        internal static object? Read(ref AvroBinaryReader reader, AvroSchema schema, string member)
        {
            var logical = schema.LogicalType!;
            switch (logical)
            {
                case "decimal":
                {
                    var bytes = schema.Kind == AvroKind.Fixed
                        ? reader.ReadFixed(schema.FixedSize, $"decimal fixed '{schema.DisplayName}'")
                        : reader.ReadBytes("decimal bytes");
                    // Empty bytes would be a BigInteger sign byte with no magnitude: refused, because no decimal
                    // was ever written that way and guessing 0 would hide a payload that disagrees with its schema.
                    if (bytes.Length == 0)
                        throw new ScriptException(
                            $"{member}: a decimal carries at least one byte of two's-complement value, and this " +
                            "payload has none. The payload does not match the schema.");
                    var unscaled = new BigInteger(bytes, isUnsigned: false, isBigEndian: true);
                    return FromUnscaled(unscaled, schema, member);
                }

                case "uuid":
                {
                    if (schema.Kind == AvroKind.Fixed)
                        return new Guid(reader.ReadFixed(16, $"uuid fixed '{schema.DisplayName}'"), bigEndian: true);
                    var text = reader.ReadString("a uuid string");
                    if (!Guid.TryParse(text, out var parsed))
                        throw new ScriptException(
                            $"{member}: '{text}' is not a RFC 4122 uuid, so it cannot be read as the schema's " +
                            "uuid logical type.");
                    return parsed;
                }

                case "date":
                {
                    var days = reader.ReadInt("a date");
                    try
                    {
                        // UTC midnight, per the spec: a date carries no time and no zone, and answering a Kind
                        // that invites a local conversion would make the same day two different instants.
                        return EpochUtc.AddDays(days);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        throw new ScriptException(
                            $"{member}: the payload's date is {days} days from 1970-01-01, which is outside the " +
                            "range a DateTime holds. The payload's day count has no representable date.");
                    }
                }

                case "time-millis":
                    return TimeSpan.FromTicks(reader.ReadInt("a time-millis") * TimeSpan.TicksPerMillisecond);

                case "time-micros":
                    return TimeSpan.FromTicks(
                        reader.ReadLong("a time-micros") * (TimeSpan.TicksPerMillisecond / 1000));

                case "timestamp-millis":
                    return EpochUtc.AddTicks(
                        reader.ReadLong("a timestamp-millis") * TimeSpan.TicksPerMillisecond);

                case "timestamp-micros":
                    return EpochUtc.AddTicks(
                        reader.ReadLong("a timestamp-micros") * (TimeSpan.TicksPerMillisecond / 1000));

                case "local-timestamp-millis":
                    return EpochLocal.AddTicks(
                        reader.ReadLong("a local-timestamp-millis") * TimeSpan.TicksPerMillisecond);

                case "local-timestamp-micros":
                    return EpochLocal.AddTicks(
                        reader.ReadLong("a local-timestamp-micros") * (TimeSpan.TicksPerMillisecond / 1000));

                case "duration":
                {
                    var twelve = reader.ReadFixed(12, $"duration fixed '{schema.DisplayName}'");
                    var record = new ScriptBoxedDictionary(
                        new TypeInfo(TokenType.String, false, 0, 0, null),
                        new TypeInfo(TokenType.Object, true, 0, 0, null), 3);
                    record.AddRaw(DurationFields[0], (long)BinaryPrimitives.ReadUInt32LittleEndian(twelve));
                    record.AddRaw(DurationFields[1], (long)BinaryPrimitives.ReadUInt32LittleEndian(twelve.AsSpan(4)));
                    record.AddRaw(DurationFields[2], (long)BinaryPrimitives.ReadUInt32LittleEndian(twelve.AsSpan(8)));
                    return record;
                }

                default:
                    throw Unsupported(logical, member);
            }
        }

        /// <summary>Whether a value is one this logical type could write, which is how a union picks its branch: a
        /// `["null","int"]`-shaped date union must not claim a Guid merely because the base type is an int.</summary>
        internal static bool Fits(AvroSchema schema, object? value, bool caseSensitive)
        {
            switch (schema.LogicalType)
            {
                case "decimal":
                    return value is decimal or int or long or double or float;
                case "uuid":
                    return schema.Kind == AvroKind.Fixed
                        ? value is Guid || value is byte[] { Length: 16 }
                        : value is Guid or string;
                case "date":
                case "timestamp-millis":
                case "timestamp-micros":
                case "local-timestamp-millis":
                case "local-timestamp-micros":
                    return value is DateTime;
                case "time-millis":
                case "time-micros":
                    return value is TimeSpan;
                case "duration":
                    return TryDuration(value, caseSensitive, out _, out _, out _, out _);
                default:
                    return false;   // an unsupported logical type fits nothing, so it is refused when written
            }
        }

        // ── conversions, each with the refusal its range needs ──

        /// <summary>A script number as the unscaled integer a decimal stores: the value times 10^scale. A value with
        /// MORE fractional digits than the declared scale is refused rather than rounded, because rounding would
        /// write a number the caller never asked for, and the reader could not tell.</summary>
        private static BigInteger ToUnscaled(object? value, AvroSchema schema, string member)
        {
            var number = value switch
            {
                decimal d => d,
                int i => i,
                long l => l,
                // A SCRIPT NUMBER IS A DOUBLE, and every direct (decimal) conversion of one expands its binary
                // noise: measured, `Convert.ToDecimal(12.34)` and `(decimal)12.34` BOTH give
                // 12.339999999999999857891452848, which would be "more fractional digits than the scale" for
                // every ordinary literal. The shortest round-trip STRING is the digits the caller actually wrote,
                // so that is what the decimal is parsed from.
                float f => AsDecimal(f, member),
                double d => AsDecimal(d, member),
                _ => throw new ScriptException(
                    $"{member}: decimal takes a number (a script decimal is exact), not " +
                    $"{AvroValueMap.DescribeValue(value)}.")
            };

            var scaled = number;
            try
            {
                scaled = number * PowerOfTen(schema.Scale, member);
            }
            catch (OverflowException)
            {
                throw new ScriptException(
                    $"{member}: {number} at scale {schema.Scale} overflows a script decimal. Round the value, or " +
                    "lower the schema's scale.");
            }
            if (decimal.Truncate(scaled) != scaled)
                throw new ScriptException(
                    $"{member}: {number} has more fractional digits than the schema's scale {schema.Scale}, so " +
                    "writing it would round the value. Raise the scale, or round it before encoding.");
            var unscaled = new BigInteger(scaled);
            var digits = BigInteger.Abs(unscaled).ToString().Length;
            if (unscaled != BigInteger.Zero && digits > schema.Precision)
                throw new ScriptException(
                    $"{member}: {number} needs {digits} digits, but the schema's precision is {schema.Precision}. " +
                    "A larger precision (or a wider fixed) is what holds it.");
            return unscaled;
        }

        /// <summary>A floating-point script number as a decimal, through its shortest round-trip text so the value
        /// is the digits the caller wrote rather than the binary expansion underneath them. NaN and the infinities
        /// have no decimal, and a magnitude past decimal's range has none either, so both are refused by name.</summary>
        private static decimal AsDecimal(double value, string member)
        {
            try
            {
                return decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new ScriptException(
                    $"{member}: {value.ToString(CultureInfo.InvariantCulture)} has no decimal value. A decimal " +
                    "logical type holds a finite number within the decimal range, so NaN, an infinity and an " +
                    "out-of-range magnitude cannot be written.");
            }
        }

        /// <summary>The unscaled integer back as a script decimal.</summary>
        private static decimal FromUnscaled(BigInteger unscaled, AvroSchema schema, string member)
        {
            try
            {
                return (decimal)unscaled / PowerOfTen(schema.Scale, member);
            }
            catch (OverflowException)
            {
                throw new ScriptException(
                    $"{member}: the payload's decimal is {unscaled}, which is outside the range a script decimal " +
                    "holds. The schema's precision admits values this engine cannot represent.");
            }
        }

        /// <summary>10^scale as a decimal. A decimal's own scale stops at 28, so a schema declaring more is refused
        /// rather than multiplied past the type's range: an OverflowException here would name no schema at all.</summary>
        private static decimal PowerOfTen(int scale, string member)
        {
            if (scale > 28)
                throw new ScriptException(
                    $"{member}: the schema's scale is {scale}, but a script decimal carries at most 28 digits after " +
                    "the point. A scale that large cannot be represented, so no value could round-trip through it.");
            var power = 1m;
            for (var i = 0; i < scale; i++) power *= 10m;
            return power;
        }

        private static Guid ToGuid(object? value, string member) => value switch
        {
            Guid guid => guid,
            // A STRING IS THE EXPECTED FORM, so a string that does not parse is reported as a bad uuid rather
            // than as "not a string": the value's KIND was right and its CONTENT was wrong, and a refusal that
            // says the wrong one sends the caller looking at the wrong place.
            string text when Guid.TryParse(text, out var parsed) => parsed,
            string text => throw new ScriptException(
                $"{member}: '{text}' is not a RFC 4122 uuid. The accepted form is 8-4-4-4-12, as in " +
                "\"f81d4fae-7dec-11d0-a765-00a0c91e6bf6\"."),
            byte[] { Length: 16 } bytes => new Guid(bytes, bigEndian: true),
            byte[] bad => throw new ScriptException(
                $"{member}: a uuid as bytes is exactly 16 bytes, and the value is {bad.Length}."),
            _ => throw new ScriptException(
                $"{member}: uuid takes a Guid (or a string in the 8-4-4-4-12 form), not " +
                $"{AvroValueMap.DescribeValue(value)}.")
        };

        /// <summary>The value as a DateTime. For an INSTANT (`utc: true`) a local time is converted to UTC and an
        /// unspecified one is READ AS UTC, which is the only reading an instant can have; a `local-timestamp-*`
        /// keeps the wall clock untouched, whatever a DateTime's Kind says.</summary>
        private static DateTime ToDateTime(object? value, string member, bool utc)
        {
            var moment = value switch
            {
                DateTime dt => dt,
                DateTimeOffset dto => dto.UtcDateTime,
                _ => throw new ScriptException(
                    $"{member}: this logical type takes a DateTime, not {AvroValueMap.DescribeValue(value)}.")
            };
            if (!utc) return moment;
            return moment.Kind switch
            {
                DateTimeKind.Utc => moment,
                DateTimeKind.Local => moment.ToUniversalTime(),
                _ => DateTime.SpecifyKind(moment, DateTimeKind.Utc)
            };
        }

        private static TimeSpan ToTimeSpan(object? value, string member) => value switch
        {
            TimeSpan span => span,
            _ => throw new ScriptException(
                $"{member}: this logical type takes a TimeSpan, not {AvroValueMap.DescribeValue(value)}.")
        };

        /// <summary>The three whole numbers a duration is made of, from the record shape a script writes: a
        /// dictionary, a class or a struct instance.</summary>
        private static bool TryDuration(object? value, bool caseSensitive, out int months, out int days,
            out int millis, out string why)
        {
            months = days = millis = 0;
            why = "";
            if (value is null
                || !AvroValueMap.TryReadMember(value, DurationFields[0], caseSensitive, out var m)
                || !AvroValueMap.TryReadMember(value, DurationFields[1], caseSensitive, out var d)
                || !AvroValueMap.TryReadMember(value, DurationFields[2], caseSensitive, out var ms))
            {
                why = "All three are required.";
                return false;
            }
            if (!TryWholeNumber(m, out months) || !TryWholeNumber(d, out days) || !TryWholeNumber(ms, out millis))
            {
                why = "Each of the three is a whole number of its own unit.";
                return false;
            }
            if (months < 0 || days < 0 || millis < 0)
            {
                why = "A duration is three UNSIGNED 32-bit counts, so none of them may be negative.";
                return false;
            }
            return true;
        }

        private static bool TryWholeNumber(object? value, out int number)
        {
            switch (value)
            {
                case int i: number = i; return true;
                case long l when l is >= int.MinValue and <= int.MaxValue: number = (int)l; return true;
                default: number = 0; return false;
            }
        }

        private static byte[] PadLeft(byte[] bytes, int size, byte fill)
        {
            if (bytes.Length == size) return bytes;
            var padded = new byte[size];
            bytes.CopyTo(padded, size - bytes.Length);
            for (var i = 0; i < size - bytes.Length; i++) padded[i] = fill;
            return padded;
        }

        private static string Describe(object? value) => value switch
        {
            decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => AvroValueMap.DescribeValue(value)
        };

        /// <summary>The refusal for a logical type the spec gives no C# mapping for, or one this engine has not
        /// implemented. It names the type, says the base type is still readable, and says what to do instead.</summary>
        private static ScriptException Unsupported(string logical, string member)
            => new($"{member}: the logical type '{logical}' is not supported by this engine. " +
                 "big-decimal, timestamp-nanos and local-timestamp-nanos have no C# mapping (the spec says only " +
                 "Java and Rust implement big-decimal, and a nanosecond count does not fit DateTime's tick), so " +
                 "they are refused by name rather than read as their base type, which would hand back a " +
                 "different value than the schema promises. Use decimal, timestamp-micros or local-timestamp-micros.");
    }
}
