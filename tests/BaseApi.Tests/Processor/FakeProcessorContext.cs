using BaseProcessor.Core.Identity;
using Messaging.Contracts;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Hand-rolled <see cref="IProcessorContext"/> test double for the liveness-heartbeat facts: the
/// heartbeat only READS <see cref="IsHealthy"/> / <see cref="Id"/> / <see cref="InputDefinition"/> /
/// <see cref="OutputDefinition"/>, so the mutators are no-ops and the latch members are inert. Properties
/// are caller-settable so each fact configures the not-yet-Healthy vs Healthy shape directly.
/// </summary>
internal sealed class FakeProcessorContext : IProcessorContext
{
    // Defaults to a resolved identity: the EntryStepDispatchConsumer increments processor metrics tagged
    // context.Id!.Value at the top of Consume (METRIC-05) and runs ONLY post-MarkHealthy at runtime, so a
    // consumer fact needs a non-null Id to reflect that invariant. Liveness facts that exercise the
    // not-yet-Healthy "no Id → no write" path override this explicitly with `Id = null`.
    public Guid? Id { get; set; } = Guid.NewGuid();
    public Guid? InputSchemaId { get; set; }
    public Guid? OutputSchemaId { get; set; }
    public Guid? ConfigSchemaId { get; set; }
    public string? InputDefinition { get; set; }
    public string? OutputDefinition { get; set; }
    public bool IsHealthy { get; set; }
    public Task WhenHealthy { get; } = Task.CompletedTask;

    public void SetIdentity(ProcessorIdentityFound identity) { }
    public void SetDefinition(Guid schemaId, string definition) { }
    public void MarkHealthy() => IsHealthy = true;
}
