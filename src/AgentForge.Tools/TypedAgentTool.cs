using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentForge.Abstractions;

namespace AgentForge.Tools;

internal sealed class TypedAgentTool<TArguments, TResult> : IAgentTool, IAgentToolArgumentValidator
{
    private readonly Func<TArguments, ToolExecutionContext, CancellationToken, ValueTask<TResult>> _handler;
    private readonly JsonTypeInfo<TArguments> _argumentTypeInfo;
    private readonly JsonTypeInfo<TResult> _resultTypeInfo;
    private readonly SchemaValidator _validator;

    public TypedAgentTool(
        string name,
        string description,
        Func<TArguments, ToolExecutionContext, CancellationToken, ValueTask<TResult>> handler,
        JsonTypeInfo<TArguments> argumentTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        JsonElement schema,
        SchemaValidator validator)
    {
        Descriptor = new ToolDescriptor(name, description, schema);
        _handler = handler;
        _argumentTypeInfo = argumentTypeInfo;
        _resultTypeInfo = resultTypeInfo;
        _validator = validator;
    }

    public ToolDescriptor Descriptor { get; }

    public void ValidateArguments(JsonElement arguments)
    {
        _validator.Validate(arguments);
        try
        {
            if (JsonSerializer.Deserialize(arguments, _argumentTypeInfo) is null)
                throw new JsonException("The argument root deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Tool arguments do not satisfy the registered JSON contract.", nameof(arguments), exception);
        }
    }

    public async Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        TArguments? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize(invocation.Arguments, _argumentTypeInfo);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Validated tool arguments could not be deserialized.", exception);
        }

        if (arguments is null)
            throw new InvalidOperationException("Validated tool arguments produced a null value.");

        var result = await _handler(
            arguments,
            new ToolExecutionContext(invocation.ExecutionId, invocation.CallId),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new ToolResult(JsonSerializer.SerializeToElement(result, _resultTypeInfo));
    }
}
