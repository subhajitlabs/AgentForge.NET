using AgentForge.Abstractions;
using AgentForge.Hosting;
using AgentForge.Providers.OpenAI;
using Microsoft.Extensions.DependencyInjection;

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
{
    Console.WriteLine("Set OPENAI_API_KEY and OPENAI_MODEL to run this opt-in sample.");
    return;
}

await using var provider = new ServiceCollection()
    .AddOpenAIModelClient(options => options.ApiKey = apiKey)
    .AddAgentForge()
    .AddAgentDefinition("openai", agent => agent
        .WithInstructions("Answer concisely.")
        .UseModel(model)
        .WithLimits(4, 2))
    .BuildServiceProvider();

var agent = provider.GetRequiredKeyedService<IAgent>("openai");
var result = await agent.RunAsync("Reply with a short greeting.");
Console.WriteLine(result.FinalMessage.Text);

await foreach (var update in agent.StreamAsync("Reply with another short greeting."))
    if (update is AssistantTextDelta delta)
        Console.Write(delta.Text);
Console.WriteLine();
