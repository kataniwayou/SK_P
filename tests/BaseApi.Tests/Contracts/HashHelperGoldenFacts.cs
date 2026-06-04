using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Messaging.Contracts.Hashing;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// Wave-0 golden facts (req-1 + req-7). Pins the cross-process determinism invariant BEFORE any
/// consumer uses the helper: <see cref="MessageIdentity.ComputeH"/> is a stable, byte-identical,
/// field-sensitive lowercase 64-hex; the per-execution lineage id is structurally excluded from H
/// (the 5-arg signature has no such parameter, D-02); the L2 <c>data</c>/<c>flag</c> key builders
/// emit <c>skp:{data|flag}:{64hex}</c>; the empty manifest hashes to a stable terminal EntryId; and
/// the helper matches the cross-process SourceHash UTF-8 -&gt; SHA-256 -&gt; lowercase-x2 convention.
/// Hermetic (default Category) — runs in the &lt;30s tier, no real stack.
/// </summary>
public sealed class HashHelperGoldenFacts
{
    // Fixed identity vector — literal Guids so the golden is reproducible forever.
    private static readonly Guid Correlation = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Workflow    = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Step         = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Processor    = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string EntryHex = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    // Pinned goldens — captured once from the helper, then frozen (RED -> capture -> GREEN).
    private const string GoldenH = "5fc25824864d45758719cc0928689e4ade21a0cb7dc771cb34f27eccd043985f";
    private const string GoldenEmptyManifest = "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945";

    [Fact]
    public void H_FixedVector_MatchesGolden()
    {
        Assert.Equal(GoldenH, MessageIdentity.ComputeH(Correlation, Workflow, Step, Processor, EntryHex));
    }

    [Fact]
    public void H_FixedVector_Is_64_Lowercase_Hex()
    {
        Assert.Matches("^[a-f0-9]{64}$", MessageIdentity.ComputeH(Correlation, Workflow, Step, Processor, EntryHex));
    }

    [Fact]
    public void H_Recompute_IsByteIdentical()
    {
        var first  = MessageIdentity.ComputeH(Correlation, Workflow, Step, Processor, EntryHex);
        var second = MessageIdentity.ComputeH(Correlation, Workflow, Step, Processor, EntryHex);
        Assert.Equal(first, second);   // simulated retry — same 5 fields, identical H
    }

    public enum Field { Correlation, Workflow, Step, Processor, Entry }

    [Theory]
    [InlineData(Field.Correlation)]
    [InlineData(Field.Workflow)]
    [InlineData(Field.Step)]
    [InlineData(Field.Processor)]
    [InlineData(Field.Entry)]
    public void H_ChangingAnyField_ChangesHash(Field which)
    {
        var baseline = MessageIdentity.ComputeH(Correlation, Workflow, Step, Processor, EntryHex);

        var other = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var mutated = which switch
        {
            Field.Correlation => MessageIdentity.ComputeH(other, Workflow, Step, Processor, EntryHex),
            Field.Workflow    => MessageIdentity.ComputeH(Correlation, other, Step, Processor, EntryHex),
            Field.Step        => MessageIdentity.ComputeH(Correlation, Workflow, other, Processor, EntryHex),
            Field.Processor   => MessageIdentity.ComputeH(Correlation, Workflow, Step, other, EntryHex),
            Field.Entry       => MessageIdentity.ComputeH(Correlation, Workflow, Step, Processor, "0000000000000000000000000000000000000000000000000000000000000000"),
            _ => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        Assert.NotEqual(baseline, mutated);
    }

    [Fact]
    public void ComputeH_HasNo_ExecutionId_Parameter_StructurallyExcluded()
    {
        // D-02: H is computed only from the 5 identity fields. There is no lineage-id parameter, so two
        // unrelated "surrounding" lineage Guids cannot affect H — we demonstrate it by computing H twice
        // (the lineage ids exist only in the test, never reach the helper) and asserting identity.
        var lineageA = Guid.NewGuid();
        var lineageB = Guid.NewGuid();
        Assert.NotEqual(lineageA, lineageB);   // genuinely different lineage ids

        var hWithA = MessageIdentity.ComputeH(Correlation, Workflow, Step, Processor, EntryHex);
        var hWithB = MessageIdentity.ComputeH(Correlation, Workflow, Step, Processor, EntryHex);
        Assert.Equal(hWithA, hWithB);          // lineage id is not an input — H is invariant to it

        // Structural guarantee: the signature accepts exactly 4 Guids + 1 string and nothing else.
        var parameters = typeof(MessageIdentity).GetMethod(nameof(MessageIdentity.ComputeH))!.GetParameters();
        Assert.Equal(5, parameters.Length);
        Assert.DoesNotContain(parameters, p => p.Name!.Contains("execution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExecutionData_And_Flag_Match64HexFormat()
    {
        var hex = MessageIdentity.ComputeH(Correlation, Workflow, Step, Processor, EntryHex);
        Assert.Matches("^skp:data:[a-f0-9]{64}$", L2ProjectionKeys.ExecutionData(hex));
        Assert.Matches("^skp:flag:[a-f0-9]{64}$", L2ProjectionKeys.Flag(hex));
    }

    [Fact]
    public void HashManifest_Empty_MatchesGolden()
    {
        // "[]" is the JSON of an empty manifest — the empty-result terminal EntryId (req-3 precursor).
        Assert.Equal(GoldenEmptyManifest, MessageIdentity.HashManifest("[]"));
    }

    [Fact]
    public void HashBlob_MatchesSourceHashConvention()
    {
        const string sample = "some-output-blob";

        // INDEPENDENT reference computed exactly per the cross-process SourceHash convention
        // (UTF-8 -> SHA-256 -> lowercase x2), NOT via the helper under test.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sample));
        var reference = string.Concat(bytes.Select(b => b.ToString("x2")));

        Assert.Equal(reference, MessageIdentity.HashBlob(sample));
    }
}
