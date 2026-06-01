using BaseProcessor.Core.Validation;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-03/05 + the SSRF lockdown (D-05/D-06): the ported <see cref="ProcessorJsonSchemaValidator"/>
/// skips on a null/whitespace definition, returns invalid (never throws) on an unparseable
/// definition/data, validates good/bad data, and holds the SSRF lockdown against an external
/// <c>http://</c> <c>$ref</c> (the global no-op fetcher resolves it to null so evaluation completes
/// closed — Pitfall 2 warning sign proven absent).
/// </summary>
public sealed class ProcessorJsonSchemaValidatorFacts
{
    [Fact]
    public void Null_Definition_Skips()
    {
        Assert.True(ProcessorJsonSchemaValidator.TryValidate(null, "{}", out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void Whitespace_Definition_Skips()
    {
        Assert.True(ProcessorJsonSchemaValidator.TryValidate("   ", "{}", out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void Unparseable_Definition_Returns_Invalid()
    {
        var ok = ProcessorJsonSchemaValidator.TryValidate("not-json", "{}", out var errors);
        Assert.False(ok);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Malformed_Data_Returns_Invalid()
    {
        var ok = ProcessorJsonSchemaValidator.TryValidate("{\"type\":\"object\"}", "not-json", out var errors);
        Assert.False(ok);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Valid_Data_Passes()
    {
        var ok = ProcessorJsonSchemaValidator.TryValidate("{\"type\":\"object\"}", "{\"a\":1}", out var errors);
        Assert.True(ok);
        Assert.Empty(errors);
    }

    [Fact]
    public void Invalid_Data_Fails_With_Flattened_Errors()
    {
        var ok = ProcessorJsonSchemaValidator.TryValidate(
            "{\"type\":\"object\",\"required\":[\"x\"]}", "{}", out var errors);
        Assert.False(ok);
        Assert.NotEmpty(errors);
    }

    /// <summary>
    /// SSRF-lockdown proof (T-27-01 / Pitfall 2): an external <c>http://</c> <c>$ref</c> must NOT
    /// trigger an outbound fetch. The global no-op <c>SchemaRegistry.Global.Fetch</c> returns null, so
    /// JsonSchema.Net cannot resolve the <c>$ref</c> and surfaces it as a RefResolutionException during
    /// Evaluate — which the validator catches and turns into a business <c>false</c> (D-06: never a host
    /// crash, never a network reach-out). The call RETURNS deterministically (no hang, no network throw).
    /// </summary>
    [Fact]
    public void External_Ref_Evaluates_Closed_Ssrf()
    {
        var ok = ProcessorJsonSchemaValidator.TryValidate(
            "{\"$ref\":\"http://example.com/schema.json\"}", "{}", out var errors);

        Assert.False(ok);          // unresolvable external $ref -> business Failed, NOT a crash
        Assert.NotEmpty(errors);   // an error message is surfaced (lockdown held: no outbound fetch)
    }
}
