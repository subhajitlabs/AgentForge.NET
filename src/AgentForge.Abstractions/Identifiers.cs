namespace AgentForge.Abstractions;

/// <summary>Identifies an agent definition.</summary>
/// <param name="Value">The stable identifier value.</param>
public readonly record struct AgentId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies the model selected for an execution.</summary>
/// <param name="Value">The provider-neutral model identifier.</param>
public readonly record struct ModelId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Identifies one runtime execution.</summary>
/// <param name="Value">The identifier value.</param>
public readonly record struct ExecutionId(Guid Value)
{
    /// <summary>Creates a new execution identifier.</summary>
    public static ExecutionId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
