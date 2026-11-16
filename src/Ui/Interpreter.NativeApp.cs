namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public static partial class NativeApp
    {
        private static readonly HashSet<IntPtr> ActiveForms = [];
        /// <summary>The forms of this process that have actually been SHOWN, which is the set a BLOCKING wait asks
        /// about (`Application.Run`, `RunFor`, the worker pump). It is separate from <see cref="ActiveForms"/> because
        /// a form's WINDOW now exists from the moment `Application.CreateForm` returns (measured reason: controls and
        /// their items must be usable before `Show()`, and a control cannot be created on a parent with no window), and
        /// `WM_CREATE` is what joins `ActiveForms`. Without this set, `var f = CreateForm(...); Application.Run();`
        /// would wait for ever on a form nobody put on screen, where it used to return at once.</summary>
        private static readonly HashSet<IntPtr> ShownForms = [];
        private static readonly Lock FormsLock = new();   // guards ActiveForms, ShownForms and the maps beside them
        private static MessageInfo? _currentMsg;
        private static readonly Lock MsgLock = new();
        // Store the last peeked raw message for dispatching later
        private static Msg _lastPeekedMsg;
        private static bool _hasPeekedMsg;

        static NativeApp()
        {
            InitCommonControls();
        }

        private static void InitCommonControls()
        {
            var icc = new INITCOMMONCONTROLSEX
            {
                dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
                dwICC = ICC_ALL_CLASSES
            };
            InitCommonControlsEx(ref icc);
        }

        // ---------- Form lifetime management ----------
        /// <summary>
        /// THE LIVE FORMS, BY TITLE, which is what makes `Application.SingleInstance` mean one window per title.
        /// A LIST per title rather than one form, because `SingleInstance` OFF deliberately allows several windows
        /// with one title: with a single slot, closing the SECOND one un-indexed the FIRST, and the ON case then
        /// found nothing to reuse (measured: the suite's checks 1e and 1f failed exactly that way).
        /// Keyed with OrdinalIgnoreCase on purpose: Windows itself matches a window title that way (`FindWindowA`,
        /// which is how a script finds a window it did not create), so a lookup here and a lookup there cannot
        /// disagree about whether two titles name the same window.
        /// </summary>
        private static readonly Dictionary<string, List<Form>> FormsByTitle = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// `Application.SingleInstance`, DEFAULT FALSE, process-wide (VB's `IsSingleInstance` is per application,
        /// and the engine's application IS the process). Two effects:
        ///   * in-process: `Application.CreateForm(title)` answers the form already carrying that title instead of
        ///     creating a second window, so a snippet pasted twice reuses its panel;
        ///   * cross-process: turning it ON takes a named mutex for the run's script, so a SECOND run of the same
        ///     script ends instead of building a second set of windows (see
        ///     `Interpreter.SetSingleInstanceFromScript`).
        /// With it off, a duplicate title still WARNS, because two windows with one title is a script mistake a
        /// reader should hear about even when the script meant it.
        /// </summary>
        public static bool SingleInstance { get; set; }

        private static IntPtr _instanceMutex;
        private const int ERROR_ALREADY_EXISTS = 183;

        /// <summary>
        /// Takes the single-instance lock for <paramref name="identity"/>, or answers false when another process
        /// already holds it. `Local\` is per sign-in session, which is the scope VB's `IsSingleInstance` has: two
        /// users may run the same script, one user may not. The name is sanitized because a mutex name may hold a
        /// path's letters but not its separators.
        /// </summary>
        public static bool TryTakeInstanceLock(string identity, out string mutexName)
        {
            mutexName = "Local\\ScriptLangCore.SingleInstance."
                + string.Concat(identity.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            if (_instanceMutex != IntPtr.Zero) return true;          // already ours
            var handle = CreateMutexW(IntPtr.Zero, true, mutexName);
            if (handle == IntPtr.Zero)
            {
                // A mutex that cannot be created must not stop a script: answer true, so the run behaves as it did
                // before the property existed rather than refusing to start.
                return true;
            }
            // `Marshal.GetLastPInvokeError()` rather than a GetLastError() P/Invoke: with `SetLastError = true` the
            // runtime captures the code for the call, and asking it again through a SECOND P/Invoke reads a value
            // the marshalling in between can clobber (measured: the second instance was not detected at all).
            if (Marshal.GetLastPInvokeError() == ERROR_ALREADY_EXISTS)
            {
                // Somebody else owns it. Close OUR handle (we never owned the object) and say so.
                _ = CloseHandle(handle);
                return false;
            }
            _instanceMutex = handle;
            return true;
        }

        /// <summary>Releases the single-instance lock, which `Application.SingleInstance = false` asks for. The
        /// process ending releases it too, because Windows closes our handle, which is why an aborted run cannot
        /// leave a stale lock behind.</summary>
        public static void ReleaseInstanceLock()
        {
            if (_instanceMutex == IntPtr.Zero) return;
            _ = ReleaseMutex(_instanceMutex);
            _ = CloseHandle(_instanceMutex);
            _instanceMutex = IntPtr.Zero;
        }

        public static void RegisterForm(IntPtr hwnd)
        {
            lock (FormsLock)
            {
                _ = ActiveForms.Add(hwnd);
                // WHICH INTERPRETER OWNS IT: a form must not outlive the run that created it, and a later run must never
                // be handed it (see CloseFormsOwnedBy and FormByTitle).
                FormOwners[hwnd] = Interpreter.Current;
                // WHICH RUN MADE IT, recorded where it is registered: Application.Run waits only on the forms of its own
                // execution unit, so closing a nested run's window ends THAT run even when the run shares this
                // interpreter and this thread (a bundle's ENTRY body, which is how `Compile first` runs a file).
                lock (FormsLock) { FormUnits[hwnd] = Interpreter.ExecutionUnit; }
                //if (_activeForms.Add(hwnd))
                //    Console.WriteLine($"[NativeApp] RegisterForm: hwnd=0x{hwnd:X}, count={_activeForms.Count}");
                //else
                //    Console.WriteLine($"[NativeApp] RegisterForm: duplicate hwnd=0x{hwnd:X} ignored, count={_activeForms.Count}");
            }
        }

        /// <summary>Records that a form's window has been put ON SCREEN, which is the fact a blocking wait asks about
        /// (see <see cref="ShownForms"/>). Called from `Form.Show()`, i.e. after the window exists and is visible.
        /// Guarded on membership of the live set so the two cannot disagree about a window that has already closed.</summary>
        public static void MarkFormShown(IntPtr hwnd)
        {
            lock (FormsLock) { if (ActiveForms.Contains(hwnd)) { _ = ShownForms.Add(hwnd); } }
        }

        public static void UnregisterForm(IntPtr hwnd)
        {
            lock (FormsLock)
            {
                _ = ActiveForms.Remove(hwnd);
                _ = ShownForms.Remove(hwnd);   // a closed window cannot be waited on
                lock (FormsLock) { FormUnits.Remove(hwnd); }
                lock (FormsLock) { FormOwners.Remove(hwnd); }
                // A CLOSED form must leave the TITLE index too, or a later CreateForm would hand back a dead one.
                // Only THAT form leaves: closing the second window of a shared title keeps the first indexed.
                foreach (var pair in FormsByTitle)
                {
                    if (pair.Value.RemoveAll(f => f.Handle == hwnd) > 0)
                    {
                        if (pair.Value.Count == 0) { _ = FormsByTitle.Remove(pair.Key); }
                        break;
                    }
                }
                // AND ANY ENTRY WHOSE WINDOW IS ALREADY GONE, because the removal above matches a HANDLE and a form
                // that has been torn down may no longer report the one it was registered with. Pruning here keeps the
                // index from accumulating forms no caller may reuse (FormByTitle also filters, as the net under this).
                foreach (var pair in FormsByTitle)
                {
                    _ = pair.Value.RemoveAll(f => (f.Handle != IntPtr.Zero && !IsWindow(f.Handle)) || f.OwnerInterpreter is null);
                    if (pair.Value.Count == 0) { _ = FormsByTitle.Remove(pair.Key); }
                }
            }
        }

        /// <summary>The FIRST live form carrying <paramref name="title"/>, or null. A closed form has already left
        /// the index through <see cref="UnregisterForm"/>, so the head of the list is still on screen. THIS IS ALSO THE
        /// NET UNDER THAT: an entry whose WINDOW no longer exists is dropped here rather than handed back, because a
        /// caller cannot tell a live form from a destroyed one and `CreateForm` would answer a handle that cannot parent
        /// a control. Measured: a runner re-ran the RAD designer in ONE process and `CreateForm` answered the PREVIOUS
        /// run's form, whose window was gone, so the next `CreateButton` failed with 1400
        /// (ERROR_INVALID_WINDOW_HANDLE) and the whole run stopped after 70 ms.</summary>
        public static Form? FormByTitle(string title)
        {
            lock (FormsLock)
            {
                if (!FormsByTitle.TryGetValue(title, out var forms) || forms.Count == 0) { return null; }
                // Handle Zero means "created, not shown yet", which IS reusable, so only a non-zero handle is tested.
                _ = forms.RemoveAll(f => (f.Handle != IntPtr.Zero && !IsWindow(f.Handle)) || f.OwnerInterpreter is null);
                if (forms.Count == 0) { _ = FormsByTitle.Remove(title); return null; }
                return forms[0];
            }
        }

        /// <summary>Records a form under its title, keeping every live form of that title in creation order.</summary>
        public static void RememberForm(string title, Form form)
        {
            lock (FormsLock)
            {
                if (!FormsByTitle.TryGetValue(title, out var forms))
                {
                    forms = [];
                    FormsByTitle[title] = forms;
                }
                forms.Add(form);
            }
        }

        public static bool IsRunning
        {
            get
            {
                lock (FormsLock)
                    return ActiveForms.Count > 0;
            }
        }

        /// <summary>Whether a form owned by THIS RUN is still up, which is the question `Application.Run` and the pumps
        /// must ask: the registry is PROCESS-WIDE (a runner's own window and a captured script's window are both in it),
        /// so `IsRunning` alone made a nested `Application.Run` wait for SOMEBODY ELSE'S form. Measured twice: the
        /// `dpi-probe` run through the clipboard tool closed its window and then kept running (a nested `RunScript`, so
        /// the THREAD told them apart), and the RAD designer with `Compile first` ticked did the same thing (its ENTRY
        /// executes on the caller's interpreter and thread, so the thread could not), which is what the UNIT adds: an
        /// execution unit is bumped by everything that starts a run in-process, a form records the one that created it,
        /// and a form with no recorded unit still counts, so an engine-created window keeps its old behaviour.</summary>
        /// <summary>The same question, as the WAIT must ask it: a blocked `Task.Wait` dispatches this thread's queued
        /// messages on every pass, so with a form of this run up it must come back periodically (there is no handle
        /// to signal a click). Without that, a wait is free to block until a script fact wakes it.</summary>
        internal static bool HasWindowOnThisThread() => IsRunningOnThisThread();

        private static bool IsRunningOnThisThread()
        {
            var threadId = KernelGetCurrentThreadId();
            var unit = Interpreter.ExecutionUnit;
            lock (FormsLock)
            {
                foreach (var hwnd in ActiveForms)
                {
                    // ONLY A SHOWN WINDOW IS WAITED ON. A form's window exists from `Application.CreateForm` on (so
                    // its controls can be built before `Show()`), and without this test a run that created a form and
                    // never showed it would leave `Application.Run` waiting for ever.
                    if (!ShownForms.Contains(hwnd)) { continue; }
                    if (GetWindowThreadProcessId(hwnd, out _) != threadId) { continue; }
                    if (!FormUnits.TryGetValue(hwnd, out var madeByUnit) || madeByUnit == unit) { return true; }
                }
            }
            return false;
        }

        /// <summary>The execution unit that created each live form (see `IsRunningOnThisThread`).</summary>
        private static readonly Dictionary<nint, int> FormUnits = new();

        /// <summary>The interpreter that created each live form, so a FINISHED run's windows can be closed with it.</summary>
        private static readonly Dictionary<nint, Interpreter?> FormOwners = new();



        /// <summary>
        /// CLOSES EVERY WINDOW AN INTERPRETER CREATED, called when that interpreter is DISPOSED. Without it a finished run
        /// leaves its forms on screen (a cancelled run never reaches its own teardown) and they stay "stuck in memory";
        /// measured from a runner clicking Repeat, where after every hand-closed window there was still one left, and the
        /// NEXT run then reused a form of the disposed interpreter (`handle 0`, then a failed WM_NCCALCSIZE). The close is
        /// POSTED so a `Closing` handler can still refuse while its run is alive, then pumped briefly, and a window that is
        /// still there is destroyed: its interpreter is gone, so no handler could ever run again.
        /// </summary>
        internal static void CloseFormsOwnedBy(Interpreter owner)
        {
            var mine = new List<nint>();
            lock (FormsLock)
            {
                foreach (var hwnd in ActiveForms)
                {
                    if (FormOwners.TryGetValue(hwnd, out var madeBy) && ReferenceEquals(madeBy, owner)) { mine.Add(hwnd); }
                }
            }
            foreach (var hwnd in mine)
            {
                try
                {
                    _ = PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    var deadline = Environment.TickCount64 + 400;
                    while (IsWindow(hwnd) && Environment.TickCount64 < deadline)
                    {
                        var pending = new Msg();
                        if (PeekMessageW(ref pending, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */)) { DispatchWithTab(ref pending); }
                        else { System.Threading.Thread.Sleep(5); }
                    }
                    if (IsWindow(hwnd)) { _ = DestroyWindow(hwnd); }
                }
                catch
                {
                    // a window that cannot be closed any more is already gone as far as this run is concerned
                }
            }
        }

        // LibraryImport BINDS TO THE MEMBER'S NAME, so a member renamed to dodge a clash needs its EntryPoint spelled
        // (measured here: without it the tool died at startup with "Unable to find an entry point named
        // 'KernelGetCurrentThreadId' in DLL 'kernel32.dll'").
        [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        private static partial uint KernelGetCurrentThreadId();

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        /// <summary>Closes EVERY form, for a run that was stopped rather than one that ended: a form whose `Closing`
        /// vetoes is closed by force after a short grace, because a kill is not a negotiation, and the loop keeps
        /// pumping while it happens so each close is a real message the form's own handlers see. Runs on the loop's
        /// thread, the only one that may destroy those windows. The first move is a posted <c>WM_CLOSE</c>, which is the
        /// same message the title bar's X sends, so the app's own `Closing`/`Closed` handlers run in order.</summary>
        /// <summary>
        /// Closes the windows of THE RUN whose cancellation ended `Application.Run`, and NOTHING else. `ActiveForms` is
        /// PROCESS-WIDE, so closing every form closed a runner's own window too and ended the runner as well (measured:
        /// pressing Kill on a captured UI app killed BOTH programs). The run's windows are the ones its own interpreter
        /// created (`FormOwners`), with the thread-and-unit filter as the fallback when there is no current interpreter.
        /// </summary>
        private static void CloseAllFormsForKill()
        {
            var owner = Interpreter.Current;
            var mine = new List<nint>();
            lock (FormsLock)
            {
                foreach (var hwnd in ActiveForms)
                {
                    if (owner is not null)
                    {
                        if (FormOwners.TryGetValue(hwnd, out var madeBy) && ReferenceEquals(madeBy, owner)) { mine.Add(hwnd); }
                    }
                    else if (FormUnits.TryGetValue(hwnd, out var unit) && unit == Interpreter.ExecutionUnit)
                    {
                        mine.Add(hwnd);
                    }
                }
            }
            if (mine.Count == 0) { return; }

            foreach (var hwnd in mine) { _ = PostMessageW(hwnd, WM_CLOSE, 0, 0); }

            // The grace: pump whatever those closes produce, up to two seconds, so a Closing handler that reads a control
            // or saves something has its turn. Then the leftovers go directly through the form's own Close (which is what
            // unregisters it) and, if a close is still refused, by Dispose.
            var deadline = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < deadline && AnyStillOpen(mine))
            {
                var pending = new Msg();
                if (PeekMessageW(ref pending, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */)) { DispatchWithTab(ref pending); }
                else { Thread.Sleep(10); }
            }

            foreach (var hwnd in mine)
            {
                if (Control.FromHandle(hwnd) is Form leftover) { try { leftover.Close(); } catch (Exception) { } }
            }
            foreach (var hwnd in mine)
            {
                if (Control.FromHandle(hwnd) is Form stubborn) { try { stubborn.Dispose(); } catch (Exception) { } }
            }
        }

        private static bool AnyStillOpen(List<nint> handles)
        {
            foreach (var hwnd in handles) { if (IsWindow(hwnd)) { return true; } }
            return false;
        }
        // ---------- Message pumping ----------
        public static MessageInfo? WaitMessage()
        {
            Msg msg = new();
            var ret = GetMessageW(ref msg, IntPtr.Zero, 0, 0);
            if (ret == 0) { TakeQuitCode(ref msg); return null; }
            if (ret == -1) { return null; }

            var info = new MessageInfo(msg.hwnd, msg.message, msg.wParam, msg.lParam, msg.time);
            DispatchWithTab(ref msg);
            return info;
        }

        [ThreadStatic]
        private static bool _isPumping;

        [ThreadStatic]
        private static bool _dispatchingFromWait;

        /// <summary>The queue drain on its own: every queued message dispatched, and nothing else. `DoEvents`
        /// adds the coroutine pump and a CPU yield to it, while the WAIT path below must not (the pump is what is
        /// calling, so pumping from here would recurse).</summary>
        internal static void DispatchQueuedMessages()
        {
            Msg msg = new();
            while (PeekMessageW(ref msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                DispatchWithTab(ref msg);
            }
        }

        /// <summary>
        /// THE SLICE POINT AN `await` GIVES THE WINDOWS THIS RUN OWNS. `Task.Wait` drives the coroutine pump in a
        /// wait loop that dispatches no messages, so a script that awaits used to freeze its own window: measured
        /// in a probe, a 1.2 s `await Task.Delay` left a form with ZERO paints and its posted click undelivered
        /// while the same wait inside `Application.DoEvents` stayed alive. C# resumes a continuation through the
        /// UI thread's message loop, so draining here is the faithful behaviour. Refused when nothing of ours has
        /// a window (a console run pays one field read) and refused when already dispatching from this path, so a
        /// handler that awaits again cannot recurse.
        /// </summary>
        internal static void PumpWindowsFromWait()
        {
            if (_dispatchingFromWait || !IsRunning) { return; }

            _dispatchingFromWait = true;
            try
            {
                DispatchQueuedMessages();
            }
            finally
            {
                _dispatchingFromWait = false;
            }
        }

        /// <summary>
        /// Processes all pending Windows messages and returns.
        /// Safe to call anywhere – if already inside a message pump, only yields the CPU.
        /// </summary>
        /// <summary>The pump a RUNNER'S run uses between its statements and inside `sleep`: it dispatches this thread's
        /// queued messages (which is what keeps a form the captured script SHOWED from being a frozen window, since that
        /// thread owns the window and nothing else pumps it) and it does nothing at all when no window of this run is up,
        /// so a headless run pays one list check per statement. It is the engine's own `DoEvents`, so the re-entrancy
        /// guard and the coroutine pump are the ones every other pump uses.</summary>
        internal static void PumpForWorkerRun()
        {
            if (!IsRunningOnThisThread()) 
                return;
            DoEvents();
        }

        public static void DoEvents()
        {
            if (_isPumping)
            {
                Sleep(1);
                return;
            }

            _isPumping = true;
            try
            {
                DispatchQueuedMessages();

                Current?.PumpCoroutines();

                Sleep(1);             // keep for now; prevents a tight busy‑loop
            }
            finally
            {
                _isPumping = false;
            }
        }

        private const uint VK_TAB = 0x09;
        private const int VK_SHIFT = 0x10;

        // A tab order has to ask the WINDOW these two, not the properties: a control inside a hidden or disabled parent
        // reports VISIBLE and ENABLED of its own while Windows refuses it the focus.
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(nint hWnd);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowEnabled(nint hWnd);

        /// <summary>Tab and Shift+Tab move the FOCUS instead of reaching the control, which is what WS_TABSTOP is set
        /// for. Win32&#39;s own answer is IsDialogMessage, which also claims Enter and Escape for the dialog&#39;s
        /// meanings, and the prompt&#39;s pump gives those two its own: this does the one job and leaves them alone.
        /// The next stop is asked where the dialog manager asks it, from the FOCUSED control&#39;s parent, and only a
        /// control the engine wraps navigates at all, so a key reaching anything else is none of this business.</summary>
        private static bool TryNavigateTab(Msg msg)
        {
            if (msg.message != WM_KEYDOWN || (uint)msg.wParam != VK_TAB) { return false; }
            if (Control.FromHandle(msg.hwnd) is not Control focused) { return false; }
            var containerHandle = GetParent(msg.hwnd);
            if (containerHandle == IntPtr.Zero) { containerHandle = msg.hwnd; }   // the focus is ON the container itself
            if (Control.FromHandle(containerHandle) is not Form parentForm) { return false; }
            // THE ORDER IS COMPUTED HERE, from TabIndex and nothing else. It is not read out of the Z-order, because
            // TabIndex is an ORDERING value that must stay independent of it: a control that is moved to the back of
            // the Z-order keeps its place in the tab order, and two controls may share a TabIndex (the sort is stable,
            // so they then follow the order they were added in, which is what WinForms does too).
            var stops = TabOrder(parentForm);
            if (stops.Count == 0) { return false; }
            var back = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
            var at = stops.IndexOf(focused);
            var next = at < 0
                ? (back ? stops[stops.Count - 1] : stops[0])                      // nothing focused yet: first, or last
                : stops[(at + (back ? -1 : 1) + stops.Count) % stops.Count];       // and the order WRAPS, as VB&#39;s does
            if (next == focused || next.Handle == IntPtr.Zero) { return false; }
            FocusWithCue(next.Handle);
            return true;
        }

        /// <summary>The controls of a form IN TAB ORDER: by TabIndex, ties following the order they were added (the
        /// sort is stable), and only those that can take the focus. ONE query, because the Tab walk and the focus a
        /// Run() gives a form must not be able to disagree about what that order is.</summary>
        private static List<Control> TabOrder(Form form)
            => form.Controls.Where(CanTakeTabFocus).OrderBy(c => c.TabIndex).ToList();
        /// <summary>Whether a control can hold the focus at all, which is what a tab order has to filter on: it is a tab
        /// stop, and it is ENABLED and VISIBLE. Those last two are asked of the WINDOW rather than of the property,
        /// because Windows means it down the whole chain: a control inside a disabled or hidden form cannot take the
        /// focus however enabled it says it is.</summary>
        private static bool CanTakeTabFocus(Control candidate)
        {
            if (candidate.Handle == IntPtr.Zero) { return false; }
            if ((ReadWindowStyle(candidate.Handle, false) & WS_TABSTOP) == 0) { return false; }
            return IsWindowVisible(candidate.Handle) && IsWindowEnabled(candidate.Handle);
        }

        /// <summary>The ONE place a message is handed to Windows, after the engine has had its say. TAB is answered here for
        /// BOTH pumps, which is the whole point of it being one method: PumpMessage peeks and Run blocks, and a Tab that
        /// worked in one of them and not the other is exactly the bug this replaced.</summary>
        private static void DispatchWithTab(ref Msg msg)
        {
            if (TryNavigateTab(msg)) { return; }
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        public static MessageInfo? PumpMessage()
        {
            Msg msg = new();
            if (PeekMessageW(ref msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT)
                    return null;

                var info = new MessageInfo(msg.hwnd, msg.message, msg.wParam, msg.lParam, msg.time);
                lock (MsgLock) { _currentMsg = info; }
                // TAB IS ANSWERED BEFORE THE DISPATCH, and that is the whole point: a key that reaches a multi-line
                // EDIT becomes a CHARACTER in it, which is what pressing Tab in a prompt&#39;s input used to be, rather
                // than a move to the next control. The message is still returned to the caller, so whoever owns the
                // window sees it exactly once.
                DispatchWithTab(ref msg);
                lock (MsgLock) { _currentMsg = null; }
                return info;
            }
            return null;
        }

        public static MessageInfo? CurrentMsg
        {
            get { lock (MsgLock) return _currentMsg; }
        }

        public static MessageInfo? PeekMessage()
        {
            Msg msg = new();
            if (PeekMessageW(ref msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                _lastPeekedMsg = msg;
                _hasPeekedMsg = true;
                return new MessageInfo(msg.hwnd, msg.message, msg.wParam, msg.lParam, msg.time);
            }
            _hasPeekedMsg = false;
            return null;
        }

        public static void DispatchMessage(MessageInfo info)
        {
            if (!_hasPeekedMsg)
                return;

            // Translate and dispatch the stored message
            TranslateMessage(ref _lastPeekedMsg);
            DispatchMessageW(ref _lastPeekedMsg);
            _hasPeekedMsg = false;
        }

        // ---------- Standard message loop (blocking) ----------
        public static void Run()
        {
            // A RUN THAT CAN BE CANCELLED HAS TO BE ABLE TO LEAVE THIS LOOP. `GetMessageW` blocks until a message arrives,
            // so a script sitting in `Application.Run` (a UI app under a runner's Stop button) could not be stopped at all:
            // measured, a kill clicked five times changed nothing and the window stayed standing, which is the report "if
            // I close the executed form it seems to stay forever and impossible to kill". With a token the loop WAITS ON
            // THE TOKEN AS WELL AS THE QUEUE (the canonical `MsgWaitForMultipleObjects` shape), so a cancel wakes it at
            // once; the forms are then closed HERE, on the thread that owns their windows, and the run reports Cancelled
            // at its next statement poll.
            var runToken = Interpreter.ActiveRunCancellation;
            var cancellable = runToken is { CanBeCanceled: true };
            var waited = new IntPtr[1];
            if (cancellable)
            {
                waited[0] = runToken!.Value.WaitHandle.SafeWaitHandle.DangerousGetHandle();
            }

            var cancelledByRun = false;
            while (IsRunningOnThisThread())
            {
                if (cancellable && runToken!.Value.IsCancellationRequested)
                {
                    cancelledByRun = true;
                    break;
                }

                if (cancellable)
                {
                    // 0 is the token, 0 + 1 means "a message is queued" (which the GetMessageW below then takes without
                    // blocking), anything else is a failure this loop cannot mend.
                    var waitResult = MsgWaitForMultipleObjects(1, waited, false, 0xFFFFFFFF /* INFINITE */, 0x04FF /* QS_ALLINPUT */);
                    if (waitResult == 0)
                    {
                        cancelledByRun = true;
                        break;
                    }
                    if (waitResult != 1)
                    {
                        Console.WriteLine($"[NativeApp] MsgWaitForMultipleObjects: {Marshal.GetLastWin32Error()}");
                        break;
                    }
                }

                Msg msg = new();
                var ret = GetMessageW(ref msg, IntPtr.Zero, 0, 0);
                if (ret == 0) // WM_QUIT
                {
                    TakeQuitCode(ref msg);
                    break;
                }
                if (ret == -1)
                {
                    Console.WriteLine($"[NativeApp] GetMessageW error: {Marshal.GetLastWin32Error()}");
                    break;
                }

                lock (MsgLock) { _currentMsg = new MessageInfo(msg.hwnd, msg.message, msg.wParam, msg.lParam, msg.time); }
                DispatchWithTab(ref msg);
                lock (MsgLock) { _currentMsg = null; }

                // Drain Task ContinueWith marshalling queue
                Task.PumpPending();
            }

            // A KILL CLOSES EVERY FORM here, on the thread that owns their windows, and keeps pumping until they are gone:
            // the end-of-run policy below only closes forms WITHOUT a close box, which is right for a script that ended
            // normally and wrong for a run somebody stopped, where a window left standing is exactly the "impossible to
            // kill" the report described.
            if (cancelledByRun)
            {
                CloseAllFormsForKill();
            }
            // A FORM THE USER CANNOT DISMISS IS NOT LEFT STANDING when the run ends. CloseBox = false takes the title bar's X
            // and the system menu away, so such a window is one the user has no way to close, and a quit ends the LOOP
            // without closing anything: measured, Application.Quit on a form with no close box left it alive with its window
            // still standing, which is a process nobody can end. Only those forms are closed, since one WITH a close box is
            // the user's to dismiss, and a close a script's own Closing event vetoes stays vetoed: this goes through the same
            // Close() a script would call, so the event has its say either way.
            foreach (var hwnd in ActiveForms.ToArray())
            {
                if (Control.FromHandle(hwnd) is Form unclosable && !unclosable.CloseBox) { unclosable.Close(); }
            }

            // A QUIT THAT ARRIVED WHILE THE LAST FORM WAS CLOSING IS STILL IN THE QUEUE, and the condition above can leave
            // without ever retrieving it: PostQuitMessage creates no window, so WM_QUIT is exactly the message that can be
            // left behind, and its code with it. Measured: form.Quit(5) left the process on 0, because Close() unregistered
            // the last form, the loop's condition failed before its GetMessageW ran again, and the 5 was never read. Drain
            // it here, so a code cannot be lost to the ORDER of a close and a post.
            Msg leftover = new();
            while (PeekMessageW(ref leftover, IntPtr.Zero, WM_QUIT, WM_QUIT, PM_REMOVE))
            {
                TakeQuitCode(ref leftover);
            }
        }

        public static void Run(Form form) => Run(form, null);

        /// <summary>Runs a form and gives a CHOSEN tab stop the focus, which is what a script means when it wants to
        /// open onto one control in particular: <code>Application.Run(form, 2)</code> takes the focus to the control
        /// whose TabIndex is 2, or to the FIRST stop when no control carries that number, because a form that opens
        /// focused nowhere is worse than one focused somewhere. A script that needs to know can check Focused.</summary>
        public static void Run(Form form, int? wantedTabIndex)
        {
            if (form.Handle == IntPtr.Zero) { form.CreateControl(); }
            form.Show();
            // THE FOCUS A FORM OPENS WITH, and it is the same order Tab walks, so the two cannot disagree about what
            // the order is. Runs on the form it was handed, or on the first RUNNING one when a script names only a
            // number, which is the shape Application.Run(tabIndex) has.
            var focus = TabStopToFocus(form, wantedTabIndex);
            if (focus != null) { FocusWithCue(focus.Handle); }
            Run();
        }

        /// <summary>Runs whatever is already up and puts a tab stop in focus, for a script that kept no form:
        /// <code>Application.Run(3)</code> is "start pumping and take the focus to TabIndex 3", asked of the first
        /// form that is running. No running form, or no stop to focus, leaves the focus where it was.</summary>
        /// <summary>Run the loop and POST WM_CLOSE to every open form after that many milliseconds, so a demo can end
        /// itself. WM_CLOSE rather than Close() on purpose: a form's Closing handler can still REFUSE it and keep the
        /// application up, which is what a hand-rolled script timer cannot do. The deadline is dropped when the loop
        /// returns, because the last form may well have closed before it fired.</summary>
        public static void RunFor(int timeoutMs, Form? form = null)
        {
            System.Threading.Timer? deadline = null;
            if (timeoutMs > 0)
            {
                deadline = new System.Threading.Timer(_ =>
                {
                    IntPtr[] open;
                    lock (FormsLock) { open = [.. ActiveForms]; }
                    foreach (var hwnd in open) { _ = PostMessageW(hwnd, WM_CLOSE, 0, 0); }
                }, null, timeoutMs, System.Threading.Timeout.Infinite);
            }
            try
            {
                if (form != null) { Run(form); } else { Run(); }
            }
            finally
            {
                deadline?.Dispose();
            }
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);
        public static void Run(int wantedTabIndex)
        {
            // WHICH FORM? The first one the engine is RUNNING, from its own registry of forms, so a script that kept no
            // form can still say "open onto TabIndex 3": it should not have to hold a reference for the engine to
            // understand which control it means. No running form, or no stop on it that can take the focus, leaves the
            // focus exactly where it was, and Run() then returns at once because there is no form to pump.
            List<IntPtr> running;
            lock (FormsLock) { running = ActiveForms.ToList(); }
            foreach (var hwnd in running)
            {
                if (Control.FromHandle(hwnd) is not Form form) { continue; }
                var focus = TabStopToFocus(form, wantedTabIndex);
                if (focus != null) { FocusWithCue(focus.Handle); }
                break;
            }
            Run();
        }

        /// <summary>The control a form should open focused on, or null when it has none that can take the focus. One
        /// implementation, because Run(form), Run(form, n) and Run(n) all ask this and must answer it the same way.</summary>
        private static Control? TabStopToFocus(Form form, int? wantedTabIndex)
        {
            var stops = TabOrder(form);
            if (wantedTabIndex is int wanted)
            {
                foreach (var stop in stops) { if (stop.TabIndex == wanted) { return stop; } }
            }
            return stops.Count > 0 ? stops[0] : null;
        }

        /// <summary>Stop the message loop, carrying a CODE to the process when one is given, which is what WM_QUIT
        /// carries and what the loop honours. This is NOT a second spelling of a script&#39;s exit(n): that one ends the
        /// SCRIPT where it stands, unwinding and running finallys, while this ends the LOOP and lets the script carry
        /// on. Close versus Quit, one level down.</summary>
        public static void Exit()
        {
            PostQuitMessage(0);
        }

        public static void Exit(int code)
        {
            PostQuitMessage(code);
        }

        /// <summary>The code a WM_QUIT carries becomes the process&#39;s, when it carries one: PostQuitMessage(n) puts n in
        /// the message, and the loops used to drop it. A ZERO is left alone rather than assigned, because 0 is what the
        /// process exits with anyway and assigning it could overwrite a code the engine set on the way out.</summary>
        private static void TakeQuitCode(ref Msg msg)
        {
            var code = (int)msg.wParam;
            if (code != 0) { Environment.ExitCode = code; }
        }

        /// <summary>
        /// A NEW form, or the one already carrying this title. Measured before this existed: two calls with one
        /// title produced TWO independent live windows (handles 3541904 and 2624450, with `FindWindowA` answering
        /// the first), which is what a snippet pasted twice used to leak.
        /// The rule follows `Application.SingleInstance`: ON means the title identifies the window, so the existing
        /// form comes back and nothing is created; OFF keeps the ability to run two windows with one title, and
        /// WARNS, because a reader should hear about it either way.
        /// </summary>
        public static Form CreateForm(string title)
        {
            var existing = FormByTitle(title);
            if (existing is not null)
            {
                if (SingleInstance)
                {
                    Logging.Log.Warning("Application",
                        $"CreateForm(\"{title}\") answered the form this script already has (handle {existing.Handle}) "
                        + "instead of creating a second one, because Application.SingleInstance is true. Close it "
                        + "first if you meant a second window.",
                        Interpreter.CurrentScriptLine, Interpreter.CurrentScriptColumn);
                    return existing;
                }
                Logging.Log.Warning("Application",
                    $"CreateForm(\"{title}\") is creating a SECOND window with a title this script already uses "
                    + $"({(existing.Handle == IntPtr.Zero ? "not shown yet" : "handle " + existing.Handle)}). "
                    + "Set Application.SingleInstance = true to reuse the first.",
                    Interpreter.CurrentScriptLine, Interpreter.CurrentScriptColumn);
            }
            var form = new Form(title);
            RememberForm(title, form);
            // THE WINDOW EXISTS FROM HERE ON, and it is NOT on screen: the engine's own window creation applies the
            // properties and runs the design/DPI pass, while `Show()` is what makes it visible.
            // WHY, measured (2026-11-03): a control cannot be created on a parent that has no window, so before this a
            // script that built its controls and their items up front depended on the engine creating the parent's
            // handle as a side effect of the first child; and `form.Handle`/`Form.Created` answered 0/False until then,
            // which reads as "no form" in a script that just created one. Asked for directly ("when
            // Application.CreateForm(..) is invoked it should already create the window, but not visible").
            // THE SECOND HALF THIS NEEDS is `ShownForms`: `WM_CREATE` puts every created form in `ActiveForms`, and
            // `Application.Run` waits on that set, so a created-but-never-shown form would otherwise make a run wait
            // for ever. `form.Visible` stays FALSE until `Show()`, which is the question "is it on screen".
            form.CreateControl();
            return form;
        }

        // The single-instance lock's own native calls: CreateMutexW with bInitialOwner, and GetLastError straight
        // after it, which is the documented way to learn that another process already holds the name.
        [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateMutexW(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

        [LibraryImport("kernel32.dll", EntryPoint = "ReleaseMutex")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ReleaseMutex(IntPtr mutex);

        [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);



        /// <summary>A modal prompt: a title, a prompt line, an edit box with an optional starting value, and OK and
        /// Cancel. Returns what was typed, or null when it was cancelled, so "cancelled" stays distinguishable from
        /// "entered nothing".
        ///
        /// It is a window the engine assembles from its OWN Form and controls rather than a Win32 dialog template,
        /// which is what lets it take the theme of the window it opens over, and it runs the engine's own pump while
        /// it is up, so a timer still fires inside it (which is how a suite answers it without a person). The window
        /// it was opened from is disabled until it ends, which is what modality means here: a prompt that leaves its
        /// caller clickable is not a prompt. Enter answers it, Escape and the close box cancel it, and a button click
        /// arrives as the WM_COMMAND that button sends, matched by its handle.</summary>
        /// <summary>The prompt's font on one of its own controls: the three properties every control already has, so
        /// the box only decides the DEFAULT (one step up from the engine's, which is what makes a prompt read like a
        /// message box) and a script can reach past it through <see cref="InputBox.Input"/>.</summary>
        private static void ApplyFont(InputBox box, Control control)
        {
            if (box.FontName.Length > 0) control.FontName = box.FontName;
            if (box.FontSize > 0) control.FontSize = box.FontSize;
            if (box.FontBold) control.FontBold = true;
        }

        internal static int ShowInputBox(InputBox box)
        {
            var owner = GetActiveWindow();
            var ownerForm = owner != IntPtr.Zero ? Control.FromHandle(owner) as Form : null;
            // A prompt narrower or shorter than this cannot hold its buttons, so it is raised to the minimum rather
            // than opened as a sliver: a defined size beats a window nobody can answer.
            // The minimums are the LAYOUT's own requirement rather than round numbers: the buttons need the insets
            // plus their two widths, and the height needs the prompt line, the input and the button row. A prompt
            // asked for smaller than that is raised to fit rather than opened with its parts overlapping.
            var w = Math.Max(box.Width, box.TextLeft + box.TextRight + 256);
            var h = Math.Max(box.Height, 44 + box.TextHeight + 16 + 28 + 16);
            var dlg = new Form(box.Title) { Width = w, Height = h };

            // WHERE IT OPENS IS ASSIGNED BEFORE THE HANDLE EXISTS, because a form stores its design bounds as it
            // creates the window and re-applies them there: measured, a position assigned after the handle was
            // ignored and the prompt came up at the Form's own default of 100,100. The style bits below are the
            // opposite case. Each comment says which way round it is, because the two look alike and are not.
            if (box.StartPosition == 0)
            {
                dlg.Left = box.Left;
                dlg.Top = box.Top;
            }
            else if (box.StartPosition == 1 || owner == IntPtr.Zero)
            {
                dlg.StartPosition = 1;
            }
            else
            {
                GetWindowRect(owner, out var ownerRect);
                dlg.Left = ownerRect.Left + ((ownerRect.Right - ownerRect.Left) - w) / 2;
                dlg.Top = ownerRect.Top + ((ownerRect.Bottom - ownerRect.Top) - h) / 2;
            }

            // The window a MessageBox is and a resizable one is not: a title bar, no minimise or maximise box, and a
            // fixed frame. These are applied AFTER the handle exists, which is what the Form's own properties
            // require: measured, a prompt that set them before its handle came up still carried WS_THICKFRAME and
            // BOTH boxes, which is why it looked nothing like a message box.
            dlg.InitHandle();
            dlg.MaximizeBox = false;
            dlg.MinimizeBox = false;
            dlg.Resizable = false;
            // The close box is the Form's own property, so the prompt and a form cannot disagree about what it means.
            dlg.CloseBox = box.CloseBox;

            // The theme: what the script asked for, and the theme of the window it was opened over when it asked for
            // none, so a prompt in a dark or paletted application matches it.
            if (ownerForm != null) box.InheritTheme(ownerForm.Theme);
            dlg.Theme = box.Theme;
            if (ownerForm != null && !box.ThemeAssigned)
            {
                dlg.ThemeBackground = ownerForm.ThemeBackground;
                dlg.ThemeText = ownerForm.ThemeText;
                dlg.ThemeBorder = ownerForm.ThemeBorder;
                dlg.ThemeSelection = ownerForm.ThemeSelection;
                dlg.ThemeDisabled = ownerForm.ThemeDisabled;
            }
            // The buttons keep a height of their own: a tall input is a place to type, not a reason for fat buttons.
            var btnH = 28;
            var inputY = 14 + 24 + box.TextPadding;

            // THE LAYOUT LIVES IN THE CLIENT, and a window's Width and Height INCLUDE its frame. Measured from a
            // screenshot of the prompt as it shipped: laid out from the window size, the buttons landed BELOW the
            // client and were invisible, and the input touched the window's right edge. So the window grows until
            // the client holds the layout, and every part is placed inside the client.
            // The icon the prompt asked for, replayed through the Form&#39;s own SetIcon and AFTER the theme, since a
            // rendered glyph&#39;s default colour is the host&#39;s own. Nothing asked of it leaves the Form&#39;s default,
            // which is the running executable&#39;s icon.
            // THE SCREEN IS APPLIED BEFORE WINDOWS EXISTS, which is the whole point of it: a Form honours a requested screen
            // as it CREATES its window, and every call below this one forces that creation (the icon, the client rect, the
            // first control). Applied after them it moves a window that already laid itself out at the OLD dpi, and the
            // prompt's parts came out scaled TWICE: measured on a 144/96 box, the prompt at 296x209 with its input at
            // 171x37, which is 0.77 of the reference size times 0.667 again. The position is assigned before this, so an
            // explicit screen still moves the prompt while a manual Left and Top keep their ground.
            if (box.ScreenRequested && box.StartPosition != 0) { dlg.Screen = box.Screen; }

            box.ApplyIcon(dlg);


            GetClientRect(dlg.Handle, out var client);

            // THE LAYOUT SPEAKS REFERENCE UNITS, which is what every control the engine creates expects: each one scales its
            // own design bounds from the form's ReferenceDpi as its handle is created, so handing it the dialog's LIVE pixels
            // scales the layout a SECOND time. Measured on a 144/96 two-monitor box: the prompt came out 296x209 with its
            // input at 171x37, which is 0.77 of the reference size taken again, and the parts looked truncated. Reading the
            // client rect in pixels and converting back is what makes one set of numbers mean one thing on any monitor. The
            // script's own values (TextLeft, TextHeight, Width) are already reference units and stay untouched.
            var hereDpi = (int)GetDpiForWindow(dlg.Handle);
            var toRef = hereDpi > 0 ? (double)dlg.ReferenceDpiX / hereDpi : 1.0;
            int Ref(int px) => (int)Math.Round(px * toRef);

            var clientRight = Ref(client.Right);
            var clientBottom = Ref(client.Bottom);
            var needW = box.TextLeft + box.TextRight + 256;
            var needH = inputY + box.TextHeight + 16 + btnH + 16;
            if (clientRight < needW || clientBottom < needH)
            {
                dlg.Width += Math.Max(0, needW - clientRight);
                dlg.Height += Math.Max(0, needH - clientBottom);
                GetClientRect(dlg.Handle, out client);
                clientRight = Ref(client.Right);
                clientBottom = Ref(client.Bottom);
            }
            var textW = Math.Max(80, clientRight - box.TextLeft - box.TextRight);
            var btnY = clientBottom - btnH - 16;
            var label = dlg.CreateLabel(box.Prompt, box.TextLeft, 14, textW, 24);
            var edit = dlg.CreateTextBox(box.Value, box.TextLeft, inputY, textW, box.TextHeight);
            var ok = dlg.CreateButton("OK", clientRight - box.TextRight - 176, btnY, 80, btnH);
            var cancel = dlg.CreateButton("Cancel", clientRight - box.TextRight - 80, btnY, 80, btnH);
            box.AttachInput(edit);
            box.AttachButtons(ok, cancel);
            ApplyFont(box, label);
            ApplyFont(box, edit);
            ApplyFont(box, ok);
            ApplyFont(box, cancel);

            string? result = null;
            var clicked = false;   // a REAL click on a button, recorded so that the loop closes on its own path

            // The timeout map, read once: { 1: 2000 } answers OK after two seconds, { 2: 2000 } cancels. The EARLIEST
            // deadline wins when both are named, and the answer is recorded exactly as a click would record it, so the
            // prompt closes on the loop's own path rather than from inside a dispatch.
            var timeoutMs = 0;
            var timeoutAccepts = false;
            if (box.Timeouts is IDictionary<object, object?> map)
            {
                foreach (var entry in map)
                {
                    if (entry.Key is null || entry.Value is null) { continue; }
                    var code = Convert.ToInt32(entry.Key);
                    var ms = Convert.ToInt32(entry.Value);
                    if (ms <= 0) { continue; }
                    if (timeoutMs == 0 || ms < timeoutMs) { timeoutMs = ms; timeoutAccepts = code == 1; }
                }
            }
            var deadline = Environment.TickCount64 + timeoutMs;
            // A REAL CLICK ANSWERS THE PROMPT, which it did not. A button SENDS WM_COMMAND to its parent while the loop
            // below reads the QUEUE, so clicking OK or Cancel did nothing at all: reported from the field, and no suite
            // saw it because they POST VK_ESCAPE or VK_RETURN, which IS queued. The engine's own Click route is where a
            // real click lands, so the prompt answers it there and the posted door keeps going through the loop.
            // ...and they RECORD rather than close, because closing the dialog from inside the click's own dispatch is a
            // re-entrant close: measured, it made this suite crash intermittently, a run in three dying with no output.
            // The loop below ends on the flag and closes the window on ITS path, the one the Escape key already uses.
            ok.EngineClick = () => { result = edit.Text; clicked = true; };
            cancel.EngineClick = () => { clicked = true; };
            try
            {
                if (owner != IntPtr.Zero && box.Modal) EnableWindow(owner, 0);
                dlg.Show();
                _inputBoxWindow = dlg.Handle;
                SetFocus(edit.Handle);

                while (IsWindow(dlg.Handle) && !clicked)
                {
                    var pending = PumpMessage();
                    // THE DEADLINE IS CHECKED ON EVERY TURN, before the queue is consulted. Reported from the field as
                    // "nothing happens until I put focus on the form": looking at it only where a message HAD arrived
                    // means an idle window never times out, and a window is idle exactly when nobody is touching it.
                    if (timeoutMs > 0 && Environment.TickCount64 >= deadline)
                    {
                        if (timeoutAccepts) { result = edit.Text; }
                        clicked = true;
                        break;
                    }

                    if (pending == null)
                    {
                        Sleep(1);   // as the engine's own message loop does, rather than spinning on the queue
                        continue;
                    }
                    var info = pending.Value;   // MessageInfo is a struct, so the nullable wrapper is unwrapped here
                    if (info.Message == WM_KEYDOWN && (info.WParam == 13 || info.WParam == 27))
                    {
                        // The DEFAULT BUTTON decides what Enter does: 0 (the default) answers and 1 cancels, which is
                        // what a MessageBox's default button means. Escape cancels either way, and the input keeps
                        // the caret: a prompt that FOCUSED a button instead would be one a script cannot type into.
                        if (info.WParam == 13 && box.DefaultButton == 0) result = edit.Text;
                        dlg.Close();
                    }
                    else if (info.Message == WM_COMMAND)
                    {
                        if (info.LParam == ok.Handle) { result = edit.Text; dlg.Close(); }
                        else if (info.LParam == cancel.Handle) dlg.Close();
                    }
                }
            }
            finally
            {
                _inputBoxWindow = IntPtr.Zero;
                if (owner != IntPtr.Zero && box.Modal) EnableWindow(owner, 1);
            }
            if (IsWindow(dlg.Handle)) { dlg.Close(); }   // a click ended the loop, so it is closed HERE, on the loop's own path rather than inside the click
            if (result == null) return 2;
            box.Value = result;
            return 1;
        }

        /// <summary>The one-shot spelling: build a prompt, show it, and answer its text, or null when it was
        /// cancelled. The same core, so the two spellings cannot behave differently.</summary>
        public static string? InputBoxCore(string title, string prompt, string? defaultValue,
            object? defaultButton = null, object? timeouts = null)
        {
            var box = new InputBox { Title = title, Prompt = prompt, Value = defaultValue ?? "" };
            if (defaultButton != null) { box.DefaultButton = Convert.ToInt32(defaultButton); }
            if (timeouts != null) { box.Timeouts = timeouts; }
            return ShowInputBox(box) == 1 ? box.Value : null;
        }

        private static nint _inputBoxWindow;

        /// <summary>The prompt's own window while InputBox is up, and 0 the rest of the time. INTERNAL: the script
        /// reaches it through the InputBox object's Window member, not by name.</summary>
        internal static nint InputBoxWindow => _inputBoxWindow;

        // ---------- P/Invokes ----------
        private const uint PM_REMOVE = 0x0001;
        private const uint WM_QUIT = 0x0012;
    }
}

