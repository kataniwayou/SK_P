using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BaseApi.Core.Middleware;

/// <summary>
/// Reads or generates a per-request correlation ID, stashes it in
/// <c>HttpContext.Items["CorrelationId"]</c>, echoes it on the response
/// <c>X-Correlation-Id</c> header, and pushes it onto the MEL log scope.
///
/// <para>
/// <b>Pipeline placement (D-01):</b> registered AFTER <c>UseExceptionHandler</c>
/// so the IExceptionHandler chain (NotFound / Validation / DbUpdate / Fallback)
/// can read <c>HttpContext.Items["CorrelationId"]</c> when shaping ProblemDetails.
/// </para>
///
/// <para>
/// <b>Correlation format (D-02):</b> generated IDs use
/// <c>Guid.NewGuid().ToString("N")</c> — 32-char lowercase hex no dashes.
/// Inbound <c>X-Correlation-Id</c> values are echoed verbatim only if non-empty,
/// length ≤ 128, and ASCII-printable (0x20..0x7E) — see <see cref="IsValid"/>.
/// </para>
///
/// <para>
/// <b>Pitfall 3 mitigation (T-04-INJECT):</b> the ASCII-printable check rejects
/// CR/LF/null/control characters that would otherwise enable HTTP response-header
/// CRLF injection or fake-log-line injection via the echoed header value.
/// Invalid inbound values trigger a fresh <c>Guid.NewGuid</c> — NEVER fall back
/// to the unsafe value.
/// </para>
///
/// <para>
/// <b>Header echo timing (D-03 step 3):</b> uses
/// <c>Response.OnStarting(...)</c> rather than setting <c>Response.Headers</c>
/// directly after <c>await _next</c>. OnStarting fires deterministically before
/// headers flush, including on the exception path (when the IExceptionHandler
/// chain writes the response, OnStarting still fires). Direct assignment after
/// <c>_next</c> would race response commit on short-circuit paths.
/// </para>
///
/// <para>
/// <b>Log scope key (D-03 step 4):</b> dictionary key is the literal
/// <c>"CorrelationId"</c> (PascalCase) — matches what Phase 5's OTel
/// <c>IncludeScopes = true</c> will surface as a log attribute without renaming.
/// </para>
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";
    private const int MaxLength = 128;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var corrId = ResolveCorrelationId(context);

        // 1. Stash for downstream readers (IExceptionHandler chain, ProblemDetails customizer)
        context.Items[ItemKey] = corrId;

        // 2. Echo header on the way out — OnStarting fires before headers flush even on exception path
        //    (Pitfall 5: direct assignment, not Append — avoids duplicate headers)
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = corrId;
            return Task.CompletedTask;
        });

        // 3. Push log scope (PascalCase key matches Phase 5 OTel IncludeScopes=true serialization)
        using var scope = _logger.BeginScope(
            new Dictionary<string, object> { [ItemKey] = corrId });

        await _next(context);
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var header)
            && header.Count > 0
            && IsValid(header[0]))
        {
            return header[0]!;
        }
        return Guid.NewGuid().ToString("N");  // D-02: 32-char lowercase hex no dashes
    }

    private static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength) return false;
        foreach (var c in value)
        {
            // ASCII-printable only (0x20..0x7E) — rejects CR, LF, null, control chars
            // Mitigates Pitfall 3 (CRLF injection) and log injection (T-04-INJECT)
            if (c < 0x20 || c > 0x7E) return false;
        }
        return true;
    }
}
