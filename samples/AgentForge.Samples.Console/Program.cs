using System.Text.Json;
using AgentForge.Abstractions;
using AgentForge.Core;
using AgentForge.Hosting;
using AgentForge.Tools;
using Microsoft.Extensions.DependencyInjection;

var addCall = new ToolCall(
    "call-1",
    "calculator_add",
    JsonSerializer.SerializeToElement(new AddArguments(20, 22)));

var model = new ScriptedAgentModelClient(
[
    (_, _) => Task.FromResult(ModelResponse.ToolCalls(AssistantMessage.FromToolCalls(addCall))),
    (request, _) =>
    {
        var toolResult = request.Messages.OfType<ToolResultMessage>().Single();
        var value = toolResult.Result.GetProperty("Value").GetDecimal();
        return Task.FromResult(ModelResponse.Completed(AssistantMessage.FromText($"20 + 22 = {value}")));
    }
]);

var services = new ServiceCollection()
    .AddSingleton<IAgentModelClient>(model)
    .AddAgentTool(AgentTool.Create<AddArguments, AddResult>(
        "calculator_add",
        "Adds two numbers.",
        static (arguments, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AddResult(arguments.Left + arguments.Right));
        }))
    .AddAgentForge()
    .AddAgentDefinition("calculator", agent => agent
        .WithInstructions("Use the calculator tool for arithmetic.")
        .UseModel("fake-calculator-model")
        .AllowTool("calculator_add")
        .WithLimits(maxModelCalls: 3, maxToolCalls: 2))
    .BuildServiceProvider();

var agent = services.GetRequiredKeyedService<IAgent>("calculator");
var result = await agent.RunAsync("What is 20 + 22?");

Console.WriteLine(result.FinalMessage.Text);
Console.WriteLine($"Model calls: {result.ModelCalls}; Tool calls: {result.ToolCalls}");

internal sealed record AddArguments(decimal Left, decimal Right);

internal sealed record AddResult(decimal Value);
