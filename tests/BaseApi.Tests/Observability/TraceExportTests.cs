using System.Net;
using System.Text.Json;
using BaseApi.Tests.Middleware;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// SC#5 (OBSERV-12): a Postgres query during a request produces a CHILD span under the
/// ASP.NET Core request span. Plus T-05-PII regression: <c>db.statement</c> attribute
/// carries only the SQL TEMPLATE (parameter placeholder <c>$1</c>); NO bound parameter
/// values appear in any span attribute.
/// </summary>
[Collection("Observability")]
public sealed class TraceExportTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public TraceExportTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Test_NpgsqlChildSpan_Under_AspNetCore_Request_Span()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        await using var factory = new OtelCollectorFixture(_pg.ConnectionString);
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var seededId = Guid.NewGuid();
        var response = await client.GetAsync($"/test-obs/db-roundtrip?id={seededId}", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.FlushAsync(TimeSpan.FromSeconds(1));

        var traces = factory.ReadExportedTraces();
        var allSpans = traces.SelectMany(EnumerateSpans).ToList();

        // ASP.NET Core span name is "GET test-obs/db-roundtrip" (no leading slash — the OTel
        // AspNetCore instrumentation uses the http.route template, not the raw url.path)
        var aspSpan = allSpans.FirstOrDefault(s =>
            s.GetProperty("name").GetString()?.Contains("test-obs/db-roundtrip", StringComparison.Ordinal) == true);
        Assert.True(aspSpan.ValueKind == JsonValueKind.Object, "ASP.NET Core request span missing");

        // Find an Npgsql child span whose parentSpanId == aspSpan.spanId
        var aspSpanId = aspSpan.GetProperty("spanId").GetString();
        var npgChildren = allSpans
            .Where(s => s.TryGetProperty("parentSpanId", out var p) && p.GetString() == aspSpanId)
            .ToList();

        var dbSpan = npgChildren.FirstOrDefault(s =>
        {
            if (!s.TryGetProperty("attributes", out var attrs)) return false;
            return attrs.EnumerateArray().Any(a => a.GetProperty("key").GetString() == "db.statement");
        });

        Assert.True(dbSpan.ValueKind == JsonValueKind.Object,
            "Npgsql child span with db.statement missing — Npgsql instrumentation not wired");
    }

    [Fact]
    public async Task Test_NpgsqlChildSpan_DbStatement_Has_NoParameterValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        await using var factory = new OtelCollectorFixture(_pg.ConnectionString);
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Use a deliberately-distinctive Guid so we can prove its STRING form does NOT appear in spans
        var distinctive = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        _ = await client.GetAsync($"/test-obs/db-roundtrip?id={distinctive}", ct);

        await factory.FlushAsync(TimeSpan.FromSeconds(1));

        var traces = factory.ReadExportedTraces();
        var allSpans = traces.SelectMany(EnumerateSpans).ToList();
        var dbSpans = allSpans
            .Where(s => s.TryGetProperty("attributes", out var attrs)
                     && attrs.EnumerateArray().Any(a => a.GetProperty("key").GetString() == "db.statement"))
            .ToList();
        Assert.NotEmpty(dbSpans);

        // T-05-PII assertion #1: at least one db.statement carries a parameter PLACEHOLDER —
        // either Npgsql's positional `$N` form OR EF Core's named `@__name_N` form. Both
        // prove that the SQL TEMPLATE is captured, not the bound value. The point of T-05-PII
        // is that bound VALUES don't leak — placeholder shape is implementation detail.
        var statements = dbSpans
            .SelectMany(s => s.GetProperty("attributes").EnumerateArray())
            .Where(a => a.GetProperty("key").GetString() == "db.statement")
            .Select(a => a.GetProperty("value").GetProperty("stringValue").GetString() ?? string.Empty)
            .ToList();
        Assert.NotEmpty(statements);
        var anyParametrizedStatement = statements.Any(stmt =>
            stmt.Contains("$1", StringComparison.Ordinal)        // Npgsql positional placeholder
            || stmt.Contains("@__", StringComparison.Ordinal)    // EF Core named placeholder prefix
            || stmt.Contains("@p", StringComparison.Ordinal));   // alternate EF named-param style
        Assert.True(anyParametrizedStatement,
            $"Expected at least one db.statement to use a parameter placeholder template "
            + $"($1 or @__name_N or @pN). Statements: {string.Join(" | ", statements.Select(s => s[..Math.Min(s.Length, 100)]))}");

        // T-05-PII assertion #2: NO attribute key starts with "db.parameter"
        foreach (var span in dbSpans)
        {
            var attrs = span.GetProperty("attributes").EnumerateArray().ToList();
            Assert.DoesNotContain(attrs, a => (a.GetProperty("key").GetString() ?? string.Empty)
                .StartsWith("db.parameter", StringComparison.Ordinal));
        }

        // T-05-PII assertion #3: distinctive Guid string does NOT appear in ANY span attribute string-value
        var distinctiveStr = distinctive.ToString();
        foreach (var span in allSpans)
        {
            if (!span.TryGetProperty("attributes", out var attrs)) continue;
            foreach (var attr in attrs.EnumerateArray())
            {
                if (!attr.GetProperty("value").TryGetProperty("stringValue", out var sv)) continue;
                var s = sv.GetString();
                Assert.False(s?.Contains(distinctiveStr, StringComparison.Ordinal) == true,
                    $"Bound parameter value '{distinctiveStr}' leaked into span attribute "
                    + $"'{attr.GetProperty("key").GetString()}' = '{s}' (T-05-PII regression)");
            }
        }
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        // Ensure the schema exists in the per-class throwaway DB. EnsureCreatedAsync is
        // idempotent — PostgresFixture already pre-created it; this is defensive against
        // future fixture refactors.
        var opts = new DbContextOptionsBuilder<TestErrorDbContext>()
            .UseNpgsql(_pg.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new TestErrorDbContext(opts);
        await db.Database.EnsureCreatedAsync(ct);
    }

    private static IEnumerable<JsonElement> EnumerateSpans(JsonElement resourceSpansContainer)
    {
        foreach (var rs in resourceSpansContainer.GetProperty("resourceSpans").EnumerateArray())
        foreach (var ss in rs.GetProperty("scopeSpans").EnumerateArray())
        foreach (var s in ss.GetProperty("spans").EnumerateArray())
            yield return s;
    }
}
