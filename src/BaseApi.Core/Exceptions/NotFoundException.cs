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

    public NotFoundException(string resourceType, object id)
        : base($"{resourceType} with id '{id}' was not found.")
    {
        ResourceType = resourceType;
        Id = id;
    }
}
