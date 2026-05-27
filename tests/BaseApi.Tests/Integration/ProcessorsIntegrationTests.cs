using System.Net;
using System.Net.Http.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Integration;

/// <summary>
/// Phase 8 Wave B smoke integration tests for the Processor feature (5 [Fact]s per CONTEXT D-07).
/// Uses <see cref="Phase8WebAppFactory"/> (Wave A 08-01) which encapsulates a per-class
/// throwaway Postgres DB via <c>PostgresFixture</c>.
/// <para>
/// <b>GREEN-state dependency:</b> these tests run against the PRODUCTION
/// <c>AppDbContext</c>. They will only pass AFTER Wave C 08-07 lands (a) DbSets on
/// AppDbContext, (b) the MigrateAsync swap on StartupCompletionService, and (c) the
/// per-entity DI registration (<see cref="ProcessorServiceCollectionExtensions.AddProcessorFeature"/>).
/// Phase 8 Plan 08-08 verifies the GREEN-state regression after Wave C completes.
/// </para>
/// <para>
/// <b>SourceHash collision discipline:</b> each Create body uses <see cref="RandomSha256Hex"/>
/// to generate a unique 64-char lowercase hex string — avoids colliding with the
/// duplicate-sourceHash error-mapping fact in Plan 08-08 and with sibling test runs.
/// </para>
/// </summary>
[Trait("Phase8Wave", "B")]
public sealed class ProcessorsIntegrationTests : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public ProcessorsIntegrationTests(Phase8WebAppFactory factory) => _factory = factory;

    private static string RandomSha256Hex()
    {
        var bytes = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    private static ProcessorCreateDto NewValidCreateDto(string suffix = "") => new(
        Name: $"my-processor{suffix}",
        Version: "1.0.0",
        Description: "Integration test processor",
        SourceHash: RandomSha256Hex(),
        InputSchemaId: null,                                   // source processor (ENTITY-04)
        OutputSchemaId: null);

    [Fact]
    public async Task List_ReturnsEmptyArray_OnEmptyDb()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/processors", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal("[]", body);
    }

    [Fact]
    public async Task Create_Returns201_AndLocationHeader_WhenValid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/processors", NewValidCreateDto(), ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Matches(@"^/api/v1/processors/[a-f0-9\-]{36}$", response.Headers.Location!.ToString());

        var read = await response.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        Assert.NotNull(read);
        Assert.NotEqual(Guid.Empty, read!.Id);
        Assert.Null(read.InputSchemaId);
        Assert.Null(read.OutputSchemaId);
        Assert.NotEqual(default, read.CreatedAt);            // HTTP-07: audit populated
        Assert.NotEqual(default, read.UpdatedAt);
    }

    [Fact]
    public async Task GetById_Returns200_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/v1/processors", NewValidCreateDto("-getbyid"), ct);
        var created = await create.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);

        var getResp = await client.GetAsync($"/api/v1/processors/{created!.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var read = await getResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        Assert.Equal(created.Id, read!.Id);
        Assert.Equal(created.SourceHash, read.SourceHash);
    }

    [Fact]
    public async Task Update_Returns200_AndChangedFields_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/v1/processors", NewValidCreateDto("-update"), ct);
        var created = await create.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);

        var update = new ProcessorUpdateDto(
            Name: created!.Name,
            Version: "1.0.1",
            Description: "Updated processor",
            SourceHash: created.SourceHash,                    // keep same hash (no collision)
            InputSchemaId: null,
            OutputSchemaId: null);
        var putResp = await client.PutAsJsonAsync($"/api/v1/processors/{created.Id}", update, ct);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/processors/{created.Id}", ct);
        var read = await getResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        Assert.Equal("1.0.1", read!.Version);
        Assert.Equal("Updated processor", read.Description);
    }

    [Fact]
    public async Task Delete_Returns204_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var create = await client.PostAsJsonAsync("/api/v1/processors", NewValidCreateDto("-delete"), ct);
        var created = await create.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);

        var deleteResp = await client.DeleteAsync($"/api/v1/processors/{created!.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/processors/{created.Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }
}
