using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BaseProcessor.Core.Configuration;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;
using BaseProcessor.Core.Processing;

namespace BaseApi.Tests.Processor;

/// <summary>
/// The typed-config seam (Phase 56): the generic <see cref="BaseProcessor{TConfig}"/> declares the author
/// seam <c>ProcessAsync(string validatedData, TConfig? config, CancellationToken ct)</c> returning
/// <see cref="List{ProcessItem}"/>; the framework owns the non-generic <c>internal ExecuteAsync</c> body.
/// A test-double subclass overriding the typed seam compiles, DI-resolves as the non-generic
/// <see cref="BaseProcessorBase"/>, and invoking the override returns the expected author-minted
/// <see cref="ProcessItem"/> list.
/// </summary>
public sealed class BaseProcessorSeamFacts
{
    /// <summary>A trivial author config for the test double — derives from the framework marker.</summary>
    private sealed record TestConfig(string? V) : ProcessorConfig;

    /// <summary>
    /// A concrete processor that overrides the typed seam and exposes it publicly so the test
    /// can invoke it directly (the framework invokes via the internal ExecuteAsync forwarder).
    /// </summary>
    private sealed class TestProcessor : BaseProcessor<TestConfig>
    {
        public static readonly ProcessItem Result = new(ProcessOutcome.Completed, "output", Guid.NewGuid());

        protected override Task<List<ProcessItem>> ProcessAsync(
            string validatedData, TestConfig? config, Guid executionId, CancellationToken ct)
            => Task.FromResult(new List<ProcessItem> { Result });

        public Task<List<ProcessItem>> InvokeAsync(
            string validatedData, TestConfig? config, Guid executionId, CancellationToken ct)
            => ProcessAsync(validatedData, config, executionId, ct);
    }

    [Fact]
    public async Task TestDouble_Overrides_Seam_DI_Resolves_And_Returns_Items()
    {
        var ct = TestContext.Current.CancellationToken;

        // DI-resolve the concrete processor as its abstract base (the production-expected shape).
        await using var provider = new ServiceCollection()
            .AddSingleton<TestProcessor>()
            .AddSingleton<BaseProcessorBase>(sp => sp.GetRequiredService<TestProcessor>())
            .BuildServiceProvider(true);

        var resolvedAsBase = provider.GetRequiredService<BaseProcessorBase>();
        Assert.IsType<TestProcessor>(resolvedAsBase);

        var concrete = provider.GetRequiredService<TestProcessor>();
        var results = await concrete.InvokeAsync("input", new TestConfig("config"), Guid.NewGuid(), ct);

        var single = Assert.Single(results);
        Assert.Same(TestProcessor.Result, single);
    }
}
