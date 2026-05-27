using BaseApi.Core.Validation;
using FluentValidation;

namespace BaseApi.Tests.Validation;

/// <summary>
/// Concrete validator for SC#1 (VALID-04) — proves the
/// <c>Include(new BaseDtoValidator&lt;MyDto&gt;())</c> pattern absorbs the base
/// rules without restating Name/Version/Description rules in this body.
/// </summary>
public sealed class TestDtoValidator : AbstractValidator<TestUpdateDto>
{
    public TestDtoValidator()
    {
        Include(new BaseDtoValidator<TestUpdateDto>());
        // No body — SC#1 verifies BaseDtoValidator rules fire without restating them.
    }
}
