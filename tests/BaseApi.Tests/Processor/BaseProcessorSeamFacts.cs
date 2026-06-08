using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;
using BaseProcessor.Core.Processing;

namespace BaseApi.Tests.Processor;

/// <summary>
/// D-12 / BPC-02 (Phase 44 retype): the abstract <see cref="BaseProcessorBase"/> declares exactly one
/// seam — the In-Process <c>ProcessAsync(string validatedData, string payload, CancellationToken ct)</c>
/// returning <see cref="List{ProcessItem}"/>. A test-double subclass overriding it compiles and
/// DI-resolves; calling the override returns the expected author-minted <see cref="ProcessItem"/> list.
/// </summary>
public sealed class BaseProcessorSeamFacts
{
    /// <summary>
    /// A concrete processor that overrides the protected seam and exposes it publicly so the test
    /// can invoke it directly (the framework invokes via the internal ExecuteAsync forwarder).
    /// </summary>
    private sealed class TestProcessor : BaseProcessorBase
    {
        public static readonly ProcessItem Result = new(ProcessOutcome.Completed, "output", Guid.NewGuid());

        protected override Task<List<ProcessItem>> ProcessAsync(
            string validatedData, string payload, CancellationToken ct)
            => Task.FromResult(new List<ProcessItem> { Result });

        public Task<List<ProcessItem>> InvokeAsync(string validatedData, string payload, CancellationToken ct)
            => ProcessAsync(validatedData, payload, ct);
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
        var results = await concrete.InvokeAsync("input", "config", ct);

        var single = Assert.Single(results);
        Assert.Same(TestProcessor.Result, single);
    }
}
