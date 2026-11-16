namespace ScriptLangCore;

internal partial class Interpreter
{
    public readonly struct ScriptResult
    {
        public ScriptResult() { }

        public int ExitCode { get; init; }
        public bool HasError { get; init; }
        public bool Aborted { get; init; }
    /// <summary>THE RUN WAS STOPPED BY ITS TOKEN, which is a runner's kill: `RunScript(source, isFile, preserve,
    /// token)` cancelled from another thread ends with this set (and <see cref="HasError"/> too, because the run did not
    /// finish). Its own member rather than a code to compare, for the reason <see cref="Aborted"/> has one: a caller
    /// says `result.Cancelled` instead of knowing that <c>ErrorInfo.Code == ErrorCode.Cancelled</c>, and the two can
    /// never drift because the engine sets both from the same exception.</summary>
    public bool Cancelled { get; init; }
        public int HResult { get; init; }
        public string? ErrorMessage { get; init; }
        public ScriptErrorInfo? ErrorInfo { get; init; }
        public int ErrorLine { get; init; }
        public int ErrorColumn { get; init; }
        public string? StackTrace { get; init; }
        public object? ReturnValue { get; init; }

        // Null-safe typed access: matches the return value against T; returns
        // default (null for reference types / Nullable-default for value types)
        // when the value is null or not a T. Never throws.
        public T As<T>()
        {
            if (ReturnValue is T v) return v;
            return default!;
        }

        // Strict typed access following the engine's native-method error pattern:
        // on a type mismatch it throws ScriptException(TypeMismatch) instead of a
        // generic InvalidCastException, so callers can catch it uniformly (Code,
        // Message) in host code and inside scripts.
        public T Return<T>()
        {
            if (ReturnValue is T v) return v;
            throw new ScriptException(ErrorCode.TypeMismatch,
                $"script returned '{ReturnValue?.GetType().Name ?? "null"}', expected '{typeof(T).Name}'.");
        }
    }


    /// <summary>ONE CHECK THAT FAILED, with the position it was reported at. The Check family keeps these so a run can
    /// say WHY it ended with a non-zero code: `exit(CheckFailed())` is the script's OWN verdict, so a host that reads
    /// the status alone learns the code and nothing else, which is exactly as weak as it looks.
    /// A pass is not recorded (the counter holds it), a failure is, and the label is the whole point: it is the text
    /// the author wrote in `Check(...)`.</summary>
    internal readonly record struct CheckFailure(string Label, int Line, int Column);

    /// <summary>WHAT THE CHECK FAMILY SAW DURING ONE RUN: the counts, and the checks that failed with their
    /// positions. A run that reported no check has no report at all, so a plain script's verdict gains nothing.</summary>
    internal sealed class CheckReport(int passed, int failed, IReadOnlyList<CheckFailure> failures, int more)
    {
        public int Passed { get; } = passed;

        public int Failed { get; } = failed;

        /// <summary>The failures this run reported, in the order they happened.</summary>
        public IReadOnlyList<CheckFailure> Failures { get; } = failures;

        /// <summary>The failures the ledger's cap dropped, so <see cref="Failed"/> and the list cannot be read as
        /// contradicting each other (a loop of failing checks stays bounded in a verdict).</summary>
        public int More { get; } = more;

        /// <summary>The same facts as a document, which is the ONE shape both doors carry (the verdict's `Checks`
        /// member and the run's own answer when it left none), serialized by the engine's serializer. Its member names
        /// are PascalCase like every other JSON this engine writes (`ScriptResult`, a form document, a dictionary key
        /// a script sees), which is the house pattern for a member a host reads.</summary>
        public Dictionary<string, object?> ToDocument()
        {
            var doc = new Dictionary<string, object?>(4)
            {
                ["Passed"] = Passed,
                ["Failed"] = Failed,
            };
            if (Failures.Count > 0)
            {
                var rows = new List<object?>(Failures.Count);
                foreach (var failure in Failures)
                {
                    rows.Add(new Dictionary<string, object?>(3)
                    {
                        ["Label"] = failure.Label,
                        ["Line"] = failure.Line,
                        ["Column"] = failure.Column,
                    });
                }
                doc["Checks"] = rows;
            }
            if (More > 0) doc["More"] = More;
            return doc;
        }
    }

    /// <summary>WHERE the ledger stood when a run began, so its report describes THAT run: a hosted session keeps the
    /// interpreter across runs (only the CLI zeroes the counters per file), so a report computed from the totals would
    /// describe every run so far.</summary>
    internal readonly record struct CheckSnapshot(int Passed, int Failed, int Failures, int More);
}
