#pragma warning disable OPENAI001 // Tests intentionally exercise the pinned experimental Responses SDK surface.
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Net.Http.Headers;
using OpenAI.Responses;

namespace AgentForge.Providers.OpenAI.Tests;

internal sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    public int Calls { get; private set; }
    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await respond(request, cancellationToken);
    }
}

internal static class TestTransport
{
    public static OpenAIAgentModelClient Create(TestHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
        var options = new ResponsesClientOptions
        {
            Endpoint = httpClient.BaseAddress,
            Transport = new HttpClientPipelineTransport(httpClient),
            RetryPolicy = new ClientRetryPolicy(0)
        };
        return new OpenAIAgentModelClient(new ResponsesClient(new ApiKeyCredential("synthetic-key"), options));
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json, string? requestId = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (requestId is not null) response.Headers.TryAddWithoutValidation("x-request-id", requestId);
        return response;
    }

    public static HttpResponseMessage Sse(params string[] events)
    {
        var payload = string.Concat(events.Select(value => $"data: {value}\n\n")) + "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    public static HttpResponseMessage FragmentedSse(params string[] events)
    {
        var payload = string.Concat(events.Select(value => $"data: {value}\n\n")) + "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new FragmentedContent(payload) };
    }

    private sealed class FragmentedContent : HttpContent
    {
        private readonly byte[] _bytes;

        public FragmentedContent(string value)
        {
            _bytes = Encoding.UTF8.GetBytes(value);
            Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            foreach (var value in _bytes)
                await stream.WriteAsync(new[] { value });
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }

}
