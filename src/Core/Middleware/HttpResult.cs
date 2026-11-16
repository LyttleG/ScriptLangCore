namespace ScriptLangCore;

/// <summary>The answer a route handler returns when the STATUS, the content type or the BYTES are its own business.
/// `http.Text`, `Html`, `Json`, `Status`, `NotFound` and `Created` build one, and the route middleware applies it to
/// the request's context.
///
/// It is this engine's counterpart of ASP.NET's `IResult`, and the three cases a handler's return covers are the
/// same ones:
///   * a returned STRING is the body, written as `text/plain` (the server's default for a text body);
///   * a returned <c>HttpResult</c> decides its own content type, and its own status;
///   * any OTHER object is serialized as `application/json` (a `{ }` literal, a script class, a list), and a
///     `byte[]` is sent as bytes rather than as a JSON array of numbers.
/// A handler that answers through the context (or a void one) returns nothing, and a matched route with nothing in
/// it is still an answer: 200 with an empty body, where a request NOTHING answered is the server's own 404.
///
/// There is deliberately no `Ok` alongside `Status(200, ...)`: one behaviour, one spelling, which is this
/// repository's rule for a member a replacement already covers. `TypedResults` has no counterpart either, and that
/// is structural rather than an omission: what makes it "typed" is a compile-time return type, which a dynamically
/// typed script does not have.
///
/// The FIFTH case is a STREAM: `http.StreamLines` and `http.StreamEvents` answer one, carrying what to stream rather
/// than bytes, and the server writes it with chunked framing because its length is not known when it starts. A
/// stream is exclusive with `Body` and `Bytes`, which is why it is one more optional slot on the same record instead
/// of a second result type: the route middleware has ONE answer to interpret, and a result cannot then say two
/// things at once.</summary>
public sealed record HttpResult(
    int Status,
    string? ContentType,
    string? Body,
    byte[]? Bytes,
    IReadOnlyDictionary<string, string>? Headers,
    HttpStream? Stream = null)
{
    public const string TextType = "text/plain; charset=utf-8";
    public const string HtmlType = "text/html; charset=utf-8";
    public const string JsonType = "application/json; charset=utf-8";
    public const string BytesType = "application/octet-stream";

    /// <summary>An Avro Object Container File, which is `http.Avro`'s answer: the SAME type
    /// `AvroConvert`'s HTTP layer uses, so a client written against that library reads this one. The body is a
    /// container, so it carries its own schema and its own codec and a reader needs nothing else to decode it.</summary>
    public const string AvroType = "application/avro";
}
