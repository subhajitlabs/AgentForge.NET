# AgentForge.NET

AgentForge.NET is an early-stage, provider-neutral AI agent runtime for .NET 10. Phase 1 provides a deterministic single-agent execution loop, explicit tool authorization, bounded model/tool calls, dependency-injection registration, and a scripted fake model client.

## Build and test

Install the SDK pinned by `global.json`, then run:

```powershell
dotnet build AgentForge.slnx --configuration Release
dotnet test AgentForge.slnx --configuration Release --no-build
```

## Minimal runtime

Register one `IAgentModelClient`, any trusted `IAgentTool` implementations, and AgentForge:

```csharp
var services = new ServiceCollection()
    .AddSingleton<IAgentModelClient>(modelClient)
    .AddAgentTool<MyTool>()
    .AddAgentForge()
    .BuildServiceProvider();

var runtime = services.GetRequiredService<IAgentRuntime>();
var result = await runtime.RunAsync(request, cancellationToken);
```

Tools execute in process with the application's identity. Registration alone does not authorize use: each `AgentDefinition` must explicitly allow every tool it can invoke.

Run the deterministic calculator sample with:

```powershell
dotnet run --project samples/AgentForge.Samples.Console --configuration Release
```

See [the architecture document](docs/architecture.md) for lifecycle invariants, package boundaries, security constraints, non-goals, and planned phases.
