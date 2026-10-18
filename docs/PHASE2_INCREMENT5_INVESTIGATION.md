# Phase 2 Increment 5: OpenAI .NET SDK Investigation

Status: Complete — architecture review required before implementation  
Date: 2026-10-18  
Verdict: **READY**

## 1. Executive decision

Increment 5 can proceed after explicit implementation approval using the official `OpenAI` NuGet package version **2.14.0** and its `OpenAI.Responses` API surface. The investigation verified the release tag `OpenAI_2.14.0` at commit `2e77b08828145f658ec04e49aec87abb1543c553`, rather than relying on the moving `main` branch.

The two Increment 5 checkpoints are resolved:

1. Use the SDK's supported public `ResponsesClientOptions.Transport` property with `HttpClientPipelineTransport` and a test-owned `HttpClient`/`HttpMessageHandler`. This permits deterministic JSON and SSE wire tests through the real SDK serializer and streaming parser.
2. Require an explicit terminal `ResponseStatus.Completed` or `StreamingResponseCompletedUpdate`. EOF is never success. Incomplete, failed, cancelled, error-event, nonterminal-status, duplicate-terminal, and post-terminal cases fail according to the mapping below.

No provider code, project, package reference, or live request was created during this investigation.

## 2. Verified version and dependencies

Recommended package:

```xml
<PackageReference Include="OpenAI" Version="2.14.0" />
```

Version 2.14.0 is the latest stable release published before this investigation date (2026-10-18). The Responses surface remains marked `[Experimental("OPENAI001")]`, so Increment 5 will require a **narrow project-level suppression of `OPENAI001` in the provider project only**, with a comment naming the pinned dependency and review trigger. This is not a global warning suppression. The official README explains that experimental SDK APIs require explicit acknowledgement, and the official changelog records the 2.14.0 release on 2026-09-15.

The 2.14.0 source centrally pins these runtime dependencies:

- `System.ClientModel` 1.15.0;
- `System.Diagnostics.DiagnosticSource` 10.0.11;
- `System.Net.ServerSentEvents` 10.0.11.

They should remain transitive dependencies of `OpenAI`; AgentForge should not reference them directly unless compilation of the provider's public construction or error/transport code requires it. `System.ClientModel` types must not escape the provider package.

Verified sources:

- [OpenAI .NET 2.14.0 changelog](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/CHANGELOG.md#2140-2026-09-15)
- [OpenAI package project and dependencies](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI/src/OpenAI.csproj)
- [Repository central dependency versions](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/Directory.Packages.props)
- [Official SDK experimental-API guidance](https://github.com/openai/openai-dotnet#experimental-apis)

## 3. Verified client construction and call APIs

### Construction

The stable-tag public constructors needed by the adapter are:

```csharp
new ResponsesClient(string apiKey)
new ResponsesClient(ApiKeyCredential credential)
new ResponsesClient(ApiKeyCredential credential, ResponsesClientOptions options)
new ResponsesClient(AuthenticationPolicy authenticationPolicy, ResponsesClientOptions options)
```

The recommended provider-owned construction path is:

```csharp
var sdkOptions = new ResponsesClientOptions
{
    Endpoint = validatedEndpoint,
    RetryPolicy = new ClientRetryPolicy(0)
};

var client = new ResponsesClient(
    new ApiKeyCredential(validatedApiKey),
    sdkOptions);
```

`ResponsesClientOptions` derives from `ClientPipelineOptions` and publicly exposes `Endpoint`; inherited pipeline properties include `Transport` and `RetryPolicy`. `ResponsesClient` is thread-safe according to the official ASP.NET Core example and should be held for the application/provider lifetime.

The configuration-binding constructor and official `AddResponsesClient` DI integration use experimental `ResponsesClientSettings` (`SCME0002`). AgentForge should not depend on that surface. Its own provider options should create the SDK client directly.

An advanced overload may accept a preconfigured `ResponsesClient`. When that overload is used, AgentForge cannot prove its endpoint or retry policy; documentation must state that the caller owns those settings. The normal AgentForge configuration path must enforce them.

### Non-streaming

The exact typed method is:

```csharp
Task<ClientResult<ResponseResult>> CreateResponseAsync(
    CreateResponseOptions options,
    CancellationToken cancellationToken = default);
```

Convenience overloads accept `(string model, IEnumerable<ResponseItem> inputItems, string? previousResponseId, CancellationToken)`, but the adapter needs `CreateResponseOptions` to set tools, storage, parallel-tool behavior, and streaming explicitly.

### Streaming

The exact typed method is:

```csharp
AsyncCollectionResult<StreamingResponseUpdate> CreateResponseStreamingAsync(
    CreateResponseOptions options,
    CancellationToken cancellationToken = default);
```

`CreateResponseOptions.StreamingEnabled` must be `true`; the SDK sends an unbuffered request and parses server-sent events. The adapter must `await foreach` the returned collection using the caller token and continue until natural termination even after receiving the terminal SDK update, allowing it to reject a contradictory later event.

Relevant sources:

- [Pinned `ResponsesClient` implementation](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Custom/ResponsesClient.cs)
- [Pinned `ResponsesClientOptions`](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Custom/ResponsesClientOptions.cs)
- [Official Responses streaming example](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/examples/Responses/Example02_SimpleResponseStreamingAsync.cs)
- [Official SDK client and transport documentation](https://github.com/openai/openai-dotnet)

## 4. Deterministic transport testing

### Feasibility decision

**Use the SDK transport seam; do not introduce an AgentForge protocol abstraction or direct BCL implementation.**

The SDK publicly supports:

```csharp
using var httpClient = new HttpClient(testHandler)
{
    Timeout = Timeout.InfiniteTimeSpan
};

var options = new ResponsesClientOptions
{
    Endpoint = new Uri("https://unit.test/v1"),
    RetryPolicy = new ClientRetryPolicy(0),
    Transport = new HttpClientPipelineTransport(httpClient)
};

var client = new ResponsesClient(
    new ApiKeyCredential("test-key-not-a-secret"),
    options);
```

`HttpClientPipelineTransport` is an SDK-documented transport choice. OpenAI's own mTLS example passes a custom `HttpClient` through it and instructs callers to keep that client alive for the SDK client's lifetime. Because the Responses stream uses ordinary HTTP SSE, the same transport handles streaming; no WebSocket or internal SDK hook is involved.

### Proposed test handler

A test-only sealed `HttpMessageHandler` should:

1. capture method, absolute URI, selected safe headers, and request bytes;
2. assert `POST /v1/responses` and parse the request JSON;
3. return a synthetic `HttpResponseMessage` with controlled status and headers;
4. return `application/json` for non-streaming cases;
5. return `text/event-stream` with a test-owned `Stream` for streaming cases;
6. split SSE bytes at varied boundaries, including inside UTF-8 sequences, JSON tokens, and event delimiters;
7. support signal-controlled reads for cancellation tests, without sleeps;
8. record request count so retry tests prove exactly one attempt.

Fixtures should contain only synthetic values. Tests must pass a fake credential, never inspect or snapshot the `Authorization` value, and never contact DNS/network. Wire fixtures should cover terminal events, EOF before terminal, malformed JSON, duplicate/conflicting fragments, repeated terminal events, post-terminal events, HTTP errors, and cancellation.

The production adapter should consume typed SDK updates. Tests validate both outbound JSON and typed mapping through the same production path; they must not instantiate undocumented SDK models, reflect over internals, subclass internal clients, or use the SDK repository's internal `MockPipelineTransport`.

Sources:

- [Official custom `HttpClient`/transport example](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/examples/MutualTls/README.md)
- [Official SDK transport construction](https://github.com/openai/openai-dotnet#using-mutual-tls)
- [Pinned SSE collection construction](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Custom/ResponsesClient.cs)

## 5. Request and transcript mapping

Every provider call must construct a complete stateless request:

```csharp
var options = new CreateResponseOptions
{
    Model = request.Model.Value,
    StoredOutputEnabled = false,
    ParallelToolCallsEnabled = false,
    StreamingEnabled = streaming
};
```

Do not set `PreviousResponseId`, `ConversationOptions`, background mode, or provider-hosted tools.

Map the complete AgentForge transcript in order:

| AgentForge value | OpenAI 2.14.0 value |
|---|---|
| `SystemMessage.Text` | `ResponseItem.CreateSystemMessageItem(text)` |
| `UserMessage.Text` | `ResponseItem.CreateUserMessageItem(text)` |
| assistant text | `ResponseItem.CreateAssistantMessageItem(text)` |
| assistant `ToolCall` | `new FunctionCallResponseItem(call.Id, call.Name, BinaryData.FromString(arguments.GetRawText()))` |
| `ToolResultMessage` | `ResponseItem.CreateFunctionCallOutputItem(callId, result.GetRawText())` |

Tool calls from an assistant turn are appended in their AgentForge order. Each correlated result follows in execution order. AgentForge continues to enforce call-ID uniqueness across the execution and executes validated calls sequentially. `ParallelToolCallsEnabled = false` asks the provider for zero or one function call in a response; the adapter must nevertheless preserve and validate any returned order rather than assume the provider honored it.

The provider input supports assistant function-call items and function-call-output items directly, so no provider response ID or managed conversation state is needed.

Sources:

- [Pinned `ResponseItem` factories](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Custom/Items/ResponseItem.cs)
- [Pinned function-call item](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Generated/Models/FunctionCallResponseItem.cs)
- [Pinned function-output item](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Generated/Models/FunctionCallOutputResponseItem.cs)
- [Official function-calling lifecycle](https://developers.openai.com/api/docs/guides/function-calling)

## 6. Tool-definition and output mapping

Map each already-filtered `ToolDescriptor` with:

```csharp
ResponseTool.CreateFunctionTool(
    descriptor.Name,
    BinaryData.FromString(descriptor.ParametersSchema.GetRawText()),
    strictModeEnabled: false,
    functionDescription: descriptor.Description)
```

`strictModeEnabled` must initially be `false`. AgentForge typed-tool schemas intentionally preserve serializer optional/default semantics, while OpenAI strict mode requires every property to appear in `required` and represents optional values through nullability. Enabling strict mode globally would reject or alter valid AgentForge descriptors. This does not weaken AgentForge validation: Core validates complete returned arguments before execution.

Only `FunctionTool` definitions are sent. File search, web search, code interpreter, computer use, MCP, image generation, custom tools, and tool search remain out of scope.

Returned `FunctionCallResponseItem` exposes:

- `CallId` — map to AgentForge `ToolCall.Id`;
- `FunctionName` — map to `ToolCall.Name`;
- `FunctionArguments` (`BinaryData`) — parse as exactly one JSON value and clone it;
- `Status` — require a complete item when populated.

Do not use the provider output-item `Id` for AgentForge correlation. The API's `call_id` is the identifier required by `function_call_output` and is therefore the correct AgentForge tool-call ID.

For streaming, `StreamingResponseFunctionCallArgumentsDeltaUpdate` carries `ItemId`, `OutputIndex`, and a `BinaryData Delta`; `StreamingResponseFunctionCallArgumentsDoneUpdate` carries the completed function name and arguments. The adapter may accumulate deltas privately for protocol checks, keyed by output index/item ID, but must create AgentForge tool calls only from the authoritative terminal `ResponseResult.OutputItems`. It must compare any accumulated/done data with that terminal result and reject missing, duplicate, conflicting, malformed, or reordered identities as `Protocol`.

The official API documents that a response may contain multiple function calls and that each function result is returned using the same `call_id`. AgentForge's existing batch validation and sequential execution remain authoritative.

Sources:

- [Pinned function-tool factory](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Custom/Tools/ResponseTool.cs)
- [Official function-call fields and result correlation](https://developers.openai.com/api/docs/guides/function-calling)
- [Pinned streaming argument-delta type](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Generated/Models/StreamingResponseFunctionCallArgumentsDeltaUpdate.cs)
- [Pinned streaming argument-done type](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Generated/Models/StreamingResponseFunctionCallArgumentsDoneUpdate.cs)

## 7. Exact terminal and finish-reason mapping

### Non-streaming `ResponseResult`

| SDK state | AgentForge mapping |
|---|---|
| `Status == Completed`, exactly one or more complete `FunctionCallResponseItem`, no assistant text/refusal | `ModelFinishReason.ToolCalls` |
| `Status == Completed`, non-empty assistant output text, no function calls/refusal | `ModelFinishReason.Completed` |
| `Status == Completed`, refusal content, no text/function calls | `ModelFinishReason.Refused` |
| `Status == Incomplete`, reason `MaxOutputTokens` | `ModelFinishReason.LengthLimit` |
| `Status == Incomplete`, reason `ContentFilter` | `ModelFinishReason.ContentFiltered` |
| `Status == Incomplete`, missing or unknown reason | `ModelFinishReason.Unknown` |
| `Status == Cancelled` | `ModelFinishReason.Cancelled` |
| `Status == Failed` | throw sanitized `ModelProviderException`; classify from safe status/error metadata |
| `Status == InProgress` or `Queued` | throw `ModelProviderException(Protocol)` |
| null/unknown status, incompatible mixed output, unknown output item/content type, malformed function arguments | throw `ModelProviderException(Protocol)` |

Refusal is represented as a refusal content part/event rather than a distinct response status. Partial refusal text may be accumulated for shape validation but must not enter exception messages/logs.

A completed response containing only reasoning or otherwise no usable text/function call is a protocol error. A completed response mixing text, refusal, and/or function calls cannot satisfy AgentForge's message contract and is a protocol error; the adapter must not guess.

### Streaming events

| SDK event | Adapter behavior |
|---|---|
| `StreamingResponseOutputTextDeltaUpdate` | emit provider-neutral `ModelTextDelta` after validating non-empty delta/order |
| function-argument delta/done and output-item events | reconstruct/validate privately; emit no AgentForge event |
| refusal delta/done | reconstruct privately; emit no text delta |
| `StreamingResponseCompletedUpdate` | validate embedded `ResponseResult` is `Completed`, reconstruct authoritative `ModelResponse`, remember terminal, continue enumeration |
| `StreamingResponseIncompleteUpdate` | reconstruct an incomplete terminal `ModelResponse` using its embedded response/reason, remember terminal, continue enumeration |
| `StreamingResponseFailedUpdate` | throw sanitized classified provider failure; never emit completion |
| `StreamingResponseErrorUpdate` | throw sanitized provider/protocol failure; never expose its provider message |
| normal EOF before one terminal update | `ModelProviderException(Protocol)` |
| second terminal update or any update after terminal | `ModelProviderException(Protocol)` |

Only `response.completed` establishes successful provider completion. A function-call arguments-done event, output-item-done event, or transport EOF is not terminal success. The adapter yields exactly one `ModelResponseCompleted` only after the SDK enumeration ends cleanly. This satisfies Core's requirement that an application-visible completion cannot precede a later transport/protocol error.

The SDK has no separate streaming cancelled update in 2.14.0; a terminal embedded response may have `Status == Cancelled`, and caller cancellation may instead terminate enumeration with `OperationCanceledException`. Both paths are handled explicitly.

Sources:

- [Pinned response statuses](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Custom/ResponseStatus.cs)
- [Pinned incomplete reasons](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Generated/Models/ResponseIncompleteStatusReason.cs)
- [Pinned terminal streaming update types](https://github.com/openai/openai-dotnet/tree/OpenAI_2.14.0/OpenAI.Responses/src/Custom/Streaming)
- [Official semantic streaming lifecycle](https://developers.openai.com/api/docs/guides/streaming-responses)

## 8. Usage mapping

`ResponseTokenUsage` exposes `int InputTokenCount`, `int OutputTokenCount`, and `int TotalTokenCount`. Map input and output counts to AgentForge `long` values only after checking they are nonnegative. Compute/check the total with `checked` arithmetic and reject a provider total that is negative or inconsistent with the checked input-plus-output sum.

Do not aggregate inside the adapter across model turns; each `ModelResponse` reports one provider turn and the shared Core engine performs execution-wide checked aggregation.

Usage may be absent on nonterminal/failure states. A missing usage object maps to `null`, not zero.

Source: [Pinned `ResponseTokenUsage`](https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI.Responses/src/Custom/ResponseTokenUsage.cs)

## 9. Errors, cancellation, retries, and request IDs

### Retry decision

The SDK retries HTTP 408, 429, 500, 502, 503, and 504 up to three additional times by default. AgentForge must disable this in its normal construction path with:

```csharp
RetryPolicy = new ClientRetryPolicy(0)
```

This is necessary because Core owns invocation counts and Phase 2 introduces no hidden retry policy. Tests must assert one handler invocation for retryable status codes. A caller-supplied advanced `ResponsesClient` is accepted as preconfigured and cannot be audited; its documentation must explicitly require retries to be disabled.

Source: [Official SDK retry behavior](https://github.com/openai/openai-dotnet#automatically-retrying-errors)

### Cancellation

Pass the caller token to `CreateResponseAsync` or `CreateResponseStreamingAsync` and to async enumeration. Check it before the call, after each awaited SDK/stream operation, and before returning/yielding terminal completion. If that token is requested, propagate `OperationCanceledException` unchanged. A timeout/cancellation exception without caller cancellation maps to `ModelProviderErrorKind.Timeout` when it is demonstrably a timeout; otherwise `Transport`.

### HTTP and provider error classification

The adapter should classify safe public metadata, in this order:

| Evidence | `ModelProviderErrorKind` | Transient |
|---|---|---|
| caller token requested | propagate cancellation | n/a |
| HTTP 401 | `Authentication` | no |
| HTTP 403 | `Authorization` | no |
| HTTP 408 or non-caller timeout | `Timeout` | yes |
| HTTP 429 or response error `rate_limit_exceeded` | `RateLimited` | yes |
| HTTP 500/502/503/504 or response error `server_error` | `ServiceUnavailable` | yes |
| HTTP 404 with safely parsed model-not-found code/parameter | `ModelNotFound` | no |
| HTTP 400/404/409/422 otherwise, or `invalid_prompt` | `InvalidRequest` | no |
| `HttpRequestException`/I/O failure | `Transport` | only when qualifying and not caused by invalid local configuration |
| malformed/missing/contradictory SDK protocol data | `Protocol` | no |
| everything else | `Unknown` | no by default |

HTTP status alone cannot distinguish model-not-found from other 404 resources, so `ModelNotFound` requires safe structured evidence; otherwise use `InvalidRequest`.

The SDK throws `ClientResultException` for non-success HTTP responses. Its message can contain provider content. AgentForge must create a fixed-message `ModelProviderException`, never copy the SDK message/body, and never log the SDK exception. Per the already-approved architecture, the SDK exception remains the inner exception for root-cause inspection; callers must treat inner exceptions as sensitive and must not log them by default. Sentinel tests must cover AgentForge-owned messages and all default logs.

### Request IDs

For successful buffered responses, inspect `ClientResult<ResponseResult>.GetRawResponse().Headers` for `x-request-id`. For HTTP failures, use a request ID only when the public exception/response surface exposes headers without parsing/logging the body. For typed streaming, request headers are not conveniently exposed while also consuming typed updates; request ID may therefore be `null` unless a provider-owned transport observer captures it safely. Request-ID capture is optional metadata and must not force a protocol-method implementation.

Accept only a conservative ASCII identifier (recommended: `[A-Za-z0-9._:-]`, 1–128 characters); otherwise store `null`. Never accept CR/LF or arbitrary header text.

## 10. Minimal configuration and security design

`OpenAIProviderOptions` should contain only:

```csharp
public sealed class OpenAIProviderOptions
{
    public string? ApiKey { get; set; }
    public Uri? Endpoint { get; set; }
    public bool AllowInsecureLoopbackEndpoint { get; set; }
}
```

Rules:

- `ApiKey` is required and non-whitespace for provider-owned construction.
- Do not override `ToString`; annotate the type/property with debugger-display/browsing protection where practical, and never include the key in validation messages.
- Default endpoint: let the SDK use its official HTTPS endpoint when `Endpoint` is null.
- A custom endpoint must be absolute HTTPS.
- HTTP is accepted only when the explicit flag is true **and** the host is loopback (`localhost`, `127.0.0.0/8`, or `::1`). Reject user-info, fragments, and non-loopback HTTP.
- Convert options immediately into a private immutable validated snapshot; retain no `IConfiguration` or service provider.
- Environment variables, .NET user secrets, or deployment secret stores supply credentials. No committed default key or model exists.
- Provider-owned SDK construction disables retries and uses no provider-managed state.
- Advanced preconfigured-client registration takes a `ResponsesClient` factory/instance and does not combine it with API-key/endpoint options.
- Default logging records only stable AgentForge identifiers, durations, counts, safe classification, and sanitized request IDs—never prompts, bodies, arguments, results, credentials, provider messages, or headers.

The official SDK recommends environment/configuration storage rather than source control and documents custom endpoints through client options.

Sources:

- [Official SDK credential and endpoint guidance](https://github.com/openai/openai-dotnet#getting-started)
- [OpenAI production guidance](https://developers.openai.com/api/docs/guides/production-best-practices)

## 11. Proposed package structure

Implementation approval should add only:

```text
src/AgentForge.Providers.OpenAI/
  AgentForge.Providers.OpenAI.csproj
  OpenAIProviderOptions.cs
  OpenAIAgentModelClient.cs
  OpenAIServiceCollectionExtensions.cs
  internal/
    OpenAIRequestMapper.cs
    OpenAIResponseMapper.cs
    OpenAIStreamAccumulator.cs
    OpenAIExceptionMapper.cs
    OpenAIProviderOptionsSnapshot.cs

tests/AgentForge.Providers.OpenAI.Tests/
  deterministic handler and JSON/SSE fixtures
  request/response/stream/error/redaction contract tests
```

The provider project references `AgentForge.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions` only if needed for its registration extension, and `OpenAI` 2.14.0. It must not reference Core, Hosting, Tools, or Microsoft.Extensions.AI. Implementation-specific types remain internal. If DI extensions would require a new package not otherwise needed, put registration in the provider package using the existing DI dependency pattern; do not make Hosting reference the provider.

## 12. Deterministic acceptance strategy

The Increment 5 suite should verify, offline:

1. exact ordered mapping of system/user/assistant/tool-call/tool-result transcript items;
2. only `ModelRequest.AvailableTools` are sent;
3. `store=false`, `parallel_tool_calls=false`, no previous response/conversation/background/provider tools;
4. function schema/name/description mapping and JSON ownership;
5. completed text and completed function-call mapping;
6. refusal, max-token incomplete, content-filter incomplete, cancelled, failed, unknown/nonterminal status mapping;
7. streamed text ordering and authoritative completed response;
8. private function-argument fragment reconstruction with varied byte boundaries;
9. malformed JSON, missing/conflicting identity/name, duplicate terminal, event after terminal, and EOF before terminal;
10. usage nonnegativity, consistency, and Core-compatible checked values;
11. caller cancellation before request, during handler wait, during SSE read, after SDK return, and after terminal event before clean EOF;
12. every HTTP/provider error category, `Retry-After`, transient metadata, and zero automatic retries;
13. request-ID sanitization;
14. absence of sentinel keys, headers, prompts, arguments, results, provider bodies, and provider messages from AgentForge exception messages and default logs;
15. immutable mapped collections/JSON and concurrent-client isolation.

No default test should use a network socket, real key, real model, wall-clock delay, SDK internal mock, or undocumented reflection.

## 13. Remaining limitations and review points

No critical SDK capability remains unresolved. The following are implementation review points, not blockers:

1. Responses APIs in 2.14.0 are still marked `OPENAI001`; pinning and narrow suppression are required, and upgrades require contract-test review.
2. Request-ID extraction from typed streaming is optional and may remain unavailable without a safe public raw-response path; `ProviderRequestId` already permits null.
3. A caller-supplied `ResponsesClient` may have retries or an insecure/custom endpoint configured. The advanced overload must document that ownership boundary; the default provider path remains strict.
4. OpenAI strict function schemas are not compatible with every approved AgentForge typed-tool optional/default contract. Increment 5 should send `strict: false`; changing that requires a later schema-compatibility design.
5. The approved requirement to preserve the SDK exception as `InnerException` means inner exceptions are potentially content-bearing. AgentForge-owned messages/logs remain sanitized, and documentation must tell consumers not to log inner exceptions by default.
6. Unknown future extensible response error/incomplete values must map conservatively to `Unknown` or `Protocol`, never silently to success.

## 14. Readiness verdict

**READY.**

The official SDK version, construction APIs, request/response models, streaming events, tool correlation, terminal statuses, cancellation path, retry control, and deterministic transport seam are sufficiently verified to implement Increment 5 without an extra provider transport abstraction or direct HTTP protocol layer.

Implementation remains unauthorized until the maintainer explicitly approves Increment 5. Increment 6 remains out of scope.
