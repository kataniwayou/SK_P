using System.Reflection;
using BaseProcessor.Core.Processing;
using Processor.Sample;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Hermetic unit facts for <see cref="SampleProcessor"/> (SAMPLE-01 / D-04): the one concrete
/// transform returns exactly one deterministic <see cref="ProcessResult"/>.
///
/// <para>
/// <see cref="SampleProcessor"/> is <c>sealed</c> and its <c>ProcessAsync</c> is <c>protected</c>
/// (BaseProcessor.Core grants no <c>InternalsVisibleTo</c> to this test assembly), so the seam is
/// invoked by reflection — the hermetic equivalent of the framework's internal forwarder.
/// </para>
/// </summary>
public sealed class SampleProcessorFacts
{
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
    public async Task ProcessAsync_Returns_Single_Deterministic_Result()
    {
        var processor = new SampleProcessor();

        var result = await InvokeProcessAsync(processor, "any-input", "any-config");

        // Exactly one result (result.Count == 1) — Assert.Single is the xUnit-sanctioned size check.
        var only = Assert.Single(result);
        Assert.Equal("processor-sample-ok", only.OutputData);
    }

    [Fact]
    public async Task ProcessAsync_Is_Deterministic_Across_Inputs()
    {
        var processor = new SampleProcessor();

        var first = await InvokeProcessAsync(processor, "input-A", "config-A");
        var second = await InvokeProcessAsync(processor, "input-B", "config-B");

        var firstOnly = Assert.Single(first);
        var secondOnly = Assert.Single(second);
        Assert.Equal(firstOnly.OutputData, secondOnly.OutputData);
        Assert.Equal("processor-sample-ok", secondOnly.OutputData);
    }
}
