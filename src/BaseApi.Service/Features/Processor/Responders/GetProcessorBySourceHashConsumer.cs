using BaseApi.Core.Exceptions;
using MassTransit;
using Messaging.Contracts;

namespace BaseApi.Service.Features.Processor.Responders;

/// <summary>
/// RPC-01 bus responder: answers <see cref="GetProcessorBySourceHash"/> by source hash with the
/// dual-response identity contract. On a hit it responds <see cref="ProcessorIdentityFound"/> (a
/// direct field projection of <c>ProcessorReadDto</c>); on a miss the backing
/// <see cref="ProcessorService.GetBySourceHashAsync"/> throws <see cref="NotFoundException"/>, which
/// is caught and translated to <see cref="ProcessorIdentityNotFound"/> (D-04 — lets the Phase 26
/// client pattern-match cleanly). Stateless query responder: NO correlation filters, NO
/// <c>ConsumerDefinition</c> (client-side retry is Phase 26). The CancellationToken is threaded
/// from the consume context.
/// </summary>
public sealed class GetProcessorBySourceHashConsumer(ProcessorService processors)
    : IConsumer<GetProcessorBySourceHash>
{
    public async Task Consume(ConsumeContext<GetProcessorBySourceHash> context)
    {
        try
        {
            var p = await processors.GetBySourceHashAsync(context.Message.SourceHash, context.CancellationToken);
            await context.RespondAsync<ProcessorIdentityFound>(
                new ProcessorIdentityFound(p.Id, p.InputSchemaId, p.OutputSchemaId, p.ConfigSchemaId, p.Name, p.Version));
        }
        catch (NotFoundException)
        {
            await context.RespondAsync<ProcessorIdentityNotFound>(
                new ProcessorIdentityNotFound(context.Message.SourceHash));
        }
    }
}
