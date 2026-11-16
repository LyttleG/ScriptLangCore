namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>The internal helper a compiled PROGRAM BODY calls to define a top-level `const`: one name, shared
    /// by the compiler that emits the call and the registration below, so the two cannot drift. Not a
    /// script-visible member: the name exists for exactly one call site, which the compiler writes.</summary>
    internal const string DefineConstHelperName = "__define_const__";

    /// <summary>PHASE-0: turns a NESTED function declaration into a value for a compiled body.</summary>
    internal const string NestedFunctionHelperName = "__nested_fn__";

    /// <summary>The internal helper a compiled body calls for the TARGET of `obj[i].Member = value`. The
    /// element it answers is what SET_PROP writes to, and it runs the CS1612 test on the way, because only the
    /// RUNTIME knows which container the target came from (a list's indexer hands back a copy, so a struct
    /// element write is refused, while an ARRAY element IS a variable). One name, shared by the compiler that
    /// emits the call and the registration below, and not a script-visible member: it exists for exactly one
    /// call site, which the compiler writes.</summary>
    internal const string IndexedWriteTargetHelperName = "__indexed_target__";
    /// <summary>The internal helper a compiled body calls for an EXPLICITLY TYPED declaration's initializer, so
    /// the declared type is honoured the way the TREE honours it (`VisitVarDeclStmt` coerces through CoerceTo).
    /// A compiled body emitted NO coercion, so `short v = 7;` kept an int: measured inside a function,
    /// `typeof(v)` answered "int" and `v is short` was False, where the tree answered "short" and True, and the
    /// top-level path hid the gap by DECLINING such a declaration (the tree walks it), which left a function
    /// body as the one place it showed. The type travels as the TypeInfo the compiler already pushes for
    /// `is`/`as`, the position as arguments (a native has no statement to read one from), and the answer is what
    /// the store writes. Same shape as the other two helpers: one name, shared by the compiler that emits the
    /// call and the registration below, and not a script-visible member.</summary>
    internal const string CoerceDeclHelperName = "__coerce_decl__";

    /// <summary>The internal helper a COMPILED TOP-LEVEL DECLARATION calls to record the type it was declared with.
    /// A declaration at a program body's own top level is a GLOBAL, and the compiled store is a bare
    /// `STORE_GLOBAL_FAST`, which carries a value and no type: measured, a stripped bundle answered `typeof(n)` "long"
    /// for `long? n = 1;` and "null" for `long? n;`, where the file (whose top level tree-walks that declaration)
    /// answers "long?" for both. So the declaration's own spelling travels, exactly as the tree records it
    /// (`TypeInfo.DeclaredSpelling`, the same call `VisitVarDeclStmt` makes), and the binding keeps it across later
    /// assignments (`RewriteKeepingType`). Nothing on the wire changes: a name and a string are two consts and a CALL.</summary>
    internal const string DeclareVarHelperName = "__declare_var__";



    /// <summary>The internal helper `typeof(x)` calls when its operand IS A NAME: the VALUE alone cannot say what a
    /// NULL is, so the name travels with it and the DECLARED type answers (`long? db;` is "long?", where the
    /// value-based builtin could only say "null"). A non-null value keeps the value-based answer, which is the
    /// runtime type with the annotation erased, the rule the C# table in typeof_nameof_test states. Same shape as the
    /// helpers above: one name, the parser writes the call, and it is not a script-visible member.</summary>
    internal const string TypeofNamedHelperName = "__typeof__";

    /// <summary>The internal helper an interpolation ALIGNMENT calls: `$"{x,5}"` pads the value's text to five columns
    /// (right-aligned, and a negative width left-aligns), AFTER any format specifier has been applied, which is C#'s
    /// order. It was silently dropped before this existed: `{x,5}` left the width to the expression parser, which read
    /// the expression and stopped at the comma. The width travels as TEXT because a hole is built from its own source
    /// text, and it is read here so a width that is not a whole number is refused with the engine's own message rather
    /// than by a parse of the template. Same shape as the other helpers: one name, shared by the parser that emits the
    /// call and this registration, and not a script-visible member.</summary>
    internal const string InterpPadHelperName = "__interp_pad__";

    /// <summary>The internal helper a compiled PROGRAM BODY calls for a TOP-LEVEL `using var`. Disposal belongs to
    /// the exit of the scope the declaration lives in, and a program body's own top level has no scope to leave:
    /// the RUN's end is that exit. So the value is handed to the engine's ONE registry
    /// (`Scope.RegisterDisposeOnExit`, drained by `Scope.RunDisposeOnExit`), which is the same mechanism the tree
    /// uses for this shape, and it answers the value so the statement's own store still gets it. Registering here
    /// rather than disposing in the body's own bytecode is also what makes an early `exit()` release it. Not a
    /// script-visible member: the name exists for exactly one call site, which the compiler writes.</summary>
    internal const string DeferDisposeHelperName = "__defer_dispose__";

    /// <summary>The internal helper a compiled `var` declaration calls, so a HANDLE a script binds to a name is
    /// remembered and released when the interpreter is disposed even if the script never closes it
    /// (<see cref="Interpreter.TrackDeclaredResource"/>). It exists because the VM's store carries only the value: the
    /// tree registers at its own declaration site (`VisitVarDeclStmt`), and the two legs must remember the same set of
    /// values or a leaked handle would be released in a file run and not in a `--no-source` bundle of the same file.
    /// The callee is pushed BEFORE the initializer, the order the VM's CALL pops in (args from the top, then the
    /// callee), and the answer is the value so the statement's own store still gets it. Not a script-visible member:
    /// one call site, written by the compiler.</summary>
    internal const string TrackHandleHelperName = "__track_handle__";

    /// <summary>The internal helper a compiled body calls for a `ref`/`out` argument whose target is a GLOBAL
    /// (which at the top level is every variable). It answers a CELL over the named global, the shape the VM's ref
    /// params already read and write through, because the VM's own encoding (`LOAD_REF` plus a slot index) names a
    /// LOCAL slot and cannot name a global. Not a script-visible member: one call site, written by the compiler.</summary>
    internal const string RefGlobalHelperName = "__ref_global__";

    /// <summary>The internal helper a compiled body calls for a `ref`/`out` argument whose target is a FIELD or a
    /// PROPERTY (`Bump(ref h.F)`). Same reason as the global one: the VM's own ref encoding names a local slot, so the
    /// callee is handed a CELL instead, built here over the container the body already evaluated. The engine's own
    /// member resolver builds it (`Interpreter.RefCellForMemberValue`), so a reference and `h.F = v` are one storage, and
    /// the access check, the inheritance walk and an auto-property's backing field are asked once.
    /// Not a script-visible member: one call site, written by the compiler.</summary>
    internal const string RefFieldHelperName = "__ref_field__";

    /// <summary>The same for an ELEMENT (`Bump(ref a[i])`): the container and the index arrive already evaluated, so the
    /// index expression runs ONCE (a `ref a[i++]` increments the counter once, rule 7 of the ref spec), and the cell
    /// reads and writes through the same value-level core a compound assignment uses. Not a script-visible member.
    /// </summary>
    internal const string RefElemHelperName = "__ref_elem__";

    /// <summary>The cell a `ref &lt;name&gt;` expression answers when the target is a bare NAME, which the parser cannot
    /// classify: the name may be a local, a captured local, a global or a ref parameter, and each lives in a different
    /// place. Resolved at RUN time by the interpreter's own scope walk (`RefCellForName`), so `var r = ref x;` binds `r`
    /// to whatever storage `x` has, and writes through it. Not a script-visible member.</summary>
    internal const string RefNameHelperName = "__ref_name__";

    /// <summary>The write a `F(..) = value;` statement performs on the reference a ref-returning call answered. The
    /// compiled path emits a call to it (the cell, the value, the position), and the tree's VisitRefAssignExpr asks the
    /// same interpreter method, so the two engines share ONE implementation and ONE refusal. Not a script-visible
    /// member.</summary>
    internal const string RefAssignHelperName = "__ref_assign__";

    /// <summary>The internal helper a compiled PROGRAM BODY calls for a TOP-LEVEL STATEMENT it could not compile:
    /// the statement's own SOURCE TEXT is parsed here and run by the TREE, which is the same walk a source run takes
    /// for that statement, so the carried statement behaves exactly as it does in the file. See
    /// <see cref="Compiler.CompileProgramWithCarries"/>, which emits the call. Not a script-visible member: the name
    /// exists for exactly one call site, which the compiler writes.</summary>
    internal const string RunCarriedStatementHelperName = "__run_carried__";

    /// <summary>The internal helper a compiled PROGRAM BODY calls for a TOP-LEVEL `import`. An import's whole job
    /// is a SCOPE side effect, and it belongs AT ITS POSITION: a file may export a library and import it further
    /// down, and one end-of-file replay of it runs before anything has been written. Measured before this existed:
    /// a stripped bundle of such a file failed at LOAD with "Module 'x' not found", where its source runs to the
    /// end. The LOADER still replays imports for a DECLARATION-ONLY load (`.loadslcbc`, `Bytecode.LoadScript`),
    /// where no top level ever runs and a loaded function must still find its module's names.</summary>
    internal const string RunImportHelperName = "__run_import__";

    /// <summary>Answers what a NESTED run's result means for the Task that carries it: a nested script that FAILED
    /// THROWS, so that Task faults (C#'s convention for a Task-returning member), and anything else is handed back as
    /// the Task's result. ONE decision, read by both forms of `RunScriptAsync`, so they cannot diverge.</summary>
    private static object? NestedRunOrThrow(object? nestedResult)
    {
        if (nestedResult is ScriptResult failed && failed.HasError)
            throw new ScriptException(failed.ErrorMessage ?? "the nested script failed.");
        return nestedResult;
    }

    private void RegisterHelpers()
    {
        // A compiled program body defines a top-level `const` through this helper, and this is the only place it
        // exists: the value goes into the global scope exactly as STORE_GLOBAL_FAST puts it there, and the name
        // joins the current const set exactly as the tree's own declaration does, so a stripped bundle keeps BOTH
        // the value and the const-ness. The name is not a script-visible member of the surface: it is an internal
        // call site the compiler emits, spelled so that no script would write it by accident.
        // `typeof(<a name>)`: the DECLARED type when the value is null, the value's own type otherwise. The declared
        // spelling is the one the declaration wrote (mark included), which is why it answers "long?" and "List<int>?".
        RegisterNative(TypeofNamedHelperName, TokenType.StringType, args =>
        {
            if (args.Length < 2 || args[0] is not string typeofName) return "null";
            var self = Current ?? this;
            // THE DECLARATION WINS, and that is the point of asking a NAME: a variable keeps the type it was declared with
            // across every assignment, which is what an LValue IS in a typed language (`long? db;` stays "long?" after
            // `db = Sqlite.OpenDb(...)`). Reporting the runtime type of whatever it holds made one variable answer two
            // names in one run. A name with NO declaration (`var`, a parameter today) has none to report, so the value's
            // own type is the fallback, and so it is for a non-name operand, which keeps C#'s erasure rule.
            return self.DeclaredTypeNameOf(typeofName) ?? GetFriendlyTypeNameOfValue(args[1]);
        });
        RegisterNative(NestedFunctionHelperName, TokenType.Object, args =>
        {
            if (args.Length == 0 || args[0] is not string nestedName) return null;
            return Current?.MakeNestedFunction(nestedName, args);
        });
        RegisterNative(DefineConstHelperName, TokenType.Void, args =>
        {
            if (args.Length < 2 || args[0] is not string constName || constName.Length == 0)
                throw new ScriptException("__define_const__: a name and a value are required.");
            // The CALLING interpreter's state, not the registering one's: a module interpreter inherits its parent's
            // natives, so `this` can be the importer, and a definition written there is a name leaked into the wrong
            // program (the rule and its measurement are on __run_import__ below).
            var self = Current ?? this;
            self.SetGlobalValue(constName, args[1], 0, 0, define: true);   // a definition, not an assignment
            self.CurrentFrame.Scopes.GetCurrentConstSet().Add(constName);
            return null;
        });

        // THE DECLARED TYPE OF A COMPILED TOP-LEVEL DECLARATION, recorded after its store: a program body's store is a
        // bare `STORE_GLOBAL_FAST`, which carries a value and no type, so a stripped bundle answered `typeof(n)` "long"
        // for `long? n = 1;` and "null" for `long? n;` where the file answers "long?" for both. The spelling is the one
        // the tree records (`TypeInfo.DeclaredSpelling`), so `typeof(<a name>)` cannot answer two names for one
        // declaration depending on who ran it. It only sets the TYPE, never the value: the store has already run.
        RegisterNative(DeclareVarHelperName, TokenType.Void, args =>
        {
            if (args.Length < 2 || args[0] is not string declName || declName.Length == 0 || args[1] is not string spelling)
                throw new ScriptException("__declare_var__: a name and the declared type are required.");
            var owner = Current ?? this;
            owner.CurrentFrame.Scopes.SetGlobalTypeName(declName, spelling);
            return null;
        });

        // The padding an interpolation alignment asks for. The value is rendered through the SAME display rule a hole
        // and `+` use, so `$"{list,8}"` pads what the concatenation would have written.
        RegisterNative(InterpPadHelperName, TokenType.String, args =>
        {
            var text = ValueFormatter.ToDisplay(args.Length > 0 ? args[0] : null);
            var spec = ValueFormatter.ToDisplay(args.Length > 1 ? args[1] : null).Trim();
            int width;
            try { width = Convert.ToInt32(spec); }
            catch (Exception)
            {
                throw new ScriptException($"an interpolation alignment has to be a whole number, got '{spec}'.");
            }
            if (width == 0 || text.Length >= Math.Abs(width)) return text;
            return width > 0 ? text.PadLeft(Math.Abs(width)) : text.PadRight(Math.Abs(width));
        });

        // A top-level `using var` disposes when its RUN ends, and a program body's top level is the one scope the
        // body does not leave, so the compiled form registers the value with the very registry the tree uses
        // (VisitUsingDeclStmt does the same) and lets the run-end drain release it. The answer is the value, so the
        // statement's store consumes it and the operand stack stays balanced.
        RegisterNative(DeferDisposeHelperName, TokenType.Object, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("__defer_dispose__: one value is required.");
            var value = args[0];
            CurrentFrame.Scopes.RegisterDisposeOnExit(() => TryDispose(value));
            return value;
        });

        // A compiled `var` declaration's own hook: the value is remembered for the interpreter's dispose
        // (`Interpreter.TrackDeclaredResource`) without being disposed here, because a declaration is not a scope exit
        // and the script may still use the handle. The answer is the value, so the statement's store still gets it.
        RegisterNative(TrackHandleHelperName, TokenType.Object, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("__track_handle__: one value is required.");
            // `Current ?? this`, NOT `this`: helper natives live in the ROOT scope, so a nested interpreter
            // (`RunScript`, a bundle's entry body) resolves the lambda the OUTER interpreter registered, and a plain
            // `this` would file the value in the wrong registry. Measured: a nested run's declared writer was tracked
            // by the outer interpreter, so the child's own dispose released nothing and the file stayed locked.
            (Current ?? this).TrackDeclaredResource(args[0]);
            return args[0];
        });

        // `obj[i].Member = value` needs the ELEMENT and the CS1612 test on it, and only the runtime knows
        // whether the container is a LIST (whose indexer hands back a copy, so a struct element write is
        // refused) or an ARRAY (whose element IS a variable). The compiler cannot tell, so it asks this helper
        // for the TARGET rather than declining the shape, and the helper runs THE SAME rule the tree's own
        // check runs (RefuseStructElementWriteFromValues), on values, so the two engines refuse exactly the
        // same writes. The position travels as arguments because a native has no statement to read one from,
        // where the tree refuses at its own node's line and column.
        RegisterNative(IndexedWriteTargetHelperName, TokenType.Object, args =>
        {
            if (args.Length != 4)
                throw new ScriptException("__indexed_target__: a container, an index, a line and a column are required.");
            var line = Convert.ToInt32(args[2]);
            var column = Convert.ToInt32(args[3]);
            var element = GetArrayElementValues(args[0], [args[1]], line, column);
            RefuseStructElementWriteFromValues(args[0], element, args[1], line, column);
            return element;
        });

        // A top-level STATEMENT the compiler could not model is handed to the TREE by its own SOURCE TEXT: this
        // helper re-parses it with the engine's own parser and `Execute` runs it, which is exactly the walk a source
        // run takes for that statement. THREE details, each of which is a measured failure when missed:
        //  (1) the ROOT frame is pushed for the duration, because `Execute` otherwise writes the statement's
        //      globals into a frame the compiled statements of the same body do not read (the same reason the
        //      import replay states), and it is popped in a `finally` so a throwing statement cannot leave the
        //      frame stack unbalanced;
        //  (2) the VM's global SLOT CACHE is flushed AFTER the call (the VM does that by name for this helper),
        //      because a compiled statement after this one would otherwise read the value it cached before the
        //      carried statement ran;
        //  (3) the statement's own failure PROPAGATES: `Execute` returns a result carrying a Throw flow rather than
        //      throwing, so this re-raises it through the engine's one payload rule.
        RegisterNative(RunCarriedStatementHelperName, TokenType.Void, args =>
        {
            if (args.Length < 1 || args[0] is not string text || text.Length == 0)
                throw new ScriptException("__run_carried__: the statement's source text is required.");
            // The CALLING interpreter, for the reason __run_import__ states below: a module interpreter inherits its
            // parent's natives, so `this` can be the importer, and the statement would then run against the wrong
            // program's scopes and classes.
            var self = Current ?? this;
            var parsed = new Parser(new Lexer(text.AsMemory()).GetTokens(), self.Delegates, self._caseSensitive,
                source: text).GetStatements().ToList();
            var frames = self._callFrames.Value!;
            frames.Push(self._rootFrame);
            try
            {
                foreach (var node in parsed)
                {
                    var result = self.Execute(node);
                    if (result.Flow == ControlFlow.Throw)
                        throw ThrowPayloadException(result.Value, node.Line, node.Column);
                    if (result.Flow == ControlFlow.Return)
                        return result.Value;
                }
            }
            finally { frames.Pop(); }
            return null;
        });
        // A TOP-LEVEL IMPORT, run where the file wrote it. The frame juggling is the carried-statement path's, for
        // the same measured reason: the module's names must land in the scope a compiled statement of this body
        // reads, and Execute otherwise binds them into a frame those statements never look at.
        // IT RUNS ON THE INTERPRETER THAT CALLED IT, never on the one that registered this native: a module
        // interpreter inherits its parent's natives, so `this` can be the IMPORTER. Measured on `Bytecode.ExportLibrary`
        // of a module that imports a bundle and derives from a class in it: the alias's classes registered on the
        // importer (interpreter A, `hasParent=True`) while the module's `class Dog extends PXA.Geo.Animal` ran on the
        // module interpreter (interpreter B, `pxaKeys=0`) and answered `Parent class 'PXA.Geo.Animal' not found`. Only
        // the COMPILED leg showed it, because a compiled statement is what reaches this helper.
        RegisterNative(RunImportHelperName, TokenType.Void, args =>
        {
            // The ARITY is the compiler's contract; the PATH's value is the engine's own rule, shared with the
            // tree's visitor so the two cannot word one mistake differently (see RequireImportPathText).
            if (args.Length != 4)
                throw new ScriptException("__run_import__: a path, an alias, a line and a column are required.");
            var self = Current ?? this;
            var alias = args[1] as string;
            var line = Convert.ToInt32(args[2]);
            var column = Convert.ToInt32(args[3]);
            var importPath = self.RequireImportPathText(args[0], line, column);
            var frames = self._callFrames.Value!;
            frames.Push(self._rootFrame);
            ExecutionResult result;
            try { result = self.Execute(ImportStmt.Create(importPath, alias, line, column)); }
            finally { frames.Pop(); }
            if (result.Flow == ControlFlow.Throw)
                throw ThrowPayloadException(result.Value, line, column);
            return null;
        });
        // (VisitVarDeclStmt -> CoerceTo) and a compiled body did not, so inside a FUNCTION `short v = 7` kept an
        // int where the tree said "short" (measured: `typeof(v)` "int", `v is short` False). The compiler
        // pushes the TypeInfo it already uses for `is`/`as`, so the rule and the name are the engine's, not a
        // second table's.
        RegisterNative(CoerceDeclHelperName, TokenType.Object, args =>
        {
            if (args.Length != 4 || args[1] is not TypeInfo declared)
                throw new ScriptException("__coerce_decl__: a value, a type, a line and a column are required.");
            return CoerceTo(args[0], declared, Convert.ToInt32(args[2]), Convert.ToInt32(args[3]));
        });
        // A ref/out target that is NOT a LOCAL: the top level's own case, `Swap(ref x, ref y)` where `x` is a
        // GLOBAL, since at the top level every variable is one. The VM's ref encoding names a local SLOT (LOAD_REF
        // plus a slot index), which cannot name a global, so the compiler hands the callee a CELL over the global
        // instead: that is the very shape the VM's ref params already use for a local slot (a local holding a cell
        // is DEREFERENCED on read and written THROUGH on store), so nothing else had to learn a new encoding. The
        // name and the position travel as arguments because a native has no statement to read them from, exactly
        // like __indexed_target__. Measured before this existed: the whole top level DECLINED for one
        // `Swap(ref x, ref y)`, which is 21 suites of the corpus.
        RegisterNative(RefGlobalHelperName, TokenType.Object, args =>
        {
            if (args.Length != 3 || args[0] is not string name)
                throw new ScriptException("__ref_global__: a name, a line and a column are required.");
            var line = Convert.ToInt32(args[1]);
            var column = Convert.ToInt32(args[2]);
            // THE INTERPRETER RUNNING THE BODY, not the one that registered this native: a namespace member captures
            // its registering interpreter, so inside a nested `RunScript` this cell used to read and write the OUTER
            // run's globals, and `Bump(ref g)` over a global of the nested file answered `Undefined variable 'g'`
            // (reported from the clipboard tool, which runs a captured file through RunScript; the same file run
            // directly was fine). `InvokeScriptFunction` pushes `Current` for the body it enters, so this answers the
            // run even on a pool thread, which is why the four sibling helpers (__ref_field__, __ref_elem__,
            // __ref_name__, __ref_assign__) already ask `Current` and this one did not.
            var self = Current ?? this;
            // A name nothing binds is the TREE's own refusal, because the tree evaluates the argument expression:
            // `Swap(ref typo, ...)` reports an undefined variable there, and this report is the same sentence.
            if (self.ResolveGlobalValue(name) is VmFallback)
                RuntimeError($"Undefined variable '{name}'.", line, column);
            // The setter writes through SetGlobalValue, the ONE place every global write goes, so a ref write to a
            // top-level const is refused with the engine's own message exactly as a plain assignment is.
            return new RefCell(() => self.ResolveGlobalValue(name), v => self.SetGlobalValue(name, v, line, column));
        });
        // A FIELD/PROPERTY target (four arguments: the container the body evaluated, the member name, and the call
        // site's keyword, so a swapped `ref`/`out` is still judged by the bind) and an ELEMENT target (the container and
        // its index, both already evaluated). Both ask the interpreter's own member/element resolver, so the shapes a
        // script can write and the storage a reference reaches are the same question asked once.
        RegisterNative(RefFieldHelperName, TokenType.Object, args =>
        {
            if (args.Length != 5 || args[1] is not string memberName)
                throw new ScriptException("__ref_field__: a container, a member name, the call site's keyword and a position are required.");
            var fieldByOut = args[2] is bool fb ? fb : (bool?)null;
            return (Current ?? throw new ScriptException("__ref_field__: no interpreter is active."))
                .RefCellForMemberValue(args[0], memberName, fieldByOut,
                    Convert.ToInt32(args[3]), Convert.ToInt32(args[4]));
        });
        RegisterNative(RefElemHelperName, TokenType.Object, args =>
        {
            if (args.Length != 5)
                throw new ScriptException("__ref_elem__: a container, an index, the call site's keyword and a position are required.");
            var elemByOut = args[2] is bool eb ? eb : (bool?)null;
            return (Current ?? throw new ScriptException("__ref_elem__: no interpreter is active."))
                .RefCellForElementValue(args[0], args[1], elemByOut,
                    Convert.ToInt32(args[3]), Convert.ToInt32(args[4]));
        });
        // The cell a `ref <name>` expression answers: the name is classified at RUN time (a local, a captured local, a
        // global, or a ref parameter that is already a cell), because the parser cannot know which.
        RegisterNative(RefNameHelperName, TokenType.Object, args =>
        {
            if (args.Length != 3 || args[0] is not string refName)
                throw new ScriptException("__ref_name__: a name, a line and a column are required.");
            return (Current ?? throw new ScriptException("__ref_name__: no interpreter is active."))
                .RefCellForName(refName, Convert.ToInt32(args[1]), Convert.ToInt32(args[2]));
        });
        // The write a REF-ASSIGNMENT statement performs (`F(..) = 7;`), for the compiled path: the cell the call answered
        // arrives first, the value second, and the ONE implementation in the interpreter performs the write and answers
        // the value (which the statement then discards), so a compiled body and a walked one cannot word a refusal
        // differently.
        RegisterNative(RefAssignHelperName, TokenType.Object, args =>
        {
            if (args.Length != 4)
                throw new ScriptException("__ref_assign__: a reference, a value, a line and a column are required.");
            return (Current ?? throw new ScriptException("__ref_assign__: no interpreter is active."))
                .AssignThroughRefCell(args[0], args[1], Convert.ToInt32(args[2]), Convert.ToInt32(args[3]));
        });
        RegisterNative("sleep", TokenType.Void, args =>
        {
            if (args.Length != 1) throw new ScriptException("sleep expects 1 argument.");
            var millisec = Convert.ToInt32(args[0]);
            if (millisec < 1) millisec = 1;
            // Sleep in small increments so Ctrl+C can interrupt it
            while (millisec > 0)
            {
                if (Current?._interruptRequested == true)
                {
                    Current._interruptRequested = false;
                    throw new ScriptException(ErrorCode.Interrupt, " ⚠️ Interrupted by user");
                }
                // THE RUN WAS CANCELLED, which is a runner's Stop button (`Task.Run(fn, token)` plus `cts.Cancel()`): a
                // sleep is where a script WAITS, so a cancelled run has to leave here too, otherwise a capture that sleeps
                // in a loop would outlive its token. Same exception as the interrupt above, so a script's own `finally`
                // runs and `RunScript` reports an ordinary refusal.
                if (Interpreter.RunCancellationRequested)
                {
                    throw new ScriptException(ErrorCode.Cancelled, "Stopped: the run was cancelled");
                }
                var chunk = Math.Min(millisec, 100);
                Thread.Sleep(chunk);
                // A SLEEPING RUNNER'S RUN KEEPS ITS WINDOWS ALIVE: `sleep` is where a captured UI script waits, and a
                // window nothing dispatches for is a frozen one (a caption drag never paints, a posted close never lands).
                // Same gate as the per-statement pump, so a plain run is untouched.
                Interpreter.NativeApp.PumpForWorkerRun();
                millisec -= chunk;
            }
            return null;
        });

        // Non-blocking stdin read: returns a ReadLineRequest for coroutine yield.
        // Usage inside a coroutine: var line = yield ReadLineAsync();
        RegisterNative("ReadLineAsync", TokenType.String, args =>
        {
            if (_currentCoroutine == null)
                return Console.ReadLine();  // blocking fallback outside coroutine

            return new ReadLineRequest();
        });

        RegisterNative("print", TokenType.Object, args =>
        {
            // The CURRENT interpreter's sink, so an embedded run prints into its own session (see Interpreter.Out).
            var writer = ScriptOut;
#if DEBUG
            if (_currentLine > 0)
                writer.Write($"[L{_currentLine:00000}] ");
#endif
            for (var i = 0; i < args.Length; i++)
            {
                if (i > 0)
                    writer.Write(' ');
                // The ONE display rule, shared with string concatenation: a `Type` reports the name a script can
                // write, everything else keeps its ToString (see ValueFormatter.ToDisplay).
                writer.Write(ValueFormatter.ToDisplay(args[i]));
            }
            writer.WriteLine();

            return null;
        });

        // Beep
        RegisterNative("beep", TokenType.Void, args =>
        {
            var freq = args.Length > 0 ? Convert.ToInt32(args[0]) : 800;   // Hz
            var duration = args.Length > 1 ? Convert.ToInt32(args[1]) : 200; // ms
            Console.Beep(freq, duration);
            return null;
        });

        // Pause variable, blocks until keypress, but auto-continues when stdin is redirected
        CurrentFrame.Scopes.Define("pause", new RefCell(
            Getter: () =>
            {
                if (Console.IsInputRedirected || Console.IsOutputRedirected)
                {
                    // Running as subprocess (stdin or stdout redirected), skip wait
                    return null;
                }
                var writer = Console.Out;
                ConsoleSafe.SetColor(ConsoleColor.Green);
                writer.Write("Press any key to continue...");
                //Console.Write("Press any key to continue...");
                Console.ReadKey(true);
                writer.WriteLine();
                ConsoleSafe.ResetColor();
                return null;
            },
            Setter: _ => { }
        ));

        // ExitCode variable with validation
        CurrentFrame.Scopes.Define("exitcode", new RefCell(
            Getter: () => Environment.ExitCode,
            Setter: val =>
            {
                if (IsNumeric(val))
                {
                    var longVal = Convert.ToInt64(val);
                    switch (longVal)
                    {
                        case > int.MaxValue:
                        case < int.MinValue:
                            Environment.ExitCode = -1;
                            break;
                        default:
                            Environment.ExitCode = (int)longVal;
                            break;
                    }
                }
                else
                    Environment.ExitCode = -2;
            }
        ));

        RegisterNative("abort", TokenType.Void, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("abort expects exactly one argument.");

            var exitCode = -2;
            if (IsNumeric(args[0]))
            {
                var longVal = Convert.ToInt64(args[0]);
                switch (longVal)
                {
                    case > int.MaxValue:
                    case < int.MinValue:
                        exitCode = -1;
                        break;
                    default:
                        exitCode = (int)longVal;
                        break;
                }
            }

            // Cancel in-flight cancellable work (e.g. Http calls) before unwinding.
            CancelProcessToken();
            // Environment.ExitCode is set by Interpret() when ScriptAbortException propagates uncaught
            throw new ScriptAbortException(exitCode);
        });

        RegisterNative("atexit", TokenType.Void, args =>
        {
            if (args.Length < 1)
                throw new ScriptException("atexit expects a function or array of functions.");

            // The statement that REGISTERS a handler is the position a handler failure names when the body names no
            // place of its own (see Interpreter.ReportUnobservableFailure): `CurrentLine`/`CurrentColumn` are the
            // statement being executed on both engines, which is the position every builtin reports from.
            var owner = Current;
            void AddHandler(object? arg)
            {
                if (arg is ScriptFunction fn)
                {
                    if (owner is not null) owner._exitHandlers.Add((fn, owner.CurrentLine, owner.CurrentColumn));
                }
                else if (arg is not null)
                    throw new ScriptException($"atexit expects a function, got '{arg.GetType().Name}'.");
            }

            if (args[0] is Array arr)
            {
                for (var i = 0; i < arr.Length; i++)
                    AddHandler(arr.GetValue(i));
            }
            else
            {
                AddHandler(args[0]);
            }
            return null;
        });

        RegisterNative("exit", TokenType.Void, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("exit expects exactly one argument.");

            var exitCode = -2;
            if (IsNumeric(args[0]))
            {
                var longVal = Convert.ToInt64(args[0]);
                switch (longVal)
                {
                    case > int.MaxValue:
                    case < int.MinValue:
                        exitCode = -1;
                        break;
                    default:
                        exitCode = (int)longVal;
                        break;
                }
            }

            // Cancel in-flight cancellable work (e.g. Http calls) before unwinding.
            CancelProcessToken();
            // Environment.ExitCode is set by Interpret() when ScriptAbortException propagates uncaught
            throw new ScriptAbortException(exitCode);
        });

        // NOT AOT compatible, Assembly.Load(byte[]) has no NativeAOT implementation, because dynamic
        // assembly loading needs the JIT runtime. On the SHIPPING target that must fail as a clear,
        // catchable SCRIPT error (this native is script-reachable and had no coverage at all), not as a
        // raw runtime exception surfacing through the interpreter. The JIT build behaves as before.
        RegisterNative("LoadAssembly", TokenType.Object, args =>
        {
            var path = args[0] as string ?? throw new ScriptException("Path required");
            // platform: capability-guard, dynamic assembly loading requires the JIT runtime, which the shipping AOT build genuinely does not have. The script-visible outcome must be a clear, catchable error rather than a raw runtime exception surfacing through the interpreter.
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                RuntimeError(
                    "LoadAssembly is not supported in a NativeAOT build: dynamic assembly loading requires the "
                    + "JIT runtime. Compile the types you need into the engine's application instead, or run the JIT "
                    + "build; embedding the engine in a host does not help, because a host cannot hand its own loaded "
                    + "types to a script.",
                    0, 0, ErrorCode.Unspecified);
                return null;
            }
            var assemblyBytes = File.ReadAllBytes(path);
            var assembly = Assembly.Load(assemblyBytes);

            return assembly;
        });

        RegisterNative("GetType", TokenType.Object, args =>
        {
            if (args[0] is Assembly asm && args[1] is string typeName)
                return asm.GetType(typeName);
            throw new ScriptException("Usage: GetType(assembly, typeName)");
        });

        RegisterNative("CreateInstance", TokenType.Object, args =>
        {
            if (args[0] is Type type)
                return Activator.CreateInstance(type);
            throw new ScriptException("Usage: CreateInstance(type)");
        });

        RegisterNative("Invoke", TokenType.Object, args =>
        {
            if (args.Length < 1)
                throw new ScriptException("Invoke requires at least one argument.");

            var first = args[0];

            // ---------- 1. MethodInfo (from previous reflection) ----------
            if (first is MethodInfo method)
            {
                // Determine instance for non‑static methods
                object? instance = null;
                var offset = 1;
                if (!method.IsStatic)
                {
                    if (args.Length < 2)
                        throw new ScriptException("Invoke requires an instance for non‑static methods.");
                    instance = args[1];
                    offset = 2;
                }
                var parameters = method.GetParameters();
                var methodArgs = new object?[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (offset + i < args.Length)
                        methodArgs[i] = CoerceTo(args[offset + i],
                            MapTypeInfo(parameters[i].ParameterType), 0, 0);
                    else if (parameters[i].IsOptional)
                        methodArgs[i] = parameters[i].DefaultValue;
                    else
                        throw new ScriptException($"Missing required argument for parameter '{parameters[i].Name}'.");
                }
                return method.Invoke(instance, methodArgs);
            }

            // ---------- 2. Script function or delegate wrapper ----------
            if (first is ScriptFunction || first is ScriptDelegateWrapper)
            {
                // The array's LENGTH IS THE CALL'S ARITY: the callee reads args.Length for its parameters and its
                // arity check, so the block must be EXACT. `ArrayPool.Rent` was wrong here for exactly that
                // reason, it returns an array at least as long as asked (16 for small sizes), and handing that
                // over turned ONE argument into ~16: the callee bound its missing parameters to the null tail and
                // reported nothing. Measured: `Invoke(Two, "x")` on a two-parameter function returned "[x][]",
                // where the same call written script-side refused. A pool is a buffer, not a view.
                var invokeArgs = new object?[args.Length - 1];
                Array.Copy(args, 1, invokeArgs, 0, invokeArgs.Length);
                return InvokeDelegate(first, invokeArgs);
            }

            // ---------- 3. .NET delegate ----------
            if (first is Delegate del)
            {
                // Exact length for the same reason as above, and here it also decides whether the call works at
                // all: `DynamicInvoke` requires the argument count to match the delegate's signature, so an
                // over-long pooled array was refused by the CLR rather than by us.
                var invokeArgs = new object?[args.Length - 1];
                Array.Copy(args, 1, invokeArgs, 0, invokeArgs.Length);
                return del.DynamicInvoke(invokeArgs);
            }

            // ---------- 4. Native function pointer (numeric) ----------
            if (IsNumeric(first))
            {
                var invokeArgs = args.Skip(1).Select(Convert.ToInt64).ToArray();
                return AotInvoker.InvokeCdeclPtr(ToNInt(first), invokeArgs);
            }

            // ---------- 5. NEW: Call a method on a .NET type or instance by name ----------
            if (args is not [_, string methodName, ..]) throw new ScriptException("Invalid arguments for Invoke.");
            {
                var target = first;
                var name = methodName;
                var arguments = args.Skip(2).ToArray();

                Type type;
                object? instance;
                if (target is Type t)
                {
                    type = t;
                    instance = null;
                }
                else if (target != null)
                {
                    type = target.GetType();
                    instance = target;
                }
                else
                {
                    throw new ScriptException("Target cannot be null.");
                }

                // Find a method with matching name and argument count
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    .Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                MethodInfo? best = null;
                foreach (var m in methods)
                {
                    if (m.GetParameters().Length == arguments.Length)
                    {
                        best = m;
                        break;
                    }
                }
                if (best == null)
                    throw new ScriptException(MissingMemberText(target, name, arguments.Length));

                var paramInfos = best.GetParameters();
                var converted = new object?[paramInfos.Length];
                for (var i = 0; i < paramInfos.Length; i++)
                {
                    converted[i] = CoerceTo(arguments[i], MapTypeInfo(paramInfos[i].ParameterType), 0, 0);
                }
                return best.Invoke(instance, converted);
            }

        });

        // RunScriptAsync(source [, concurrent]) -> the engine's Task, so a nested run can be awaited and composed
        // rather than blocking the caller's statement the way RunScript does. TWO forms, ONE flag:
        //   RunScriptAsync(src)        the run is PUMP WORK, performed on the caller's next pump pass (a wait or an
        //                              await), so nothing ever runs on another thread: the default, because this
        //                              engine's UI and COM paths assume the main thread;
        //   RunScriptAsync(src, true)  the run starts on a BACKGROUND thread the way Task.Run does: real concurrency
        //                              for a pure subject, with the documented Task.Run caveat for one that touches a
        //                              window or COM.
        RegisterNative("RunScriptAsync", TokenType.Object, args =>
        {
            var source = args[0] as string ?? throw new ScriptException("RunScriptAsync expects a script source string.");
            // A Task that is EXECUTED takes an optional CancellationToken, and a missing one means
            // CancellationToken.None: the rule every executing member here follows (Task.Run, Task.DelayAsync). The
            // concurrent flag stays because it is what picks the FORM, so the two are told apart by TYPE.
            var concurrent = false;
            var token = CancellationToken.None;
            foreach (var extra in args.Skip(1))
            {
                switch (extra)
                {
                    case bool flag: concurrent = flag; break;
                    case CancellationToken given: token = given; break;
                    default:
                        throw new ScriptException("RunScriptAsync takes the script source, an optional `concurrent` "
                            + "flag and an optional CancellationToken, and nothing else.");
                }
            }
            // A token that already fired means the run never starts and the task it would have produced is
            // cancelled, which is what Task.Run answers for the same argument.
            if (token.IsCancellationRequested) return Task.CanceledTask(token);
            var nested = new object?[] { source, false, false };
            if (concurrent)
            {
                // ONE task: the engine's Task.Run IS the answer, so nothing is discarded and there is no second
                // task to keep in step. It faults on a throw (measured), which is what surfaces a nested failure.
                var background = Task.Run(() => NestedRunOrThrow(CallBuiltinByName("RunScript", nested)));
                background.SetPumpDriver(PumpCoroutines);
                background.SetPumpWork(PumpWorkSignal, PumpNextDueMs);   // a nested run that `sleep`s is due by the clock
                return background;
            }
            var task = new Task();
            task.SetPumpDriver(PumpCoroutines);
            task.SetPumpWork(PumpWorkSignal, PumpNextDueMs);
            EnqueuePumpAction(() =>
            {
                try
                {
                    // The token is asked AGAIN here: a pump pass can be a while after the call, and a run that must
                    // not start says so LOUDLY rather than completing as if it had.
                    if (token.IsCancellationRequested)
                        task.SetException(new ScriptException("RunScriptAsync did not start: its CancellationToken was cancelled before the run could begin."));
                    else task.SetResult(NestedRunOrThrow(CallBuiltinByName("RunScript", nested)));
                }
                catch (Exception ex) { task.SetException(ex); }
            });
            return task;
        });
        RegisterNative("RunScript", TokenType.Object, args => RunScriptNative(args));
        object? RunScriptNative(object?[] args)
        {
            // A FOURTH ARGUMENT IS THE RUN'S CANCELLATION, and it is what turns a token into a KILL SIGNAL for this run:
            // the tree walker asks between statements, `sleep` between chunks and the VM's loop back-edge, so a
            // `cts.Cancel()` from ANOTHER THREAD stops the run where it is (before this, a token only kept a cancelled
            // run from starting). Installed on the thread that runs it, restored on the way out, and handed to the
            // shared body by recursion so the token is read in exactly one place.
            if (args.Length > 3 && args[3] is CancellationToken runToken)
            {
                var previousRun = ActiveRunCancellation;
                ActiveRunCancellation = runToken;
                try
                {
                    return RunScriptNative([args[0], args[1], args[2]]);
                }
                finally
                {
                    ActiveRunCancellation = previousRun;
                }
            }
            _runScriptDepth++;
            EnterExecutionUnit();   // a nested run is a unit of its own (see Interpreter.ExecutionUnit)
            try
            {
                var source = args[0] as string ?? throw new ScriptException("RunScript expects a string.");
                var isFilePath = args.Length > 1 && Convert.ToBoolean(args[1]);
                var preserveContext = args.Length > 2 && Convert.ToBoolean(args[2]);

                // A nested SOURCE TEXT is not something the reader of the outer file can see, so a diagnostic
                // raised inside it is attributed to THIS call rather than to a line of that text: measured, a
                // suite whose own line 1 is a comment printed a warning at L1:C8, which was the nested text's
                // line 1 (reported 2026-10-06). A nested FILE keeps its own positions, since it can be opened.
                using var attribution = isFilePath
                    ? (IDisposable?)null
                    : Logging.Log.Attribution(Interpreter.CurrentScriptLine, Interpreter.CurrentScriptColumn);

                var fileName = "in-memory";
                ReadOnlyMemory<char> sourceMem = default;
                char[]? fileBuf = null;
                if (isFilePath)
                {
                    // THE ONE PATH RULE, applied here as `File.*` and `import` already apply it: `./x` is BESIDE THE RUN,
                    // any other relative spelling is against the process directory. Measured before this: a nested
                    // `RunScript("./inner_src.slc", true)` answered "File not found" for a file sitting beside the
                    // running script, because the path went to `File.Exists` untouched while the process directory was
                    // somewhere else.
                    var resolved = (Current ?? this).ResolvePath(source);
                    if (!File.Exists(resolved))
                        return new ScriptResult { ExitCode = -1, HasError = true, ErrorMessage = $"File not found: {resolved}" };
                    // A COMPILED BUNDLE IS NOT SOURCE TEXT. Left to the lexer this reads `Unexpected character ''`, which
                    // names neither the cause nor the fix; the two doors that DO run a bundle are named instead.
                    try
                    {
                        using var head = File.OpenRead(resolved);
                        var magic = new byte[4];
                        if (head.Read(magic, 0, 4) == 4
                            && (BitConverter.ToInt32(magic, 0) == VmBytecodeCodec.ProgramMagic
                                || BitConverter.ToInt32(magic, 0) == VmBytecodeCodec.LibraryMagic))
                        {
                            return new ScriptResult
                            {
                                ExitCode = -1,
                                HasError = true,
                                ErrorMessage = $"RunScript: '{Path.GetFileName(resolved)}' is a compiled bundle, not source text. "
                                    + "Load it with `Bytecode.LoadScript(path)` and call the entry it answers, or use "
                                    + "`import \"x.slcbc\" as Alias` for a library bundle.",
                            };
                        }
                    }
                    catch (Exception) { /* an unreadable header leaves the lexer to report what it can */ }
                    source = resolved;
                    fileName = Path.GetFileName(resolved);
                    (sourceMem, fileBuf) = FileReader.ReadFile(resolved);
                }
                else
                {
                    sourceMem = source.AsMemory();
                }

                List<Stmt> stmts;
                try
                {
                    var lexer = new Lexer(sourceMem);
                    var tokens = lexer.GetTokens().ToArray();
                    var parser = new Parser(tokens, DelegateInfos, _caseSensitive);
                    // MATERIALIZED, because the same list is compiled and then interpreted: the parser hands back
                    // a lazy sequence, and walking it twice would re-run the parse work for the second reader.
                    stmts = parser.GetStatements().ToList();
                }
                catch (Exception ex)
                {
                    return new ScriptResult { ExitCode = -1, HasError = true, Cancelled = ex is ScriptException sce && sce.ErrorCode == ErrorCode.Cancelled, ErrorMessage = ex.Message };
                }

                FileReader.ReleaseBuffer(fileBuf);

                if (preserveContext)
                {
                    // Execute directly in the current interpreter's scope.
                    // All variables, functions, classes persist across RunScript calls.
                    var prevLastExpr = _lastExpressionValue;
                    _lastExpressionValue = null;

                    // COMPILED THE WAY A FILE RUN IS, which is what makes the same text behave the same whoever
                    // runs it. Without this a nested run left every function body to the TREE walker, so a shape
                    // that only the VM can finish (two awaits in one expression, a tail call, a loop that has to
                    // be fast) refused or crawled here and worked in a CLI run: measured with
                    // `probe-delay-wait.slc`, whose section 12 answered "72 passed" from the command line and
                    // refused with "'await' can be used once per statement" through this builtin.
                    Program.PrecompileFunctions(stmts, this);

                    try
                    {
                        // One-pass: execute in declaration order
                        foreach (var stmt in stmts)
                        {
                            ExecutionResult result;
                            _fileStmtAttempts++;   // a NESTED run's own attempt: it must not take the driver's suspension
                            try { result = Execute(stmt); }
                            finally { _fileStmtAttempts--; }
                            if (result.Flow == ControlFlow.Return)
                            {
                                _lastExpressionValue = result.Value;
                                break;
                            }
                        }

                        var returnValue = _lastExpressionValue;
                        _lastExpressionValue = prevLastExpr;
                        return new ScriptResult { ReturnValue = returnValue, ExitCode = 0 };
                    }
                    catch (ScriptException ex) when (AbortExitCode(ex) is int abortCode)
                    {
                        _lastExpressionValue = prevLastExpr;
                        // A script's own exit() is the run's VERDICT rather than an error, and AbortExitCode
                        // is the one place that question is asked: an abort rerolled by a call site (no
                        // longer a ScriptAbortException) used to fall into the error arm below and report
                        // the code as a MESSAGE ("hasError=true, aborted=false, msg=2").
                        return new ScriptResult { ExitCode = abortCode, Aborted = true, ReturnValue = _lastExpressionValue };
                    }
                    catch (ScriptException ex)
                    {
                        _lastExpressionValue = prevLastExpr;
                        var errorInfo = ex.Value is ScriptErrorInfo sei ? sei : new ScriptErrorInfo(ex.ErrorCode, ex.Message);
                        return new ScriptResult { HasError = true, Cancelled = ex.ErrorCode == ErrorCode.Cancelled, ErrorMessage = errorInfo.Message, ErrorInfo = errorInfo, ErrorLine = ex.Line, ErrorColumn = ex.Column, StackTrace = ex.StackTrace, ReturnValue = _lastExpressionValue };
                    }
                    catch (Exception ex)
                    {
                        _lastExpressionValue = prevLastExpr;
                        return new ScriptResult { HasError = true, ErrorMessage = ex.Message, StackTrace = ex.StackTrace };
                    }
                }
                else
                {
                    // Original isolated execution.
                    // A nested FILE is handed its FULL path so the nested run's own directory is the file's:
                    // "./x" then means "x beside the nested script", the rule a direct run already follows.
                    // Passing the bare name put the nested `_currentDirectory` at the PROCESS directory, so the
                    // same file resolved "./x" differently depending on who ran it (measured 2026-10-07).
                    using var subHandle = InterpreterManager.Create(_caseSensitive, context: "RunScript");
                    var sub = subHandle.Instance;
                    // COMPILED LIKE A FILE RUN (`Program.RunFile` is parse, `PrecompileFunctions`, `Interpret`),
                    // because the whole point of a nested file run is that the file behaves as it does from the
                    // command line. Measured before this: `probe-delay-wait.slc` answered "72 passed, 0 failed"
                    // as a CLI run and stopped at its section 12 through this builtin, with `VM: 0` in its own
                    // counters, i.e. no body had been compiled at all.
                    Program.PrecompileFunctions(stmts, sub);
                    return sub.Interpret(stmts, isFilePath ? source : fileName);
                }
            }
            catch (ScriptException ex) when (AbortExitCode(ex) is int abortCode)
            {
                return new ScriptResult { ExitCode = abortCode, Aborted = true };
            }
            catch (Exception ex)
            {
                return new ScriptResult { ExitCode = -1, HasError = true, Cancelled = ex is ScriptException sce && sce.ErrorCode == ErrorCode.Cancelled, ErrorMessage = ex.Message };
            }
            finally
            {
                _runScriptDepth--;
            }
        }

        RegisterNative("ClearPureCache", TokenType.Void, _ =>
        {
            _pureMemoCache.Clear();
            Console.WriteLine("[GC] Pure cache cleared.");
            return null;
        });

        RegisterNative("GetPureCacheSize", TokenType.Int, _ => _pureMemoCache.Count);

        RegisterNative("GetScriptDepth", TokenType.Int, _ => _runScriptDepth);

        RegisterNative("AssertEqual", TokenType.Bool, args =>
        {
            if (args.Length is < 3 or > 3)
                throw new ScriptException("AssertEqual requires 3 parameters (expected, actual, message).");

            var expect = args[0];
            var actual = args[1];
            var prompt = args[2];

            var equals = AreEqual(expect, actual);

            ConsoleSafe.SetColor(equals ? ConsoleColor.DarkGreen : ConsoleColor.DarkYellow);
            Console.WriteLine(equals
                ? $"PASS: {prompt}"
                : $"FAIL: {prompt} | Expected: {ValueFormatter.Format(expect)}, Got: {ValueFormatter.Format(actual)}");
            ConsoleSafe.ResetColor();

            return equals;
        });

        RegisterNative("AssertTrue", TokenType.Bool, args =>
        {
            if (args.Length is < 3 or > 3)
                throw new ScriptException("AssertTrue requires 3 parameters (condition, success message, failure message).");

            var equals = args[0] is true;
            var successprompt = args[1];
            var failureprompt = args[2];

            ConsoleSafe.SetColor(equals ? ConsoleColor.DarkGreen : ConsoleColor.DarkYellow);
            Console.WriteLine(equals
                ? $"PASS: {successprompt}"
                : $"FAIL: {failureprompt}");
            ConsoleSafe.ResetColor();

            return equals;
        });

        // ── Native Check family, the canonical battery assertion pattern as built-ins.
        // `Check` mirrors `void Check(string name, bool ok) { if (ok) { passed++; print("  ✅ " + name); }
        // else { failed++; print("  ❌ " + name); } }` with native counters; `CheckSummary` mirrors the
        // closing block. A script-defined `void Check(...)` redefinition shadows the native, so existing
        // batteries keep their local version unchanged (coexistence).
        RegisterNative("Check", TokenType.Bool, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("Check requires 2 parameters (label, ok).");

            var label = args[0] as string ?? "";
            var ok = args[1] is true;
            // THE RUNNING INTERPRETER OWNS THE COUNT, not the one that registered this native. A namespace member
            // captures the interpreter that REGISTERED it, and in a hosted session that is a different instance from
            // the one executing (measured: `current=12633937 this=39343880`), so counting on `this` left the run's own
            // verdict reporting zero checks while the ledger had the failure. The same rule `Com.Run`, `Parallel` and
            // the internal helpers follow, and it is what makes ONE instance hold both the counters and the ledger.
            var home = Current ?? this;

            ScriptOut.WriteLine(ok ? $"  ✅ {label}" : $"  ❌ {label}");
            if (ok) Interlocked.Increment(ref home._checkPassed);
            else
            {
                Interlocked.Increment(ref home._checkFailed);
                // THE LEDGER takes the label AND the position, so a run that ends with `exit(CheckFailed())` can say
                // what failed and where.
                home.RecordCheckFailure(label, home.CurrentLine, home.CurrentColumn);
            }

            return ok;
        });

        RegisterNative("CheckSummary", TokenType.Bool, _ =>
        {
            var home = Current ?? this;
            ScriptOut.WriteLine();
            var passed = Volatile.Read(ref home._checkPassed);
            var failed = Volatile.Read(ref home._checkFailed);
            ScriptOut.WriteLine($"═══ {passed} passed, {failed} failed ═══");
            if (failed == 0)
            {
                ScriptOut.WriteLine("  ✅ ALL TESTS PASSED");
                return true;
            }
            ScriptOut.WriteLine($"  ❌ {failed} TEST(S) FAILED");
            return false;
        });

        RegisterNative("CheckPassed", TokenType.Int, _ => Volatile.Read(ref (Current ?? this)._checkPassed));
        RegisterNative("CheckFailed", TokenType.Int, _ => Volatile.Read(ref (Current ?? this)._checkFailed));
        RegisterNative("CheckReset", TokenType.Void, _ =>
        {
            (Current ?? this).ResetCheckCounters();
            return null;
        });

        RegisterNative("TypeName", TokenType.StringType, args =>
        {
            if (args.Length == 0) return "null";
            return args[0]?.GetType().FullName ?? "null";
        });

        // ── CallNative, call a C function pointer, returns long (Int64) ──
        // String args → UTF-8 pointers (auto-freed after call).
        RegisterNative("CallNative", TokenType.Long, args =>
        {
            var fnPtr = Convert.ToInt64(args[0]);
            if (fnPtr == 0) throw new ScriptException("CallNative: null function pointer.");
            Span<long> nativeArgs = stackalloc long[args.Length - 1];
            var tempPtrs = new List<IntPtr>(args.Length);
            try
            {
                for (var i = 1; i < args.Length; i++)
                {
                    if (args[i] is string str)
                    {
                        var utf8 = Marshal.StringToCoTaskMemUTF8(str);
                        tempPtrs.Add(utf8);
                        nativeArgs[i - 1] = utf8.ToInt64();
                    }
                    else
                        nativeArgs[i - 1] = Convert.ToInt64(args[i]);
                }
                return AotInvoker.InvokeCdeclPtr(new IntPtr(fnPtr), nativeArgs) ?? 0L;
            }
            finally
            {
                foreach (var p in tempPtrs)
                    if (p != nint.Zero) Marshal.FreeCoTaskMem(p);
                tempPtrs.Clear();
            }
        });

        // ── CallNativeDbl, call a C function returning double ──
        RegisterNative("CallNativeDbl", TokenType.Double, args =>
        {
            var fnPtr = Convert.ToInt64(args[0]);
            if (fnPtr == 0) throw new ScriptException("CallNativeDbl: null function pointer.");
            Span<long> nativeArgs = stackalloc long[args.Length - 1];
            for (var i = 1; i < args.Length; i++)
                nativeArgs[i - 1] = Convert.ToInt64(args[i]);
            return AotInvoker.InvokeCdeclDouble(new IntPtr(fnPtr), nativeArgs) ?? 0.0;
        });

        // ── CallNativeStr, call a C function, read result as UTF-8 string ──
        // String args → UTF-8 pointers (auto-freed). Returns the function's
        // return pointer read as ANSI string (for sqlite3_column_text, etc.).
        RegisterNative("CallNativeStr", TokenType.StringType, args =>
        {
            var fnPtr = Convert.ToInt64(args[0]);
            if (fnPtr == 0) throw new ScriptException("CallNativeStr: null function pointer.");
            Span<long> nativeArgs = stackalloc long[args.Length - 1];
            var tempPtrs = new List<IntPtr>(args.Length);
            try
            {
                for (var i = 1; i < args.Length; i++)
                {
                    if (args[i] is string str)
                    {
                        var utf8 = Marshal.StringToCoTaskMemUTF8(str);
                        tempPtrs.Add(utf8);
                        nativeArgs[i - 1] = utf8.ToInt64();
                    }
                    else
                        nativeArgs[i - 1] = Convert.ToInt64(args[i]);
                }
                var result = AotInvoker.InvokeCdeclPtr(new IntPtr(fnPtr), nativeArgs) ?? 0L;
                return Marshal.PtrToStringAnsi(new IntPtr((long)result)) ?? "";
            }
            finally
            {
                foreach (var p in tempPtrs)
                    if (p != nint.Zero) Marshal.FreeCoTaskMem(p);
                tempPtrs.Clear();
            }
        });

        RegisterNativeHttp();
    }

    private void RegisterNativeHttp()
    {
        var httpNs = new Namespace(_comparer);

        //Func<string, string, string?, string> blockingCall = (method, url, body) =>
        //{
        //    try
        //    {
        //        var verb = method.ToUpperInvariant();

        //        Task<HttpResponseMessage> Send() => verb switch
        //        {
        //            "GET" => _httpClient.GetAsync(url),
        //            "DELETE" => _httpClient.DeleteAsync(url),
        //            "POST" => _httpClient.PostAsync(url, CreateContent(body)),
        //            "PUT" => _httpClient.PutAsync(url, CreateContent(body)),
        //            _ => throw new InvalidOperationException($"Unsupported method: {method}")
        //        };

        //        using var response = Task.Run(() => Send()).GetAwaiter().GetResult();
        //        response.EnsureSuccessStatusCode();

        //        using var reader = new StreamReader(response.Content.ReadAsStream());
        //        return reader.ReadToEnd();
        //    }
        //    catch (Exception ex)
        //    {
        //        return $"__HTTP_ERROR__:{ex.Message}";
        //    }
        //};



        // Register methods
        foreach (var verb in new[] { "Get", "Post", "Put", "Delete" })
        {
            var upperVerb = verb.ToUpperInvariant();

            httpNs.AddMethod(verb, args =>
            {
                if (args.Length < 1 || args[0] is not string url)
                    throw new ScriptException($"Http.{verb} expects a string URL.");

                var timeoutMs = 30000;
                var body = (string?)null;
                CancellationToken? explicitToken = null;

                // A CancellationToken is recognised by RUNTIME TYPE anywhere after the URL (the named
                // `token: ct` form arrives positionally for namespace methods). Everything else keeps
                // today's positional grammar: [body?] [, timeoutMs].
                var positional = new List<object?>(args.Length);
                for (var i = 1; i < args.Length; i++)
                {
                    if (args[i] is CancellationToken tk)
                    {
                        if (explicitToken != null)
                            throw new ScriptException($"Http.{verb}: only one CancellationToken is allowed.");
                        explicitToken = tk;
                        continue;
                    }
                    positional.Add(args[i]);
                }

                if (verb == "Get" || verb == "Delete")
                {
                    if (positional.Count > 0 && positional[0] is int to)
                        timeoutMs = to;
                }
                else
                {
                    body = positional.Count > 0 ? positional[0] as string : null;
                    if (positional.Count > 1 && positional[1] is int to)
                        timeoutMs = to;
                }

                // Yield if in coroutine, otherwise block
                return _currentCoroutine != null
                    ? new HttpRequest(upperVerb, url, body, timeoutMs, explicitToken ?? AmbientToken)
                    : BlockingCall(upperVerb, url, body, timeoutMs, explicitToken);
            });
        }

        // Http.Token, read-only ambient cancellation token (host request scope, else process).
        httpNs.AddDynamicProperty("Token", () => AmbientToken);
        // Http.LastErrorKind, cause of the most recent failure ("" after a success).
        httpNs.AddDynamicProperty("LastErrorKind", () => LastHttpErrorKind);

        // ── application/x-www-form-urlencoded ────────────────────────────────────────────────────────────────
        // A form decoder belongs to the engine because it does the WHOLE parse in one call: it splits the "&"/"="
        // pairs and decodes each component as one BYTE SEQUENCE ("%C3%A9" is "é", where a per-byte `(char)` cast
        // gives two characters nobody typed). A middleware can now reach `Uri.UnescapeDataString` directly, so the
        // reason is the shape of the job rather than what the pipeline can name.
        httpNs.AddMethod("DecodeForm", args => DecodeFormComponent(args.Length > 0 ? "" + args[0] : ""));
        httpNs.AddMethod("EncodeForm", args => EncodeFormComponent(args.Length > 0 ? "" + args[0] : ""));
        // The whole parse, so no app splits on "&" and the first "=" by hand. It answers a SCRIPT dictionary, whose
        // absent key is REFUSED rather than null (a raw CLR dictionary answers null, see the index cores), a
        // repeated field keeps its LAST value, and a pair with an empty key is skipped.
        httpNs.AddMethod("ParseForm", args =>
        {
            var body = args.Length > 0 ? "" + args[0] : "";
            var fields = new ScriptBoxedDictionary(
                new TypeInfo(TokenType.String, false, 0, 0, null),
                new TypeInfo(TokenType.String, false, 0, 0, null));
            foreach (var pair in body.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                fields.AddRaw(DecodeFormComponent(pair.Substring(0, eq)), DecodeFormComponent(pair.Substring(eq + 1)));
            }
            return fields;
        });

        // ── the BYTES doors, and both are genuinely asynchronous, so both carry the Async postfix ─────────────
        // Http.GetBytesAsync(url [, timeoutMs] [, token]): the body as BYTES. `Http.Get` answers a STRING, which is
        // wrong twice for a binary body: it decodes what is not text, and it buffers the whole thing. This reads
        // incrementally (`ResponseHeadersRead`) and answers the bytes.
        // Http.DownloadAsync(url, path [, timeoutMs] [, token]): the same transfer straight to a FILE, answering the
        // byte count, so a 500 MB download never passes through a script string.
        // Both answer the ENGINE's Task, so `await Http.DownloadAsync(...)` works, and a failure travels as that
        // task's exception: a `catch (var e)` around the await reads the engine's own sentence, and `Http.LastErrorKind`
        // carries the same kind `Http.Get` reports (network, timeout, cancelled).
        httpNs.AddMethod("GetBytesAsync", args =>
        {
            if (args.Length < 1 || args[0] is not string url)
                throw new ScriptException("Http.GetBytesAsync expects a string URL.");
            var (timeoutMs, token) = HttpTail("Http.GetBytesAsync", args, 1);
            return RunOffThread(() =>
            {
                var (bytes, _, kind, error) = SendHttpBytes(url, null, timeoutMs, token ?? default, AmbientToken);
                LastHttpErrorKind = kind;
                if (error.Length > 0) throw new ScriptException(ErrorCode.InvalidOperation, error);
                return bytes;
            });
        });
        httpNs.AddMethod("DownloadAsync", args =>
        {
            if (args.Length < 1 || args[0] is not string url)
                throw new ScriptException("Http.DownloadAsync expects a string URL.");
            if (args.Length < 2 || args[1] is not string destination || destination.Length == 0)
                throw new ScriptException("Http.DownloadAsync expects the file to write as its second argument.");
            var path = ResolvePath(destination);
            var (timeoutMs, token) = HttpTail("Http.DownloadAsync", args, 2);
            return RunOffThread(() =>
            {
                var (_, count, kind, error) = SendHttpBytes(url, path, timeoutMs, token ?? default, AmbientToken);
                LastHttpErrorKind = kind;
                if (error.Length > 0) throw new ScriptException(ErrorCode.InvalidOperation, error);
                return count;
            });
        });

        // The tail both BYTES doors share: [timeoutMs] [, token], the same grammar `Http.Get` documents, so a caller
        // moves between the three spellings without relearning the arguments.
        (int TimeoutMs, CancellationToken? Token) HttpTail(string member, object?[] args, int first)
        {
            var timeout = 30000;
            CancellationToken? token = null;
            for (var i = first; i < args.Length; i++)
            {
                if (args[i] is CancellationToken tk)
                {
                    if (token != null) throw new ScriptException($"{member}: only one CancellationToken is allowed.");
                    token = tk;
                }
                else if (args[i] is int ms) timeout = ms;
            }
            return (timeout, token);
        }

        CurrentFrame.Scopes.Define("Http", httpNs);

        // ── StrBuilder namespace ──
        var sbNs = new Namespace(_comparer);
        sbNs.AddMethod("Create", static args => new StringBuilder(args.Length > 0 ? Convert.ToInt32(args[0]) : 16));
        CurrentFrame.Scopes.Define("StrBuilder", sbNs);

        // ── WeakRef namespace ──
        var wrNs = new Namespace(_comparer);
        wrNs.AddMethod("Create", static args => args.Length > 0 ? new WeakRef(args[0]) : throw new ScriptException("WeakRef.Create expects an object argument."));
        CurrentFrame.Scopes.Define("WeakRef", wrNs);

        // ── Cipher namespace ──
        var cipherNs = new Namespace(_comparer);
        cipherNs.AddMethod("GenerateKey", static _ => Cipher.GenerateKey());
        cipherNs.AddMethod("GenerateRandomToken", static args => Cipher.GenerateRandomToken(args.Length > 0 ? Convert.ToInt32(args[0]) : 32));
        cipherNs.AddMethod("Encrypt", static args =>
        {
            if (args.Length < 2 || args[0] is not string data || args[1] is not string key)
                throw new ScriptException("Cipher.Encrypt expects (string data, string base64Key).");
            return Cipher.Encrypt(data, key);
        });
        cipherNs.AddMethod("Decrypt", static args =>
        {
            if (args.Length < 2 || args[0] is not string cipher || args[1] is not string key)
                throw new ScriptException("Cipher.Decrypt expects (string base64Cipher, string base64Key).");
            return Cipher.Decrypt(cipher, key);
        });
        // Post-quantum: ML-KEM (FIPS 203) and ML-DSA (FIPS 204), byt in and bytes out. `SlhDsa` (FIPS 205) is
        // deliberately NOT offered: it is `[Experimental("SYSLIB5006")]` in the runtime, and a script surface must
        // not rest on an API the platform says may be removed. See Cipher.cs for the measurement.
        cipherNs.AddMethod("KemGenerate", static args =>
        {
            if (args.Length < 1 || args[0] is not string algorithm)
                throw new ScriptException("Cipher.KemGenerate expects an algorithm name, as in Cipher.KemGenerate('ML-KEM-768').");
            return Cipher.KemGenerate(algorithm);
        });
        cipherNs.AddMethod("KemEncapsulate", static args =>
        {
            if (args.Length < 2 || args[0] is not byte[] publicKey || args[1] is not string algorithm)
                throw new ScriptException("Cipher.KemEncapsulate expects (byte[] encapsulationKey, string algorithm).");
            return Cipher.KemEncapsulate(publicKey, algorithm);
        });
        cipherNs.AddMethod("KemDecapsulate", static args =>
        {
            if (args.Length < 3 || args[0] is not byte[] privateKey || args[1] is not byte[] ciphertext || args[2] is not string algorithm)
                throw new ScriptException("Cipher.KemDecapsulate expects (byte[] decapsulationKey, byte[] ciphertext, string algorithm).");
            return Cipher.KemDecapsulate(privateKey, ciphertext, algorithm);
        });
        cipherNs.AddMethod("SignGenerate", static args =>
        {
            if (args.Length < 1 || args[0] is not string algorithm)
                throw new ScriptException("Cipher.SignGenerate expects an algorithm name, as in Cipher.SignGenerate('ML-DSA-65').");
            return Cipher.SignGenerate(algorithm);
        });
        cipherNs.AddMethod("Sign", static args =>
        {
            if (args.Length < 3 || args[0] is not byte[] privateKey || args[1] is not byte[] message || args[2] is not string algorithm)
                throw new ScriptException("Cipher.Sign expects (byte[] privateKey, byte[] message, string algorithm).");
            return Cipher.Sign(privateKey, message, algorithm);
        });
        cipherNs.AddMethod("Verify", static args =>
        {
            if (args.Length < 4 || args[0] is not byte[] publicKey || args[1] is not byte[] message || args[2] is not byte[] signature || args[3] is not string algorithm)
                throw new ScriptException("Cipher.Verify expects (byte[] publicKey, byte[] message, byte[] signature, string algorithm).");
            return Cipher.Verify(publicKey, message, signature, algorithm);
        });
        CurrentFrame.Scopes.Define("Cipher", cipherNs);
        return;

        // Refactored helper: Fixed switch syntax and improved exception handling
        // Blocking path: delegates to the shared helper so it classifies failures identically to the
        // coroutine path (cancelled / timeout / network) and records Http.LastErrorKind.
        string BlockingCall(string method, string url, string? body, int timeoutMs, CancellationToken? explicitToken)
        {
            var (text, kind) = SendHttp(method, url, body, timeoutMs, explicitToken ?? default, AmbientToken);
            LastHttpErrorKind = kind;
            return text;
        }
    }
}