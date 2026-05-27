using Xunit;

namespace BaseApi.Tests.Validation;

/// <summary>
/// SC#4 source-gen output verification — exercises the three Mapperly-generated
/// partial methods on <see cref="TestEntityMapper"/> to prove (a) the source generator
/// emitted method bodies (no abstract-method exception), (b) ToEntity copies the 4
/// Create-DTO fields, (c) Update mutates entity in place while preserving server-side
/// fields (the 5 [MapperIgnoreTarget] attributes are doing their job), (d) ToRead
/// projects entity → read DTO including Id.
///
/// <para>
/// Test compile-success itself is the BUILD-half of SC#4 (proven at build time by
/// Plan 06-01's Directory.Build.props RMG promotion + Plan 06-02 Task 1's
/// [MapperIgnoreTarget] attributes). This test class proves runtime behavior.
/// </para>
/// </summary>
public sealed class MapperlyCompileTests
{
    [Fact]
    public void Test_ToEntity_CopiesAllCreateDtoFields()
    {
        var mapper = new TestEntityMapper();
        var create = new TestCreateDto(Name: "alice", Version: "1.0.0", Description: "hi", Note: "n");

        var entity = mapper.ToEntity(create);

        Assert.Equal("alice", entity.Name);
        Assert.Equal("1.0.0", entity.Version);
        Assert.Equal("hi", entity.Description);
        Assert.Equal("n", entity.Note);
    }

    [Fact]
    public void Test_Update_MutatesTargetInPlace_PreservesServerSideFields()
    {
        var mapper = new TestEntityMapper();

        // Existing entity with server-side fields populated (simulates a row read from DB).
        var existing = new TestEntity
        {
            Id = Guid.NewGuid(),
            Name = "old-name",
            Version = "1.0.0",
            Description = "old",
            CreatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = "alice",
            UpdatedBy = "alice",
            Note = "old-note",
        };
        var originalId = existing.Id;
        var originalCreatedAt = existing.CreatedAt;
        var originalCreatedBy = existing.CreatedBy;

        var update = new TestUpdateDto(Name: "new-name", Version: "2.0.0", Description: null, Note: "new-note");

        mapper.Update(update, existing);

        // Update DTO fields mutated:
        Assert.Equal("new-name", existing.Name);
        Assert.Equal("2.0.0", existing.Version);
        Assert.Null(existing.Description);
        Assert.Equal("new-note", existing.Note);

        // Server-side fields preserved (the 5 [MapperIgnoreTarget] attributes did their job):
        Assert.Equal(originalId, existing.Id);
        Assert.Equal(originalCreatedAt, existing.CreatedAt);
        Assert.Equal(originalCreatedBy, existing.CreatedBy);
    }

    [Fact]
    public void Test_ToRead_ProjectsEntityToReadDto_IncludingId()
    {
        var mapper = new TestEntityMapper();
        var entity = new TestEntity
        {
            Id = Guid.NewGuid(),
            Name = "alice",
            Version = "1.0.0",
            Description = "hi",
            Note = "n",
        };

        var read = mapper.ToRead(entity);

        Assert.Equal(entity.Id, read.Id);
        Assert.Equal("alice", read.Name);
        Assert.Equal("1.0.0", read.Version);
        Assert.Equal("hi", read.Description);
        Assert.Equal("n", read.Note);
    }
}
