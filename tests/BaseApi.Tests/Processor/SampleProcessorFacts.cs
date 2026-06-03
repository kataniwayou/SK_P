using System.Reflection;
using BaseProcessor.Core.Processing;
using Microsoft.Extensions.Logging;
using Processor.Sample;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Hermetic unit facts for <see cref="SampleProcessor"/> (SAMPLE-01 / D-04): the one concrete
/// transform deserializes the dispatch <c>config</c> — the per-step assignment payload — logs it,
/// and echoes it back as the single <see cref="ProcessResult"/>. A blank payload falls back to the
/// fixed <c>"processor-sample-ok"</c> token.
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

    private static Task<IReadOnlyList<ProcessResult>> InvokeProcessAsync(
        SampleProcessor processor, string inputData, string config)
    {
        var method = typeof(SampleProcessor).GetMethod(
            "ProcessAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<IReadOnlyList<ProcessResult>>)method.Invoke(
            processor, new object[] { inputData, config, CancellationToken.None })!;
    }

    [Fact]
    public async Task ProcessAsync_Deserializes_Payload_Logs_It_And_Echoes_It()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        // config is a JSON string — exactly what the assignment payload "\"StepA1\"" looks like on the wire.
        var result = await InvokeProcessAsync(processor, "any-input", "\"StepA1\"");

        var only = Assert.Single(result);
        Assert.Equal("StepA1", only.OutputData);

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
        Assert.Equal("processor-sample-ok", only.OutputData);
        Assert.Single(logger.Entries); // still logs (payload null) — proves the seam always runs
    }
}
