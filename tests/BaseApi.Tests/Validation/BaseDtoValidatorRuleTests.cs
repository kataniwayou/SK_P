using BaseApi.Core.Validation;
using Xunit;

namespace BaseApi.Tests.Validation;

/// <summary>
/// Verifies the three <see cref="BaseDtoValidator{T}"/> rules
/// (VALID-05 / VALID-06 / VALID-07) by directly instantiating the validator and
/// asserting <c>ValidationResult.IsValid</c> + per-property error membership.
/// No DI, no HTTP wire — pure unit test.
/// </summary>
public sealed class BaseDtoValidatorRuleTests
{
    private static BaseDtoValidator<TestUpdateDto> NewValidator() => new();

    private static TestUpdateDto Dto(string name = "ok", string version = "1.0.0", string? description = null, string note = "n")
        => new(name, version, description, note);

    // ---------- VALID-05: Name NotEmpty + MaxLength(200) ----------

    [Fact]
    public void Test_Name_Empty_Rejected()
    {
        var result = NewValidator().Validate(Dto(name: ""));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Name");
    }

    [Fact]
    public void Test_Name_201Chars_Rejected()
    {
        var name201 = new string('a', 201);
        var result = NewValidator().Validate(Dto(name: name201));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Name");
    }

    [Fact]
    public void Test_Name_200Chars_Accepted()
    {
        var name200 = new string('a', 200);
        var result = NewValidator().Validate(Dto(name: name200));
        // Other rules may still hold; only assert no Name error
        Assert.DoesNotContain(result.Errors, e => e.PropertyName == "Name");
    }

    // ---------- VALID-06: Version strict SemVer ----------

    [Theory]
    [InlineData("")]
    [InlineData("01.0.0")]   // leading zero
    [InlineData("1.0")]      // only two parts
    [InlineData("v1.0.0")]   // prefix
    [InlineData("1.0.0-alpha")]  // pre-release tag
    [InlineData("1.0.0+build")]  // build metadata
    public void Test_Version_BadShape_Rejected(string version)
    {
        var result = NewValidator().Validate(Dto(version: version));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Version");
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.0.0")]
    [InlineData("99.99.99")]
    [InlineData("1.2.3")]
    public void Test_Version_StrictSemVer_Accepted(string version)
    {
        var result = NewValidator().Validate(Dto(version: version));
        Assert.DoesNotContain(result.Errors, e => e.PropertyName == "Version");
    }

    // ---------- VALID-07: Description MaxLength(2000), null valid ----------

    [Fact]
    public void Test_Description_Null_Accepted()
    {
        var result = NewValidator().Validate(Dto(description: null));
        Assert.DoesNotContain(result.Errors, e => e.PropertyName == "Description");
    }

    [Fact]
    public void Test_Description_2001Chars_Rejected()
    {
        var d2001 = new string('x', 2001);
        var result = NewValidator().Validate(Dto(description: d2001));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Description");
    }

    [Fact]
    public void Test_Description_2000Chars_Accepted()
    {
        var d2000 = new string('x', 2000);
        var result = NewValidator().Validate(Dto(description: d2000));
        Assert.DoesNotContain(result.Errors, e => e.PropertyName == "Description");
    }
}
