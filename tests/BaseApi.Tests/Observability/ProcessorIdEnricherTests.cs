using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// LOG-04: the <see cref="ProcessorIdLogEnricher"/> appends <c>ProcessorId</c> (from the singleton
/// <see cref="IProcessorContext.Id"/>) to a processor <see cref="LogRecord"/>, null-safe:
/// <list type="bullet">
///   <item>Case A (Id set) → the FINAL <c>record.Attributes</c> carries exactly one
///         <c>KeyValuePair("ProcessorId", id.ToString())</c>.</item>
///   <item>Case B (Id null) → NO "ProcessorId" attribute, no exception, no <see cref="System.Guid.Empty"/>
///         anywhere.</item>
/// </list>
/// The test wires the SUT enricher then a downstream in-memory capturing <see cref="BaseProcessor{T}"/>
/// (registered AFTER the enricher, so it observes the enriched record) on a real OTel logger provider,
/// emits one log, and inspects the captured record. A tiny settable <see cref="IProcessorContext"/> fake
/// drives the two cases.
/// </summary>
public sealed class ProcessorIdEnricherTests
{
    /// <summary>A captured-by-reference in-memory processor: records each finished LogRecord's attributes.</summary>
    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        public List<KeyValuePair<string, object?>> LastAttributes { get; } = new();

        public override void OnEnd(LogRecord record)
        {
            LastAttributes.Clear();
            if (record.Attributes is not null)
                LastAttributes.AddRange(record.Attributes);
        }
    }

    /// <summary>Minimal settable <see cref="IProcessorContext"/> — the enricher only reads <c>Id</c>.</summary>
    private sealed class StubContext : IProcessorContext
    {
        public Guid? Id { get; set; }
        public Guid? InputSchemaId { get; }
        public Guid? OutputSchemaId { get; }
        public Guid? ConfigSchemaId { get; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? InputDefinition { get; }
        public string? OutputDefinition { get; }
        public string? ConfigDefinition { get; }
        public bool IsHealthy { get; }
        public Task WhenHealthy { get; } = Task.CompletedTask;

        public void SetIdentity(ProcessorIdentityFound identity) { }
        public void SetDefinition(Guid schemaId, string definition) { }
        public void MarkHealthy() { }
    }

    /// <summary>
    /// Builds a logger factory whose OTel logger provider runs the SUT enricher first, then the capturing
    /// processor (so the capture observes the enriched attributes), emits ONE log, and returns the capture.
    /// </summary>
    private static CapturingProcessor EmitOneLog(IProcessorContext context)
    {
        var capture = new CapturingProcessor();
        using var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.ParseStateValues = true;
            o.AddProcessor(new ProcessorIdLogEnricher(context));   // SUT — enriches first
            o.AddProcessor(capture);                               // then capture observes the enriched record
        }));

        factory.CreateLogger("test").LogInformation("enricher probe");
        return capture;
    }

    [Fact]
    public void Case_A_Id_Set_Appends_Exactly_One_ProcessorId_Attribute()
    {
        var id = Guid.NewGuid();
        var capture = EmitOneLog(new StubContext { Id = id });

        var matches = capture.LastAttributes
            .Where(kvp => kvp.Key == ExecutionLogScope.ProcessorId)
            .ToList();

        Assert.Single(matches);                                   // exactly one ProcessorId attribute
        Assert.Equal(id.ToString(), matches[0].Value);            // value is id.ToString() (keyword-mapped shape)
    }

    [Fact]
    public void Case_B_Id_Null_Appends_Nothing_No_Exception_No_GuidEmpty()
    {
        // Id is null → enricher must add NOTHING (and must not throw — OTel OnEnd contract).
        var capture = EmitOneLog(new StubContext { Id = null });

        Assert.DoesNotContain(capture.LastAttributes, kvp => kvp.Key == ExecutionLogScope.ProcessorId);
        Assert.DoesNotContain(capture.LastAttributes, kvp => Equals(kvp.Value, Guid.Empty.ToString()));
        Assert.DoesNotContain(capture.LastAttributes, kvp => Equals(kvp.Value, Guid.Empty));
    }
}
