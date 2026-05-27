using BaseApi.Core.Entities;

namespace BaseApi.Tests.Middleware;

/// <summary>
/// Child row with FK to <see cref="TestParentEntity"/> (FK violation surface for
/// ERROR-04) AND a unique constraint on its inherited <see cref="BaseEntity.Name"/>
/// field (UQ violation surface for ERROR-05). Constraint names follow Phase 8's
/// public ERROR-11 convention so the Option A regex (Plan 04-01 Task 5) extracts
/// <c>parent_id</c> (FK) and <c>name</c> (UQ) cleanly.
/// </summary>
public sealed class TestChildEntity : BaseEntity
{
    public Guid ParentId { get; set; }
}
