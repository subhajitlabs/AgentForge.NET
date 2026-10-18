using AgentForge.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.Responses;

namespace AgentForge.Providers.OpenAI;

/// <summary>Registers the OpenAI Responses provider.</summary>
public static class OpenAIServiceCollectionExtensions
{
    /// <summary>Adds a provider-owned OpenAI model client.</summary>
    public static IServiceCollection AddOpenAIModelClient(
        this IServiceCollection services, Action<OpenAIProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new OpenAIProviderOptions();
        configure(options);
        return AddClient(services, new OpenAIAgentModelClient(options));
    }

    /// <summary>Adds an adapter over a caller-owned Responses client.</summary>
    public static IServiceCollection AddOpenAIModelClient(
        this IServiceCollection services, Func<IServiceProvider, ResponsesClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);
        EnsureNoModelClient(services);
        services.AddSingleton(provider => new OpenAIAgentModelClient(clientFactory(provider)));
        services.AddSingleton<IAgentModelClient>(provider => provider.GetRequiredService<OpenAIAgentModelClient>());
        services.AddSingleton<IStreamingAgentModelClient>(provider => provider.GetRequiredService<OpenAIAgentModelClient>());
        return services;
    }

    private static IServiceCollection AddClient(IServiceCollection services, OpenAIAgentModelClient client)
    {
        EnsureNoModelClient(services);
        services.AddSingleton(client);
        services.AddSingleton<IAgentModelClient>(client);
        services.AddSingleton<IStreamingAgentModelClient>(client);
        return services;
    }

    private static void EnsureNoModelClient(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IAgentModelClient)
            || descriptor.ServiceType == typeof(IStreamingAgentModelClient)))
            throw new InvalidOperationException("An AgentForge model client is already registered.");
    }
}
