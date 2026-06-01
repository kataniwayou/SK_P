using BaseApi.Core.Exceptions;
using MassTransit;
using Messaging.Contracts;

namespace BaseApi.Service.Features.Schema.Responders;

/// <summary>
/// RPC-02 bus responder: answers <see cref="GetSchemaDefinition"/> by schema id with the dual-response
/// definition contract. On a hit it responds <see cref="SchemaDefinitionFound"/> carrying
/// <c>SchemaReadDto.Definition</c>; on a miss the inherited <c>BaseService.GetByIdAsync</c> throws
/// <see cref="NotFoundException"/>, caught and translated to <see cref="SchemaDefinitionNotFound"/>
/// (D-04). Stateless query responder: NO correlation filters, NO <c>ConsumerDefinition</c>
/// (client-side retry is Phase 26). The CancellationToken is threaded from the consume context.
/// </summary>
public sealed class GetSchemaDefinitionConsumer(SchemaService schemas)
    : IConsumer<GetSchemaDefinition>
{
    public async Task Consume(ConsumeContext<GetSchemaDefinition> context)
    {
        try
        {
            var s = await schemas.GetByIdAsync(context.Message.SchemaId, context.CancellationToken);
            await context.RespondAsync<SchemaDefinitionFound>(new SchemaDefinitionFound(s.Definition));
        }
        catch (NotFoundException)
        {
            await context.RespondAsync<SchemaDefinitionNotFound>(
                new SchemaDefinitionNotFound(context.Message.SchemaId));
        }
    }
}
