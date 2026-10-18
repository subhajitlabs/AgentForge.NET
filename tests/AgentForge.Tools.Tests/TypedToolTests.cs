using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using System.Reflection.Emit;
using AgentForge.Abstractions;
using AgentForge.Tools;

namespace AgentForge.Tools.Tests;

public sealed class TypedToolTests
{
    [Fact]
    public void Create_SupportedContract_ProducesDeterministicClosedSchema()
    {
        var options = Options();
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

        var first = Create(options).Descriptor.ParametersSchema.GetRawText();
        var second = Create(options).Descriptor.ParametersSchema.GetRawText();

        Assert.Equal(first, second);
        using var schema = JsonDocument.Parse(first);
        Assert.True(schema.RootElement.GetProperty("additionalProperties").GetBoolean() is false);
        var properties = schema.RootElement.GetProperty("properties");
        Assert.True(properties.TryGetProperty("name", out _));
        Assert.True(properties.TryGetProperty("mode", out _));
        Assert.True(properties.TryGetProperty("values", out _));
        Assert.Contains("name", schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Create_IntegerRecord_ProducesOpenAICompatibleSchema()
    {
        var tool = AgentTool.Create<AddArguments, SimpleResult>(
            "calculator_add",
            "Adds two integers.",
            static (arguments, _, _) => ValueTask.FromResult(new SimpleResult(arguments.Left + arguments.Right)));

        var schema = tool.Descriptor.ParametersSchema;

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var properties = schema.GetProperty("properties");
        Assert.Equal("integer", properties.GetProperty("Left").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("Right").GetProperty("type").GetString());
        Assert.Equal(
            ["Left", "Right"],
            schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"mode\":\"Fast\",\"values\":[1,2],\"nested\":{\"enabled\":true}}")]
    [InlineData("{\"name\":\"x\",\"mode\":\"Slow\",\"values\":[],\"nested\":null}")]
    public void ValidateArguments_ValidPayload_Accepts(string json)
    {
        var validator = Assert.IsAssignableFrom<IAgentToolArgumentValidator>(Create(Options()));
        validator.ValidateArguments(Json(json));
    }

    [Theory]
    [InlineData("{\"mode\":\"Fast\",\"values\":[]}")]
    [InlineData("{\"name\":null,\"mode\":\"Fast\",\"values\":[]}")]
    [InlineData("{\"name\":\"x\",\"mode\":5,\"values\":[]}")]
    [InlineData("{\"name\":\"x\",\"mode\":\"Unknown\",\"values\":[]}")]
    [InlineData("{\"name\":\"x\",\"mode\":\"Fast\",\"values\":[\"bad\"]}")]
    [InlineData("{\"name\":\"x\",\"mode\":\"Fast\",\"values\":[],\"extra\":\"secret\"}")]
    [InlineData("[]")]
    public void ValidateArguments_InvalidPayload_RejectsWithoutPayloadInMessage(string json)
    {
        var validator = Assert.IsAssignableFrom<IAgentToolArgumentValidator>(Create(Options()));

        var exception = Assert.Throws<ArgumentException>(() => validator.ValidateArguments(Json(json)));

        Assert.DoesNotContain(json, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_OptionsMutatedAfterRegistration_DoesNotChangeContract()
    {
        var options = Options();
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        var tool = Create(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.PropertyNameCaseInsensitive = true;
        var validator = Assert.IsAssignableFrom<IAgentToolArgumentValidator>(tool);

        validator.ValidateArguments(Json("{\"name\":\"x\",\"mode\":\"Fast\",\"values\":[]}"));
        Assert.Throws<ArgumentException>(() => validator.ValidateArguments(Json("{\"Name\":\"x\",\"Mode\":\"Fast\",\"Values\":[]}")));
    }

    [Fact]
    public async Task InvokeAsync_DeserializesContextCancellationAndOwnedResult()
    {
        ToolExecutionContext? receivedContext = null;
        CancellationToken receivedToken = default;
        var tool = AgentTool.Create<SimpleArguments, MutableResult>(
            "typed", "description",
            (arguments, context, token) =>
            {
                receivedContext = context;
                receivedToken = token;
                return ValueTask.FromResult(new MutableResult { Value = arguments.Value + 1 });
            });
        var executionId = ExecutionId.New();
        using var source = new CancellationTokenSource();
        ToolResult result;
        using (var document = JsonDocument.Parse("{\"Value\":41}"))
            result = await tool.InvokeAsync(new ToolInvocation(executionId, "call-1", document.RootElement), source.Token);

        Assert.Equal(42, result.Value.GetProperty("Value").GetInt32());
        Assert.Equal(executionId, receivedContext!.ExecutionId);
        Assert.Equal("call-1", receivedContext.ToolCallId);
        Assert.Equal(source.Token, receivedToken);
    }

    [Fact]
    public async Task InvokeAsync_CancelledHandler_PropagatesCancellation()
    {
        var tool = AgentTool.Create<SimpleArguments, int>(
            "typed", "description",
            static (_, _, token) => ValueTask.FromCanceled<int>(token));
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.InvokeAsync(new ToolInvocation(ExecutionId.New(), "call", Json("{\"Value\":1}")), source.Token));
    }

    [Theory]
    [MemberData(nameof(UnsupportedContracts))]
    public void Create_UnsupportedArgumentType_Rejects(Type argumentType)
    {
        var method = typeof(TypedToolTests).GetMethod(nameof(CreateUnsupported), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.ThrowsAny<Exception>(() => method.MakeGenericMethod(argumentType).Invoke(null, null));
    }

    public static TheoryData<Type> UnsupportedContracts => new()
    {
        typeof(object), typeof(string), typeof(OpenArguments<>), typeof(DictionaryArguments),
        typeof(CyclicArguments), typeof(UnsealedArguments), typeof(ExtensionArguments)
    };

    [Fact]
    public void Create_CustomConverter_Rejects()
    {
        var options = Options();
        options.Converters.Add(new DateOnlyConverter());
        Assert.Throws<ArgumentException>(() => AgentTool.Create<SimpleArguments, int>(
            "typed", "description", static (_, _, _) => ValueTask.FromResult(0), options));
    }

    [Fact]
    public void Create_CaseInsensitiveProperties_RejectsBecauseSchemaCannotExpressThem()
    {
        var options = Options();
        options.PropertyNameCaseInsensitive = true;
        Assert.Throws<ArgumentException>(() => Create(options));
    }

    [Fact]
    public void ValidateArguments_NumberHandling_UsesFrozenSerializerSemantics()
    {
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };
        var tool = AgentTool.Create<SimpleArguments, int>(
            "typed", "description", static (value, _, _) => ValueTask.FromResult(value.Value), options);
        var validator = Assert.IsAssignableFrom<IAgentToolArgumentValidator>(tool);

        validator.ValidateArguments(Json("{\"Value\":\"42\"}"));
        Assert.Throws<ArgumentException>(() => validator.ValidateArguments(Json("{\"Value\":\"not-a-number\"}")));
    }

    [Fact]
    public void Create_ContractBeyondMaximumDepth_Rejects()
    {
        Assert.Throws<ArgumentException>(() => AgentTool.Create<Depth01, int>(
            "typed", "description", static (_, _, _) => ValueTask.FromResult(0)));
    }

    [Fact]
    public void Create_ContractBeyondMaximumPropertyCount_Rejects()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("AgentForge.PropertyLimit"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Main").DefineType(
            "HugeArguments", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        for (var index = 0; index < 129; index++)
        {
            var field = type.DefineField($"_p{index}", typeof(int), FieldAttributes.Private);
            var property = type.DefineProperty($"P{index}", PropertyAttributes.None, typeof(int), null);
            var getter = type.DefineMethod($"get_P{index}", MethodAttributes.Public | MethodAttributes.SpecialName, typeof(int), Type.EmptyTypes);
            var il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            property.SetGetMethod(getter);
        }
        var argumentType = type.CreateType()!;
        var method = typeof(TypedToolTests).GetMethod(nameof(CreateUnsupported), BindingFlags.NonPublic | BindingFlags.Static)!;

        var exception = Assert.Throws<TargetInvocationException>(() => method.MakeGenericMethod(argumentType).Invoke(null, null));
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    private static IAgentTool Create(JsonSerializerOptions options) =>
        AgentTool.Create<ComplexArguments, SimpleResult>(
            "typed", "description", static (_, _, _) => ValueTask.FromResult(new SimpleResult(1)), options);

    private static void CreateUnsupported<T>() => AgentTool.Create<T, int>(
        "typed", "description", static (_, _, _) => ValueTask.FromResult(0));

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private enum Mode { Fast, Slow }
    private sealed record NestedArguments(bool Enabled);
    private sealed record ComplexArguments(string Name, Mode Mode, List<int> Values, NestedArguments? Nested = null);
    private sealed record SimpleArguments(int Value);
    private sealed record AddArguments(int Left, int Right);
    private sealed record SimpleResult(int Value);
    private sealed class MutableResult { public int Value { get; set; } }
    private sealed record OpenArguments<T>(T Value);
    private sealed record DictionaryArguments(Dictionary<string, int> Values);
    private sealed class CyclicArguments { public required CyclicArguments Child { get; init; } }
    private class UnsealedArguments { public int Value { get; init; } }
    private sealed class ExtensionArguments { [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; } }
    private sealed record Depth01(Depth02 Value);
    private sealed record Depth02(Depth03 Value);
    private sealed record Depth03(Depth04 Value);
    private sealed record Depth04(Depth05 Value);
    private sealed record Depth05(Depth06 Value);
    private sealed record Depth06(Depth07 Value);
    private sealed record Depth07(Depth08 Value);
    private sealed record Depth08(Depth09 Value);
    private sealed record Depth09(Depth10 Value);
    private sealed record Depth10(Depth11 Value);
    private sealed record Depth11(Depth12 Value);
    private sealed record Depth12(Depth13 Value);
    private sealed record Depth13(Depth14 Value);
    private sealed record Depth14(Depth15 Value);
    private sealed record Depth15(Depth16 Value);
    private sealed record Depth16(Depth17 Value);
    private sealed record Depth17(int Value);
    private sealed class DateOnlyConverter : JsonConverter<DateOnly>
    {
        public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => default;
        public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
