using BaseApi.Core.Entities;

namespace BaseApi.Tests.Persistence;

/// <summary>
/// Trivial BaseEntity subclass used only by Phase 3 verification facts.
/// Has no entity-specific fields — exercises the BaseEntity audit + xmin
/// + snake_case wiring without depending on Phase 8's real entities.
/// </summary>
public sealed class TestEntity : BaseEntity
{
}
