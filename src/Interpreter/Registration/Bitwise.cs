namespace ScriptLangCore;

internal partial class Interpreter
{
    private void RegisterBitwiseFunctions()
    {
        // ============================================================================
        // BITWISE HELPER FUNCTIONS
        // ============================================================================

        // ToBinary(value) - converts a number to binary string
        RegisterNative("ToBinary", TokenType.StringType, args =>
        {
            if (args.Length == 0)
                throw new ScriptException("ToBinary expects at least one argument.");

            var value = Convert.ToInt64(args[0]);

            // Determine the number of bits needed
            int bits;
            if (args.Length >= 2)
            {
                bits = Convert.ToInt32(args[1]);
            }
            else
            {
                // Auto-determine bits (multiples of 4 for nice formatting)
                bits = 8;
                if (value > 0xFF) bits = 16;
                if (value > 0xFFFF) bits = 32;
                if (value > 0xFFFFFFFF) bits = 64;
            }

            return ToBinaryString(value, bits);
        });

        // ToBinary(value, bits) - with specified a bit of width
        RegisterNative("ToBinaryWithBits", TokenType.StringType, args =>
        {
            if (args.Length < 2)
                throw new ScriptException("ToBinaryWithBits expects value and bits arguments.");

            var value = Convert.ToInt64(args[0]);
            var bits = Convert.ToInt32(args[1]);

            return ToBinaryString(value, bits);
        });

        // ToHex(value) - converts a number to hexadecimal string
        RegisterNative("ToHex", TokenType.StringType, args =>
        {
            if (args.Length == 0)
                throw new ScriptException("ToHex expects at least one argument.");

            var value = Convert.ToInt64(args[0]);

            if (args.Length >= 2)
            {
                var digits = Convert.ToInt32(args[1]);
                return value.ToString("X" + digits);
            }

            return value.ToString("X");
        });

        // ToHexWithPrefix(value) - returns "0x" + hex string
        RegisterNative("ToHexWithPrefix", TokenType.StringType, args =>
        {
            if (args.Length == 0)
                throw new ScriptException("ToHexWithPrefix expects at least one argument.");

            var value = Convert.ToInt64(args[0]);

            if (args.Length < 2)
                return "0x" + value.ToString("X");

            var digits = Convert.ToInt32(args[1]);
            return "0x" + value.ToString("X" + digits);

        });

        // BitCount(value) - returns number of set bits (population count)
        RegisterNative("BitCount", TokenType.Int, args =>
        {
            if (args.Length == 0)
                throw new ScriptException("BitCount expects an argument.");

            var value = Convert.ToInt64(args[0]);
            var count = 0;

            while (value != 0)
            {
                count++;
                value = value & (value - 1); // Clear the lowest set bit
            }

            return count;
        });

        // IsPowerOfTwo(value) - checks if value is a power of 2
        RegisterNative("IsPowerOfTwo", TokenType.Bool, args =>
        {
            if (args.Length == 0)
                throw new ScriptException("IsPowerOfTwo expects an argument.");

            var value = Convert.ToInt64(args[0]);
            return value != 0 && (value & (value - 1)) == 0;
        });

        // BitSet(value, bit) - sets a specific bit
        RegisterNative("BitSet", TokenType.Long, args =>
        {
            if (args.Length < 2)
                throw new ScriptException("BitSet expects value and bit arguments.");

            var value = Convert.ToInt64(args[0]);
            var bit = Convert.ToInt32(args[1]);

            return value | (1L << bit);
        });

        // BitClear(value, bit) - clears a specific bit
        RegisterNative("BitClear", TokenType.Long, args =>
        {
            if (args.Length < 2)
                throw new ScriptException("BitClear expects value and bit arguments.");

            var value = Convert.ToInt64(args[0]);
            var bit = Convert.ToInt32(args[1]);

            return value & ~(1L << bit);
        });

        // BitToggle(value, bit) - toggles a specific bit
        RegisterNative("BitToggle", TokenType.Long, args =>
        {
            if (args.Length < 2)
                throw new ScriptException("BitToggle expects value and bit arguments.");

            var value = Convert.ToInt64(args[0]);
            var bit = Convert.ToInt32(args[1]);

            return value ^ (1L << bit);
        });

        // BitTest(value, bit) - tests if a specific bit is set
        RegisterNative("BitTest", TokenType.Bool, args =>
        {
            if (args.Length < 2)
                throw new ScriptException("BitTest expects value and bit arguments.");

            var value = Convert.ToInt64(args[0]);
            var bit = Convert.ToInt32(args[1]);

            return (value & (1L << bit)) != 0;
        });

        // RotateLeft(value, shift, bits) - circular left rotation
        RegisterNative("RotateLeft", TokenType.Long, args =>
        {
            if (args.Length < 3)
                throw new ScriptException("RotateLeft expects value, shift, and bits arguments.");

            var value = Convert.ToInt64(args[0]);
            var shift = Convert.ToInt32(args[1]) % Convert.ToInt32(args[2]);
            var bits = Convert.ToInt32(args[2]);

            var mask = (1L << bits) - 1;
            return ((value << shift) | (value >> (bits - shift))) & mask;
        });

        // RotateRight(value, shift, bits) - circular right rotation
        RegisterNative("RotateRight", TokenType.Long, args =>
        {
            if (args.Length < 3)
                throw new ScriptException("RotateRight expects value, shift, and bits arguments.");

            var value = Convert.ToInt64(args[0]);
            var shift = Convert.ToInt32(args[1]) % Convert.ToInt32(args[2]);
            var bits = Convert.ToInt32(args[2]);

            var mask = (1L << bits) - 1;
            return ((value >> shift) | (value << (bits - shift))) & mask;
        });

        // SwapBytes(value) - swaps endianness of a 32-bit value
        RegisterNative("SwapBytes", TokenType.Long, args =>
        {
            if (args.Length == 0)
                throw new ScriptException("SwapBytes expects an argument.");

            var value = Convert.ToInt64(args[0]);

            return ((value & 0xFF) << 24) |
                   ((value & 0xFF00) << 8) |
                   ((value & 0xFF0000) >> 8) |
                   ((value & 0xFF000000) >> 24);
        });
    }

    // Helper method for binary string conversion
    private static string ToBinaryString(long value, int bits)
    {
        var result = new StringBuilder(bits + (bits / 4));

        for (var i = bits - 1; i >= 0; i--)
        {
            result.Append((value & (1L << i)) != 0 ? '1' : '0');

            // Add underscore every 4 bits for readability
            if (i > 0 && i % 4 == 0)
                result.Append('_');
        }

        return result.ToString();
    }
}
