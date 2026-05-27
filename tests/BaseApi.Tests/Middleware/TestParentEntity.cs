using BaseApi.Core.Entities;

namespace BaseApi.Tests.Middleware;

/// <summary>
/// Parent row for the FK-violation SQLSTATE test (ERROR-04, Plan 04-02 SC#3).
/// A child row with a non-existent ParentId triggers Postgres SQLSTATE 23503
/// against the <c>fk_testchild_parent_id</c> constraint, which the
/// <see cref="BaseApi.Core.Persistence.Exceptions.PostgresExceptionMapper"/>
/// (Option A regex, Plan 04-01 Task 5) extracts as <c>parent_id</c> in the
/// 422 response detail.
/// </summary>
public sealed class TestParentEntity : BaseEntity
{
}
