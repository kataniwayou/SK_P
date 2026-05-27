using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Tests.Composition;
using BaseApi.Tests.Persistence;
using BaseApi.Tests.Validation;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

// Deviation [Rule 1 - Bug]: `BaseApi.Tests.Persistence.TestEntity` (Phase 3) and
// `BaseApi.Tests.Validation.TestEntity` (Phase 6) coexist in the same assembly.
// We import the Persistence namespace for `PostgresFixture` but need Validation.TestEntity
// for the Phase 7 ordering fact. An extern alias is unavailable (both are in the same
// assembly); a type alias `using` directive disambiguates without renaming either source.
using TestEntity = BaseApi.Tests.Validation.TestEntity;

namespace BaseApi.Tests.Services;

/// <summary>
/// SC#2 — BaseService.CreateAsync runs in the locked 6-step order:
/// validate -> ToEntity -> repo.Add -> SyncJunctionsAsync -> SaveChanges -> ToRead.
/// SyncJunctionsAsync must see <see cref="EntityState.Added"/> on the tracker.
/// </summary>
public sealed class BaseServiceOrderingFacts : IAsyncLifetime
{
    private PostgresFixture _fixture = null!;
    private Phase7TestDbContext _dbContext = null!;

    public async ValueTask InitializeAsync()
    {
        // Warning 5 fix: PostgresFixture.InitializeAsync is PARAMETERLESS — no CancellationToken arg.
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        // Blocker 1 fix: PostgresFixture has NO `DbContext` property, only `ConnectionString`.
        // Construct Phase7TestDbContext directly so ChangeTracker tracks
        // BaseApi.Tests.Validation.TestEntity (the type the fact uses).
        var opts = new DbContextOptionsBuilder<Phase7TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _dbContext = new Phase7TestDbContext(opts);
        await _dbContext.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_Runs_SixSteps_In_LockedOrder()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange — NSubstitute mocks for the 3 collaborators that DO get mocked.
        // Validator/Mapper/Repo are mocked; DbContext is REAL (needed for ChangeTracker state).
        var createValidator = Substitute.For<IValidator<TestCreateDto>>();
        var updateValidator = Substitute.For<IValidator<TestUpdateDto>>();
        var mapper          = Substitute.For<IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>();
        var repo            = Substitute.For<IRepository<TestEntity>>();

        DateTime? validateTime = null, mapTime = null, addTime = null, readTime = null;

        // Deviation [Rule 1 - Bug from plan body]: BaseService.CreateAsync calls
        // `_createValidator.ValidateAndThrowAsync(dto, ct)`. That FluentValidation extension
        // method internally invokes the base IValidator.ValidateAsync(IValidationContext, CT)
        // overload (NOT the IValidator<T>.ValidateAsync(T, CT) generic overload that the plan
        // body mocked). Mocking the wrong overload leaves the real validator stub returning a
        // default Task and validateTime is never written. Mock the IValidationContext overload
        // explicitly to capture the call.
        ((IValidator)createValidator)
            .ValidateAsync(Arg.Any<IValidationContext>(), Arg.Any<CancellationToken>())
            .Returns(call => { validateTime = DateTime.UtcNow; return Task.FromResult(new ValidationResult()); });

        var mappedEntity = new TestEntity { Id = Guid.NewGuid(), Name = "x", Version = "1.0.0", Note = "" };
        mapper.ToEntity(Arg.Any<TestCreateDto>())
            .Returns(call => { mapTime = DateTime.UtcNow; return mappedEntity; });

        repo.AddAsync(Arg.Any<TestEntity>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                addTime = DateTime.UtcNow;
                // Stage the entity into the REAL DbContext so SyncJunctionsAsync sees EntityState.Added.
                await _dbContext.AddAsync((TestEntity)call.Args()[0]!, (CancellationToken)call.Args()[1]!);
            });

        mapper.ToRead(Arg.Any<TestEntity>())
            .Returns(call =>
            {
                readTime = DateTime.UtcNow;
                return new TestReadDto(mappedEntity.Id, "x", "1.0.0", null, "");
            });

        var service = new RecordingTestService(
            createValidator, updateValidator, mapper, repo,
            _dbContext);

        // Act
        var dto = new TestCreateDto("x", "1.0.0", null, "note");
        _ = await service.CreateAsync(dto, ct);

        // Assert — timestamps fired in order:
        Assert.NotNull(validateTime); Assert.NotNull(mapTime);
        Assert.NotNull(addTime);      Assert.NotNull(readTime);
        Assert.Single(service.JunctionRecords);
        var (junctionTime, trackerState) = service.JunctionRecords[0];

        Assert.True(validateTime <= mapTime,  "Step 1 validate must precede Step 2 ToEntity");
        Assert.True(mapTime      <= addTime,  "Step 2 ToEntity must precede Step 3 AddAsync");
        Assert.True(addTime      <= junctionTime, "Step 3 AddAsync must precede Step 4 SyncJunctionsAsync");
        Assert.True(junctionTime <= readTime, "Step 4 SyncJunctionsAsync must precede Step 6 ToRead");
        Assert.Equal(EntityState.Added, trackerState);

        // Cross-mock ordered grammar — same overload reconciliation as the .Returns setup above:
        // `Received.InOrder` must replay the actual interface call, which is the
        // IValidator.ValidateAsync(IValidationContext, CT) overload.
        Received.InOrder(() =>
        {
            ((IValidator)createValidator).ValidateAsync(Arg.Any<IValidationContext>(), Arg.Any<CancellationToken>());
            mapper.ToEntity(Arg.Any<TestCreateDto>());
            repo.AddAsync(Arg.Any<TestEntity>(), Arg.Any<CancellationToken>());
            mapper.ToRead(Arg.Any<TestEntity>());
        });
    }
}
