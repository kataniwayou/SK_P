namespace BaseConsole.Core.Messaging;

/// <summary>
/// Ambient correlation-id accessor. The inbound MassTransit consume filter (Plan 02) sets the
/// correlation id read off the message into the ambient store and opens a <c>"CorrelationId"</c>
/// MEL log scope; the outbound send/publish filter reads it back to stamp <c>ICorrelated</c>
/// messages.
///
/// <para>
/// The value is <c>string?</c> (not <c>Guid</c>) so an arbitrary inbound HTTP correlation id is
/// preserved verbatim for the log scope; the outbound filter applies <c>Guid.TryParse</c> when
/// it needs the envelope's <c>Guid CorrelationId</c>.
/// </para>
/// </summary>
public interface ICorrelationAccessor
{
    string? Get();
    void Set(string? value);
}
