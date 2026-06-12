using BaseProcessor.Core.Processing;

namespace BaseProcessor.Core.Configuration;

/// <summary>
/// Phase 57 D-01: the stubbable seam that supplies the concrete <c>TConfig</c> CLR type the startup
/// orchestrator's Gate A (<see cref="ConfigSchemaCoverageCheck"/>) checks the fetched config-schema
/// definition against. Mirrors <see cref="BaseProcessor.Core.Identity.ISourceHashProvider"/>: the default
/// implementation (<see cref="BaseProcessorConfigTypeProvider"/>) resolves the author-registered
/// <see cref="BaseProcessor{TConfig}"/> ONCE and reads only its generic type argument
/// (<c>GetType().BaseType!.GenericTypeArguments[0]</c>) — the <c>Type</c> is process-stable, so it is
/// captured without holding the (possibly scoped) processor instance (RESEARCH Pitfall 4: no
/// captive-dependency). Tests register an in-memory stub returning a known <c>TConfig</c> type so they
/// need no real <see cref="BaseProcessor{TConfig}"/> registration.
/// </summary>
public interface IConfigTypeProvider
{
    /// <summary>Returns the concrete author <c>TConfig</c> type (the generic argument of the registered
    /// <see cref="BaseProcessor{TConfig}"/>) for Gate A to reflect over.</summary>
    Type Get();
}
