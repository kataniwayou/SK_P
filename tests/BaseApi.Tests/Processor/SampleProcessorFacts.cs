using System.Reflection;
using BaseProcessor.Core.Processing;
using Microsoft.Extensions.Logging;
using Processor.Sample;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Hermetic unit facts for <see cref="SampleProcessor"/> (SAMPLE-01 / D-07): the one concrete
/// In-Process transform receives a framework-deserialized typed <see cref="SampleConfig"/> — the
/// framework owns deserialization of the per-step assignment payload — logs the config's value, and
/// echoes it back as a single completed <see cref="ProcessItem"/>. A null config (blank/absent payload)
/// falls back to the fixed <c>"processor-sample-ok"</c> token.
///
/// <para>
/// <see cref="SampleProcessor"/> is <c>sealed</c> and its typed <c>ProcessAsync</c> is <c>protected</c>
/// (BaseProcessor.Core grants no <c>InternalsVisibleTo</c> to this test assembly), so the seam is
/// invoked by reflection — passing a typed <see cref="SampleConfig"/> exactly as the framework's internal
/// forwarder would after deserialize.
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
        SampleProcessor processor, string validatedData, SampleConfig? config)
    {
        var method = typeof(SampleProcessor).GetMethod(
            "ProcessAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<List<ProcessItem>>)method.Invoke(
            processor, new object?[] { validatedData, config, CancellationToken.None })!;
    }

    [Fact]
    public async Task ProcessAsync_Receives_Typed_Config_Logs_It_And_Echoes_It()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        // The framework deserialized {"value":"StepA1"} into this typed config before the seam ran.
        var result = await InvokeProcessAsync(processor, "any-input", new SampleConfig("StepA1"));

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
    public async Task ProcessAsync_Null_Config_Falls_Back_To_Fixed_Token()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        // Blank/absent payload → the framework hands the seam a null config (D-04).
        var result = await InvokeProcessAsync(processor, "any-input", (SampleConfig?)null);

        var only = Assert.Single(result);
        Assert.Equal("processor-sample-ok", only.Data);
        Assert.Single(logger.Entries); // still logs (config null) — proves the seam always runs
    }

    [Fact]
    public void ProcessAsync_Fail_Config_Throws_FailedException()
    {
        var logger = new CapturingLogger();
        var processor = new SampleProcessor(logger);

        // D-07 worked example: a "fail" Value demonstrates the author status-exception path.
        // The seam throws SYNCHRONOUSLY (before returning the Task), so the reflection invoke
        // surfaces it wrapped in a TargetInvocationException — assert the inner is FailedException.
        var outer = Assert.Throws<TargetInvocationException>(
            () => { _ = InvokeProcessAsync(processor, "any-input", new SampleConfig("fail")); });
        Assert.IsType<FailedException>(outer.InnerException);
    }
}
