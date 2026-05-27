using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseApi.Tests.Middleware;

/// <summary>
/// D-03a / T-04-XMIN — Phase 3 carry-forward. Two writes racing on the same row
/// produce a 409 with the exact D-09 generic detail string AND the response body
/// contains NO numeric xmin substring (information-disclosure guard).
///
/// <para>
/// <b>Race orchestration:</b> the EF concurrency exception fires when one
/// DbContext's tracked entity has a stale xmin while another DbContext has
/// already committed an update. We orchestrate the race in-test via two
/// <see cref="TestErrorDbContext"/> instances, then verify the HTTP-mapping
/// shape via the <c>/test/concurrency</c> endpoint. The CRITICAL assertion is
/// the 409 response body shape (D-09 + T-04-XMIN); the in-process race is the
/// means to trigger that path through the IExceptionHandler chain.
/// </para>
/// </summary>
public sealed class ConcurrencyTokenTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public ConcurrencyTokenTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        var options = new DbContextOptionsBuilder<TestErrorDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        // Seed parent row.
        var parentId = Guid.NewGuid();
        await using (var db = new TestErrorDbContext(options))
        {
            await db.Database.EnsureCreatedAsync(ct);
            db.Parents.Add(new TestParentEntity
            {
                Id = parentId,
                Name = "p-initial",
                Version = "1.0.0",
            });
            await db.SaveChangesAsync(ct);
        }

        // Direct EF-level race assertion (proves the EF / xmin layer is wired).
        await using var stale = new TestErrorDbContext(options);
        var p1 = await stale.Parents.FirstAsync(p => p.Id == parentId, ct);

        await using (var freshWriter = new TestErrorDbContext(options))
        {
            var p2 = await freshWriter.Parents.FirstAsync(p => p.Id == parentId, ct);
            p2.Name = "p-secondWriter";
            await freshWriter.SaveChangesAsync(ct);
        }

        // stale context's xmin is behind — modifying + saving raises DbUpdateConcurrencyException.
        p1.Name = "p-thirdWriter-stale";
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await stale.SaveChangesAsync(ct));
        Assert.NotNull(ex);

        // Now verify the HTTP-level mapping: two concurrent POSTs to the endpoint that
        // does load+mutate+save in one method. Whichever wins commits; the loser hits
        // the DbUpdateExceptionHandler concurrency branch → 409 with D-09 detail.
        var task1 = Task.Run(async () => await client.PostAsync($"/test/concurrency?id={parentId}", null, ct), ct);
        var task2 = Task.Run(async () => await client.PostAsync($"/test/concurrency?id={parentId}", null, ct), ct);
        var responses = await Task.WhenAll(task1, task2);

        var conflict = responses.FirstOrDefault(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.NotNull(conflict);

        Assert.Equal("application/problem+json", conflict!.Content.Headers.ContentType?.MediaType);
        var body = await conflict.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(409, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "The resource was modified by another request; reload and retry.",
            doc.RootElement.GetProperty("detail").GetString());

        // T-04-XMIN: no numeric xmin substring or `xmin` token in body.
        Assert.DoesNotMatch(@"""xmin""\s*:\s*\d+", body);
        Assert.DoesNotContain("xmin", body);
    }
}
