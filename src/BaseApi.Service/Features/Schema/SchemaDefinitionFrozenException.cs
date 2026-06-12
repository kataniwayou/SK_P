namespace BaseApi.Service.Features.Schema;

/// <summary>
/// Thrown by <c>SchemaService.UpdateAsync</c> when an attempt is made to mutate the <c>Definition</c>
/// of a schema that is referenced by a processor (frozen-once-referenced — CFG-10 / D-06). Claimed by
/// <see cref="SchemaDefinitionFrozenExceptionHandler"/> → HTTP 409 Conflict + RFC-7807. Carries ONLY the
/// schema Guid (information-disclosure guard, mirrors <c>NotFoundException</c> IN-02).
/// </summary>
public sealed class SchemaDefinitionFrozenException : Exception
{
    public Guid SchemaId { get; }

    public SchemaDefinitionFrozenException(Guid schemaId)
        : base($"Schema '{schemaId}' is referenced by a processor; its Definition cannot be modified.")
        => SchemaId = schemaId;
}
