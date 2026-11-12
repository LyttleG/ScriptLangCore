namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public readonly struct MessageInfo : IDisposable
    {
        public long Hwnd { get; }
        public uint Message { get; }
        public long WParam { get; }
        public long LParam { get; }
        /// <summary>When the message was posted. It is what makes a per-message guard EXACT: a second, genuine
        /// click always carries a different time, so a repeat of the same numbers is the SAME delivery and nothing else.</summary>
        public uint Time { get; }

        // The packed halves a message carries: WM_COMMAND's id and its notification, a mouse position's x and y, a
        // control's own packed wParam. These read a VALUE the caller hands over (its own WParam/LParam normally),
        // so the masking exists in one place a script can name instead of writing it by hand.
        /// <summary>The low 16 bits, which is the id in a WM_COMMAND.</summary>
        public long LowWord(long value) => (int)value & 0xFFFF;
        /// <summary>The high 16 bits, which is the notification code in a WM_COMMAND.</summary>
        public long HighWord(long value) => (int)value >> 16 & 0xFFFF;
        /// <summary>The low 8 bits.</summary>
        public long LowByte(long value) => (int)value & 0xFF;
        /// <summary>The second byte.</summary>
        public long HighByte(long value) => (int)value >> 8 & 0xFF;
        /// <summary>Pack two 16-bit halves into the value a message wants.</summary>
        public long MakeLong(int low, int high) => (long)(low | (high << 16));
        /// <summary>Pack two 8-bit halves into the value a message wants.</summary>
        public long MakeWord(int low, int high) => (long)(low | (high << 8));

        public void Dispose() { } // pure data, IDisposable for uniform script using/is
        public override string ToString() => ScriptNameOf(typeof(MessageInfo)) ?? "MessageInfo";

        internal MessageInfo(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint time = 0)
        {
            Hwnd = hwnd.ToInt64();
            Message = msg;
            WParam = wParam.ToInt64();
            LParam = lParam.ToInt64();
            Time = time;
        }
    }
}

