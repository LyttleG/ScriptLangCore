namespace ScriptLangCore;

/// <summary>One `http.Map*` registration: the HTTP method, the pattern split into segments, and the script value to
/// call when a request matches.</summary>
/// <param name="Handler">A function, or a method taken off an instance (`app.Handle`), which is a <c>BoundMethod</c>
/// rather than a <c>ScriptFunction</c>.</param>
/// <param name="Source">The file that registered it, so a duplicate can name WHO already owns the pattern: measured on a
/// live session, `http.MapGet("/")` from a board read only "the FIRST registration answers and this one never will",
/// while the thing that owned `/` was the engine's own `mcp_httpserver.slc`.</param>
public sealed record HttpRoute(string Method, string[] Segments, object? Handler, string? Source = null);

/// <summary>The route table the `http.MapGet`/`MapPost`/`MapPut`/`MapDelete` members fill, and the match itself.
///
/// It is in the ENGINE rather than in a script helper for the reason `Http.ParseForm` is: the two things a
/// hand-rolled router gets wrong are exactly these, splitting a pattern into segments (with the query string and a
/// trailing slash off) and deciding which pattern a request matches, and a router that answers the wrong route is
/// the kind of defect a whole app is then built on.
///
/// A PATTERN IS WRITTEN WITHOUT A LEADING SLASH and a segment is either LITERAL (`"favicon.ico"`, `"stats/fragment"`)
/// or a `{name}` WILDCARD that captures one non-empty segment. There is deliberately no optional segment and no
/// type constraint here: the captured value is the segment's TEXT, and the handler converts what it needs (that is
/// what `Convert.ToInt32(args["id"])` says, where ASP.NET writes `int id`).</summary>
public sealed class HttpRouteTable
{
    public List<HttpRoute> Routes { get; } = [];

    /// <summary>Every table this PROCESS has built, so a 404 can name what a session DOES answer. The pipeline holds
    /// these tables alive through its middlewares anyway, so a static list adds no lifetime.</summary>
    private static readonly List<HttpRouteTable> Live = [];

    public HttpRouteTable() => Live.Add(this);

    /// <summary>The files whose `http.Map*` calls registered into this table, in first-registration order. It is a SET
    /// rather than one name because `http`'s members are registered ONCE, on the interpreter that BUILT the namespace,
    /// so a later `.run` registers into that same table from a different file (measured: the engine's sample tool and
    /// the htmx board share one table, and naming only the first would send a reader to the wrong file).</summary>
    public List<string> Sources { get; } = [];

    /// <summary>Records a registering file, once.</summary>
    public void NoteSource(string? source)
    {
        if (string.IsNullOrEmpty(source) || Sources.Contains(source)) return;
        Sources.Add(source);
    }

    /// <summary>One sentence naming what every table in this process serves, or "" when there are no routes at all.
    /// It exists for the 404 message: measured on a live session, a request to a route the file had not registered
    /// read only "nothing is registered for '/todos'", while the truth was that the session served six routes from a
    /// DIFFERENT file (the engine's sample tool, which `--tools-dir` had left loaded), which the list says at once.
    /// A pattern two files registered is listed ONCE, because only the first one answers.
    /// </summary>
    public static string DescribeLive(int maxPatterns = 10)
    {
        var seen = new List<string>();
        var sources = new List<string>();
        var hidden = 0;
        foreach (var table in Live.ToArray())
        {
            var routes = table.Routes.ToArray();
            if (routes.Length == 0) continue;
            foreach (var source in table.Sources)
            {
                if (!sources.Contains(source)) sources.Add(source);
            }
            foreach (var route in routes)
            {
                var pattern = route.Segments.Length == 0 ? "/" : string.Join('/', route.Segments);
                var key = $"{route.Method} {pattern}";
                if (seen.Contains(key)) continue;
                if (seen.Count >= maxPatterns) { hidden++; continue; }
                seen.Add(key);
            }
        }
        if (seen.Count == 0) return "";

        var text = " This session serves " + string.Join(", ", seen);
        if (hidden > 0) text += $", and {hidden} more";
        if (sources.Count > 0) text += $", from {string.Join(" and ", sources)}";
        return text + ".";
    }

    public void Add(string method, string pattern, object? handler, string? source = null) =>
        Routes.Add(new HttpRoute(method, SegmentsOf(pattern), handler, source));

    /// <summary>The route that OWNS a pattern (the first one registered for it, which is the one that answers), or null.
    /// It is what lets a duplicate name the file to go and look at.</summary>
    public HttpRoute? OwnerOf(string method, string pattern, StringComparison comparison)
    {
        var wanted = SegmentsOf(pattern);
        foreach (var route in Routes)
        {
            if (!string.Equals(route.Method, method, comparison)) continue;
            if (SamePattern(route.Segments, wanted, comparison)) return route;
        }
        return null;
    }

    /// <summary>How many routes this table ALREADY holds for the same method and pattern, compared the way a MATCH
    /// is rather than byte for byte: a pattern is normalized through <see cref="SegmentsOf"/> first (so `"/stats/"`,
    /// `"stats"` and `"//stats"` are one pattern), the method and a LITERAL segment follow the interpreter's rule, and
    /// two WILDCARD segments are the same slot whatever they are named.
    ///
    /// WHY IT EXISTS: the table matches in REGISTRATION ORDER, so a pattern it already holds is a route that will
    /// NEVER run. That is not an error (re-running a file is legitimate and re-adds equivalent routes), which is why
    /// the caller reports it rather than this refusing it, and why the count is answered instead of a replacement
    /// being performed.</summary>
    public int CountSame(string method, string pattern, StringComparison comparison)
    {
        var wanted = SegmentsOf(pattern);
        var count = 0;
        foreach (var route in Routes)
        {
            if (!string.Equals(route.Method, method, comparison)) continue;
            if (SamePattern(route.Segments, wanted, comparison)) count++;
        }
        return count;
    }

    /// <summary>Is this route's segment list the same PATTERN as that one, by the rule above: same length, a slot
    /// matching a slot, a literal matching itself. One implementation, asked by <see cref="CountSame"/> and
    /// <see cref="OwnerOf"/>, so the duplicate warning and its count cannot disagree about what "same" means.</summary>
    private static bool SamePattern(string[] mine, string[] wanted, StringComparison comparison)
    {
        if (mine.Length != wanted.Length) return false;
        for (var i = 0; i < wanted.Length; i++)
        {
            var a = mine[i];
            var b = wanted[i];
            var aIsSlot = a.Length > 2 && a[0] == '{' && a[^1] == '}';
            var bIsSlot = b.Length > 2 && b[0] == '{' && b[^1] == '}';
            if (aIsSlot != bIsSlot) return false;
            if (!aIsSlot && !string.Equals(a, b, comparison)) return false;
        }
        return true;
    }

    /// <summary>The request's path, without its query string: a route is matched on the PATH, and `ctx.Url` carries
    /// the query.</summary>
    public static string PathOf(string url)
    {
        var q = url.IndexOf('?');
        return q < 0 ? url : url[..q];
    }

    /// <summary>A pattern or a path as its NON-EMPTY segments, so a trailing slash, a doubled slash and an empty
    /// string all mean the same thing: `"/"` and `""` are BOTH the root, and `"/stats/"` is `"/stats"`.</summary>
    public static string[] SegmentsOf(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The FIRST route that matches, in REGISTRATION ORDER, with its `{name}` segments captured by name.
    /// The method and the literal segments are compared with the interpreter's own rule (<paramref
    /// name="comparison"/>), so `.lax` is lax here too and the default is C#'s strict comparison, while a `{name}`
    /// segment matches any one segment whatever it holds.</summary>
    /// <param name="argComparer">The comparer the captured names are stored with: it follows the interpreter's case
    /// flag like every other name a script writes.</param>
    public bool TryMatch(string method, string url, StringComparison comparison,
        IEqualityComparer<string>? argComparer,
        out object? handler, out Dictionary<string, object?> routeArgs)
    {
        var segments = SegmentsOf(PathOf(url));
        foreach (var route in Routes)
        {
            if (!string.Equals(route.Method, method, comparison)) continue;
            if (route.Segments.Length != segments.Length) continue;

            Dictionary<string, object?>? captured = null;
            var matched = true;
            for (var i = 0; i < segments.Length; i++)
            {
                var wanted = route.Segments[i];
                if (wanted.Length > 2 && wanted[0] == '{' && wanted[^1] == '}')
                {
                    captured ??= new Dictionary<string, object?>(argComparer ?? StringComparer.Ordinal);
                    captured[wanted[1..^1]] = segments[i];
                }
                else if (!string.Equals(wanted, segments[i], comparison))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched) continue;
            handler = route.Handler;
            routeArgs = captured ?? new Dictionary<string, object?>(argComparer ?? StringComparer.Ordinal);
            return true;
        }

        handler = null;
        routeArgs = new Dictionary<string, object?>(argComparer ?? StringComparer.Ordinal);
        return false;
    }
}
