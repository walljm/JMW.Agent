namespace JMW.Discovery.Server.Api;

/// <summary>
/// The single seam for the API's error responses. Every failure emits an RFC 7807
/// <c>application/problem+json</c> body via
/// <see
///     cref="Results.Problem(string, string, int?, string, string, System.Collections.Generic.IDictionary{string, object})" />
/// ,
/// carrying the framework-standard <c>type/title/status/detail</c> plus a machine-readable
/// <c>code</c> extension member (e.g. <c>"invalid_cursor"</c>, <c>"not_found"</c>) that programmatic
/// clients can switch on. Centralizing it here keeps the contract consistent and makes the
/// "no raw SQL / internals in responses" rule enforceable in one place (review D9). The named
/// shortcuts pin each code to its canonical status.
/// </summary>
public static class ApiError
{
    /// <summary>Builds a ProblemDetails response with an explicit status, machine code, and human detail.</summary>
    public static IResult Problem(int status, string code, string message) =>
        Results.Problem(
            detail: message,
            statusCode: status,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
            }
        );

    /// <summary>422 — the pagination cursor could not be decoded.</summary>
    public static IResult InvalidCursor() =>
        Problem(422, "invalid_cursor", "Invalid pagination cursor.");

    /// <summary>404 — the requested resource does not exist.</summary>
    public static IResult NotFound(string message) => Problem(404, "not_found", message);

    /// <summary>400 — a supplied identifier was malformed.</summary>
    public static IResult InvalidId(string message) => Problem(400, "invalid_id", message);

    /// <summary>400 — the request was otherwise invalid.</summary>
    public static IResult InvalidRequest(string message) => Problem(400, "invalid_request", message);

    /// <summary>400 — the request body was missing or unparseable.</summary>
    public static IResult InvalidBody(string message) => Problem(400, "invalid_body", message);

    /// <summary>409 — the request conflicts with current state.</summary>
    public static IResult Conflict(string message) => Problem(409, "conflict", message);
}