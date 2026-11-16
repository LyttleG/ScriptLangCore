namespace ScriptLangCore;

/// <summary>The page this engine serves for a status the FRAMEWORK set (a 404 where no route, file or middleware
/// produced an answer, a 500 from an unhandled failure), which is what IIS ships and what a browser renders instead of
/// a blank document.
///
/// It is a DEFAULT and not a rule: a program replaces it per code with `http.ErrorPage(code, ...)`, and a status that
/// must carry no body at all (204, 304) never reaches here. One implementation for every writer: the MCP server's
/// HttpListener path and the engine's own socket server both answer a framework-set status with this.</summary>
internal static class HttpErrorPages
{
    /// <summary>What a default page is served as. A program's own page may say otherwise (a JSON buffer, for one).</summary>
    internal const string ContentType = "text/html; charset=utf-8";

    /// <summary>The default document for a status: the status itself in the title and the heading, and one sentence
    /// naming the KIND of failure, so a reader knows whether to fix a URL or report a bug.</summary>
    internal static string Default(int status, string reason)
    {
        var sentence = status switch
        {
            400 => "The request could not be understood by the server.",
            401 => "This resource needs authentication.",
            403 => "The server understood the request and refuses it.",
            404 => "The requested resource was not found.",
            405 => "The method is not allowed for this resource.",
            500 => "The request could not be completed because of a server error.",
            _ => "The server answered this request itself."
        };
        return "<!doctype html>\n<meta charset=\"utf-8\">\n"
             + $"<title>{status} {reason}</title>\n"
             + $"<h1>{status} {reason}</h1>\n"
             + $"<p>{sentence}</p>\n";
    }

    /// <summary>The BYTES and the content type to answer a framework-set status with: the page the running session
    /// registered for that code with `http.ErrorPage`, or the engine's own default above. The MCP server's interpreter
    /// is the session that owns a registered page (it is the only place a script can register one), and BOTH HTTP
    /// writers ask here, so the socket server and the HttpListener path cannot answer a framework-set status
    /// differently.</summary>
    internal static (byte[] Body, string ContentType) Resolve(int status, string reason)
    {
        var registered = McpServer.CurrentHttpInterpreter?.ErrorPageFor(status, reason, null);
        var (text, type) = registered ?? (Default(status, reason), ContentType);
        return (Encoding.UTF8.GetBytes(text), type);
    }
}
