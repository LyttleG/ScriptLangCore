namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>
    /// Registers the Process namespace for launching and managing external processes.
    /// Callbacks run on background threads, keep them simple (no yield, no UI).
    /// </summary>
    private void RegisterProcessFunctions()
    {
        var processNs = new Namespace(_comparer);

        // Process.Start(executable, arguments, onOutput?, onError?, onExited?, stdinText?)
        // Returns a ProcessHandle for Kill(), WaitForExit(), IsRunning, ExitCode, WriteStdin().
        processNs.AddMethod("Start", args =>
        {
            if (args.Length < 2 || args[0] is not string exe || args[1] is not string arguments)
                throw new ScriptException("Process.Start expects: (executable, arguments, onOutput?, onError?, onExited?)");

            var onOutput = args.Length > 2 ? args[2] as ScriptFunction : null;
            var onError = args.Length > 3 ? args[3] as ScriptFunction : null;
            var onExited = args.Length > 4 ? args[4] as ScriptFunction : null;
            var stdinText = args.Length > 5 ? args[5] as string : null;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                RedirectStandardOutput = onOutput != null,
                RedirectStandardError = onError != null,
                RedirectStandardInput = stdinText != null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            // THE CALLBACKS BELONG TO THE INTERPRETER WHOSE SCRIPT HANDED THEM OVER, and that is `Current` when the call is
            // made, not `this`. They are the same in a plain run; they differ inside a NESTED `RunScript`, where the delegate
            // that ran the builtin belongs to the PARENT interpreter while the script, its closure and its top-level
            // variables live in the nested one. Invoking on `this` then ran the callback against the parent's globals:
            // measured, a child script's `Process.Start(..., fun(string line) { hits = hits + 1; })` answered
            // `Undefined variable 'hits'` (the exception is swallowed by the handler, so the run silently saw NO output),
            // and the same subject run directly was fine. The tool that reported it is a watcher that runs every capture
            // through `RunScript`, so every capture that started a child lost its output.
            var callbackOwner = Current ?? this;
            var handle = new ProcessHandle(process, this);

            // Signal that allows WaitForExit to block until the onExited callback completes.
            // Only created when an exit callback is registered, and always signaled
            // (via the finally block) even if the process never actually exits.
            var exitedCallbackDone = onExited != null ? new TaskCompletionSource<bool>() : null;
            handle.ExitedCallbackDone = exitedCallbackDone;

            if (onOutput != null)
            {
                void OutputHandler(object _, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    Interlocked.Increment(ref handle.PendingHandlers);
                    try
                    {
                        lock (handle.InvokeLock)
                        {
                            try
                            {
                                ProcessHandle.InHandlerOf = handle;
                                callbackOwner.InvokeScriptFunction(onOutput, [e.Data], 0, 0);
                            }
                            catch (ScriptException exFail)
                            {
                                // A CALLBACK THAT FAILS IS REPORTED, NEVER RETHROWN (this runs on a pool thread, so a throw
                                // here is not a script's refusal), and the SAME wording is said once: a chatty child would
                                // otherwise print one line per line of output. Before this the failure was DISCARDED, so a
                                // callback that could not resolve its own variables made a run silently see NO output at all
                                // (measured through the clipboard watcher: a suite's 9 checks read as 3 passed, 6 failed).
                                handle.ReportHandlerFailure(exFail.Message, exFail.Line, exFail.Column);
                            }
                            catch (Exception exFail)
                            {
                                handle.ReportHandlerFailure(FriendlyExceptionMessage(exFail));
                            }
                        }
                    }
                    finally { ProcessHandle.InHandlerOf = null; Interlocked.Decrement(ref handle.PendingHandlers); }

                }

                process.OutputDataReceived += OutputHandler;
            }

            if (onError != null)
            {
                void ErrorHandler(object _, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    Interlocked.Increment(ref handle.PendingHandlers);
                    try
                    {
                        lock (handle.InvokeLock)
                        {
                            try
                            {
                                ProcessHandle.InHandlerOf = handle;
                                callbackOwner.InvokeScriptFunction(onError, [e.Data], 0, 0);
                            }
                            catch (ScriptException exFail)
                            {
                                // A CALLBACK THAT FAILS IS REPORTED, NEVER RETHROWN (this runs on a pool thread, so a throw
                                // here is not a script's refusal), and the SAME wording is said once: a chatty child would
                                // otherwise print one line per line of output. Before this the failure was DISCARDED, so a
                                // callback that could not resolve its own variables made a run silently see NO output at all
                                // (measured through the clipboard watcher: a suite's 9 checks read as 3 passed, 6 failed).
                                handle.ReportHandlerFailure(exFail.Message, exFail.Line, exFail.Column);
                            }
                            catch (Exception exFail)
                            {
                                handle.ReportHandlerFailure(FriendlyExceptionMessage(exFail));
                            }
                        }
                    }
                    finally { ProcessHandle.InHandlerOf = null; Interlocked.Decrement(ref handle.PendingHandlers); }

                }

                process.ErrorDataReceived += ErrorHandler;
            }

            if (onExited != null)
            {
                EventHandler? exitedHandler = null;
                exitedHandler = (_, _) =>
                {
                    process.Exited -= exitedHandler;
                    ProcessHandle.InHandlerOf = handle;
                    try { callbackOwner.InvokeScriptFunction(onExited, [process.ExitCode], 0, 0); }
                    finally { ProcessHandle.InHandlerOf = null; exitedCallbackDone?.TrySetResult(true); }
                };
                process.Exited += exitedHandler;
            }

            try
            {
                process.Start();

                if (stdinText != null)
                {
                    process.StandardInput.Write(stdinText);
                    process.StandardInput.Close();
                }

                if (onOutput != null) process.BeginOutputReadLine();
                if (onError != null) process.BeginErrorReadLine();
            }
            catch (System.ComponentModel.Win32Exception wex)
            {
                throw new ScriptException($"Process.Start failed: {wex.Message} (error code: {wex.NativeErrorCode})");
            }
            catch (Exception ex)
            {
                throw new ScriptException($"Process.Start failed: {FriendlyExceptionMessage(ex)}");
            }
            // AND IT DOES NOT WAIT FOR THE EXIT CALLBACK, which is the C# shape: `Exited` fires later. It used to wait
            // here "so the exit code had landed before the handle was read", and that made
            // `Process.Start(..., onExited)` BLOCK for the child's whole lifetime: measured, a ~2 s child made Start
            // answer after 2,105 ms, so a script that only wanted an exit notification could not run anything
            // meanwhile. The ordering guarantee lives where it is ASKED FOR: `ExitCode` answers -1 while the process
            // runs, and `WaitForExit()` waits for the callback (bounded at 5 s) before it answers the code.

            return handle;
        });

        CurrentFrame.Scopes.Define("Process", processNs);
    }

    /// <summary>Script-side handle for a running process. Returned by Process.Start().</summary>
    public sealed class ProcessHandle : IDisposable
    {
        private readonly Process _process;
        internal readonly Lock InvokeLock = new();
        internal TaskCompletionSource<bool>? ExitedCallbackDone;
        private Interpreter? _interpreter;
        internal volatile int PendingHandlers;

        /// <summary>Which handle's callback THIS THREAD is inside, so a wait for the handle that is running the callback can be
        /// REFUSED instead of spinning. Measured: a callback calling `WaitForExit()` on its OWN handle hung the run for ever (it
        /// spins on `PendingHandlers`, which includes its own invocation), and the two-handle shape, where a callback waits for a
        /// DIFFERENT process, completes normally. Per THREAD because the handlers run on pool threads while the run thread waits.</summary>
        [ThreadStatic] internal static ProcessHandle? InHandlerOf;
        private string? _lastHandlerError;   // the failure this handle already reported (the same wording is one line)

        /// <summary>Set by Dispose(). Every member that QUERIES the process goes through Live(), so a script that
        /// asks a released handle gets the engine's sentence rather than the framework's own for it, which on an
        /// invariant-globalization build is its resource KEY ("NoAssociatedProcess"). It is the shape the COM
        /// bridge uses for a released handle ("the COM object is disconnected").</summary>
        private volatile bool _released;

        internal ProcessHandle(Process process, Interpreter interpreter)
        {
            _process = process;
            _interpreter = interpreter;
        }

        /// <summary>The live process, or a refusal that names the RELEASE.</summary>
        private Process Live()
        {
            if (_released)
                RuntimeError("this process handle was released: Dispose() ran, so the process cannot be queried", 0, 0);
            return _process;
        }

        /// <summary>True while the process is still running.</summary>
        public bool IsRunning => !Live().HasExited;

        /// <summary>Process exit code (available after process exits).</summary>
        public int ExitCode
        {
            get
            {
                var p = Live();
                return p.HasExited ? p.ExitCode : -1;
            }
        }

        /// <summary>Write text to the process's stdin stream.</summary>
        public void WriteStdin(string text)
        {
            try { _process.StandardInput.Write(text); _process.StandardInput.Flush(); }
            catch { /* pipe may be closed */ }
        }

        /// <summary>Close the process's stdin stream (signals EOF to the child process).</summary>
        public void CloseStdin()
        {
            try { _process.StandardInput.Close(); }
            catch { /* already closed */ }
        }

        /// <summary>Wait until all pending output/error handler invocations complete.</summary>
        private void WaitForHandlers()
        {
            var spin = new SpinWait();
            while (PendingHandlers > 0) spin.SpinOnce();
        }
        /// <summary>A wait for the process whose callback is RUNNING would never finish, because the wait ends when the callback
        /// does: refused with the recipe rather than spun. `Timeout` does not save it either, since the exit callback is what
        /// the caller is inside.</summary>
        private void RefuseWaitInOwnCallback(string member)
        {
            if (!ReferenceEquals(InHandlerOf, this)) return;
            throw new ScriptException($"{member} cannot wait for the process whose own callback is running: that wait finishes only when this callback returns, so the run would deadlock. Set a flag here and wait outside the callback, or wait on a DIFFERENT handle.");
        }

        /// <summary>Say a callback's failure once, through the ENGINE's log (the map owns the wording, never `ex.Message`).
        /// The position is part of the identity, the rule the timer's own reporter states: the same wording from another
        /// line is another failure.</summary>
        internal void ReportHandlerFailure(string message, int line = 0, int column = 0)
        {
            var key = line > 0 ? $"{line}:{column}:{message}" : message;
            if (key == _lastHandlerError) return;
            _lastHandlerError = key;
            if (line > 0) Logging.Log.Error("Process", message, line, column);
            else Logging.Log.Error("Process", message);
        }

        public void Kill()
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* already exited */ }
        }

        /// <summary>Block until the process exits and all async output is drained. Returns the exit code.</summary>
        public int WaitForExit()
        {
            RefuseWaitInOwnCallback(nameof(WaitForExit));
            while (!Live().WaitForExit(250))
            {
                if (!_interpreter!._interruptRequested)
                    continue;
                try { _process.Kill(entireProcessTree: true); }
                catch
                {
                    // ignored
                }

                throw new ScriptException(ErrorCode.Interrupt, "KeyboardInterrupt", 0, 0);
            }
            _process.WaitForExit();          // drain async output streams
            WaitForHandlers();              // ensure all output/error callbacks completed
            ExitedCallbackDone?.Task.Wait(5000);  // wait for onExited callback (5s timeout)
            return _process.ExitCode;
        }

        /// <summary>Block until the process exits or the timeout (milliseconds) elapses. Returns true if the process exited.</summary>
        public bool WaitForExit(int timeoutMs)
        {
            RefuseWaitInOwnCallback(nameof(WaitForExit));
            var exited = Live().WaitForExit(timeoutMs);
            if (exited)
            {
                // Drain async output streams (race: output events fire after exit)
                _process.WaitForExit();
            }
            return exited;
        }

        /// <summary>Dispose the underlying Process object, releasing all OS resources. ONE call: `Process.Close`
        /// and `Process.Dispose` are the SAME cleanup, the docs state it ("The Dispose method calls Close") and the
        /// source has Dispose(bool) call Close, so the pair this used to make was that work twice, guarded only by
        /// the framework's own disposed flag. The class implements IDisposable so a script's `using` reaches it: the
        /// engine's `using` asks `obj is IDisposable` rather than looking for a method named Dispose, so a handle
        /// without the interface was silently left undisposed while its `using` block reported nothing (measured:
        /// `handle as IDisposable` was null and the block simply ran).</summary>
        public void Dispose()
        {
            _released = true;
            try { _process.Dispose(); }
            catch
            {
                // ignored
            }
            _interpreter = null;
        }
    }
}