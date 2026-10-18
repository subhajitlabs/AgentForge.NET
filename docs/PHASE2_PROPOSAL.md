# AgentForge.NET Phase 2 Architecture Proposal

Status: Proposed — approval required before implementation  
Date: 2026-10-18  
Scope: One real provider, streaming, Microsoft.Extensions.AI interoperability, typed tools, and an ergonomic single-agent API

## 1. Decision summary

Phase 2 should extend the hardened Phase 1 single-agent runtime without replacing its execution policy. The recommended design is:

1. Add `AgentForge.Providers.OpenAI` as the first real provider adapter, targeting the OpenAI Responses API through the official .NET SDK. Keep credentials and provider SDK types inside that package.
2. Add provider-neutral streaming contracts to `AgentForge.Abstractions` and implement streaming orchestration in `AgentForge.Core`. Streaming is a second execution surface over the same authorization, limits, validation, transcript, and cancellation rules.
3. Add `AgentForge.Interop.MicrosoftExtensionsAI` as an optional adapter from `IChatClient` to AgentForge model-client contracts. Core and Abstractions do not reference Microsoft.Extensions.AI.
4. Add `AgentForge.Tools` for typed delegate tools, BCL-based schema generation, deserialization, and argument validation. Typed validation participates in atomic batch prevalidation through an optional capability interface.
5. Add a small `IAgent` facade and registered agent definitions in `AgentForge.Hosting` for beginner-friendly `RunAsync(string)` and `StreamAsync(string)` usage. The existing low-level runtime remains supported.
6. Add finish-reason contracts so incomplete model output fails explicitly rather than being guessed. Represent streaming capability through the streaming interface itself instead of adding a general capability object before a second native provider creates a concrete need.
7. Add deterministic provider/streaming/tool contract suites plus opt-in live OpenAI integration tests that never run during the default test command.

This proposal does not add persistence, graphs, multi-agent orchestration, retries, distributed execution, vector memory, OpenTelemetry exporters, or ASP.NET endpoints.

## 2. Goals and non-goals

### Goals

- Exercise the existing provider boundary against one production API.
- Preserve provider-neutral, immutable foundational contracts.
- Stream useful assistant progress without allowing partial tool arguments to bypass validation.
- Preserve Phase 1 cancellation, hard limits, explicit authorization, execution-wide call-ID uniqueness, and atomic batch validation.
- Offer Microsoft.Extensions.AI interoperability as an optional edge adapter.
- Make typed tools safe and pleasant without requiring application authors to manually write JSON parsing and schema boilerplate.
- Provide a beginner API without hiding limits, tool authorization, or cancellation.
- Define deterministic semantics for streaming capability detection, finish reasons, terminal completion, and partial failure.

### Non-goals

- Supporting more than one real provider in this phase.
- Selecting among multiple provider clients within a single agent execution.
- Durable or resumable streams.
- Retrying provider calls or tool invocations.
- Parallel tool execution.
- Streaming tool output.
- Provider-native hosted tools, background mode, file search, code execution, or computer use.
- Multimodal input/output.
- Persisting provider response identifiers or using provider-managed conversation state.
- Reverse-adapting an AgentForge agent into `IChatClient`.
- Automatic tool discovery by assembly scanning.
- Arbitrary JSON Schema support or a general-purpose JSON Schema validator.

These non-goals and every Phase 1 invariant in section 3 apply to each increment independently. An increment approval cannot implicitly relax them; doing so requires an explicit architecture amendment.

## 3. Architectural invariants carried forward

Phase 2 must not weaken these Phase 1 guarantees:

- `AgentForge.Abstractions` contains only provider-neutral contracts and BCL types.
- Public collections are non-mutable snapshots and JSON values are owned clones.
- Tool execution requires registration and an explicit per-agent allow-list.
- Unknown allow-list entries fail before any model request.
- `MaxModelCalls` and `MaxToolCalls` are positive and enforced before invocation.
- Tool-call batches are completely validated before the first tool executes.
- Tool-call IDs are unique across an execution.
- Tool execution remains sequential and deterministic.
- Caller cancellation is checked before and after awaited operations and before successful completion.
- Prompts, content, tool arguments/results, credentials, and model-controlled identifiers are not logged by default.
- Provider and tool causes remain available through inner exceptions without leaking sensitive response bodies into messages.
- `RunAsync` retains its current exception-first semantics.

Streaming adds observable provisional output; it does not turn that output into durable state or weaken final transcript validation.

## 4. Proposed package structure

```text
src/
  AgentForge.Abstractions/                 existing; additive contracts
  AgentForge.Core/                         existing; streaming orchestration
  AgentForge.Hosting/                      existing; DI and agent facade registration
  AgentForge.Tools/                        new; typed tool/schema/validation helpers
  AgentForge.Providers.OpenAI/             new; official OpenAI SDK adapter
  AgentForge.Interop.MicrosoftExtensionsAI/ new; optional IChatClient adapter

tests/
  AgentForge.Core.Tests/                   existing; runtime and streaming tests
  AgentForge.Tools.Tests/                  new; schema and argument-validation tests
  AgentForge.Providers.OpenAI.Tests/        new; deterministic mapping/error tests
  AgentForge.Interop.MicrosoftExtensionsAI.Tests/ new; deterministic mapping tests
  AgentForge.IntegrationTests/             new; opt-in live OpenAI tests only

samples/
  AgentForge.Samples.Console/              existing; retain deterministic sample
  AgentForge.Samples.OpenAI.Console/       new; real provider, explicit opt-in
```

`AgentForge.Tools` is justified because schema generation and typed invocation are reusable runtime-edge behavior, not DI composition and not orchestration. `AgentForge.Interop.MicrosoftExtensionsAI` is separate so consumers that do not use Microsoft.Extensions.AI do not acquire it transitively.

### Dependency direction

```text
AgentForge.Hosting ---------------------> AgentForge.Core
       |                                      |
       +--------------------------------------v
                                      AgentForge.Abstractions

AgentForge.Tools ----------------------> AgentForge.Abstractions

AgentForge.Providers.OpenAI -----------> AgentForge.Abstractions
       |
       +----> official OpenAI .NET SDK

AgentForge.Interop.MicrosoftExtensionsAI -> AgentForge.Abstractions
       |
       +----> Microsoft.Extensions.AI.Abstractions

Samples -> Hosting + selected provider/interoperability/tools packages
Tests   -> package under test + deterministic fakes
```

Forbidden dependencies:

- Core must not reference OpenAI, Microsoft.Extensions.AI, Hosting, or Tools.
- Abstractions must not reference provider SDKs, Microsoft.Extensions.AI, DI, configuration, or logging implementations.
- The OpenAI and Microsoft.Extensions.AI packages must not reference each other.
- Tools must not reference Core or Hosting.

## 5. Provider-neutral model capabilities

Phase 1 assumes one configured `IAgentModelClient`. Phase 2 should retain that assumption rather than prematurely adding a multi-provider resolver. The selected adapter interprets `ModelId`.

Do not add `IAgentModelClientCapabilities` or a general capability record in the first increment. That surface would duplicate facts already expressed by contracts and would invite speculative flags.

Rules:

- Phase 1 `IAgentModelClient` continues to mean non-streaming text and tool-call completion.
- Streaming support is expressed by implementing `IStreamingAgentModelClient`. A streaming run against a client that does not implement it fails before provider invocation with stable `CapabilityNotSupported` classification.
- Usage remains optional data on the terminal response; no capability flag is required.
- Provider or model limits such as maximum tool definitions are validated inside the adapter when known. A server rejection is mapped through the provider error taxonomy.
- A new public capability contract may be proposed only when at least two adapters need callers to negotiate the same behavior before invocation. That proposal requires separate approval.

This is intentionally capability detection, not broad capability negotiation. It answers the only Phase 2 question Core must ask—whether streaming is available—without exposing provider-specific model matrices.

### Why not add provider routing now?

Adding `ProviderId`, keyed client resolution, fallback, or per-run provider selection would materially expand Phase 2 and affect `AgentDefinition`, DI lifetimes, failure policy, and observability. One real provider plus the optional MEAI adapter can be selected at application composition time. Multi-provider routing should be designed when the second native provider is approved.

## 6. Finish-reason semantics

Provider adapters must map provider-specific terminal states into a closed provider-neutral enum:

```csharp
public enum ModelFinishReason
{
    Completed,
    ToolCalls,
    LengthLimit,
    ContentFiltered,
    Refused,
    Cancelled,
    Unknown
}

public sealed record ModelResponse(
    AssistantMessage Message,
    ModelFinishReason FinishReason,
    ModelUsage? Usage = null);
```

Runtime rules:

| Finish reason | Valid message shape | Runtime behavior |
|---|---|---|
| `Completed` | Non-empty assistant text, no tool calls | Complete the run |
| `ToolCalls` | One or more complete tool calls, no text | Prevalidate and execute the batch |
| `LengthLimit` | Optional partial text | Fail with `ModelOutputIncomplete` |
| `ContentFiltered` | Optional safe provider text | Fail with `ModelOutputFiltered` |
| `Refused` | Optional refusal text | Fail with `ModelRefused` |
| `Cancelled` | No contractually useful output | Propagate caller cancellation when requested; otherwise provider failure |
| `Unknown` | Any provider-safe mapped content | Fail with `InvalidModelResponse` |

The error-code additions are provider-neutral policy outcomes, not OpenAI types. Partial/refusal text must not be copied into exception messages or logs. A future result-oriented policy could expose refusals as data, but Phase 2 should retain the existing exception-first runtime behavior.

Providers must never infer `Completed` solely because a stream ended. They require an explicit provider terminal event or a documented, deterministic SDK terminal result.

## 7. Streaming contracts

### Provider streaming boundary

Add a separate optional interface so existing `IAgentModelClient` implementations remain source-compatible:

```csharp
public interface IStreamingAgentModelClient
{
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}
```

The minimal provider event hierarchy is:

```csharp
public abstract record ModelStreamEvent;

public sealed record ModelTextDelta(string Text) : ModelStreamEvent;

public sealed record ModelResponseCompleted(ModelResponse Response)
    : ModelStreamEvent;
```

Provider adapters may accumulate tool-call argument fragments internally, but they must expose tool calls to Core only in the immutable terminal `ModelResponse`. This prevents partial JSON, duplicated fragments, or provider ordering details from entering tool authorization and validation.

Provider-stream invariants:

- Zero or more text deltas followed by exactly one `ModelResponseCompleted`.
- No event after completion.
- Tool-call responses do not emit text deltas in Phase 2.
- The completed response is authoritative and contains the fully reconstructed assistant message, usage, and finish reason.
- Missing, repeated, or contradictory completion events produce a provider protocol failure.
- All event payloads use immutable snapshots and owned values.

#### Adapter and Core responsibilities

`IAsyncEnumerable<T>` exposes only three observable outcomes to its consumer: a yielded item, an exception, or the end of enumeration. A normal end is not evidence that the remote response completed successfully. The interface also cannot prove what happened on the transport before the adapter yielded an item. Responsibilities therefore divide as follows:

The provider adapter owns transport-protocol validation. It must:

- parse the raw HTTP/SSE or SDK stream and require the provider's documented terminal event;
- reconstruct text, tool-call IDs, names, and argument fragments without exposing partial tool calls;
- reject duplicate/conflicting fragments, provider failure events, a missing terminal event, or transport EOF before terminal completion;
- map exactly one valid provider terminal response to one `ModelResponseCompleted`;
- after yielding that completion, continue far enough to establish clean transport termination, and throw a sanitized `ModelProviderException` with `Protocol` kind if a later contradictory event appears;
- pass cancellation to every transport read and check cancellation around awaited reads.

Core owns defensive validation of the provider-neutral enumeration. It must:

- explicitly acquire and dispose the async enumerator, enumerate with the caller/iterator cancellation token, and continue through normal end after receiving completion;
- accept text deltas only before completion and reject zero completions, more than one completion, or any event after completion;
- treat normal enumeration end before `ModelResponseCompleted` as an invalid provider response;
- validate the terminal `ModelResponse`, then apply limits, transcript, atomic tool-batch validation, tool execution, usage, and cancellation policy exactly as in the non-streaming path.

Core cannot validate raw SSE frames or infer a provider terminal condition from EOF; doing so would cross the adapter boundary. Conversely, an adapter cannot decide AgentForge authorization, budgets, transcript validity, or tool execution policy.

Cancellation through `IAsyncEnumerable<T>` is cooperative. A token on `StreamAsync` does not by itself guarantee that an iterator observes a token passed by `WithCancellation`; implementations must wire iterator cancellation correctly (including `[EnumeratorCancellation]` when an async iterator is used). Core should use a linked iterator token and dispose the enumerator in `finally`. Early consumer termination (`break`, disposal, or abandoned enumeration when properly disposed) cancels local work and prevents later tool execution, but cannot guarantee that a remote server has stopped work already accepted.

### Runtime streaming boundary

Add a separate runtime interface:

```csharp
public interface IStreamingAgentRuntime
{
    IAsyncEnumerable<AgentRunEvent> StreamAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);
}
```

Keep the application event surface to the two facts a caller needs to render output and recognize authoritative success:

```csharp
public abstract record AgentRunEvent;

public sealed record AssistantTextDelta(
    string Text) : AgentRunEvent;

public sealed record AgentRunCompleted(
    AgentRunResult Result) : AgentRunEvent;
```

Enumeration order already orders deltas, and each enumeration represents one execution, so public sequence and execution-ID fields add no Phase 2 behavior. `AssistantTextDelta` exists for a console/UI to render incremental text. `AgentRunCompleted` exists because displayed deltas are provisional and callers need an unambiguous success/result signal. Start and tool-lifecycle events are deferred: no approved Phase 2 example consumes them, and exposing them would make timing, identifiers, and ordering permanent compatibility surfaces. Metadata-only internal logging may still observe lifecycle transitions under the existing redaction rules.

### Terminal and partial-failure semantics

- A successful stream emits exactly one final `AgentRunCompleted` event and then ends.
- Core does not emit `AgentRunCompleted` merely when it sees the provider's `ModelResponseCompleted`; it first validates that provider enumeration ends cleanly and completes all applicable transcript/tool turns. This prevents a later adapter protocol exception from following an application-visible success event.
- Failure is represented by the same exceptions as `RunAsync`; there is no competing `AgentRunFailed` event.
- Caller cancellation terminates enumeration with `OperationCanceledException` and emits no completion event.
- Events already consumed before failure are provisional observations. They are not a resumable checkpoint or a successful partial result.
- Text deltas may have been displayed before a later `LengthLimit`, content filter, provider failure, or cancellation. Consumers must treat completion as authoritative.
- No tool executes until its entire response is assembled and the whole batch passes the same authorization, registration, ID, JSON/schema, and budget checks used by non-streaming execution.
- If a later tool invocation fails, earlier successful tool side effects are not rolled back, matching Phase 1.
- Disposing/stopping enumeration must cancel the active provider/tool operation and prevent further tool execution. Implementation must use an iterator-linked cancellation source and dispose it deterministically. The consumer is responsible for disposing the enumerator; normal `await foreach`/`break` does this, while simply abandoning a manually acquired enumerator does not.

### Relationship between `RunAsync` and `StreamAsync`

Do not implement `RunAsync` by publicly consuming `AgentRunEvent` objects, and do not maintain two independent policy loops. Extract an internal execution engine whose sinks receive validated lifecycle notifications:

```text
AgentRuntime.RunAsync ------+
                            +--> internal execution engine --> one policy implementation
StreamingAgentRuntime ------+
```

The non-streaming path continues to use `IAgentModelClient.CompleteAsync`. The streaming path requires `IStreamingAgentModelClient`. Both share request validation, limits, tool prevalidation, transcript construction, usage aggregation, error translation, and tool execution.

### Public surface budget

Every proposed public abstraction must satisfy an external composition need:

| Public surface | Concrete need | Why an existing type is insufficient |
|---|---|---|
| `IStreamingAgentModelClient` | A third-party provider adapter streams model output into Core. | Adding streaming to `IAgentModelClient` would break Phase 1 implementations. |
| `ModelTextDelta` and `ModelResponseCompleted` | Core receives provisional text and one authoritative reconstructed response. | Partial provider tool fragments must not enter Core, and EOF is not completion. |
| `IStreamingAgentRuntime` | Advanced callers stream an existing `AgentRunRequest`. | Adding a method to `IAgentRuntime` would break implementations. |
| `AssistantTextDelta` and `AgentRunCompleted` | A console renders text as it arrives, then accepts the final result only on completion. | One type cannot safely make provisional deltas look terminal without a discriminator. |
| `IAgentToolArgumentValidator` | Core performs typed validation during its existing atomic batch prevalidation. | Core cannot reference `AgentForge.Tools` or know its type metadata. |
| `IAgent` | A web endpoint depends on one configured agent and calls `RunAsync(string)` without constructing definitions. | `IAgentRuntime` intentionally requires the full low-level request. |

No public start event, tool-lifecycle event, event sequence number, execution factory, capability bag, provider router, or general schema abstraction is approved here. Internal types should remain internal. Any later addition requires a concrete consumer example and separate contract review.

## 8. OpenAI provider adapter

### API selection

Use the OpenAI Responses API through the official OpenAI .NET SDK for the first native provider, subject to implementation-time confirmation of the then-current SDK surface. The adapter maps AgentForge messages, JSON tool definitions, tool calls/results, usage, finish reasons, streaming events, request IDs, and errors. It does not expose OpenAI SDK types outside `AgentForge.Providers.OpenAI`.

The provider must use stateless requests built from the complete AgentForge transcript. Provider-managed conversation IDs and hosted tools remain out of scope because they would introduce provider-specific state and durability semantics.

### Configuration and credential handling

Proposed DI surface:

```csharp
services.AddOpenAIModelClient(options =>
{
    options.ApiKey = configuration["AgentForge:OpenAI:ApiKey"];
    options.Endpoint = configuration["AgentForge:OpenAI:Endpoint"];
});
```

And an advanced overload for applications that construct the official SDK client themselves:

```csharp
services.AddOpenAIModelClient(_ => preconfiguredOpenAIClient);
```

Security requirements:

- No default API key value and no key in source, samples, test data, logs, exceptions, debugger displays, or `ToString()` output.
- Documentation recommends environment variables, .NET user secrets for local development, and the deployment platform's secret store.
- Missing/blank credentials fail during options validation or provider construction, before the first model call.
- The adapter sends credentials only through the official SDK transport.
- Custom endpoints are optional and must be absolute HTTPS URIs by default; an explicit development-only opt-in is required for HTTP loopback endpoints.
- Options snapshots must not retain `IConfiguration` or service-provider references.
- Live tests read credentials only at execution time and skip cleanly when the opt-in flag or key is absent.

`OpenAIProviderOptions` should be a sealed mutable options type for DI binding, while the provider constructor converts it into a private immutable validated snapshot. It should be annotated to prevent accidental debugger/display disclosure where practical.

### Provider error classification

Add a provider-neutral adapter exception used below the runtime boundary:

```csharp
public enum ModelProviderErrorKind
{
    Authentication,
    Authorization,
    InvalidRequest,
    ModelNotFound,
    RateLimited,
    Timeout,
    ServiceUnavailable,
    Transport,
    Protocol,
    Unknown
}

public sealed class ModelProviderException : Exception
{
    public ModelProviderErrorKind Kind { get; }
    public bool IsTransient { get; }
    public TimeSpan? RetryAfter { get; }
    public string? ProviderRequestId { get; }
}
```

Rules:

- Messages are fixed, sanitized summaries and never include request/response bodies, prompts, keys, headers, tool payloads, or provider error content.
- The original SDK exception is preserved as `InnerException` but is never logged by AgentForge Core.
- `ProviderRequestId` is allowed only after format/length sanitization.
- Authentication, authorization, invalid request, and model-not-found errors are non-transient.
- Rate limiting, timeout, service unavailable, and qualifying transport errors may be marked transient; this is metadata only. Phase 2 does not retry.
- HTTP status alone is insufficient where the SDK exposes a more precise safe category.
- Core wraps `ModelProviderException` in the existing execution-scoped `AgentProviderException`, preserving the classification as the inner exception.
- SDK cancellation caused by the caller token remains `OperationCanceledException`, not `ModelProviderException`.

### OpenAI mapping requirements

- Convert the AgentForge system/user/assistant/tool-result transcript without dropping tool-call IDs or ordering.
- Send only the already-filtered `ModelRequest.AvailableTools` collection.
- Disable or decline provider parallel-tool behavior unless the adapter can guarantee deterministic ordered reconstruction; Core still executes sequentially.
- Accumulate streaming tool-call fragments privately and reject missing IDs, conflicting names, malformed JSON, or ambiguous terminal states as `Protocol` errors.
- Map provider token usage using checked arithmetic.
- Preserve no provider response body in public exceptions or default logs.
- Verify all SDK type names, streaming event mappings, finish-status mappings, and error accessors against current official OpenAI documentation and the pinned SDK during implementation.

Official implementation references to confirm at implementation time:

- OpenAI Responses API: <https://platform.openai.com/docs/api-reference/responses>
- Function calling: <https://platform.openai.com/docs/guides/function-calling>
- Streaming responses: <https://platform.openai.com/docs/guides/streaming-responses>
- Production and API-key guidance: <https://platform.openai.com/docs/guides/production-best-practices>

## 9. Microsoft.Extensions.AI interoperability

Add `AgentForge.Interop.MicrosoftExtensionsAI` with an adapter that wraps an `IChatClient`:

```csharp
IAgentModelClient modelClient =
    new MicrosoftExtensionsAIModelClient(chatClient);
```

The package owns all translations between MEAI and AgentForge types:

- roles and text content;
- assistant tool calls and correlated tool results;
- tool schemas and JSON arguments;
- usage;
- finish reasons;
- streaming text deltas and terminal responses;
- cancellation and exception classification.

Important constraints:

- AgentForge remains the tool executor. The adapter supplies tool definitions to `IChatClient` but does not enable middleware that automatically invokes tools.
- If an `IChatClient` pipeline performs tool execution itself, the adapter must reject that configuration or document it as unsupported because it bypasses authorization, budgets, correlation, and atomic batch validation.
- Unknown MEAI content types or finish reasons fail explicitly; they are not silently discarded.
- Provider-specific content is not placed into AgentForge messages in Phase 2.
- The adapter implements `IStreamingAgentModelClient` only if the supported MEAI contract can provide a real streaming path. Model-specific unsupported behavior is reported explicitly rather than advertised through a speculative capability bag.
- Core and Abstractions never reference MEAI types.

Only the `IChatClient -> IAgentModelClient/IStreamingAgentModelClient` direction is proposed. Exposing an AgentForge agent as `IChatClient` has different conversation, middleware, and tool-ownership semantics and is deferred until a concrete consumer requires it.

The exact MEAI API surface and package version must be verified against the pinned Microsoft documentation/package during implementation; the proposal intentionally does not freeze preview or release-specific method names.

## 10. Typed tool registration

### Package and API

`AgentForge.Tools` should provide a typed adapter over `IAgentTool`:

```csharp
public static class AgentTool
{
    public static IAgentTool Create<TArguments, TResult>(
        string name,
        string description,
        Func<TArguments, ToolExecutionContext, CancellationToken, ValueTask<TResult>> handler,
        JsonSerializerOptions? serializerOptions = null);
}

public sealed record ToolExecutionContext(
    ExecutionId ExecutionId,
    string ToolCallId);
```

Convenience overloads may omit `ToolExecutionContext`, but all asynchronous overloads retain a trailing `CancellationToken`. Avoid reflection-based invocation of arbitrary methods and avoid assembly scanning.

### Schema generation

Generate JSON Schema from the same frozen `System.Text.Json` contract used for validation and deserialization, using the .NET 10 schema-export surface after verifying its current API. At registration, copy the supplied `JsonSerializerOptions`, make the copy read-only, resolve one `JsonTypeInfo<TArguments>`, and retain only this immutable registration contract. Caller mutation of the original options or resolver must not change registered behavior.

Schema export, validation, and deserialization must agree on:

- JSON property names and case sensitivity;
- required, optional, nullable, and default-value behavior;
- enum representation and permitted values;
- numeric representation and `JsonNumberHandling`;
- collection element contracts and maximum nesting;
- unmapped-member handling and whether additional properties are accepted;
- every custom converter that is allowed.

The schema is derived from the resolved `JsonTypeInfo<TArguments>`—not independently from CLR reflection—and the validator is compiled/interpreted from that generated bounded schema plus the same metadata. Invocation deserializes the same owned `JsonElement` with the same `JsonTypeInfo<TArguments>`. Registration fails if any of these three stages cannot express identical semantics; the implementation must not publish a permissive schema and then apply stricter, hidden deserialization rules.

Initial supported shapes:

- sealed POCOs and records with public serializable properties;
- strings, booleans, integral/floating numeric types, decimals;
- nullable values;
- enums with one documented representation;
- arrays and bounded generic lists;
- nested supported objects.

Reject at registration:

- `object`, dynamic values, delegates, streams, pointers, open generics, and arbitrary polymorphism;
- reference cycles;
- dictionaries with unsupported key types;
- duplicate JSON property names;
- custom converters whose exact accepted JSON shape cannot be represented by the supported schema subset;
- schemas that are undefined, non-object at the argument root, or exceed configured depth/property limits.

Defaults:

- Root arguments are JSON objects.
- Unknown properties are rejected by both validation and deserialization; an eventual opt-in must change both together and requires explicit design approval.
- Requiredness follows serializer/nullability metadata with documented rules.
- Schema property ordering is deterministic.
- The resolved type metadata, schema, validator, and serializer options are immutable snapshots created together at registration.

### Atomic argument validation

Typed validation must occur before any tool in the model-requested batch executes. Add an optional capability contract:

```csharp
public interface IAgentToolArgumentValidator
{
    void ValidateArguments(JsonElement arguments);
}
```

During existing batch prevalidation, Core calls this interface after authorization/registration and before committing the batch. Validation failure becomes `AgentExecutionException` with a new `InvalidToolArguments` error code and a safe generic message. The underlying validation exception may be preserved but must not contain raw arguments.

Only after the entire batch validates does the typed tool deserialize its already-validated owned JSON with the captured `JsonTypeInfo<TArguments>` and invoke the delegate. Because validation and deserialization use one frozen contract, a post-validation deserialization failure is a tool-adapter defect or environmental failure and is classified as `AgentToolException`; it must never be treated as model-correctable invalid arguments. Handler failures are also `AgentToolException`; no automatic retry occurs.

Validation must enforce the generated subset directly rather than accepting arbitrary external JSON Schema documents. A general JSON Schema engine is out of scope.

### DI registration

```csharp
services.AddAgentTool(AgentTool.Create<AddArguments, AddResult>(
    "calculator_add",
    "Adds two decimal numbers.",
    static (arguments, context, cancellationToken) =>
        ValueTask.FromResult(new AddResult(arguments.Left + arguments.Right))));
```

Add a non-generic `AddAgentTool(IAgentTool tool)` overload. Registration still does not authorize the tool; every agent definition must explicitly allow its exact name.

## 11. Ergonomic agent creation and execution

The low-level API remains authoritative:

```csharp
var definition = new AgentDefinition(
    new AgentId("support"),
    "Answer concisely and use only explicitly allowed tools.",
    new ModelId("configured-openai-model"),
    ["orders.lookup"],
    new AgentExecutionLimits(MaxModelCalls: 6, MaxToolCalls: 4));

var result = await runtime.RunAsync(
    new AgentRunRequest(definition, [new UserMessage("Where is order 123?")]),
    cancellationToken);
```

### Registered definition API

Add immutable named definitions through Hosting:

```csharp
services.AddAgentForge();
services.AddAgentDefinition("support", agent => agent
    .WithInstructions("Answer concisely and use only explicitly allowed tools.")
    .UseModel("configured-openai-model")
    .AllowTool("orders.lookup")
    .WithLimits(maxModelCalls: 6, maxToolCalls: 4));
```

Registration validates identifiers, instructions, limits, duplicate names, and allowed-tool existence when the service provider is built or the registry is first resolved. The builder produces the existing immutable `AgentDefinition`; it does not create a parallel definition model.

### Beginner-facing facade

```csharp
public interface IAgent
{
    AgentDefinition Definition { get; }

    Task<AgentRunResult> RunAsync(
        string message,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentRunEvent> StreamAsync(
        string message,
        CancellationToken cancellationToken = default);
}

```

`IAgent` is the only new beginner-facing interface: it gives application code a narrow, mockable bound-agent dependency and removes request/definition ceremony. It does not duplicate runtime policy. Do not add `IAgentFactory`; .NET keyed DI already resolves a named agent. Applications needing runtime selection should keep that choice at their composition boundary rather than making provider/runtime selection part of AgentForge's foundational API.

Usage:

```csharp
var agent = serviceProvider
    .GetRequiredKeyedService<IAgent>("support");

var result = await agent.RunAsync(
    "Where is order 123?",
    cancellationToken);

Console.WriteLine(result.FinalMessage.Text);
```

Streaming usage:

```csharp
await foreach (var update in agent.StreamAsync(
    "Where is order 123?",
    cancellationToken))
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
```

The facade creates exactly one `UserMessage` and delegates to the existing runtime. It does not add memory, implicit conversation state, hidden retries, default tools, or unbounded limits. Advanced callers continue using `AgentRunRequest` directly.

### Full beginner setup example

```csharp
var services = new ServiceCollection();

services.AddOpenAIModelClient(options =>
{
    options.ApiKey = configuration["AgentForge:OpenAI:ApiKey"];
});

services.AddAgentTool(AgentTool.Create<WeatherArguments, WeatherResult>(
    "weather.current",
    "Gets current weather for a city.",
    GetWeatherAsync));

services.AddAgentForge();
services.AddAgentDefinition("weather", agent => agent
    .WithInstructions("Use weather.current when current weather is requested.")
    .UseModel("configured-openai-model")
    .AllowTool("weather.current")
    .WithLimits(maxModelCalls: 4, maxToolCalls: 2));

await using var provider = services.BuildServiceProvider();
var weatherAgent = provider.GetRequiredKeyedService<IAgent>("weather");
var result = await weatherAgent.RunAsync("Weather in Pune?", cancellationToken);
```

The actual model name belongs in deployment configuration rather than a library constant or committed secret-bearing sample configuration.

## 12. Deterministic contract testing

### Shared model-client contract suite

Create reusable abstract tests/factories that every adapter can run. For the OpenAI adapter, prefer deterministic transport-level fixtures: inject a controlled `HttpMessageHandler` or loopback in-memory HTTP transport through supported public SDK configuration, send real wire-format JSON/SSE chunks through the production adapter, and capture its outbound request. Vary chunk boundaries and terminal/EOF behavior. Do not mock internal SDK types, use reflection against SDK internals, subclass undocumented SDK implementation types, or serialize SDK objects as golden fixtures.

If the pinned SDK cannot accept a deterministic transport through a supported public API, the provider increment must stop at its approval checkpoint and choose explicitly between (a) a narrow internal transport seam owned by the provider package or (b) direct BCL HTTP for the protocol layer while retaining the official SDK where it adds value. This is an unresolved implementation decision, not permission to couple tests to unstable SDK internals.

The shared suite covers:

- text completion mapping;
- tool definition mapping and ordering;
- assistant tool-call and tool-result correlation;
- finish-reason mapping;
- nonnegative and overflow-safe usage mapping;
- cancellation before request, during request, during stream, and after the provider returns;
- malformed/missing/duplicate tool-call fragments;
- missing or duplicate stream completion;
- immutable collection and JSON ownership;
- streaming-interface capability detection;
- sanitized error classification for every provider error kind;
- absence of prompts, arguments, results, headers, keys, and response bodies in logs/exceptions.

No deterministic test uses the network, real credentials, wall-clock sleeps, SDK implementation timing, or undocumented SDK behavior. Wire fixtures are versioned against the pinned public API contract and contain only synthetic data.

### Core streaming tests

- exactly one completion event on success;
- no completion event on failure/cancellation;
- text deltas preserve order;
- terminal transcript equals non-streaming semantics;
- tool calls execute only after terminal provider reconstruction and full batch validation;
- limits are enforced across streamed model turns;
- consumer disposal cancels work and prevents later tools;
- cancellation from an ignored token is observed after each provider/tool await;
- partial deltas followed by length/filter/refusal/provider failure throw the expected exception;
- concurrent streaming runs do not share events, transcripts, counters, or tool-call IDs.
- normal provider enumeration end without a completion, duplicate completion, and an event after completion are rejected;
- early consumer disposal cancels local enumeration and prevents subsequent tool execution without claiming guaranteed remote cancellation.

### Typed-tool tests

- deterministic schema snapshots for every supported shape;
- required/nullable/enum/nested/list behavior;
- rejection of unsupported types, cycles, converters, depth, and duplicate names;
- validation before any batch side effect;
- safe validation errors without argument disclosure;
- serializer options and source objects cannot mutate registered behavior;
- cancellation reaches delegates;
- typed result JSON ownership.

### Microsoft.Extensions.AI adapter tests

- bidirectional message-content mapping required for the one supported adapter direction;
- tool calls/results preserve IDs and order;
- middleware does not execute tools;
- unknown content and finish reasons fail explicitly;
- streaming and cancellation follow AgentForge contracts;
- MEAI exceptions map without leaking content.

## 13. Opt-in live OpenAI integration tests

Live tests belong in `AgentForge.IntegrationTests` and are excluded from the default test run using a trait/category such as `LiveOpenAI`.

Required gates:

- `AGENTFORGE_RUN_OPENAI_LIVE_TESTS=1`;
- a non-empty API key supplied through the environment/secret store;
- an explicitly configured model name;
- no committed credentials or fallback model.

Initial live cases:

1. one non-streaming text completion;
2. one streaming text completion with a terminal event;
3. one deterministic, side-effect-free typed tool round-trip;
4. caller cancellation of a streaming request;
5. invalid credential classification, only when explicitly enabled to incur that request.

Live assertions should avoid exact prose and token counts. Assert structural invariants, non-empty output, event ordering, correlation, bounded calls, classification, and redaction. Tests should print neither prompts nor responses by default.

The default commands remain deterministic and offline:

```powershell
dotnet build AgentForge.slnx --configuration Release
dotnet test AgentForge.slnx --configuration Release
```

Live execution requires a separate documented command with a test filter and explicit environment opt-in.

## 14. Migration impact

### Source-compatible additions

- `IStreamingAgentModelClient` is an optional interface; its presence is the streaming capability check.
- `IStreamingAgentRuntime` is separate from `IAgentRuntime`.
- `IAgentToolArgumentValidator` is optional; existing untyped tools continue to work.
- New hosting builder/facade APIs wrap existing immutable definitions and requests.
- New error-code enum members are additive for source, though consumers with exhaustive switches must update.

### Intentional contract changes requiring review

- `ModelResponse` gains mandatory `ModelFinishReason`. Existing fake providers and tests must specify `Completed` or `ToolCalls`.
- Streaming introduces public event types whose ordering becomes a compatibility surface.
- `ModelProviderException` adds a provider-neutral adapter error surface.
- Typed validation adds `InvalidToolArguments` and capability failure adds `CapabilityNotSupported`; incomplete output adds finish-related error codes.

To reduce churn, provide factory methods:

```csharp
ModelResponse.Completed(AssistantMessage message, ModelUsage? usage = null);
ModelResponse.ToolCalls(AssistantMessage message, ModelUsage? usage = null);
```

Mark the raw constructor as advanced but do not hide finish reason through inference.

### Behavioral compatibility

- Existing non-streaming Phase 1 calls retain authorization, limits, cancellation, transcript, and exception behavior.
- Existing untyped tools remain valid but receive only structural JSON validity checks unless they implement the optional validator.
- No automatic retry or provider fallback is introduced.
- The deterministic console sample remains offline and unchanged except for required `ModelFinishReason` migration.

## 15. Alternatives considered

### Put OpenAI types directly in Abstractions

Rejected. It would couple Core consumers and every future provider to one SDK's versioning and protocol model.

### Implement OpenAI only through Microsoft.Extensions.AI

Not recommended for the first native adapter. It reduces mapping code but can obscure OpenAI-specific error classification, request IDs, finish details, and streaming protocol failures. A direct adapter plus an independent MEAI adapter gives users both fidelity and interoperability.

### Add streaming methods directly to existing interfaces

Rejected. It would break every existing implementation and force non-streaming providers to emulate unsupported behavior.

### Represent failures as terminal stream events

Rejected for Phase 2. It creates two error channels and diverges from `RunAsync`. Exceptions retain existing semantics; consumed deltas already express partial observation.

### Stream partial tool-call arguments publicly

Rejected. Partial JSON is provider-specific, frequently invalid, and unsafe to authorize or execute. The adapter reconstructs it privately and Core sees only complete immutable calls.

### Use provider-managed conversation state

Rejected. It creates provider coupling and hidden persistence before durable execution semantics exist.

### Generate typed tools with source generators immediately

Deferred. Source generation could improve AOT and startup later, but a bounded BCL reflection/metadata implementation validates the public design first. The tool contract should permit a future generated implementation without changing runtime semantics.

### Build a general JSON Schema validator

Rejected. Phase 2 needs validation for schemas AgentForge itself generates, not arbitrary schemas. A bounded generated subset is smaller, safer, and testable.

### Add multi-provider resolution now

Deferred. One native provider and one optional MEAI composition choice do not justify changing agent identity and resolution semantics yet.

## 16. Tradeoffs and risks

| Decision | Benefit | Cost/risk | Mitigation |
|---|---|---|---|
| Direct OpenAI adapter | Full error/stream fidelity | More mapping and SDK churn | Isolate SDK, pin version, contract tests |
| Separate MEAI adapter | Ecosystem interoperability without Core coupling | Duplicate mapping paths | Shared adapter contract suite |
| Separate streaming interfaces | Source compatibility and explicit capability | More types and DI wiring | Shared internal execution engine |
| Exceptions terminate streams | Matches existing runtime and .NET cancellation | Consumers must handle partial displayed text | Document provisional events and terminal authority |
| Private tool-fragment reconstruction | Preserves atomic safety | Tool intent is visible only after provider completion | Keep lifecycle internal; add public events only when a concrete consumer is approved |
| BCL typed schema generation | Minimal dependencies | Limited schema vocabulary | Fail registration for unsupported shapes |
| Optional validation capability | Existing tools remain compatible | Untyped tools remain less safe | Recommend typed tools; document distinction |
| Beginner facade over low-level API | Better onboarding without hidden policy | Additional public surface | Keep facade thin and contract-tested |
| One configured provider | Avoids premature routing | Cannot mix providers per agent | Revisit with second native provider |

Primary implementation risks:

- Streaming and non-streaming policy loops drift. Mitigate with one internal engine and parity tests.
- SDK streaming events change. Mitigate through strict package isolation and deterministic adapter fixtures.
- MEAI middleware invokes tools unexpectedly. Detect/reject and document ownership.
- Schema generation disagrees with deserialization. Use one serializer metadata configuration and golden tests.
- Partial text is mistaken for completion. Require `AgentRunCompleted` as the only success signal.
- Secrets leak through SDK exceptions. Sanitize public messages and test logging/exception surfaces with sentinel values.

## 17. Incremental implementation order

Architecture approval accepts only the direction and constraints in this document. It does **not** authorize implementation of any increment. Each increment below is a separate approval gate:

1. The maintainer explicitly names the increment authorized for implementation.
2. Work is limited to that increment's scope and acceptance criteria.
3. Build, tests, changed files, deviations, and residual risks are reported at the end of the increment.
4. Work stops. Completion of one increment does not authorize the next.

Later increments may be amended or declined without invalidating completed earlier increments. Dependencies state ordering, not blanket permission.

### Increment 1: finish reasons

- Add finish reasons, error codes, and response factories.
- Migrate fake provider, runtime validation, existing sample, and tests.
- No streaming or real provider yet.

Acceptance:

- All Phase 1 tests remain green after explicit finish-reason migration.
- Every finish reason has deterministic runtime behavior.

### Increment 2: internal execution-engine extraction

Approval dependency: Increment 1 accepted. Separate implementation approval required.

- Extract shared validation, counters, transcript, usage, tool execution, and cancellation logic without changing public behavior.
- Retain the existing `RunAsync` implementation surface.

Acceptance:

- Phase 1 observable behavior and all regression tests are unchanged.
- No duplicate authorization/limit/tool execution implementation exists.

### Increment 3: provider-neutral streaming

Approval dependency: Increments 1–2 accepted. Separate implementation approval required.

- Add provider/runtime streaming interfaces and immutable events.
- Extend the scripted fake with deterministic streaming steps.
- Implement cancellation/disposal and partial-failure tests.

Acceptance:

- Streaming event and terminal semantics meet section 7.
- Streaming/non-streaming parity tests pass.
- Tools never execute from partial provider events.
- A streaming request without `IStreamingAgentModelClient` fails before provider invocation.

### Increment 4: typed tools

Approval dependency: Phase 1 plus any approved Core prevalidation hook; it need not authorize provider work. Separate implementation approval required.

- Add `AgentForge.Tools`, bounded schema generation, validator capability, typed invocation, and DI overload.

Acceptance:

- Supported schemas are deterministic.
- Unsupported shapes fail registration.
- Invalid arguments reject the whole batch before side effects.
- No raw arguments appear in errors/logs.

### Increment 5: OpenAI adapter

Approval dependency: finish reasons and provider-neutral streaming accepted. Separate implementation approval required; approval must also resolve the transport-test seam in section 21.

- Verify current official SDK/docs, pin the SDK, implement mapping, credentials, streaming, finish reasons, and errors.
- Add deterministic transport-level protocol tests through supported public seams.

Acceptance:

- Adapter passes the shared model-client contract suite.
- Every documented provider error category is tested.
- No secret/content sentinel reaches logs or exception messages.
- No live credential is needed for the default suite.

### Increment 6: Microsoft.Extensions.AI adapter

Approval dependency: stable provider-neutral contracts accepted. It is independently deferrable and requires separate implementation approval.

- Pin the MEAI abstraction package and implement one-way `IChatClient` wrapping.

Acceptance:

- Adapter passes the shared model-client contract suite.
- Tool execution remains exclusively in AgentForge.
- Unknown content/status values fail explicitly.

### Increment 7: ergonomic facade and samples

Approval dependency: the low-level APIs it wraps are accepted. It is independently deferrable and requires separate implementation approval.

- Add immutable keyed definition registration, thin `IAgent`, and OpenAI sample. Do not add `IAgentFactory`.

Acceptance:

- Low-level and beginner examples perform equivalent bounded runs.
- Missing/duplicate definitions and unknown allowed tools fail deterministically.
- No implicit memory, tools, retries, or provider state is introduced.

### Increment 8: opt-in live verification

Approval dependency: the OpenAI adapter is accepted. Separate approval is required because these tests contact an external paid service.

- Add filtered OpenAI live tests and security documentation.

Acceptance:

- Default build/test remains offline.
- Live tests skip without explicit opt-in.
- Enabled live tests validate text, streaming, tools, cancellation, classification, and redaction.

## 18. Phase 2 acceptance criteria

Phase 2 is complete only when:

- Phase 1's full suite remains green.
- All new public contracts are immutable, XML documented, and provider-neutral.
- Non-streaming and streaming paths share one execution-policy implementation.
- One OpenAI adapter passes deterministic contract tests and opt-in live verification.
- Caller cancellation, consumer stream disposal, terminal completion, and partial failure have deterministic tests.
- Streaming capability detection and every finish reason have explicit behavior.
- Typed tool schemas and validation are deterministic and invalid batches cause no tool side effects.
- Explicit registration and per-agent authorization remain required for typed and untyped tools.
- The MEAI adapter is optional and Core has no MEAI dependency.
- Low-level and beginner-friendly samples compile and demonstrate hard limits and cancellation.
- Default logs and exception messages pass sentinel-based redaction tests.
- `dotnet build AgentForge.slnx --configuration Release` succeeds with zero warnings/errors.
- `dotnet test AgentForge.slnx --configuration Release` passes without network or credentials.
- Live tests require explicit opt-in and do not expose credentials/content.
- Documentation distinguishes verified implementation from planned/unimplemented behavior.

## 19. Scope additions relative to the original roadmap

The original Phase 2 roadmap in `docs/architecture.md` explicitly named a real provider, streaming, capability negotiation, Microsoft.Extensions.AI interoperability, usage accounting, secrets/configuration, provider contract tests, and opt-in integration tests.

This proposal adds three requested areas beyond that original Phase 2 statement:

1. **Typed tool registration, schema generation, and atomic argument validation.** This adds `AgentForge.Tools` and one optional validation capability in Abstractions/Core.
2. **Beginner-friendly registered agents and `IAgent`.** This adds a thin keyed-DI Hosting facade over existing definitions/runtime without a framework-level factory.
3. **Explicit execution-wide finish-reason and adapter-error taxonomies.** Capability negotiation implied part of this, but the concrete public enums/exceptions are a larger compatibility surface than the original roadmap stated.

These additions are coherent with Phase 2 but should be approved independently. If schedule pressure requires a smaller phase, implement increments 1–3 and 5 first; typed tools, MEAI interoperability, and the facade can each ship as separately approved follow-on increments without weakening the core streaming/provider design.

## 20. Architecture approval decisions

The following may be approved or amended as architectural direction. Their approval does not authorize code changes:

1. Use the official OpenAI .NET SDK and Responses API for the first native provider.
2. Keep one configured model client per application composition in Phase 2; defer multi-provider routing.
3. Add separate optional streaming interfaces and use exceptions—not failure events—for terminal failure.
4. Expose text deltas publicly but reconstruct tool calls privately until the provider terminal response.
5. Add mandatory provider-neutral finish reasons to `ModelResponse`.
6. Add `AgentForge.Tools` with a bounded generated-schema subset and optional prevalidation capability.
7. Add only the `IChatClient -> AgentForge` MEAI adapter direction.
8. Add a thin keyed `IAgent` Hosting API without a framework-level factory, memory, retries, or implicit tools.
9. Add the provider-neutral error taxonomy and sanitized provider request-ID metadata.
10. Add opt-in live OpenAI tests gated by environment variables and test filters.
11. Keep the runtime event surface to text delta and authoritative completion; defer start/tool lifecycle events, sequence fields, and execution factories.
12. Prefer transport-level OpenAI adapter tests through supported public seams and prohibit tests coupled to SDK internals.

After architecture approval, the next request must explicitly authorize one named increment from section 17. Until then, this document is design guidance only and no Phase 2 capability should be represented as implemented.

## 21. Unresolved design decisions

These decisions remain deliberately open and must be resolved at the named increment's approval checkpoint:

1. **OpenAI transport seam (Increment 5).** Confirm whether the pinned official SDK supports injection of a deterministic `HttpMessageHandler` or equivalent public transport. If not, approve either a narrow internal provider transport seam or a BCL HTTP protocol layer. Do not use unstable SDK internals as the fallback.
2. **Exact Responses terminal mapping (Increment 5).** Pin the API/SDK version and enumerate successful, incomplete, failed, and cancelled terminal events. The adapter must require an explicit documented terminal state; Core must never infer success from EOF.
3. **`ModelResponse` migration shape (Increment 1).** Decide whether the raw constructor remains public alongside factories or is narrowed to reduce invalid combinations, subject to source-compatibility review.
4. **Streaming type placement (Increment 3).** Validate whether the two provider events and two runtime events belong in existing files/namespaces or a streaming namespace. Do not add more event variants during implementation without amendment.
5. **Typed-tool converter policy (Increment 4).** Decide whether Phase 2 supports only built-in converters or a narrowly defined custom schema-provider hook. Default recommendation: reject custom converters whose accepted JSON shape cannot be proven from the frozen metadata.
6. **Typed validation implementation (Increment 4).** Verify the .NET 10 schema-export and `JsonTypeInfo` APIs and determine how the bounded validator enforces identical required/nullability/unmapped-member semantics without adding a general schema dependency.
7. **MEAI streaming/tool ownership detection (Increment 6).** Verify the pinned MEAI surface can distinguish raw chat streaming from middleware that invokes tools. If ownership cannot be established reliably, omit streaming or defer this adapter rather than weaken AgentForge policy.
8. **Keyed-agent registration validation timing (Increment 7).** Choose build-time options validation or first-resolution validation based on what Microsoft DI supports without constructing application services prematurely. Failure must remain deterministic and before model invocation.
9. **Live invalid-credential test (Increment 8).** Decide whether its limited diagnostic value justifies an intentional external failed request. It remains separately opt-in even when other live tests are enabled.

None of these open decisions permits weakening explicit tool authorization, hard call limits, atomic batch validation, immutable ownership, cancellation checks, transcript/correlation rules, error redaction, or any other Phase 1 guarantee.
