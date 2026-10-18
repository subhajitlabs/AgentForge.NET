using AgentForge.Abstractions;
using AgentForge.Core;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        services.AddSingleton<AgentRuntime>();
        services.AddSingleton<IAgentRuntime>(provider => provider.GetRequiredService<AgentRuntime>());
        services.AddSingleton<IStreamingAgentRuntime>(provider => provider.GetRequiredService<AgentRuntime>());
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
