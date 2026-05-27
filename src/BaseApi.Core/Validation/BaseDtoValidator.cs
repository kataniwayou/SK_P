using FluentValidation;

namespace BaseApi.Core.Validation;

/// <summary>
/// Reusable FluentValidation 12.x validator providing the <see cref="IBaseDto"/>
/// shared field rules: <c>Name</c> NotEmpty + MaxLength(200) (VALID-05),
/// <c>Version</c> NotEmpty + strict-SemVer regex (VALID-06), <c>Description</c>
/// MaxLength(2000) (VALID-07).
///
/// <para>
/// Concrete validators (Phase 8) absorb these rules by calling
/// <c>Include(new BaseDtoValidator&lt;MyDto&gt;())</c> in their constructor.
/// Public non-sealed (Phase 6 D-05) — must be instantiable from the
/// <c>Include</c> call site AND inheritable for forward compatibility.
/// </para>
///
/// <para>
/// <b>SemVer regex (Phase 6 D-05 / VALID-06 / RESEARCH Pitfall 3):</b>
/// strict numeric triple, no leading zeros, no pre-release tag.
/// Verbatim C# string literal <c>@"..."</c> mandatory — <c>\d</c> in a regular
/// literal triggers <c>CS1009 Unrecognized escape sequence</c> under
/// <c>TreatWarningsAsErrors=true</c> (Phase 1 D-02).
/// </para>
/// </summary>
public class BaseDtoValidator<T> : AbstractValidator<T>
    where T : IBaseDto
{
    public BaseDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Version)
            .NotEmpty()
            .Matches(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$");

        RuleFor(x => x.Description)
            .MaximumLength(2000);
    }
}
