using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using BaseApi.Core.Persistence;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Tests.Composition;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Features.Schema;

/// <summary>
/// Phase 57 CFG-10 / ROADMAP SC-5 — the frozen-once-referenced TOCTOU gate integration facts.
///
/// <para>
/// TOCTOU mechanism (records the SC-5 choice): <b>frozen-once-referenced</b> — a schema's
/// <c>Definition</c> locks the moment any <c>ProcessorEntity</c> FK
/// (<c>ConfigSchemaId</c>/<c>InputSchemaId</c>/<c>OutputSchemaId</c>) references it; enforced in
/// <c>SchemaService.UpdateAsync</c> → <c>SchemaDefinitionFrozenException</c> → 409 (D-05/D-06/D-08).
/// Editing a referenced schema's body requires creating a new id + re-pointing. <c>Name</c>/
/// <c>Description</c> stay editable (D-07); an unreferenced draft is freely editable until referenced
/// (D-06). This closes the window between startup Gate A and orchestration-start Gate B by construction.
/// </para>
///
/// <para>
/// RED state (Plan 57-01): the freeze override does NOT exist yet (Plan 57-04 lands it), so today a
/// referenced-schema <c>Definition</c> edit returns 200 — <c>Frozen_Definition_Mutation_Returns_409</c>
/// FAILS (gets 200, not 409). The file COMPILES (it references only existing types: SchemaCreateDto,
/// SchemaUpdateDto, Phase8WebAppFactory, ProcessorEntity, BaseDbContext, the schemas route), satisfying
/// Nyquist for the Plan-04 task.
/// </para>
/// </summary>
public sealed class SchemaDefinitionFreezeFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public SchemaDefinitionFreezeFacts(Phase8WebAppFactory factory) => _factory = factory;

    private const string DefinitionA =
        "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}";

    private const string DefinitionB =
        "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"integer\"}}}";

    private static SchemaCreateDto NewSchema(string definition) => new(
        Name: $"Freeze-{Guid.NewGuid():N}",
        Version: "1.0.0",
        Description: "freeze-fact",
        Definition: definition);

    /// <summary>POST a schema, return its new Id.</summary>
    private static async Task<Guid> PostSchemaAsync(HttpClient client, string definition, CancellationToken ct)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", NewSchema(definition), ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var read = await resp.Content.ReadFromJsonAsync<SchemaReadDto>(ct);
        Assert.NotNull(read);
        return read!.Id;
    }

    /// <summary>Seed a ProcessorEntity whose ConfigSchemaId references the given schema id (D-06 reference).</summary>
    private async Task SeedReferencingProcessorAsync(Guid configSchemaId, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BaseDbContext>();
        // Unique 64-char lowercase-hex SourceHash per processor — the uq_processor_source_hash unique
        // index (PERSIST-14) rejects duplicates; two facts share the test DB, so reusing a constant
        // hash would 23505 on the second insert (Rule 1 — keep the seed from clashing).
        var sourceHash = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).ToLowerInvariant();
        db.Set<ProcessorEntity>().Add(new ProcessorEntity
        {
            Name = $"proc-{Guid.NewGuid():N}",
            Version = "1.0.0",
            SourceHash = sourceHash,
            ConfigSchemaId = configSchemaId,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// CFG-10 / D-08 — mutating a frozen (referenced) schema's Definition is rejected with 409 + RFC-7807.
    /// RED until Plan 04: today the edit returns 200.
    /// </summary>
    [Fact]
    public async Task Frozen_Definition_Mutation_Returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var id = await PostSchemaAsync(client, DefinitionA, ct);
        await SeedReferencingProcessorAsync(id, ct);

        var update = new SchemaUpdateDto("Freeze-Renamed", "1.0.0", "edited", DefinitionB);
        client.DefaultRequestHeaders.Remove("X-Correlation-Id");
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "freeze-corr-id");
        var resp = await client.PutAsJsonAsync($"/api/v1/schemas/{id}", update, ct);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // RFC-7807 ProblemDetails (T-57-02 safe-disclosure contract: only the schema Guid + a generic
        // message; correlationId echoed by the CustomizeProblemDetails customizer).
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":409", body.Replace(" ", string.Empty));
        Assert.Contains("freeze-corr-id", body);
    }

    /// <summary>
    /// D-07 — a Name/Description-only edit on a referenced schema (unchanged Definition) is allowed (200).
    /// </summary>
    [Fact]
    public async Task NameDescription_Edit_On_Referenced_Schema_Returns_200()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var id = await PostSchemaAsync(client, DefinitionA, ct);
        await SeedReferencingProcessorAsync(id, ct);

        // SAME Definition, changed Name + Description (D-07 — only Definition is frozen).
        var update = new SchemaUpdateDto("Freeze-NewName", "1.0.0", "new-description", DefinitionA);
        var resp = await client.PutAsJsonAsync($"/api/v1/schemas/{id}", update, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>
    /// D-06 — an unreferenced draft schema's Definition is freely editable (200) until something references it.
    /// </summary>
    [Fact]
    public async Task Unreferenced_Draft_Definition_Edit_Returns_200()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var id = await PostSchemaAsync(client, DefinitionA, ct);
        // No referencing ProcessorEntity seeded — the schema is an unreferenced draft.

        var update = new SchemaUpdateDto("Freeze-DraftEdit", "1.0.0", "draft", DefinitionB);
        var resp = await client.PutAsJsonAsync($"/api/v1/schemas/{id}", update, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
