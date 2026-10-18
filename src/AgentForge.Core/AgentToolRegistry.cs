using AgentForge.Abstractions;

namespace AgentForge.Core;

/// <summary>Immutable registry of trusted application tools.</summary>
public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;

    /// <summary>Creates a registry from dependency-injected tools.</summary>
    /// <param name="tools">The registered tool implementations.</param>
    /// <exception cref="ArgumentException">A descriptor is invalid or a name is duplicated.</exception>
    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var registry = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var name = tool.Descriptor.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A registered tool has an empty name.", nameof(tools));
            }

            if (!registry.TryAdd(name, tool))
            {
                throw new ArgumentException($"Tool name '{name}' is registered more than once.", nameof(tools));
            }
        }

        _tools = registry;
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDescriptor> Describe(IReadOnlySet<string> allowedTools)
    {
        ArgumentNullException.ThrowIfNull(allowedTools);
        return _tools
            .Where(entry => allowedTools.Contains(entry.Key))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Value.Descriptor)
            .ToArray();
    }

    /// <inheritdoc />
    public bool TryResolve(string name, out IAgentTool? tool) => _tools.TryGetValue(name, out tool);
}
