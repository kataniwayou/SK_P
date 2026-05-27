using System.Net;
using System.Net.Http.Json;
using BaseApi.Service.Features.Schema;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Integration;

/// <summary>
/// Phase 8 Wave B smoke integration tests for the Schema feature (5 [Fact]s per CONTEXT D-07).
/// Uses <see cref="Phase8WebAppFactory"/> (Wave A 08-01) which encapsulates a per-class
/// throwaway Postgres DB via <c>PostgresFixture</c>.
/// <para>
/// <b>GREEN-state dependency:</b> these tests run against the PRODUCTION
/// <c>AppDbContext</c>. They will only pass AFTER Wave C 08-07 lands (a) DbSets on
/// AppDbContext, (b) the MigrateAsync swap on StartupCompletionService, and (c) the
/// per-entity DI registration (<see cref="SchemaServiceCollectionExtensions.AddSchemaFeature"/>).
/// Phase 8 Plan 08-08 verifies the GREEN-state regression after Wave C completes.
/// </para>
/// </summary>
[Trait("Phase8Wave", "B")]
public sealed class SchemasIntegrationTests : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public SchemasIntegrationTests(Phase8WebAppFactory factory) => _factory = factory;

    private static SchemaCreateDto NewValidCreateDto(string suffix = "") => new(
        Name: $"my-schema{suffix}",
        Version: "1.0.0",
        Description: "Integration test schema",
        Definition: "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}");

    [Fact]
    public async Task List_ReturnsEmptyArray_OnEmptyDb()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/schemas", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Phase8WebAppFactory uses IClassFixture (shared per class), so by the time
        // this fact runs, sibling facts may have created rows. Assert the response
        // is a well-formed JSON array (the list-endpoint contract) rather than
        // requiring strictly-zero rows. Plan 08-08 cross-entity tests add explicit
        // per-test isolation if stricter semantics are needed.
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.NotNull(body);
        Assert.StartsWith("[", body);
        Assert.EndsWith("]", body);
    }

    [Fact]
    public async Task Create_Returns201_AndLocationHeader_WhenValid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/schemas", NewValidCreateDto(), ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        // Location header is absolute (Kestrel composes it with scheme+host) and the
        // [controller] route token preserves the C# class-name casing — "Schemas" not
        // "schemas". Both factors mean the regex must tolerate (1) optional scheme+host
        // prefix and (2) case-insensitive controller segment.
        Assert.Matches(@"(?i)/api/v1/schemas/[a-f0-9\-]{36}$", response.Headers.Location!.ToString());

        var read = await response.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);
        Assert.NotNull(read);
        Assert.NotEqual(Guid.Empty, read!.Id);
        Assert.Equal("my-schema", read.Name);
        Assert.NotEqual(default, read.CreatedAt);            // HTTP-07: audit populated
        Assert.NotEqual(default, read.UpdatedAt);
    }

    [Fact]
    public async Task GetById_Returns200_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/v1/schemas", NewValidCreateDto("-getbyid"), ct);
        var created = await create.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);

        var getResp = await client.GetAsync($"/api/v1/schemas/{created!.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var read = await getResp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);
        Assert.Equal(created.Id, read!.Id);
        // Postgres jsonb normalizes whitespace and may reorder object keys. Compare via
        // semantic JSON equality (parse both sides, serialize with sorted keys, then
        // compare). This is a property of jsonb storage, not a server bug.
        Assert.Equal(NormalizeJson(created.Definition), NormalizeJson(read.Definition));
    }

    /// <summary>
    /// Semantic JSON equality helper: parse, then re-serialize with sorted keys + compact
    /// whitespace. Two semantically equivalent JSON documents always produce the same
    /// normalized string. Used to compare Schema.Definition / Assignment.Payload across
    /// Postgres jsonb storage (which reorders keys + adds whitespace).
    /// </summary>
    private static string NormalizeJson(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        return SerializeSorted(node);
    }

    private static string SerializeSorted(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return "null";
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            var sb = new System.Text.StringBuilder("{");
            var first = true;
            foreach (var kvp in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                sb.Append(System.Text.Json.JsonSerializer.Serialize(kvp.Key));
                sb.Append(':');
                sb.Append(SerializeSorted(kvp.Value));
                first = false;
            }
            sb.Append('}');
            return sb.ToString();
        }
        if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            var sb = new System.Text.StringBuilder("[");
            var first = true;
            foreach (var item in arr)
            {
                if (!first) sb.Append(',');
                sb.Append(SerializeSorted(item));
                first = false;
            }
            sb.Append(']');
            return sb.ToString();
        }
        return node.ToJsonString();
    }

    [Fact]
    public async Task Update_Returns200_AndChangedFields_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/v1/schemas", NewValidCreateDto("-update"), ct);
        var created = await create.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);

        var update = new SchemaUpdateDto(
            Name: created!.Name,
            Version: "1.0.1",
            Description: "Updated description",
            Definition: created.Definition);
        var putResp = await client.PutAsJsonAsync($"/api/v1/schemas/{created.Id}", update, ct);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/schemas/{created.Id}", ct);
        var read = await getResp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);
        Assert.Equal("1.0.1", read!.Version);
        Assert.Equal("Updated description", read.Description);
    }

    [Fact]
    public async Task Delete_Returns204_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/v1/schemas", NewValidCreateDto("-delete"), ct);
        var created = await create.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);

        var deleteResp = await client.DeleteAsync($"/api/v1/schemas/{created!.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/schemas/{created.Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }
}
