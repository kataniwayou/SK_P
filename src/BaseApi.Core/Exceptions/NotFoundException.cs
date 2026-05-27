namespace BaseApi.Core.Exceptions;

/// <summary>
/// Thrown by service-layer code when a lookup by ID returns no row.
///
/// <para>
/// <b>Mapping (D-06 #1 / ERROR-06):</b> claimed by <c>NotFoundExceptionHandler</c>
/// in the IExceptionHandler chain — produces HTTP 404 with
/// <c>detail = Message</c> AND <c>ProblemDetails.Extensions["resourceType"]</c>
/// + <c>ProblemDetails.Extensions["resourceId"]</c> for clients that want to
/// branch programmatically.
/// </para>
///
/// <para>
/// <b>Usage (Phase 7/8 services):</b>
/// <code>throw new NotFoundException("Schema", id);</code>
/// </para>
/// </summary>
public sealed class NotFoundException : Exception
{
    public string ResourceType { get; }
    public object Id { get; }

    /// <param name="resourceType">Human-readable resource type name (e.g., "Schema").</param>
    /// <param name="id">
    /// The identifier of the missing resource. This value is included verbatim in the
    /// HTTP 404 response body (<c>detail</c> and <c>resourceId</c> extension field).
    /// Pass only safe, client-visible identifiers (e.g., <see cref="Guid"/>, numeric id)
    /// — NEVER raw DB keys, file paths, or user-supplied strings. Enforced by Phase 8
    /// service code review (IN-02).
    /// </param>
    public NotFoundException(string resourceType, object id)
        : base($"{resourceType} with id '{id}' was not found.")
    {
        ResourceType = resourceType;
        Id = id;
    }
}
