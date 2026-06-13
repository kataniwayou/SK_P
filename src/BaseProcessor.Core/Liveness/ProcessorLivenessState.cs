using Messaging.Contracts.Projections;

namespace BaseProcessor.Core.Liveness;

/// <summary>
/// Lock-free L1 liveness holder (D-10). <c>public sealed</c> so
/// <c>services.AddSingleton&lt;IProcessorLivenessState, ProcessorLivenessState&gt;()</c> resolves across
/// the assembly boundary without <c>InternalsVisibleTo</c> — same reason as <c>ProcessorContext</c>.
/// </summary>
public sealed class ProcessorLivenessState : IProcessorLivenessState
{
    // volatile reference: atomic assignment + safe publication across the startup-thread /
    // heartbeat-thread writers and the Phase-61 probe-thread reader (D-10). Mirrors
    // ProcessorContext.IsHealthy's discipline. Reference-type assignment is atomic in the CLR.
    private volatile ProcessorLivenessEntry? _current;

    public void Update(ProcessorLivenessEntry entry) => _current = entry;
    public ProcessorLivenessEntry? Current => _current;
}
