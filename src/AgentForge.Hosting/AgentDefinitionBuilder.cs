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
    private TimeSpan? _executionTimeout;
    private AgentResourceLimits? _resourceLimits;
    private AgentTokenBudget? _tokenBudget;

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

    /// <summary>Sets a strictly positive timeout for the entire agent execution.</summary>
    public AgentDefinitionBuilder WithTimeout(TimeSpan timeout)
    {
        _ = new AgentExecutionOptions(timeout);
        _executionTimeout = timeout;
        return this;
    }

    /// <summary>Sets optional per-run Core resource and adapter provider-processing budgets.</summary>
    /// <param name="maxInputCharacters">Maximum model-facing transcript UTF-16 characters.</param>
    /// <param name="maxOutputCharacters">Maximum accumulated model-output UTF-16 characters.</param>
    /// <param name="maxToolArgumentBytes">Maximum cumulative tool-argument JSON UTF-8 bytes.</param>
    /// <param name="maxToolResultBytes">Maximum cumulative tool-result JSON UTF-8 bytes.</param>
    /// <param name="maxProviderStreamEvents">Maximum SDK stream events processed per model turn.</param>
    /// <param name="maxProviderContentCharacters">Maximum provider text retained per model turn in UTF-16 characters.</param>
    /// <param name="maxProviderToolArgumentBytes">Maximum provider tool-call arguments retained per model turn in UTF-8 bytes.</param>
    public AgentDefinitionBuilder WithResourceLimits(
        long? maxInputCharacters = null,
        long? maxOutputCharacters = null,
        long? maxToolArgumentBytes = null,
        long? maxToolResultBytes = null,
        long? maxProviderStreamEvents = null,
        long? maxProviderContentCharacters = null,
        long? maxProviderToolArgumentBytes = null)
    {
        _resourceLimits = new AgentResourceLimits(
            maxInputCharacters,
            maxOutputCharacters,
            maxToolArgumentBytes,
            maxToolResultBytes,
            maxProviderStreamEvents,
            maxProviderContentCharacters,
            maxProviderToolArgumentBytes);
        return this;
    }

    /// <summary>Sets optional execution-wide budgets for observed provider-reported token usage.</summary>
    public AgentDefinitionBuilder WithTokenBudget(long? maxInputTokens = null, long? maxOutputTokens = null, long? maxTotalTokens = null)
    {
        _tokenBudget = new AgentTokenBudget(maxInputTokens, maxOutputTokens, maxTotalTokens);
        return this;
    }

    internal AgentDefinition Build()
    {
        if (_instructions is null)
            throw new InvalidOperationException($"Agent definition '{_name}' requires instructions.");
        if (_model is null)
            throw new InvalidOperationException($"Agent definition '{_name}' requires a model.");
        var options = _executionTimeout is null && _resourceLimits is null && _tokenBudget is null
            ? AgentExecutionOptions.None
            : new AgentExecutionOptions(_executionTimeout, _resourceLimits, _tokenBudget);
        return new AgentDefinition(new AgentId(_name), _instructions, new ModelId(_model), _allowedTools, _limits, options);
    }
}
