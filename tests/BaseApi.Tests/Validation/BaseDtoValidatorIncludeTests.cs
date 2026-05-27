using Xunit;

namespace BaseApi.Tests.Validation;

/// <summary>
/// SC#1 / VALID-04 — proves <c>Include(new BaseDtoValidator&lt;MyDto&gt;())</c> absorbs
/// the base Name/Version/Description rules without restating them in the concrete
/// validator's body (<see cref="TestDtoValidator"/> has only the <c>Include</c> call,
/// no <c>RuleFor</c> calls).
/// </summary>
public sealed class BaseDtoValidatorIncludeTests
{
    [Fact]
    public void Test_Include_AbsorbsBaseRules_WithoutRestating()
    {
        // Arrange: TestDtoValidator constructor calls Include(new BaseDtoValidator<TestUpdateDto>())
        // and has NO RuleFor calls of its own. The bad DTO violates BOTH Name (empty) AND Version (bad shape).
        var validator = new TestDtoValidator();
        var bad = new TestUpdateDto(Name: "", Version: "v1", Description: null, Note: "n");

        // Act
        var result = validator.Validate(bad);

        // Assert: BOTH base rules fired despite TestDtoValidator's empty body — proof Include worked.
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Name");
        Assert.Contains(result.Errors, e => e.PropertyName == "Version");
    }

    [Fact]
    public void Test_Include_HappyPath_GoodDtoValid()
    {
        var validator = new TestDtoValidator();
        var good = new TestUpdateDto(Name: "alice", Version: "1.0.0", Description: "ok", Note: "n");

        var result = validator.Validate(good);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }
}
