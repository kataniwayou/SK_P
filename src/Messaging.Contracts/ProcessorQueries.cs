namespace Messaging.Contracts;

// RPC-01: identity-by-source-hash. Found fields are a direct copy of ProcessorReadDto's
// { Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId? } plus the DB Name/Version — the single
// source of truth for the processor's {Name}_{Version} metric identity (MLBL-03). Dual-response
// (found/not-found) lets the Phase 26 client pattern-match cleanly (D-04).
public sealed record GetProcessorBySourceHash(string SourceHash);
public sealed record ProcessorIdentityFound(
    Guid Id, Guid? InputSchemaId, Guid? OutputSchemaId, Guid? ConfigSchemaId,
    string Name, string Version);
public sealed record ProcessorIdentityNotFound(string SourceHash);

// RPC-02: schema-definition-by-id. Read is by schema Id (Guid) via BaseService.GetByIdAsync (RESEARCH A1).
public sealed record GetSchemaDefinition(Guid SchemaId);
public sealed record SchemaDefinitionFound(string Definition);
public sealed record SchemaDefinitionNotFound(Guid SchemaId);
