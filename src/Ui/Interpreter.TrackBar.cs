namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class TrackBar : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value = 50;

        public ScriptFunction? Scroll { get; set; }
        public ScriptFunction? ValueChanged { get; set; }

        public TrackBar(int x, int y, int width, int height)
        {
            InitBounds(x, y, width, height);
        }

        public int Minimum
        {
            get => _minimum;
            set
            {
                _minimum = value;
                if (handle != IntPtr.Zero && _maximum >= _minimum)
                    ApplyNativeRange();
            }
        }

        /// <summary>
        /// Applies [Minimum, Maximum] to the native control. EMPIRICAL on this OS:
        /// the 0x408 message stores its lParam as the MAXIMUM value directly (plain,
        /// not the documented packed MAKELPARAM(min,max)), the packed form made the
        /// native range [0, max &lt;&lt; 16], so every channel click scaled against a
        /// ~13.9M range and landed far past the intended position. Minimum stays 0
        /// (all script usages use Minimum = 0).
        /// </summary>
        private void ApplyNativeRange()
        {
            if (handle == IntPtr.Zero) return;
            SendMessageW(handle, TBM_SETRANGE, 1, _maximum);
            // keep the thumb inside the freshly applied range
            if (_value > _maximum) _value = _maximum;
            if (_value < _minimum) _value = _minimum;
            SendMessageW(handle, TBM_SETPOS, 1, _value);
        }

        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = value;
                if (handle != IntPtr.Zero && _maximum >= _minimum)
                    ApplyNativeRange();
            }
        }

        public int Value
        {
            get => _value;
            set
            {
                if (value < _minimum) value = _minimum;
                if (value > _maximum) value = _maximum;
                _value = value;
                if (handle != IntPtr.Zero)
                    SendMessageW(handle, TBM_SETPOS, 1, _value);
            }
        }

        protected override void CreateHandle()
        {
            CreateNativeControl("msctls_trackbar32", null,
                WS_CHILD | WS_VISIBLE | TBS_HORZ,
                0, ParentHandle, IntPtr.Zero);

            ApplyNativeRange();
        }

        protected override IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HSCROLL)
            {
                var pos = (int)SendMessageW(handle, TBM_GETPOS, 0, IntPtr.Zero);
                // Clamp: a native read can race with a range change and return an
                // out-of-range value that would otherwise reach scripts as a seek.
                if (pos < _minimum) pos = _minimum;
                if (pos > _maximum) pos = _maximum;
                _value = pos;
                InvokeSafely(this, nameof(Scroll), Scroll, this, EventArgs.Empty);
                // The VALUE changed as well as the position, and a script asks for the value. A TBM_SETPOS
                // from a script does NOT notify, so this fires on real input and on a synthetic WM_HSCROLL.
                InvokeSafely(this, nameof(ValueChanged), ValueChanged, this, EventArgs.Empty);
            }
            return base.WndProc(hwnd, msg, wParam, lParam);
        }
    }
}

