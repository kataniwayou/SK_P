using BaseApi.Core.DependencyInjection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Validation;

/// <summary>
/// SC#2 / VALID-02 — <see cref="ValidationServiceCollectionExtensions.AddBaseApiValidation"/>
/// discovers <see cref="TestDtoValidator"/> from the assembly scan with NO manual
/// per-validator <c>AddScoped&lt;IValidator&lt;T&gt;&gt;</c> registration.
/// </summary>
public sealed class ValidatorAutoDiscoveryTests
{
    [Fact]
    public void Test_AddBaseApiValidation_DiscoversTestDtoValidator()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBaseApiValidation(typeof(TestDtoValidator).Assembly);
        using var provider = services.BuildServiceProvider();

        // Act
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetService<IValidator<TestUpdateDto>>();

        // Assert
        Assert.NotNull(validator);
        Assert.IsType<TestDtoValidator>(validator);
    }

    [Fact]
    public void Test_AddBaseApiValidation_DefaultLifetime_IsScoped()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBaseApiValidation(typeof(TestDtoValidator).Assembly);
        using var provider = services.BuildServiceProvider();

        // Act: two resolutions from the SAME scope should return the SAME instance.
        using var scope = provider.CreateScope();
        var a = scope.ServiceProvider.GetRequiredService<IValidator<TestUpdateDto>>();
        var b = scope.ServiceProvider.GetRequiredService<IValidator<TestUpdateDto>>();

        // Assert
        Assert.Same(a, b);

        // Different scope returns a different instance (Scoped, not Singleton).
        using var scope2 = provider.CreateScope();
        var c = scope2.ServiceProvider.GetRequiredService<IValidator<TestUpdateDto>>();
        Assert.NotSame(a, c);
    }
}
