using BaseApi.Service.Features.Orchestration;

namespace BaseApi.Service.Features.Orchestration.Validation;

/// <summary>Phase 14 fills this body and throws a 422-mapped exception on a Payload↔ConfigSchema conformance failure. No-op in P13.</summary>
internal sealed class PayloadConfigSchemaValidator
{
    public void Validate(WorkflowGraphSnapshot snapshot) { /* no-op in P13 */ }
}
