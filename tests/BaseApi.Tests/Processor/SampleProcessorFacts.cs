using System.Reflection;
using BaseProcessor.Core.Processing;
using Microsoft.Extensions.Logging;
using Processor.Sample;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Hermetic unit facts for <see cref="SampleProcessor"/> (SAMPLE-01 / D-07): the one concrete
/// In-Process transform deserializes the dispatch <c>payload</c> — the per-step assignment payload —
/// logs it, and echoes it back as a single completed <see cref="ProcessItem"/>. A blank payload falls
/// back to the fixed <c>"processor-sample-ok"</c> token.
///
/// <para>
/// <see cref="SampleProcessor"/> is <c>sealed</c> and its <c>ProcessAsync</c> is <c>protected</c>
/// (BaseProcessor.Core grants no <c>InternalsVisibleTo</c> to this test assembly), so the seam is
/// invoked by reflection — the hermetic equivalent of the framework's internal forwarder.
/// </para>
/// </summary>
public sealed class SampleProcessorFacts
{
    /// <summary>Records every formatted log message at the level it was emitted.</summary>
    private sealed class CapturingLogger : ILogger<SampleProcessor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static Task<List<ProcessItem>> InvokeProcessAsync(
        SampleProcessor processor, string validatedData, string payload)
    {
        var method = typeof(SampleProcessor).GetMethod(
            "ProcessAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<List<ProcessItem>>)method.Invoke(
            processor, new object[] { validatedData, payload, CancellationToken.None })!;
    }

    [Fact]
    public async Task ProcessAsync_Deserializes_Payload_Logs_It_And_Echoes_It()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        // payload is a JSON string — exactly what the assignment payload "\"StepA1\"" looks like on the wire.
        var result = await InvokeProcessAsync(processor, "any-input", "\"StepA1\"");

        var only = Assert.Single(result);
        Assert.Equal(ProcessOutcome.Completed, only.Result);
        Assert.Equal("StepA1", only.Data);
        Assert.NotEqual(Guid.Empty, only.ExecutionId);   // D-03: the author mints the per-item ExecutionId

        var logged = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, logged.Level);
        Assert.Contains("sample payload received", logged.Message);
        Assert.Contains("StepA1", logged.Message);
    }

    [Fact]
    public async Task ProcessAsync_Blank_Config_Falls_Back_To_Fixed_Token()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        var result = await InvokeProcessAsync(processor, "any-input", "");

        var only = Assert.Single(result);
        Assert.Equal("processor-sample-ok", only.Data);
        Assert.Single(logger.Entries); // still logs (payload null) — proves the seam always runs
    }
}
