using System.Net;
using AgentForge.Abstractions;

namespace AgentForge.Adapters.MicrosoftExtensionsAI.Internal;

internal static class MicrosoftExtensionsAIExceptionMapper
{
    public static ModelProviderException Map(Exception exception)
    {
        if (exception is ModelProviderException provider) return provider;
        if (exception is HttpRequestException http)
        {
            var (kind, transient) = http.StatusCode switch
            {
                HttpStatusCode.Unauthorized => (ModelProviderErrorKind.Authentication, false),
                HttpStatusCode.Forbidden => (ModelProviderErrorKind.Authorization, false),
                HttpStatusCode.RequestTimeout => (ModelProviderErrorKind.Timeout, true),
                HttpStatusCode.TooManyRequests => (ModelProviderErrorKind.RateLimited, true),
                HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
                    HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                    (ModelProviderErrorKind.ServiceUnavailable, true),
                HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or
                    HttpStatusCode.UnprocessableEntity => (ModelProviderErrorKind.InvalidRequest, false),
                null => (ModelProviderErrorKind.Transport, true),
                _ => (ModelProviderErrorKind.Unknown, false)
            };
            return Create(kind, transient, exception);
        }
        if (exception is TimeoutException)
            return Create(ModelProviderErrorKind.Timeout, true, exception);
        if (exception is IOException)
            return Create(ModelProviderErrorKind.Transport, true, exception);
        return Create(ModelProviderErrorKind.Unknown, false, exception);
    }

    public static ModelProviderException Protocol(string message, Exception? inner = null) =>
        new(ModelProviderErrorKind.Protocol, false, message, inner);

    private static ModelProviderException Create(
        ModelProviderErrorKind kind, bool transient, Exception? inner = null) =>
        new(kind, transient, "The Microsoft.Extensions.AI model client failed.", inner);
}
