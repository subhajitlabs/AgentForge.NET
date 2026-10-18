using AgentForge.Abstractions;

namespace AgentForge.Core;

/// <summary>Immutable registry of trusted application tools.</summary>
public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private readonly IReadOnlyDictionary<string, RegisteredTool> _tools;

    /// <summary>Creates a registry from dependency-injected tools.</summary>
    /// <param name="tools">The registered tool implementations.</param>
    /// <exception cref="ArgumentException">A descriptor is invalid or a name is duplicated.</exception>
    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var registry = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var descriptor = tool.Descriptor
                ?? throw new ArgumentException("A registered tool has no descriptor.", nameof(tools));
            var name = descriptor.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A registered tool has an empty name.", nameof(tools));
            }

            if (string.IsNullOrWhiteSpace(descriptor.Description))
            {
                throw new ArgumentException("A registered tool has an empty description.", nameof(tools));
            }

            if (descriptor.ParametersSchema.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                throw new ArgumentException("A registered tool has an invalid parameter schema.", nameof(tools));
            }

            var snapshot = new ToolDescriptor(name, descriptor.Description, descriptor.ParametersSchema);
            if (!registry.TryAdd(name, new RegisteredTool(tool, snapshot)))
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
            .ToArray()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public bool TryResolve(string name, out IAgentTool? tool)
    {
        if (_tools.TryGetValue(name, out var registration))
        {
            tool = registration.Tool;
            return true;
        }

        tool = null;
        return false;
    }

    private sealed record RegisteredTool(IAgentTool Tool, ToolDescriptor Descriptor);
}
