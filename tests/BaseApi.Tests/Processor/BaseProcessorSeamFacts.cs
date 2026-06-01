using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;
using ProcessResult = BaseProcessor.Core.Processing.ProcessResult;

namespace BaseApi.Tests.Processor;

/// <summary>
/// D-12 / BPC-02: the abstract <see cref="BaseProcessorBase"/> declares exactly one seam — the locked
/// <c>ProcessAsync(string inputData, string config, CancellationToken ct)</c>. A test-double
/// subclass overriding it compiles and DI-resolves; calling the override returns the expected
/// <see cref="IReadOnlyList{ProcessResult}"/>. The seam is DECLARED, not invoked by the framework
/// this phase (Phase 27 wires the invocation).
/// </summary>
public sealed class BaseProcessorSeamFacts
{
    /// <summary>
    /// A concrete processor that overrides the protected seam and exposes it publicly so the test
    /// can invoke it directly (the framework invocation lands in Phase 27).
    /// </summary>
    private sealed class TestProcessor : BaseProcessorBase
    {
        public static readonly ProcessResult Result = new("output");

        protected override Task<IReadOnlyList<ProcessResult>> ProcessAsync(
            string inputData, string config, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProcessResult>>(new[] { Result });

        public Task<IReadOnlyList<ProcessResult>> InvokeAsync(string inputData, string config, CancellationToken ct)
            => ProcessAsync(inputData, config, ct);
    }

    [Fact]
    public async Task TestDouble_Overrides_Seam_DI_Resolves_And_Returns_Results()
    {
        var ct = TestContext.Current.CancellationToken;

        // DI-resolve the concrete processor as its abstract base (the Phase 27 expected shape).
        await using var provider = new ServiceCollection()
            .AddSingleton<TestProcessor>()
            .AddSingleton<BaseProcessorBase>(sp => sp.GetRequiredService<TestProcessor>())
            .BuildServiceProvider(true);

        var resolvedAsBase = provider.GetRequiredService<BaseProcessorBase>();
        Assert.IsType<TestProcessor>(resolvedAsBase);

        var concrete = provider.GetRequiredService<TestProcessor>();
        var results = await concrete.InvokeAsync("input", "config", ct);

        var single = Assert.Single(results);
        Assert.Same(TestProcessor.Result, single);
    }
}
