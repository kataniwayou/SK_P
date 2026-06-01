using BaseProcessor.Core.Identity;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// IDENT-03 / T-26-02 (DoS, fail-fast): <see cref="AssemblyMetadataSourceHashProvider"/> reads the
/// <c>[assembly: AssemblyMetadata("SourceHash", ...)]</c> attribute via reflection and THROWS
/// <see cref="InvalidOperationException"/> when the attribute is absent (rather than returning
/// null/empty that would drive an unbounded silent not-found retry on an empty hash).
///
/// <para>
/// This phase's test assembly (BaseApi.Tests) carries NO <c>SourceHash</c> assembly-metadata
/// attribute, so the default provider exercises the throw path. The happy path is exercised by an
/// NSubstitute stub of <see cref="ISourceHashProvider"/> in later plans (the embed target is
/// Phase 28); see the message-names-the-KEY-only assertion for the V7 info-disclosure mitigation.
/// </para>
/// </summary>
public sealed class SourceHashProviderFacts
{
    [Fact]
    public void Get_Throws_When_SourceHash_Attribute_Absent()
    {
        var provider = new AssemblyMetadataSourceHashProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.Get());

        // The message names the KEY ('SourceHash'), never a value (V7 / info-disclosure mitigation).
        Assert.Contains("SourceHash", ex.Message);
    }
}
