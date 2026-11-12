namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class TheTimer : IDisposable
    {
        private Timer? _timer;
        private int _interval = 100;
        private bool _enabled;
        private bool _disposed;
        private string? _lastTickError;    // the last failure a tick reported, so an unchanged one is not repeated

        public ScriptFunction? Tick { get; set; }

        public int Interval
        {
            get => _interval;
            set
            {
                _interval = value;
                if (!_enabled) return;
                Stop();
                Start();
            }
        }

        public bool Enabled
        {
            get => _enabled;
            init
            {
                _enabled = value;
                if (_enabled)
                    Start();
                else
                    Stop();
            }
        }

        public void Start()
        {
            if (_enabled) return;
            EnsureWindow();
            if (_hwnd == IntPtr.Zero)
                throw new ScriptException("TheTimer: failed to create its message window.");
            _timer = new Timer(
                callback: _ =>
                {
                    // Pool thread, only post; the tick runs on the UI thread (WndProc).
                    if (_hwnd != IntPtr.Zero)
                        PostMessageW(_hwnd, WM_APP_TIMER_TICK, IntPtr.Zero, IntPtr.Zero);
                },
                state: null,
                dueTime: _interval,
                period: _interval
            );
            _enabled = true;
        }

        public void Stop()
        {
            if (!_enabled) return;

            _timer?.Dispose();
            _timer = null;
            _enabled = false;
        }

        // ── UI-thread tick marshaling ────────────────────────────────────────
        // System.Threading.Timer callbacks run on POOL threads, where the
        // ThreadStatic Interpreter.Current is null, a raw OnTick() from there
        // silently never ran (CreateTimer ticks = 0). Each timer therefore owns a
        // message-only window; the pool callback merely posts WM_APP_TIMER_TICK,
        // and the window's WndProc, invoked by the script's message pump on the
        // main thread (Application.Run / DoEvents), runs the Tick handler there,
        // exactly like a button Click event.
        private const uint WM_APP_TIMER_TICK = 0x8000 + 300;
        private static WndProcDelegate? _timerWndProc;
        private static bool _timerClassRegistered;
        private static readonly string TimerWindowClass = "ScriptLangCoreTimerWnd";
        private readonly Interpreter? _interpreter = Current;   // captured on the creating (main) thread
        private IntPtr _hwnd;
        private GCHandle _selfHandle;

        private void EnsureWindow()
        {
            if (_hwnd != IntPtr.Zero) return;
            if (!_timerClassRegistered)
            {
                _timerWndProc = TimerWndProcImpl;
                var hInst = GetModuleHandleW(null!);
                var wc = new Wndclassexw
                {
                    cbSize = (uint)Marshal.SizeOf<Wndclassexw>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_timerWndProc),
                    hInstance = hInst,
                    lpszClassName = Marshal.StringToHGlobalUni(TimerWindowClass)
                };
                if (RegisterClassExW(ref wc) == 0)
                    throw new ScriptException($"RegisterClassEx for TheTimer failed: {Marshal.GetLastWin32Error()}");
                _timerClassRegistered = true;
            }

            // Message-only window (parent = HWND_MESSAGE = -3): invisible, driven by
            // the creating thread's message pump.
            _hwnd = CreateWindowExW(0, TimerWindowClass, "", 0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, GetModuleHandleW(null!), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new ScriptException($"TheTimer: CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            _selfHandle = GCHandle.Alloc(this);
            SetWindowLongPtrW(_hwnd, GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));
        }

        private static nint TimerWndProcImpl(nint hwnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WM_APP_TIMER_TICK)
            {
                var userData = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
                if (userData != IntPtr.Zero && GCHandle.FromIntPtr(userData).Target is TheTimer target)
                {
                    target.RunTick();
                    return IntPtr.Zero;
                }
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        private void RunTick()
        {
            if (Tick == null || _interpreter == null) return;
            // A TIMER OUTLIVES ITS RUN, and its run may be GONE: a runner disposes the interpreter that made the timer
            // while the timer's window still exists, and the next tick then reached a released frame stack (measured in a
            // runner's log, twice after EVERY capture: "[Timer] Error: ObjectDisposed_Generic ...
            // ThreadLocal`1[Stack`1[CallFrame]]"). A tick has nothing to do once the interpreter is disposed, and a
            // disposal that happens WHILE a tick is in flight is the same answer.
            if (_interpreter.IsDisposed) return;
            try
            {
                _interpreter.InvokeScriptFunction(Tick, [this, EventArgs.Empty], 0, 0);
                _lastTickError = null;                     // a tick that worked forgets the last failure
            }
            catch (ScriptException ex)
            {
                // Void-return noise is expected for `fun { }` tick bodies, ignore.
                if (ex.Message.Contains("cannot return a value")) return;
                // AND ANY OTHER SCRIPT ERROR IS REPORTED, NEVER RETHROWN: this runs inside a WINDOW PROCEDURE, which is a
                // native callback, so a throw here is not a script's refusal but the process's death. Measured from a live
                // session: "Unhandled exception. ScriptLangCore.ScriptException: Property 'X' not found on type long[]",
                // raised through this very method, killed the tool. A timer keeps its own schedule, so the run carries on
                // and the author reads the failure.
                ReportTickFailure(ex.Message, ex.Line, ex.Column);
            }
            catch (ObjectDisposedException)
            {
                // the run ended under this tick: nothing left to report
            }
            catch (InvalidOperationException)
            {
                // the same shape one step further in (a released wait handle): also the run's end, not a script's fault
            }
            catch (Exception ex)
            {
                // THE ENGINE'S ONE MAP, never `ex.Message`: a raw CLR message is a resource key plus localized text, and
                // that is exactly what this line used to print.
                ReportTickFailure(FriendlyExceptionMessage(ex));
            }
        }

        /// <summary>Report a tick that failed, through the ENGINE's log, and keep the schedule. The SAME failure is said
        /// once: a timer fires on its interval, so an unchanged message would print one line per tick and bury the run.</summary>
        private void ReportTickFailure(string message, int line = 0, int column = 0)
        {
            // The position is part of the identity: the same wording from another line is another failure.
            var key = line > 0 ? $"{line}:{column}:{message}" : message;
            if (key == _lastTickError) return;
            _lastTickError = key;
            if (line > 0)
                Logging.Log.Error("Timer", message, line, column);
            else
                Logging.Log.Error("Timer", message);
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            if (_hwnd != IntPtr.Zero)
            {
                // THE WINDOW'S USERDATA GOES BEFORE THE HANDLE IT POINTS AT IS FREED. Freeing first leaves the window
                // holding a pointer to a released GC handle, and the next message dereferences it: measured as
                // `InvalidOperation_HandleIsNotInitialized` at Control.StaticWndProc, which is what killed a runner
                // after its third capture.
                SetWindowLongPtrW(_hwnd, GWLP_USERDATA, IntPtr.Zero);
                DestroyWindow(_hwnd);
                if (_selfHandle.IsAllocated) _selfHandle.Free();
                _hwnd = IntPtr.Zero;
            }
            _disposed = true;
        }
    }

    private static IntPtr CreateDpiFont(int dpi)
    {
        // The engine's default font, in DESIGN PIXELS at the design DPI. Both numbers are named on the class now,
        // because a RichEdit's default size has to be expressed in POINTS and the only honest source for it is this
        // one: DefaultFontPixels * PointsPerInch / DesignDpi is 8.25 pt, which is what this font draws.
        var scaledHeight = (int)(-DefaultFontPixels * (dpi / (float)DesignDpi));

        return CreateFont(
            scaledHeight,               // nHeight
            0,                          // nWidth (0 = auto)
            0,                          // nEscapement
            0,                          // nOrientation
            400,                        // fnWeight (FW_NORMAL)
            0,                          // fdwItalic
            0,                          // fdwUnderline
            0,                          // fdwStrikeOut
            1,                          // fdwCharSet (DEFAULT_CHARSET)
            0,                          // fdwOutputPrecision (OUT_DEFAULT_PRECIS)
            0,                          // fdwClipPrecision (CLIP_DEFAULT_PRECIS)
            0,                          // fdwQuality (DEFAULT_QUALITY)
            0,                          // fdwPitchAndFamily (DEFAULT_PITCH | FF_DONTCARE)
            "Segoe UI"                  // pszFaceName
        );
    }
}

