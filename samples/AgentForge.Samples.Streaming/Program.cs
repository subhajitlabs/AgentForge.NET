using AgentForge.Abstractions;
using AgentForge.Core;
using AgentForge.Hosting;
using Microsoft.Extensions.DependencyInjection;

using var cancellation = new CancellationTokenSource();
var model = ScriptedStreamingAgentModelClient.FromEvents(
[
    new ModelTextDelta("Hello"),
    new ModelTextDelta(" from streaming."),
    new ModelResponseCompleted(ModelResponse.Completed(
        AssistantMessage.FromText("Hello from streaming.")))
]);

await using var provider = new ServiceCollection()
    .AddSingleton<IAgentModelClient>(model)
    .AddAgentForge()
    .AddAgentDefinition("greeter", agent => agent
        .WithInstructions("Greet the user briefly.")
        .UseModel("offline-script")
        .WithLimits(2, 1))
    .BuildServiceProvider();

var agent = provider.GetRequiredKeyedService<IAgent>("greeter");
await foreach (var update in agent.StreamAsync("Say hello.", cancellation.Token))
{
    switch (update)
    {
        case AssistantTextDelta delta:
            Console.Write(delta.Text);
            break;
        case AgentRunCompleted completed:
            Console.WriteLine();
            Console.WriteLine($"Model calls: {completed.Result.ModelCalls}");
            break;
    }
}
