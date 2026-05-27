namespace BaseApi.Core.Validation;

/// <summary>
/// Marker interface exposing the three narrative fields shared by every domain DTO
/// (Create, Update, Read) — used as the generic constraint on
/// <see cref="BaseDtoValidator{T}"/> so shared validation rules target by member name.
///
/// <para>
/// Mirrors <see cref="BaseApi.Core.Entities.BaseEntity"/> (Phase 3) field nullability:
/// <c>Name</c> + <c>Version</c> are non-null with empty-string default;
/// <c>Description</c> is nullable.
/// </para>
///
/// <para>
/// Server-side fields (<c>Id</c>, <c>CreatedAt</c>, <c>UpdatedAt</c>, <c>CreatedBy</c>,
/// <c>UpdatedBy</c>) are NOT on this interface — they are owned by
/// <c>AuditInterceptor</c> per HTTP-05 and never appear on inbound DTOs (Phase 6 D-02).
/// </para>
/// </summary>
public interface IBaseDto
{
    string Name { get; }
    string Version { get; }
    string? Description { get; }
}
