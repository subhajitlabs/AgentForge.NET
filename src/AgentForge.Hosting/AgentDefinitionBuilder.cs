using AgentForge.Abstractions;

namespace AgentForge.Hosting;

/// <summary>Builds an existing immutable <see cref="AgentDefinition"/> for named registration.</summary>
public sealed class AgentDefinitionBuilder
{
    private readonly string _name;
    private readonly HashSet<string> _allowedTools = new(StringComparer.Ordinal);
    private string? _instructions;
    private string? _model;
    private AgentExecutionLimits _limits = new();

    internal AgentDefinitionBuilder(string name) => _name = name;

    /// <summary>Sets the system instructions.</summary>
    public AgentDefinitionBuilder WithInstructions(string instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
            throw new ArgumentException("Agent instructions cannot be empty.", nameof(instructions));
        _instructions = instructions;
        return this;
    }

    /// <summary>Selects the provider-neutral model identifier.</summary>
    public AgentDefinitionBuilder UseModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model ID cannot be empty.", nameof(model));
        _model = model;
        return this;
    }

    /// <summary>Adds an exact tool name to this agent's allow-list.</summary>
    public AgentDefinitionBuilder AllowTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("Allowed tool names cannot be empty.", nameof(toolName));
        _allowedTools.Add(toolName);
        return this;
    }

    /// <summary>Sets the hard per-run model-call and tool-call limits.</summary>
    public AgentDefinitionBuilder WithLimits(int maxModelCalls, int maxToolCalls)
    {
        if (maxModelCalls <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxModelCalls), "The model-call limit must be positive.");
        if (maxToolCalls <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxToolCalls), "The tool-call limit must be positive.");
        _limits = new AgentExecutionLimits(maxModelCalls, maxToolCalls);
        return this;
    }

    internal AgentDefinition Build()
    {
        if (_instructions is null)
            throw new InvalidOperationException($"Agent definition '{_name}' requires instructions.");
        if (_model is null)
            throw new InvalidOperationException($"Agent definition '{_name}' requires a model.");
        return new AgentDefinition(new AgentId(_name), _instructions, new ModelId(_model), _allowedTools, _limits);
    }
}
