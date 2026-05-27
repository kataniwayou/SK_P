namespace BaseApi.Core.Contracts;

/// <summary>
/// Marker interface for Read DTOs (TRead) so <see cref="Controllers.BaseController{TEntity,TCreate,TUpdate,TRead}"/>
/// can read <c>Id</c> in its <c>CreatedAtAction</c> call without resorting to dynamic dispatch
/// or reflection. Phase 8 Read DTOs implement this trivially (HTTP-07 already requires
/// <c>Id</c> on every Read DTO).
/// </summary>
public interface IHasId
{
    /// <summary>The unique identifier surfaced on the Read DTO.</summary>
    Guid Id { get; }
}
