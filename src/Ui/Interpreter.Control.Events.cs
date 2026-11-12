namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public abstract partial class Control
    {
        public ScriptFunction? Click { get; set; }
        public ScriptFunction? DoubleClick { get; set; }

        /// <summary>THIS CONTROL'S CLASS OWNS ITS MOUSE MESSAGES, so the engine stays out of them. The engine subclasses
        /// every control and does two things of its own on the mouse: it takes the capture on a left-down, and it
        /// forwards the mouse messages to Control.WndProc for the script's handlers. Some classes want the message
        /// first, and the engine already knows a list of them (its own comment records the measurement: that capture
        /// broke a ListBox&#39;s selection on every click). This is that list made settable from a script, for a class
        /// the list does not know. WHILE IT IS SET: no capture and no forward, so the class sees the messages
        /// untouched and no script handler fires for them.</summary>
        public bool ClassHandlesMouse { get; set; }

        /// <summary>An ENGINE-side click callback, for a window the engine builds and must answer itself, which is
        /// the prompt and its buttons. A script sets Click; this is the same notification arriving from the routes a REAL
        /// click takes, which a script cannot use because a button SENDS WM_COMMAND to its parent instead of queueing it.</summary>
        internal Action? EngineClick { get; set; }
        public ScriptFunction? MouseDown { get; set; }
        public ScriptFunction? MouseUp { get; set; }
        public ScriptFunction? MouseMove { get; set; }
        public ScriptFunction? KeyDown { get; set; }
        public ScriptFunction? KeyUp { get; set; }
        public ScriptFunction? GotFocus { get; set; }
        public ScriptFunction? LostFocus { get; set; }
        public ScriptFunction? Paint { get; set; }
        public ScriptFunction? Resize { get; set; }

        /// <summary>ACCEPT A DROPPED FILE, on any control and not only on a form. Windows sends WM_DROPFILES to the
        /// window the mouse is over and it does NOT walk up to the parent, so a control that covers its host (a RichEdit
        /// filling a window is the shape this exists for) needs its own target: a form-level one alone means a drop on
        /// that control reaches nobody. The engine reads AND releases the list, so a handler only sees
        /// FileCount()/FileName(i) and never the raw handle.</summary>
        public ScriptFunction? DropFiles { get; set; }

        // DropFiles is Control's now, so a child and a form answer ONE event (and both take a drop).

        /// <summary>Whether this control ACCEPTS a dropped file, and the switch that turns it on: a window that never
        /// accepted a drop is sent none, so a `DropFiles` handler needs this to exist at all. shell32's DragAcceptFiles
        /// sets or clears <c>WS_EX_ACCEPTFILES</c> on the WINDOW and the getter reads that bit back, so the two cannot
        /// disagree about whether this control takes drops.</summary>
        public bool AcceptDroppedFiles
        {
            get => handle != IntPtr.Zero && (GetWindowLongPtr(GWL_EXSTYLE) & WS_EX_ACCEPTFILES) != 0;
            set { if (handle != IntPtr.Zero) DragAcceptFiles(handle, value); }
        }

        /// <summary>One drop, handled the same way for a control and for a form: the handler reads what it needs and the
        /// ENGINE releases the list (DragFinish), because a handler that forgets is a leak Windows allocated.</summary>
        internal void OnDropFiles(IntPtr hDrop)
        {
            InvokeSafely(this, nameof(DropFiles), DropFiles, this, new DropFilesEventArgs(hDrop));
            DragFinish(hDrop);
        }

        /// <summary>
        /// WHETHER THIS INSTANCE ALREADY HAS ITS NATIVE WINDOW, which is the question `Handle != 0` answers and a
        /// script had to spell out. A FORM reports true from `Application.CreateForm` on, because that call creates the
        /// window and leaves it hidden, and `Visible` is the question "is it on screen"; a control reports true once it
        /// has been constructed. It tells "handed over" apart from "on screen" in one word.
        ///
        /// WHAT IT IS NOT: it does not say whether a CALL created the form or reused one. A reused form is a real
        /// form, so it reports `Created True` exactly like a fresh one; ask
        /// `Application.FindForm(title)` for that question, which answers the form or null.
        /// </summary>
        public bool Created => handle != IntPtr.Zero;

        /// <summary>Whether this control holds the keyboard FOCUS: the question instead of a WinApi call, answered
        /// about the control rather than about a window handle.</summary>
        public bool Focused => handle != IntPtr.Zero && GetFocus() == handle;

        /// <summary>Which event raised the handler that is running NOW, as the engine's own name for it ("Click",
        /// "MouseDown", "Command"). Set by the raise funnel just before the handler is invoked, so a handler reads
        /// it from its sender: a script that handles several events with one function can tell them apart without
        /// the event's name being baked into the function. The ARGUMENTS carry the same name (IEventArguments.Event),
        /// so the two cannot disagree; this copy exists because the sender is what a handler always has.
        /// A nested raise overwrites it, so it is the LAST one, not the innermost.</summary>
        public string LastEvent { get; set; } = "";
        protected virtual void OnClick(EventArgs e)
        {
            // An owner-drawn CheckBox or RadioButton toggles ITSELF: Windows does not change the check state of a
            // BS_OWNERDRAW button (measured: with the type set, the box was no longer activable), and the painter
            // only READS that state. Doing it here covers every route that raises a click, because the parent's
            // BN_CLICKED arm, the dispatcher's left-release and a script's RaiseClick all end in this method.
            if (this is CheckBox or RadioButton && (GetWindowLongPtrW(handle, GWL_STYLE) & 0x0000000F) == BS_OWNERDRAW)
            {
                if (this is CheckBox checkBox) checkBox.Checked = !checkBox.Checked;
            }
            EngineClick?.Invoke();   // the engine&#39;s own answer, if it gave one: this is where every click route arrives
            InvokeSafely(this, nameof(Click), Click, this, e);
        }
        protected virtual void OnDoubleClick(EventArgs e) => InvokeSafely(this, nameof(DoubleClick), DoubleClick, this, e);
        protected virtual void OnMouseDown(MouseEventArgs e) => InvokeSafely(this, nameof(MouseDown), MouseDown, this, e);
        protected virtual void OnMouseUp(MouseEventArgs e) => InvokeSafely(this, nameof(MouseUp), MouseUp, this, e);
        protected virtual void OnMouseMove(MouseEventArgs e) => InvokeSafely(this, nameof(MouseMove), MouseMove, this, e);
        protected virtual void OnKeyDown(KeyEventArgs e) => InvokeSafely(this, nameof(KeyDown), KeyDown, this, e);
        protected virtual void OnKeyUp(KeyEventArgs e) => InvokeSafely(this, nameof(KeyUp), KeyUp, this, e);
        protected virtual void OnGotFocus(EventArgs e) => InvokeSafely(this, nameof(GotFocus), GotFocus, this, e);
        protected virtual void OnLostFocus(EventArgs e) => InvokeSafely(this, nameof(LostFocus), LostFocus, this, e);
        protected virtual void OnResize(EventArgs e) => InvokeSafely(this, nameof(Resize), Resize, this, e);

        /// <summary>Records which event is being raised, in BOTH places a handler can look: the sender (LastEvent)
        /// and the arguments (IEventArguments.Event). One implementation, called by every funnel, so the two can
        /// never disagree. The arguments arrive as object[], so a struct argument is already BOXED here and the
        /// stamp survives: the box is per call, and a shared value like EventArgs.Empty is never modified.</summary>
        internal static void StampEventName(object? owner, string? eventName, object?[] args)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            if (owner is Control c) c.LastEvent = eventName!;
            foreach (var a in args)
                if (a is IEventArguments stamped) stamped.Event = eventName!;
        }

        protected static void InvokeSafely(Control control, string? eventName, ScriptFunction? func, params object[] args)
        {
            StampEventName(control, eventName, args);
            if (func == null) return;
            var interpreter = control._interpreter;
            if (interpreter == null) return;
            try
            {
                interpreter.InvokeScriptFunction(func, args, 0, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Event error: {ex.Message}");
            }
        }

        protected static object? InvokeSafelyWithResult(Control control, string? eventName, ScriptFunction? func, params object[] args)
        {
            StampEventName(control, eventName, args);
            if (func == null) return null;
            StampEventName(control, eventName, args);
            var interpreter = control._interpreter;
            if (interpreter == null) return null;
            try
            {
                return interpreter.InvokeScriptFunction(func, args, 0, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] Event error: {ex.Message}");
                return null;
            }
        }
    }
}
