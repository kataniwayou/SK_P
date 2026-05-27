using System.Net;
using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// SC#1 (OBSERV-02 / OBSERV-05 / OBSERV-07): Phase 4 CorrelationIdMiddleware's
/// <c>BeginScope("CorrelationId", id)</c> propagates through the MEL bridge into the
/// OTel LoggerProvider (<c>IncludeScopes = true</c>) and surfaces on the OTLP-exported
/// log record as a <c>CorrelationId</c> attribute. Service resource attributes
/// <c>service.name = sk-api</c> and <c>service.version = 3.2.0</c> appear on every
/// exported log record.
/// </summary>
[Collection("Observability")]
public sealed class LogExportTests : IClassFixture<OtelCollectorFixture>
{
    private readonly OtelCollectorFixture _fixture;

    public LogExportTests(OtelCollectorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Test_LogRecord_Has_CorrelationId_And_ServiceResource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _fixture.CreateClient();

        var response = await client.GetAsync("/test-obs/ok", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Capture the X-Correlation-Id response header — the value Phase 4 generated
        var corrId = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.Matches("^[a-f0-9]{32}$", corrId);

        await _fixture.FlushAsync();

        // Find the log record corresponding to THIS test's correlation ID (multiple test
        // invocations against the shared fixture accumulate records in telemetry.jsonl).
        var logs = _fixture.ReadExportedLogs();
        Assert.NotEmpty(logs);

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
                    && attr.GetProperty("value").GetProperty("stringValue").GetString() == corrId);
            })
            .ToList();

        Assert.NotEmpty(hits);

        var record = hits[0];
        var attrs = record.GetProperty("attributes").EnumerateArray().ToList();
        var corrIdAttr = attrs.First(a => a.GetProperty("key").GetString() == "CorrelationId");
        Assert.Equal(corrId, corrIdAttr.GetProperty("value").GetProperty("stringValue").GetString());

        // Resource attributes (service.name / service.version) live on the resourceLogs
        // node. Find the resourceLogs container whose resource carries service.name=sk-api
        // (other processes on the host — e.g., MCP.Terminal — also emit to the same
        // collector during dev; we filter to the BaseApi.Service-owned resource).
        var skApiResources = logs
            .SelectMany(l => l.GetProperty("resourceLogs").EnumerateArray())
            .Select(rl => rl.GetProperty("resource").GetProperty("attributes").EnumerateArray().ToList())
            .Where(attrs => attrs.Any(a =>
                a.GetProperty("key").GetString() == "service.name"
                && a.GetProperty("value").GetProperty("stringValue").GetString() == "sk-api"))
            .ToList();

        Assert.NotEmpty(skApiResources);
        var resourceAttrs = skApiResources[0];
        Assert.Contains(resourceAttrs, a => a.GetProperty("key").GetString() == "service.name"
                                         && a.GetProperty("value").GetProperty("stringValue").GetString() == "sk-api");
        Assert.Contains(resourceAttrs, a => a.GetProperty("key").GetString() == "service.version"
                                         && a.GetProperty("value").GetProperty("stringValue").GetString() == "3.2.0");
    }

    [Fact]
    public async Task Test_LogRecord_CorrelationId_Survives_Sanitization()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _fixture.CreateClient();

        // Send a deliberately-malformed correlation ID (Phase 4 Pitfall 3 — control chars).
        // TryAddWithoutValidation bypasses HttpClient header validation so the middleware
        // (D-02 IsValid) is the one that rejects.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test-obs/ok");
        req.Headers.TryAddWithoutValidation("X-Correlation-Id", "invalid\rinjected");
        var response = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Phase 4 sanitization MUST have replaced the input with a fresh 32-char hex UUID
        var corrId = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.Matches("^[a-f0-9]{32}$", corrId);

        await _fixture.FlushAsync();

        var logs = _fixture.ReadExportedLogs();
        var hits = logs
            .SelectMany(GetLogRecords)
            .Where(rec => rec.TryGetProperty("body", out var body)
                       && body.TryGetProperty("stringValue", out var bs)
                       && bs.GetString() == "test-obs ok ran")
            .ToList();
        Assert.NotEmpty(hits);

        // Match the specific record whose CorrelationId equals the sanitized response value
        // (multiple test invocations against the shared fixture write multiple records)
        var record = hits.First(rec =>
        {
            var attrs = rec.GetProperty("attributes").EnumerateArray().ToList();
            var corrIdAttr = attrs.FirstOrDefault(a => a.GetProperty("key").GetString() == "CorrelationId");
            return corrIdAttr.ValueKind == JsonValueKind.Object
                && corrIdAttr.GetProperty("value").GetProperty("stringValue").GetString() == corrId;
        });
        var attrsFinal = record.GetProperty("attributes").EnumerateArray().ToList();
        var corrIdAttrFinal = attrsFinal.First(a => a.GetProperty("key").GetString() == "CorrelationId");
        var corrIdValue = corrIdAttrFinal.GetProperty("value").GetProperty("stringValue").GetString();

        // The log attribute MUST be the sanitized 32-char hex — must NOT contain raw injection chars
        Assert.Equal(corrId, corrIdValue);
        Assert.DoesNotContain("\r", corrIdValue!);
        Assert.DoesNotContain("\n", corrIdValue!);
        Assert.DoesNotContain("injected", corrIdValue!);
    }

    private static IEnumerable<JsonElement> GetLogRecords(JsonElement resourceLogsContainer)
    {
        foreach (var rl in resourceLogsContainer.GetProperty("resourceLogs").EnumerateArray())
        foreach (var sl in rl.GetProperty("scopeLogs").EnumerateArray())
        foreach (var rec in sl.GetProperty("logRecords").EnumerateArray())
            yield return rec;
    }
}
