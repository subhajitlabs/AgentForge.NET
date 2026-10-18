using AgentForge.Abstractions;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;

namespace AgentForge.Providers.OpenAI.Internal;

internal static class OpenAIExceptionMapper
{
    public static ModelProviderException FromException(Exception exception)
    {
        if (exception is ModelProviderException provider) return provider;
        if (exception is ClientResultException client)
        {
            var status = client.Status;
            var response = client.GetRawResponse();
            var modelNotFound = status == 404 && IsModelNotFound(response);
            var (kind, transient) = status switch
            {
                401 => (ModelProviderErrorKind.Authentication, false),
                403 => (ModelProviderErrorKind.Authorization, false),
                408 => (ModelProviderErrorKind.Timeout, true),
                429 => (ModelProviderErrorKind.RateLimited, true),
                500 or 502 or 503 or 504 => (ModelProviderErrorKind.ServiceUnavailable, true),
                404 when modelNotFound => (ModelProviderErrorKind.ModelNotFound, false),
                400 or 404 or 409 or 422 => (ModelProviderErrorKind.InvalidRequest, false),
                _ => (ModelProviderErrorKind.Unknown, false)
            };
            return new ModelProviderException(
                kind, transient, "The OpenAI provider request failed.", exception,
                GetRetryAfter(response), GetRequestId(response));
        }
        if (exception is TimeoutException)
            return Create(ModelProviderErrorKind.Timeout, true, exception);
        if (exception is HttpRequestException or IOException)
            return Create(ModelProviderErrorKind.Transport, true, exception);
        if (exception is System.Text.Json.JsonException or FormatException)
            return Protocol("The provider returned malformed protocol data.", exception);
        return Create(ModelProviderErrorKind.Unknown, false, exception);
    }

    public static ModelProviderException FromResponseError(ResponseError? error)
    {
        var code = error?.Code;
        if (code == ResponseErrorCode.RateLimitExceeded)
            return Create(ModelProviderErrorKind.RateLimited, true);
        if (code == ResponseErrorCode.ServerError)
            return Create(ModelProviderErrorKind.ServiceUnavailable, true);
        if (code == ResponseErrorCode.InvalidPrompt)
            return Create(ModelProviderErrorKind.InvalidRequest, false);
        return Create(ModelProviderErrorKind.Unknown, false);
    }

    public static ModelProviderException Protocol(string message, Exception? inner = null) =>
        new(ModelProviderErrorKind.Protocol, false, message, inner);

    private static ModelProviderException Create(ModelProviderErrorKind kind, bool transient, Exception? inner = null) =>
        new(kind, transient, "The OpenAI provider request failed.", inner);

    private static string? GetRequestId(PipelineResponse? response)
    {
        if (response is null || !response.Headers.TryGetValue("x-request-id", out var value) || value is null
            || value.Length is < 1 or > 128)
            return null;
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-')
            ? value : null;
    }

    private static TimeSpan? GetRetryAfter(PipelineResponse? response)
    {
        if (response is null || !response.Headers.TryGetValue("Retry-After", out var value)
            || !double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            return null;
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool IsModelNotFound(PipelineResponse? response)
    {
        if (response is null) return false;
        try
        {
            using var document = JsonDocument.Parse(response.Content.ToMemory());
            if (!document.RootElement.TryGetProperty("error", out var error)) return false;
            var code = error.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
            var parameter = error.TryGetProperty("param", out var parameterValue) ? parameterValue.GetString() : null;
            return StringComparer.Ordinal.Equals(code, "model_not_found")
                || StringComparer.Ordinal.Equals(parameter, "model");
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
