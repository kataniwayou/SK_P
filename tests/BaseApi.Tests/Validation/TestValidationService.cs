using FluentValidation;

namespace BaseApi.Tests.Validation;

/// <summary>
/// Thin Service-layer wrapper that calls <c>IValidator&lt;TestUpdateDto&gt;.ValidateAsync</c>
/// and throws <see cref="FluentValidation.ValidationException"/> on failure.
/// Used by SC#3 (VALID-03) — proves explicit Service-layer ValidateAsync (not MVC auto-validation)
/// produces the Phase 4 HTTP 400 ProblemDetails response.
/// </summary>
public sealed class TestValidationService
{
    private readonly IValidator<TestUpdateDto> _validator;

    public TestValidationService(IValidator<TestUpdateDto> validator) => _validator = validator;

    public async Task ValidateAsync(TestUpdateDto dto, CancellationToken ct)
    {
        var result = await _validator.ValidateAsync(dto, ct);
        if (!result.IsValid)
            throw new FluentValidation.ValidationException(result.Errors);
    }
}
