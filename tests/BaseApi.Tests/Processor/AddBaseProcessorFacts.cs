using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.DependencyInjection;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Startup;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Descriptor-inspection facts for the <c>AddBaseProcessor</c> composition root (BPC-03), the
/// <c>ConsoleTestHostFixture</c> analog: build a <see cref="ServiceCollection"/> with the required
/// RabbitMq/Redis config keys, call <c>AddBaseProcessor</c>, and assert the registration graph —
/// both request clients on the <c>exchange:</c> scheme, the orchestrator/context/source-hash/TimeProvider
/// registrations, AND that the base <c>StartupCompletionService</c> was REMOVED (D-02 — so MarkReady
/// fires on Healthy, not host-start). The heartbeat is NOT asserted (Plan 03 adds it).
/// </summary>
public sealed class AddBaseProcessorFacts
{
    // Dead Redis port + unreachable RabbitMQ — the multiplexer materializes lazily (abortConnect=false)
    // and the bus is wired but never reaches a broker; neither is resolved in these descriptor facts.
    private static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Service:Name"] = "processor-test",
            ["Service:Version"] = "26.0.0",
            ["ConnectionStrings:Redis"] = "127.0.0.1:6399,abortConnect=false,connectTimeout=1000",
            ["RabbitMq:Host"] = "localhost",
            ["RabbitMq:Username"] = "guest",
            ["RabbitMq:Password"] = "guest",
            ["ConsoleHealth:Port"] = "0",
            // Processor section (CONFIG-01) — bound by AddBaseProcessor.
            ["Processor:Interval"] = "10",
            ["Processor:Ttl"] = "30",
            ["Processor:RequestTimeout"] = "8",
            ["Processor:BackoffCap"] = "30",
        })
        .Build();

    private static IServiceCollection ComposeProcessor()
    {
        var services = new ServiceCollection();
        services.AddBaseProcessor(BuildConfig());
        return services;
    }

    [Fact]
    public async Task Composes_Singletons_Hosted_Service_And_Options()
    {
        var services = ComposeProcessor();

        // IProcessorContext -> ProcessorContext (singleton holder, D-06).
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IProcessorContext) &&
            d.ImplementationType == typeof(ProcessorContext) &&
            d.Lifetime == ServiceLifetime.Singleton);

        // ISourceHashProvider -> AssemblyMetadataSourceHashProvider (IDENT-03).
        Assert.Contains(services, d =>
            d.ServiceType == typeof(ISourceHashProvider) &&
            d.ImplementationType == typeof(AssemblyMetadataSourceHashProvider) &&
            d.Lifetime == ServiceLifetime.Singleton);

        // TimeProvider registered (idempotent TryAddSingleton).
        Assert.Contains(services, d => d.ServiceType == typeof(TimeProvider));

        // The startup orchestrator is the registered IHostedService.
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(ProcessorStartupOrchestrator));

        // ProcessorLivenessOptions bound from the "Processor" section (CONFIG-01). The MassTransit
        // container is IAsyncDisposable — dispose via `await using`.
        await using var provider = services.BuildServiceProvider(true);
        var opts = provider.GetRequiredService<IOptions<ProcessorLivenessOptions>>().Value;
        Assert.Equal(10, opts.IntervalSeconds);
        Assert.Equal(30, opts.TtlSeconds);
        Assert.Equal(8, opts.RequestTimeoutSeconds);
        Assert.Equal(30, opts.BackoffCapSeconds);
    }

    [Fact]
    public async Task Registers_Both_Request_Clients()
    {
        var services = ComposeProcessor();

        // MassTransit registers IRequestClient<T> for each AddRequestClient<T> call. Resolve them from
        // a scope (scoped service — Wave 0 correction) to prove both are wired without touching a broker.
        // The MassTransit container is IAsyncDisposable — dispose via `await using`.
        await using var provider = services.BuildServiceProvider(true);
        using var scope = provider.CreateScope();
        var identityClient = scope.ServiceProvider.GetService<IRequestClient<GetProcessorBySourceHash>>();
        var schemaClient = scope.ServiceProvider.GetService<IRequestClient<GetSchemaDefinition>>();

        Assert.NotNull(identityClient);
        Assert.NotNull(schemaClient);
    }

    [Fact]
    public void Removes_StartupCompletionService()
    {
        var services = ComposeProcessor();

        // D-02: the base StartupCompletionService must be gone so MarkReady fires on Healthy, not host-start.
        Assert.DoesNotContain(services, d =>
            d.ImplementationType == typeof(StartupCompletionService));
    }
}
