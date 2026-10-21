using AgentForge.Abstractions;
using AgentForge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Hosting;

/// <summary>Registers AgentForge runtime services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds the single-agent runtime and tool registry.</summary>
    /// <param name="services">The application service collection.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddAgentForge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider =>
        {
            var clients = provider.GetServices<IAgentModelClient>().ToArray();
            if (clients.Length != 1)
                throw new InvalidOperationException("Exactly one AgentForge model client must be registered.");
            return new AgentRuntime(
                clients[0],
                provider.GetRequiredService<IAgentToolRegistry>(),
                provider.GetRequiredService<TimeProvider>());
        });
        services.TryAddSingleton<IAgentRuntime>(provider => provider.GetRequiredService<AgentRuntime>());
        services.TryAddSingleton<IStreamingAgentRuntime>(provider => provider.GetRequiredService<AgentRuntime>());
        return services;
    }

    /// <summary>Adds an immutable named agent definition and keyed <see cref="IAgent"/> facade.</summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="name">The non-empty agent name and keyed-service key.</param>
    /// <param name="configure">Configures the existing immutable definition model.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddAgentDefinition(
        this IServiceCollection services,
        string name,
        Action<AgentDefinitionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Agent name cannot be empty.", nameof(name));
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IAgent)
            && descriptor.IsKeyedService
            && Equals(descriptor.ServiceKey, name)))
            throw new InvalidOperationException($"An agent named '{name}' is already registered.");

        var builder = new AgentDefinitionBuilder(name);
        configure(builder);
        var definition = builder.Build();
        services.AddKeyedSingleton<IAgent>(name, (provider, serviceKey) =>
        {
            _ = serviceKey;
            var registry = provider.GetRequiredService<IAgentToolRegistry>();
            foreach (var toolName in definition.AllowedTools)
            {
                if (!registry.TryResolve(toolName, out _))
                    throw new InvalidOperationException($"Agent '{name}' allows a tool that is not registered.");
            }
            return new RegisteredAgent(
                definition,
                provider.GetRequiredService<IAgentRuntime>(),
                provider.GetRequiredService<IStreamingAgentRuntime>());
        });
        return services;
    }

    /// <summary>Adds one trusted tool implementation.</summary>
    /// <typeparam name="TTool">The tool implementation type.</typeparam>
    /// <param name="services">The application service collection.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddAgentTool<TTool>(this IServiceCollection services)
        where TTool : class, IAgentTool
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAgentTool, TTool>();
        return services;
    }

    /// <summary>Adds one trusted tool instance.</summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="tool">The tool instance.</param>
    /// <returns>The same collection for chaining.</returns>
    public static IServiceCollection AddAgentTool(this IServiceCollection services, IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tool);
        services.AddSingleton(tool);
        return services;
    }
}
