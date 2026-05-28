using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Features.Processor;

/// <summary>
/// Phase 9 REQ-1 integration tests for
/// <see cref="ProcessorsController.GetBySourceHash"/>. Uses
/// <see cref="Phase8WebAppFactory"/> per CONTEXT D-20 (no Phase 9 factory — Phase 9
/// adds zero schema changes, no new migration).
/// <para>
/// <b>3 facts:</b>
/// <list type="number">
///   <item><c>GetBySourceHash_Returns200_AndDto_WhenExisting</c> — seed a Processor
///     via POST /api/v1/processors, then GET /api/v1/processors/by-source-hash/{hash}
///     and assert the returned ProcessorReadDto matches.</item>
///   <item><c>GetBySourceHash_Returns404_AndProblemDetails_WhenHashDoesNotExist</c>
///     — GET with a fresh <see cref="RandomSha256Hex"/> that no row has;
///     assert 404 + <c>application/problem+json</c> + <c>resourceType="ProcessorEntity"</c>.</item>
///   <item><c>GetBySourceHash_Returns404_AndProblemDetails_WhenHashMalformed</c>
///     — GET with literal <c>"not-a-hash"</c>; SPEC.md Constraint dictates no
///     route-level validation, so off-format strings 404 via row-miss.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Phase", "9")]
public sealed class GetBySourceHashFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public GetBySourceHashFacts(Phase8WebAppFactory factory) => _factory = factory;

    // Copied verbatim from tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs
    // lines 33-37. Generates a unique 64-char lowercase SHA-256 hex string per call —
    // avoids cross-fact collisions on the unique uq_processor_source_hash index.
    private static string RandomSha256Hex()
    {
        var bytes = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    private static ProcessorCreateDto NewValidCreateDto(string sourceHash, string suffix = "") => new(
        Name: $"phase9-processor{suffix}",
        Version: "1.0.0",
        Description: "Phase 9 GetBySourceHash test processor",
        SourceHash: sourceHash,
        InputSchemaId: null,
        OutputSchemaId: null,
        ConfigSchemaId: null);

    [Fact]
    public async Task GetBySourceHash_Returns200_AndDto_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Seed a Processor via the HTTP API; capture the hash we used.
        var hash = RandomSha256Hex();
        var createResp = await client.PostAsJsonAsync("/api/v1/processors", NewValidCreateDto(hash, "-hit"), ct);
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        Assert.NotNull(created);

        // GET by-source-hash with the seeded hash.
        var resp = await client.GetAsync($"/api/v1/processors/by-source-hash/{hash}", ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var read = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        Assert.NotNull(read);
        Assert.Equal(created!.Id, read!.Id);
        Assert.Equal(hash, read.SourceHash);
        Assert.Equal(created.Name, read.Name);
    }

    [Fact]
    public async Task GetBySourceHash_Returns404_AndProblemDetails_WhenHashDoesNotExist()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Use a freshly-generated hash that has not been POSTed.
        var missingHash = RandomSha256Hex();

        var resp = await client.GetAsync($"/api/v1/processors/by-source-hash/{missingHash}", ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("ProcessorEntity", doc.RootElement.GetProperty("resourceType").GetString());
        // resourceId is the supplied hash string verbatim.
        Assert.Equal(missingHash, doc.RootElement.GetProperty("resourceId").GetString());
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task GetBySourceHash_Returns404_AndProblemDetails_WhenHashMalformed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // SPEC.md Constraint: no route-level regex on {sourceHash}; off-format
        // strings reach the service and 404 via the row-miss path. Use a literal
        // non-hex string. URL-safe characters only — no escaping needed.
        const string malformed = "not-a-hash";

        var resp = await client.GetAsync($"/api/v1/processors/by-source-hash/{malformed}", ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("ProcessorEntity", doc.RootElement.GetProperty("resourceType").GetString());
        Assert.Equal(malformed, doc.RootElement.GetProperty("resourceId").GetString());
    }
}
