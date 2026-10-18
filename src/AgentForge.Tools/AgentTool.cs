using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgentForge.Abstractions;

namespace AgentForge.Tools;

/// <summary>Creates strongly typed tools backed by one frozen JSON serialization contract.</summary>
/// <remarks>
/// Argument contracts are limited to sealed object graphs with at most 16 nested levels and 128 total
/// properties. Enums use string names. Unknown JSON properties and unsupported custom converters are rejected.
/// </remarks>
public static class AgentTool
{
    private const int MaximumDepth = 16;
    private const int MaximumProperties = 128;

    /// <summary>Creates a typed tool.</summary>
    /// <typeparam name="TArguments">The sealed object argument type.</typeparam>
    /// <typeparam name="TResult">The JSON-serializable result type.</typeparam>
    /// <param name="name">The non-empty exact tool name.</param>
    /// <param name="description">The non-empty model-facing description.</param>
    /// <param name="handler">The typed asynchronous handler.</param>
    /// <param name="serializerOptions">Optional serializer configuration, which is cloned and frozen.</param>
    /// <returns>A tool compatible with the AgentForge runtime.</returns>
    public static IAgentTool Create<TArguments, TResult>(
        string name,
        string description,
        Func<TArguments, ToolExecutionContext, CancellationToken, ValueTask<TResult>> handler,
        JsonSerializerOptions? serializerOptions = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Tool description cannot be empty.", nameof(description));
        ArgumentNullException.ThrowIfNull(handler);

        var options = serializerOptions is null
            ? new JsonSerializerOptions(JsonSerializerDefaults.General)
            : new JsonSerializerOptions(serializerOptions);
        if (options.PropertyNameCaseInsensitive)
            throw new ArgumentException("Case-insensitive property matching cannot be represented by the supported schema subset.", nameof(serializerOptions));
        if (serializerOptions?.TypeInfoResolver is not null)
            throw new ArgumentException("Custom JSON type-info resolvers are not supported because they cannot be frozen independently.", nameof(serializerOptions));
        ValidateConverters(options);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.MakeReadOnly(populateMissingResolver: true);

        var argumentTypeInfo = (JsonTypeInfo<TArguments>)options.GetTypeInfo(typeof(TArguments));
        var resultTypeInfo = (JsonTypeInfo<TResult>)options.GetTypeInfo(typeof(TResult));
        ContractInspector.Validate(argumentTypeInfo, MaximumDepth, MaximumProperties);
        ContractInspector.ValidateResult(resultTypeInfo);

        var schemaNode = argumentTypeInfo.GetJsonSchemaAsNode(new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true
        });
        SchemaContract.CloseObjects(schemaNode);
        var schema = JsonSerializer.SerializeToElement(schemaNode, options).Clone();
        var validator = new SchemaValidator(schema, MaximumDepth);
        return new TypedAgentTool<TArguments, TResult>(
            name, description, handler, argumentTypeInfo, resultTypeInfo, schema, validator);
    }

    private static void ValidateConverters(JsonSerializerOptions options)
    {
        foreach (var converter in options.Converters)
        {
            if (converter is not JsonStringEnumConverter)
                throw new ArgumentException("Custom JSON converters are not supported.", nameof(options));
        }
    }
}
