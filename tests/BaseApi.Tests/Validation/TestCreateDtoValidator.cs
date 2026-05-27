using BaseApi.Core.Validation;
using FluentValidation;

namespace BaseApi.Tests.Validation;

/// <summary>
/// Sibling of <see cref="TestDtoValidator"/> covering the CREATE side.
/// Blocker 2 fix (Plan 07-02 revision iter 1): BaseService&lt;TestEntity, TestCreateDto, TestUpdateDto, TestReadDto&gt;
/// guards <c>IValidator&lt;TestCreateDto&gt;</c> in its ctor with <see cref="InvalidOperationException"/>
/// (Plan 07-01 discretion option a). Phase7WebAppFactory's AddBaseApiValidation assembly scan
/// finds this class; without it, the boot throws.
///
/// Mirrors TestDtoValidator's <c>Include(new BaseDtoValidator&lt;T&gt;())</c> pattern — no body
/// needed; the absorbed base rules are sufficient for Phase 7 verification (Phase 8's
/// concrete validators may add property-specific rules).
/// </summary>
public sealed class TestCreateDtoValidator : AbstractValidator<TestCreateDto>
{
    public TestCreateDtoValidator()
    {
        Include(new BaseDtoValidator<TestCreateDto>());
        // No body — Phase 7 only needs IValidator<TestCreateDto> to RESOLVE (not to reject inputs).
    }
}
