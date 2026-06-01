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
    public Guid? Id { get; set; }
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
