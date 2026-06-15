using System.Reflection;
using System.Text.Json;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Processing;
using Microsoft.Extensions.Logging;
using Processor.Sample;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Hermetic unit facts for <see cref="SampleProcessor"/> (PROC-01/02/03): the one concrete
/// In-Process transform receives a framework-deserialized typed <see cref="SampleConfig"/>
/// (an int + a <c>Step_*</c> label), random-adds to the integer and emits the resulting
/// <c>sum</c> as a single completed <see cref="ProcessItem"/> whose <c>Data</c> is a
/// {number,label} JSON string, and emits exactly ONE structured log entry tagged with the
/// <c>StepLabel</c> + <c>Sum</c>. A null config still produces exactly one item and one log.
/// The third fact proves PROC-01 — case-insensitive deserialization of the payload into the
/// typed config via <see cref="ProcessorConfig.SerializerOptions"/>.
///
/// <para>
/// <see cref="SampleProcessor"/> is <c>sealed</c> and its typed <c>ProcessAsync</c> is <c>protected</c>
/// (BaseProcessor.Core grants no <c>InternalsVisibleTo</c> to this test assembly), so the seam is
/// invoked by reflection — passing a typed <see cref="SampleConfig"/> exactly as the framework's internal
/// forwarder would after deserialize. The 6 correlation ids are ambient (consume-filter scope); the
/// hermetic <c>NullScope</c> swallows them, so these facts assert label+sum only (Pitfall 4).
/// </para>
/// </summary>
public sealed class SampleProcessorFacts
{
    /// <summary>Records every log entry: its level, formatted message, and structured state KVPs.</summary>
    private sealed class CapturingLogger : ILogger<SampleProcessor>
    {
        public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var kvps = state as IReadOnlyList<KeyValuePair<string, object?>>
                       ?? Array.Empty<KeyValuePair<string, object?>>();   // MEL FormattedLogValues implements this (A1)
            Entries.Add((logLevel, formatter(state, exception), kvps));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static Task<List<ProcessItem>> InvokeProcessAsync(
        SampleProcessor processor, string validatedData, SampleConfig? config)
    {
        var method = typeof(SampleProcessor).GetMethod(
            "ProcessAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<List<ProcessItem>>)method.Invoke(
            processor, new object?[] { validatedData, config, CancellationToken.None })!;
    }

    [Fact]
    public async Task ProcessAsync_Spawns_Two_Executions_Each_Adds_Random_And_Logs_Step_And_Sum()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        var result = await InvokeProcessAsync(processor, "any-input", new SampleConfig(10, "Step_A1"));

        // Multiple-execution stress test: ONE dispatch → TWO completed executions, each its own ExecutionId + log.
        Assert.Equal(2, result.Count);
        Assert.NotEqual(result[0].ExecutionId, result[1].ExecutionId);   // distinct per execution
        Assert.Equal(2, logger.Entries.Count);                           // one log per execution

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(ProcessOutcome.Completed, result[i].Result);
            Assert.NotEqual(Guid.Empty, result[i].ExecutionId);          // D-06: author-minted

            using var doc = JsonDocument.Parse(result[i].Data);
            var number = doc.RootElement.GetProperty("number").GetInt32();
            Assert.InRange(number, 10, 109);                             // D-07: [Number, Number+99], upper-exclusive Next(0,100)
            Assert.Equal("Step_A1", doc.RootElement.GetProperty("label").GetString());  // D-10 verbatim

            var logged = logger.Entries[i];                              // log[i] pairs with execution[i] (logged before add)
            Assert.Equal(LogLevel.Information, logged.Level);
            Assert.Contains(logged.State, kv => kv.Key == "StepLabel" && (string?)kv.Value == "Step_A1");
            Assert.Contains(logged.State, kv => kv.Key == "Sum" && (int)kv.Value! == number);
        }
    }

    [Fact]
    public async Task ProcessAsync_Null_Config_Still_Emits_Two_Items_And_Two_Logs()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        var result = await InvokeProcessAsync(processor, "any-input", (SampleConfig?)null);

        Assert.Equal(2, result.Count);                   // D-03: seam always runs; two executions
        Assert.Equal(2, logger.Entries.Count);           // two logs (D-08, one per execution)
        for (var i = 0; i < 2; i++)
        {
            using var doc = JsonDocument.Parse(result[i].Data);
            Assert.InRange(doc.RootElement.GetProperty("number").GetInt32(), 0, 99);   // baseNumber 0 + 0..99
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("label").ValueKind);
        }
    }

    [Fact]
    public void Deserializes_Typed_Config_From_Payload_Case_Insensitively()
    {
        var config = JsonSerializer.Deserialize<SampleConfig>(
            "{\"number\":5,\"label\":\"Step_A1\"}", ProcessorConfig.SerializerOptions);

        Assert.NotNull(config);
        Assert.Equal(5, config!.Number);                 // PROC-01: int field bound
        Assert.Equal("Step_A1", config.Label);           // PROC-01: string field bound
    }
}
