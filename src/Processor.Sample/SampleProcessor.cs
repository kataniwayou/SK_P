using System.Text.Json;                  // JsonSerializer — NOT in .NET 8 implicit usings
using BaseProcessor.Core.Configuration;  // ProcessorConfig.SerializerOptions
using BaseProcessor.Core.Processing;
using Messaging.Contracts;               // ExecutionLogScope.ExecutionId
using Microsoft.Extensions.Logging;

namespace Processor.Sample;

/// <summary>
/// The one concrete <see cref="BaseProcessor{SampleConfig}"/> (PROC-02/PROC-03). It overrides ONLY the
/// typed In-Process <c>ProcessAsync</c> seam; the framework owns deserialization (it hands in a typed
/// <see cref="SampleConfig"/>) and the entire Pre/Post/end-delete pipeline.
/// <para>
/// DELIBERATE multiple-execution stress test, keyed on the inbound <c>executionId</c>:
/// <list type="bullet">
///   <item><b>ENTRY/seed</b> (<c>executionId == Guid.Empty</c>): spawns TWO completed
///   <see cref="ProcessItem"/> executions, each a {number,label} JSON string with an INDEPENDENT random sum
///   over the config's <c>Number</c> and its own freshly-minted ExecutionId — each becomes an independently
///   traceable instance.</item>
///   <item><b>DOWNSTREAM</b> (<c>executionId != Guid.Empty</c>): returns ONE completed execution that REUSES
///   the inbound executionId; its sum is the inbound {number} plus the config's <c>Number</c>, deterministic
///   (NO random) so the per-instance accumulation is reproducible.</item>
/// </list>
/// Every execution emits ONE structured log carrying BOTH the <c>{StepLabel}</c> structured param AND that
/// execution's <c>ExecutionId</c> (via a nested <see cref="ExecutionLogScope.ExecutionId"/> BeginScope — the
/// bus-wide consume filter SKIPS ExecutionId when <c>Guid.Empty</c>, so the entry-minted ids MUST be added
/// here). The 6 correlation ids attach automatically from the ambient consume-filter scope (D-09).
/// </para>
/// </summary>
/// <remarks>
/// DI-registered <c>AddSingleton&lt;BaseProcessor, SampleProcessor&gt;</c> (Program.cs:17, unchanged) —
/// SampleProcessor IS-A BaseProcessor via BaseProcessor&lt;SampleConfig&gt;.
/// </remarks>
public sealed class SampleProcessor(ILogger<SampleProcessor> logger) : BaseProcessor<SampleConfig>
{
    /// <inheritdoc/>
    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, SampleConfig? config, Guid executionId, CancellationToken ct)
    {
        var baseNumber = config?.Number ?? 0;            // D-03 null-config default — warning-clean guard (Pitfall 2)
        var label      = config?.Label;                  // D-10 verbatim — already "Step_*", do NOT prepend

        if (executionId == Guid.Empty)
        {
            // ENTRY/seed: ONE dispatch spawns TWO completed executions, each an independent random sum and
            // its own freshly-minted ExecutionId — each is a new independently traceable instance.
            var items = new List<ProcessItem>(2);
            for (var i = 0; i < 2; i++)
            {
                var sum      = baseNumber + Random.Shared.Next(0, 100);   // 0..99 inclusive; independent random per execution
                var thisExec = Guid.NewGuid();                           // mint a NEW exec per spawned execution
                var data     = JsonSerializer.Serialize(
                    new { number = sum, label },
                    ProcessorConfig.SerializerOptions);
                // One structured log per execution: {StepLabel} stays AND the minted ExecutionId is added via a
                // nested scope (the consume filter skips Guid.Empty, so the minted id must be supplied here).
                using (logger.BeginScope(new Dictionary<string, object>
                {
                    [ExecutionLogScope.ExecutionId] = thisExec.ToString(),
                }))
                {
                    logger.LogInformation("step completed {StepLabel} sum {Sum}", label, sum);
                }
                items.Add(new(ProcessOutcome.Completed, data, thisExec));
            }
            return Task.FromResult(items);
        }

        // DOWNSTREAM: REUSE the inbound executionId, accumulate deterministically (NO random) so the
        // per-instance number is reproducible across steps.
        using var parsed = JsonDocument.Parse(validatedData);
        var incomingNumber = parsed.RootElement.GetProperty("number").GetInt32();
        var accumulated    = incomingNumber + baseNumber;   // deterministic accumulate
        var downstreamData = JsonSerializer.Serialize(
            new { number = accumulated, label },
            ProcessorConfig.SerializerOptions);
        using (logger.BeginScope(new Dictionary<string, object>
        {
            [ExecutionLogScope.ExecutionId] = executionId.ToString(),   // reuse inbound exec
        }))
        {
            logger.LogInformation("step completed {StepLabel} sum {Sum}", label, accumulated);
        }
        return Task.FromResult(new List<ProcessItem>(1)
        {
            new(ProcessOutcome.Completed, downstreamData, executionId),  // REUSE the inbound exec
        });
    }
}
