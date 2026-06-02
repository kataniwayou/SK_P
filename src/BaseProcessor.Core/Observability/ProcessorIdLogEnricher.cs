using BaseProcessor.Core.Identity;
using Messaging.Contracts;           // ExecutionLogScope.ProcessorId
using OpenTelemetry;                 // BaseProcessor<T>
using OpenTelemetry.Logs;            // LogRecord

namespace BaseProcessor.Core.Observability;

/// <summary>
/// LOG-04: appends <c>ProcessorId</c> (from the singleton <see cref="IProcessorContext.Id"/>) to
/// EVERY processor LogRecord (startup, heartbeat, consume), so OTel serializes it as
/// <c>attributes.ProcessorId</c>. Null-safe: before identity resolves (<c>Id == null</c>) it adds
/// nothing — never <see cref="System.Guid.Empty"/> (SPEC constraint). Registered ONLY on the
/// processor's logger provider (NOT the shared BaseConsole.Core observability extension — L3). Safe on
/// OTel 1.15.3: reassigning Attributes alone is correct (no State desync — the v1.5-v1.7 bug, L4).
/// </summary>
public sealed class ProcessorIdLogEnricher(IProcessorContext context) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        if (context.Id is not { } id) return;
        record.Attributes = (record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>())
            .Append(new KeyValuePair<string, object?>(ExecutionLogScope.ProcessorId, id.ToString()))
            .ToList();
    }
}
