using System;
using System.Linq;
using Messaging.Contracts;
using Xunit;

// NOTE: namespace is deliberately BaseApi.Tests.MessagingContracts (NOT BaseApi.Tests.Messaging) —
// see ProcessorResponderTests / BaseApiCoreFirewallTests for why (a BaseApi.Tests.Messaging namespace
// would shadow the top-level Messaging namespace for sibling files referencing Messaging.Contracts.*).
namespace BaseApi.Tests.MessagingContracts;

/// <summary>
/// PAUSE-01 contract-shape proof (Wave 0 RED). Encodes the acceptance contract for the two
/// orchestrator control messages the phase ships BEFORE they exist:
/// <list type="bullet">
///   <item><description><c>PauseWorkflow</c> and <c>ResumeWorkflow</c> both implement
///   <see cref="ICorrelated"/> (body-carried correlation id, D-01) — so the bus-wide inbound
///   correlation filter propagates the originating CorrelationId rather than minting a fresh one.</description></item>
///   <item><description>Each record body-carries <c>WorkflowId</c> (Guid), <c>H</c> (the deterministic
///   effect identity, string), and <c>CorrelationId</c> (init-set Guid).</description></item>
/// </list>
/// These tests are RED until Plan 02 creates the records in <c>src/Messaging.Contracts/</c>; they fail
/// ONLY because <c>PauseWorkflow</c>/<c>ResumeWorkflow</c> do not yet exist (no harness errors).
/// </summary>
public sealed class PauseResumeContractTests
{
    [Fact]
    public void PauseWorkflow_ImplementsICorrelated()
    {
        Assert.Contains(typeof(ICorrelated), typeof(PauseWorkflow).GetInterfaces());
    }

    [Fact]
    public void ResumeWorkflow_ImplementsICorrelated()
    {
        Assert.Contains(typeof(ICorrelated), typeof(ResumeWorkflow).GetInterfaces());
    }

    [Fact]
    public void PauseWorkflow_BodyCarriesWorkflowId_H_AndCorrelationId()
    {
        var workflowId = Guid.NewGuid();
        var corr = Guid.NewGuid();

        var msg = new PauseWorkflow(workflowId, "h-val") { CorrelationId = corr };

        Assert.Equal(workflowId, msg.WorkflowId);
        Assert.Equal("h-val", msg.H);
        Assert.Equal(corr, msg.CorrelationId);
    }

    [Fact]
    public void ResumeWorkflow_BodyCarriesWorkflowId_H_AndCorrelationId()
    {
        var workflowId = Guid.NewGuid();
        var corr = Guid.NewGuid();

        var msg = new ResumeWorkflow(workflowId, "h-val") { CorrelationId = corr };

        Assert.Equal(workflowId, msg.WorkflowId);
        Assert.Equal("h-val", msg.H);
        Assert.Equal(corr, msg.CorrelationId);
    }
}
