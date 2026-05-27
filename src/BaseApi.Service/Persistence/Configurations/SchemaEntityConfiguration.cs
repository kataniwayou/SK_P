using BaseApi.Service.Features.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BaseApi.Service.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="SchemaEntity"/>. Wires the
/// <c>Definition</c> column as Postgres <c>jsonb</c> (PERSIST-08) so the column is
/// efficiently stored and indexable. Other fields use the default mapping inferred
/// by <c>BaseDbContext</c> (snake_case naming + xmin shadow concurrency token).
/// </summary>
internal sealed class SchemaEntityConfiguration : IEntityTypeConfiguration<SchemaEntity>
{
    public void Configure(EntityTypeBuilder<SchemaEntity> entity)
    {
        entity.Property(e => e.Definition)
            .IsRequired()
            .HasColumnType("jsonb");  // PERSIST-08
    }
}
