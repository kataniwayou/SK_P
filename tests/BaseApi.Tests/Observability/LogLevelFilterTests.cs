using System.Net;
using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// SC#2 (OBSERV-06): Setting <c>Logging:LogLevel:Default = "Warning"</c> suppresses
/// <c>Information</c> logs from BOTH the console sink AND the OTLP-exported records.
/// This proves the single MEL filter path (Pitfall 9 / single source of truth) — the
/// filter runs BEFORE either sink, so both behave identically.
/// </summary>
[Collection("Observability")]
public sealed class LogLevelFilterTests
{
    [Fact]
    public async Task Test_Information_Log_Suppressed_When_Default_Warning()
    {
        var ct = TestContext.Current.CancellationToken;

        // Spin up a fixture with the LogLevel override (uses the internal 2-arg overload —
        // same assembly so the internal ctor is accessible). Each test gets its OWN fixture
        // instance because the LogLevel needs to be applied at host-build time.
        await using var factory = new OtelCollectorFixture(connectionString: null, logLevelDefaultOverride: "Warning");
        await factory.InitializeAsync();

        // Capture a unique correlation ID for this request so we can filter out other
        // log records that may already be in telemetry.jsonl from the shared collector.
        var thisRequestCorrId = $"{Guid.NewGuid():N}";

        using var client = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test-obs/ok");
        req.Headers.Add("X-Correlation-Id", thisRequestCorrId);
        var response = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.FlushAsync();

        var logs = factory.ReadExportedLogs();

        // Find any log record from THIS request (correlation-ID match) with body
        // "test-obs ok ran" — there should be NONE because LogInformation was filtered
        // out by MEL before reaching the OTel provider.
        var hits = logs
            .SelectMany(GetLogRecords)
            .Where(rec => rec.TryGetProperty("body", out var body)
                       && body.TryGetProperty("stringValue", out var bs)
                       && bs.GetString() == "test-obs ok ran")
            .Where(rec =>
            {
                if (!rec.TryGetProperty("attributes", out var a)) return false;
                return a.EnumerateArray().Any(attr =>
                    attr.GetProperty("key").GetString() == "CorrelationId"
                    && attr.GetProperty("value").GetProperty("stringValue").GetString() == thisRequestCorrId);
            })
            .ToList();

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Test_Information_Log_Present_When_Default_Information()
    {
        var ct = TestContext.Current.CancellationToken;

        // Default (no override) — appsettings.json says Default = "Information"
        await using var factory = new OtelCollectorFixture();
        await factory.InitializeAsync();

        var thisRequestCorrId = $"{Guid.NewGuid():N}";

        using var client = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test-obs/ok");
        req.Headers.Add("X-Correlation-Id", thisRequestCorrId);
        var response = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.FlushAsync();

        var logs = factory.ReadExportedLogs();
        var hits = logs
            .SelectMany(GetLogRecords)
            .Where(rec => rec.TryGetProperty("body", out var body)
                       && body.TryGetProperty("stringValue", out var bs)
                       && bs.GetString() == "test-obs ok ran")
            .Where(rec =>
            {
                if (!rec.TryGetProperty("attributes", out var a)) return false;
                return a.EnumerateArray().Any(attr =>
                    attr.GetProperty("key").GetString() == "CorrelationId"
                    && attr.GetProperty("value").GetProperty("stringValue").GetString() == thisRequestCorrId);
            })
            .ToList();

        Assert.NotEmpty(hits);
    }

    private static IEnumerable<JsonElement> GetLogRecords(JsonElement resourceLogsContainer)
    {
        foreach (var rl in resourceLogsContainer.GetProperty("resourceLogs").EnumerateArray())
        foreach (var sl in rl.GetProperty("scopeLogs").EnumerateArray())
        foreach (var rec in sl.GetProperty("logRecords").EnumerateArray())
            yield return rec;
    }
}
