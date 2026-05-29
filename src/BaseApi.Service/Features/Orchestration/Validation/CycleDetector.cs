using BaseApi.Service.Features.Orchestration;

namespace BaseApi.Service.Features.Orchestration.Validation;

/// <summary>Phase 14 fills this body and throws a 422-mapped exception on a detected cycle. No-op in P13.</summary>
internal sealed class CycleDetector
{
    public void Validate(WorkflowGraphSnapshot snapshot) { /* no-op in P13 */ }
}
