using AgentForge.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Adapters.MicrosoftExtensionsAI;

/// <summary>Registers the Microsoft.Extensions.AI interoperability adapter.</summary>
public static class MicrosoftExtensionsAIServiceCollectionExtensions
{
    /// <summary>
    /// Adds a singleton adapter over a caller-owned chat client. Neither the adapter nor DI disposes the supplied client.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="client">The caller-owned chat client.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddMicrosoftExtensionsAIModelClient(
        this IServiceCollection services, IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return AddMicrosoftExtensionsAIModelClient(services, _ => client);
    }

    /// <summary>
    /// Adds a singleton adapter using a caller-owned chat-client factory. The returned client is not disposed by the adapter.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="clientFactory">Creates or resolves the caller-owned chat client.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddMicrosoftExtensionsAIModelClient(
        this IServiceCollection services, Func<IServiceProvider, IChatClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IAgentModelClient)
            || descriptor.ServiceType == typeof(IStreamingAgentModelClient)))
            throw new InvalidOperationException(
                "An AgentForge model client is already registered.");

        services.AddSingleton(provider =>
            new MicrosoftExtensionsAIModelClient(clientFactory(provider)));
        services.AddSingleton<IAgentModelClient>(provider =>
            provider.GetRequiredService<MicrosoftExtensionsAIModelClient>());
        services.AddSingleton<IStreamingAgentModelClient>(provider =>
            provider.GetRequiredService<MicrosoftExtensionsAIModelClient>());
        return services;
    }
}
