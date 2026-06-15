using System.Reflection;
using System.Text.Json;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Processor.Sample;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Hermetic unit facts for <see cref="SampleProcessor"/> (PROC-01/02/03): the one concrete
/// In-Process transform receives a framework-deserialized typed <see cref="SampleConfig"/>
/// (an int + a <c>Step_*</c> label) plus the inbound per-instance <c>executionId</c>, and branches:
/// <list type="bullet">
///   <item><b>ENTRY</b> (<c>executionId == Guid.Empty</c>): spawns TWO completed
///   <see cref="ProcessItem"/> executions, each a {number,label} JSON string with an independent random
///   sum over the config's <c>Number</c> and its own freshly-minted ExecutionId; each emits ONE log
///   carrying both <c>StepLabel</c> and that execution's <c>ExecutionId</c>.</item>
///   <item><b>DOWNSTREAM</b> (<c>executionId != Guid.Empty</c>): returns ONE completed execution that
///   REUSES the inbound executionId; its number is the inbound {number} + config.Number (NO random); it
///   emits ONE log carrying <c>StepLabel</c> and the inbound <c>ExecutionId</c>.</item>
/// </list>
/// A null config at entry still produces exactly two items and two logs (baseNumber 0). The last fact
/// proves PROC-01 — case-insensitive deserialization of the payload into the typed config.
///
/// <para>
/// <see cref="SampleProcessor"/> is <c>sealed</c> and its typed <c>ProcessAsync</c> is <c>protected</c>
/// (BaseProcessor.Core grants no <c>InternalsVisibleTo</c> to this test assembly), so the seam is
/// invoked by reflection — passing a typed <see cref="SampleConfig"/> + an executionId exactly as the
/// framework's internal forwarder would after deserialize. The 6 correlation ids are ambient
/// (consume-filter scope) and absent here; the per-execution <c>ExecutionId</c> is supplied by the
/// transform's nested <see cref="ExecutionLogScope.ExecutionId"/> BeginScope, which the
/// <see cref="CapturingLogger"/> records alongside each log entry's message state.
/// </para>
/// </summary>
public sealed class SampleProcessorFacts
{
    /// <summary>
    /// Records every log entry: its level, formatted message, the structured state KVPs, AND a snapshot
    /// of the currently-active BeginScope KVPs (so the per-execution ExecutionId pushed by the transform's
    /// nested scope is observable hermetically — MEL's real logger would merge these into ES attributes).
    /// </summary>
    private sealed class CapturingLogger : ILogger<SampleProcessor>
    {
        public List<(LogLevel Level, string Message,
            IReadOnlyList<KeyValuePair<string, object?>> State,
            IReadOnlyList<KeyValuePair<string, object>> Scope)> Entries { get; } = new();

        private readonly List<IReadOnlyList<KeyValuePair<string, object>>> _scopes = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object>> kvps)
            {
                var captured = kvps.ToList();
                _scopes.Add(captured);
                return new PopScope(_scopes, captured);
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var kvps = state as IReadOnlyList<KeyValuePair<string, object?>>
                       ?? Array.Empty<KeyValuePair<string, object?>>();   // MEL FormattedLogValues implements this (A1)
            // Merge all active scope dicts into one snapshot (last-write-wins, like MEL's scope stack).
            var scopeSnapshot = _scopes.SelectMany(s => s).ToList();
            Entries.Add((logLevel, formatter(state, exception), kvps, scopeSnapshot));
        }

        private sealed class PopScope(List<IReadOnlyList<KeyValuePair<string, object>>> scopes,
            IReadOnlyList<KeyValuePair<string, object>> mine) : IDisposable
        {
            public void Dispose() => scopes.Remove(mine);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static Task<List<ProcessItem>> InvokeProcessAsync(
        SampleProcessor processor, string validatedData, SampleConfig? config, Guid executionId)
    {
        var method = typeof(SampleProcessor).GetMethod(
            "ProcessAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<List<ProcessItem>>)method.Invoke(
            processor, new object?[] { validatedData, config, executionId, CancellationToken.None })!;
    }

    [Fact]
    public async Task ProcessAsync_Entry_Spawns_Two_Executions_Each_Mints_Exec_And_Logs_Step_And_ExecutionId()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        // ENTRY: executionId == Guid.Empty → fan-out into two minted instances.
        var result = await InvokeProcessAsync(processor, "any-input", new SampleConfig(10, "Step_A1"), Guid.Empty);

        Assert.Equal(2, result.Count);
        Assert.NotEqual(result[0].ExecutionId, result[1].ExecutionId);   // distinct per execution
        Assert.Equal(2, logger.Entries.Count);                           // one log per execution

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(ProcessOutcome.Completed, result[i].Result);
            Assert.NotEqual(Guid.Empty, result[i].ExecutionId);          // newly minted

            using var doc = JsonDocument.Parse(result[i].Data);
            var number = doc.RootElement.GetProperty("number").GetInt32();
            Assert.InRange(number, 10, 109);                             // [Number, Number+99], upper-exclusive Next(0,100)
            Assert.Equal("Step_A1", doc.RootElement.GetProperty("label").GetString());  // D-10 verbatim

            var logged = logger.Entries[i];                              // log[i] pairs with execution[i] (logged before add)
            Assert.Equal(LogLevel.Information, logged.Level);
            Assert.Contains(logged.State, kv => kv.Key == "StepLabel" && (string?)kv.Value == "Step_A1");
            Assert.Contains(logged.State, kv => kv.Key == "Sum" && (int)kv.Value! == number);
            // The nested BeginScope carries THIS execution's ExecutionId (== result[i].ExecutionId).
            Assert.Contains(logged.Scope, kv =>
                kv.Key == ExecutionLogScope.ExecutionId
                && (string)kv.Value == result[i].ExecutionId.ToString());
        }
    }

    [Fact]
    public async Task ProcessAsync_Downstream_Reuses_Inbound_Exec_Accumulates_NoRandom_And_Logs_Step_And_ExecutionId()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        var inboundExec = Guid.NewGuid();
        // DOWNSTREAM: inbound {number:7,label:"Step_B"} + config(3,"Step_B") → 10, deterministic, reuse exec.
        var result = await InvokeProcessAsync(
            processor, "{\"number\":7,\"label\":\"Step_B\"}", new SampleConfig(3, "Step_B"), inboundExec);

        var item = Assert.Single(result);
        Assert.Equal(ProcessOutcome.Completed, item.Result);
        Assert.Equal(inboundExec, item.ExecutionId);                    // REUSE the inbound exec

        using var doc = JsonDocument.Parse(item.Data);
        Assert.Equal(10, doc.RootElement.GetProperty("number").GetInt32());   // 7 + 3, NO random
        Assert.Equal("Step_B", doc.RootElement.GetProperty("label").GetString());

        var logged = Assert.Single(logger.Entries);
        Assert.Contains(logged.State, kv => kv.Key == "StepLabel" && (string?)kv.Value == "Step_B");
        Assert.Contains(logged.State, kv => kv.Key == "Sum" && (int)kv.Value! == 10);
        Assert.Contains(logged.Scope, kv =>
            kv.Key == ExecutionLogScope.ExecutionId && (string)kv.Value == inboundExec.ToString());
    }

    [Fact]
    public async Task ProcessAsync_Entry_Null_Config_Still_Emits_Two_Items_And_Two_Logs()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        var result = await InvokeProcessAsync(processor, "any-input", (SampleConfig?)null, Guid.Empty);

        Assert.Equal(2, result.Count);                   // seam always runs; two executions (entry)
        Assert.Equal(2, logger.Entries.Count);           // two logs (one per execution)
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
