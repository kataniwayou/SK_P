using BaseApi.Core.DependencyInjection;
using BaseApi.Core.Mapping;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Validation;

/// <summary>
/// SC#4 DI half / HTTP-10 — <see cref="MappingServiceCollectionExtensions.AddBaseApiMapping"/>
/// scans assemblies for closed-generic <c>IEntityMapper&lt;,,,&gt;</c> implementations
/// and registers each as Singleton (Phase 6 D-15).
/// </summary>
public sealed class MapperRegistrationTests
{
    [Fact]
    public void Test_AddBaseApiMapping_RegistersClosedGenericInterface()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBaseApiMapping(typeof(TestEntityMapper).Assembly);
        using var provider = services.BuildServiceProvider();

        // Act
        var mapper = provider.GetService<IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>();

        // Assert
        Assert.NotNull(mapper);
        Assert.IsType<TestEntityMapper>(mapper);
    }

    [Fact]
    public void Test_AddBaseApiMapping_RegistersAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBaseApiMapping(typeof(TestEntityMapper).Assembly);
        using var provider = services.BuildServiceProvider();

        // Act: resolve twice from the root provider.
        var m1 = provider.GetRequiredService<IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>();
        var m2 = provider.GetRequiredService<IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>();

        // Assert: same instance — Singleton lifetime per D-15.
        Assert.Same(m1, m2);
    }
}
