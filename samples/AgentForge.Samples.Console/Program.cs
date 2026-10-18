using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;
using AgentForge.Hosting;
using Microsoft.Extensions.DependencyInjection;

var addCall = new ToolCall(
    "call-1",
    "calculator.add",
    JsonSerializer.SerializeToElement(new { left = 20, right = 22 }));

var model = new ScriptedAgentModelClient(
[
    (_, _) => Task.FromResult(new ModelResponse(AssistantMessage.FromToolCalls(addCall))),
    (request, _) =>
    {
        var toolResult = request.Messages.OfType<ToolResultMessage>().Single();
        var value = toolResult.Result.GetProperty("value").GetDecimal();
        return Task.FromResult(new ModelResponse(AssistantMessage.FromText($"20 + 22 = {value}")));
    }
]);

var services = new ServiceCollection()
    .AddSingleton<IAgentModelClient>(model)
    .AddAgentTool<CalculatorTool>()
    .AddAgentForge()
    .BuildServiceProvider();

var runtime = services.GetRequiredService<IAgentRuntime>();
var agent = new AgentDefinition(
    new AgentId("calculator-agent"),
    "Use the calculator tool for arithmetic.",
    new ModelId("fake-calculator-model"),
    new HashSet<string>(StringComparer.Ordinal) { "calculator.add" },
    new AgentExecutionLimits(MaxModelCalls: 3, MaxToolCalls: 2));

var result = await runtime.RunAsync(new AgentRunRequest(agent, [new UserMessage("What is 20 + 22?")]));

Console.WriteLine(result.FinalMessage.Text);
Console.WriteLine($"Model calls: {result.ModelCalls}; Tool calls: {result.ToolCalls}");

internal sealed class CalculatorTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            left = new { type = "number" },
            right = new { type = "number" }
        },
        required = new[] { "left", "right" },
        additionalProperties = false
    });

    public ToolDescriptor Descriptor { get; } = new(
        "calculator.add",
        "Adds two numbers.",
        Schema);

    public Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var left = invocation.Arguments.GetProperty("left").GetDecimal();
        var right = invocation.Arguments.GetProperty("right").GetDecimal();
        return Task.FromResult(new ToolResult(JsonSerializer.SerializeToElement(new { value = left + right })));
    }
}
