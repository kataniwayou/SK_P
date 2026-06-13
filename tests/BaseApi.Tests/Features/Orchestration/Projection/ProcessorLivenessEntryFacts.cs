using System;
using System.Text.Json;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration.Projection;

/// <summary>
/// Phase 59 (KEY-04 / STATE-01 / STATE-02) — hermetic shape + factory-invariant facts for the new
/// liveness-only ProcessorLivenessEntry. Serializes under DEFAULT options so the load-bearing
/// [property: JsonPropertyName] pins must hold on their own. No real stack.
/// </summary>
[Trait("Phase", "59")]
public sealed class ProcessorLivenessEntryFacts
{
    private static readonly JsonSerializerOptions Default = new();

    [Fact]
    public void ProcessorLivenessEntry_Json_Has_No_Definition_Fields()
    {
        var entry = ProcessorLivenessEntry.Create(
            SchemaOutcome.Success, SchemaOutcome.Success, SchemaOutcome.Success,
            DateTime.UnixEpoch, 30);

        var json = JsonSerializer.Serialize(entry, Default);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("inputDefinition", out _));    // KEY-04
        Assert.False(root.TryGetProperty("outputDefinition", out _));   // KEY-04
        Assert.True(root.TryGetProperty("timestamp", out _));
        Assert.True(root.TryGetProperty("interval", out _));
        Assert.True(root.TryGetProperty("status", out _));
        Assert.True(root.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("inputSchema", out _));
        Assert.True(summary.TryGetProperty("outputSchema", out _));
        Assert.True(summary.TryGetProperty("configSchema", out _));
    }

    [Theory]
    [InlineData(null,      null,      null,      "Healthy")]    // all skip => Healthy
    [InlineData("SUCCESS", "SUCCESS", "SUCCESS", "Healthy")]
    [InlineData("FAIL",    "SUCCESS", "SUCCESS", "Unhealthy")]  // any FAIL => Unhealthy
    [InlineData("SUCCESS", null,      "FAIL",    "Unhealthy")]  // null-is-skip + a FAIL
    public void Create_Derives_Status_From_Summary(
        string? input, string? output, string? config, string expectedStatus)
    {
        var entry = ProcessorLivenessEntry.Create(input, output, config, DateTime.UnixEpoch, 30);

        Assert.Equal(expectedStatus, entry.Status);
        Assert.Equal(input  ?? SchemaOutcome.Success, entry.Summary.InputSchema);   // null-is-skip => Success
        Assert.Equal(output ?? SchemaOutcome.Success, entry.Summary.OutputSchema);
        Assert.Equal(config ?? SchemaOutcome.Success, entry.Summary.ConfigSchema);
    }

    [Fact]
    public void Status_Is_One_Of_The_Two_LivenessStatus_Consts()
    {
        var healthy   = ProcessorLivenessEntry.Create(null, null, null, DateTime.UnixEpoch, 30);
        var unhealthy = ProcessorLivenessEntry.Create(SchemaOutcome.Fail, null, null, DateTime.UnixEpoch, 30);

        Assert.Equal(LivenessStatus.Healthy, healthy.Status);     // STATE-01
        Assert.Equal(LivenessStatus.Unhealthy, unhealthy.Status); // STATE-01
    }
}
