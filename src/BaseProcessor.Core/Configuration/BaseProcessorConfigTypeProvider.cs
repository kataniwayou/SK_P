using Microsoft.Extensions.DependencyInjection;
using ProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseProcessor.Core.Configuration;

/// <summary>
/// Phase 57 D-01: the production <see cref="IConfigTypeProvider"/>. It resolves the author-registered
/// <see cref="Processing.BaseProcessor{TConfig}"/> ONCE (in a throwaway scope so a Scoped registration is honored —
/// WR-02 in <c>BaseProcessorServiceCollectionExtensions</c> permits Singleton OR Scoped), reads ONLY
/// <c>GetType().BaseType!.GenericTypeArguments[0]</c> (the concrete <c>TConfig</c> type), and caches it.
/// The processor instance is NOT retained (RESEARCH Pitfall 4: a captured Scoped instance in a
/// singleton seam is a captive-dependency bug); only the process-stable <see cref="Type"/> is kept.
/// </summary>
public sealed class BaseProcessorConfigTypeProvider(IServiceProvider services) : IConfigTypeProvider
{
    private Type? _cached;
    private readonly object _gate = new();

    /// <inheritdoc/>
    public Type Get()
    {
        if (_cached is not null)
            return _cached;

        lock (_gate)
        {
            if (_cached is not null)
                return _cached;

            // Resolve in a scope so a Scoped BaseProcessor registration is honored; read the Type only,
            // then dispose the scope (the instance is never held — Pitfall 4).
            using var scope = services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<ProcessorBase>();

            // BaseProcessor<TConfig> : BaseProcessor — the concrete author type derives from the closed
            // generic, so its BaseType IS BaseProcessor<TConfig> carrying TConfig as the single generic arg.
            var baseType = processor.GetType().BaseType
                ?? throw new InvalidOperationException(
                    $"Registered BaseProcessor '{processor.GetType().FullName}' has no base type; expected a "
                    + "concrete author processor deriving from BaseProcessor<TConfig>.");

            var args = baseType.GenericTypeArguments;
            if (args.Length != 1)
                throw new InvalidOperationException(
                    $"Registered BaseProcessor '{processor.GetType().FullName}' base type "
                    + $"'{baseType.FullName}' does not carry a single TConfig generic argument.");

            _cached = args[0];
            return _cached;
        }
    }
}
