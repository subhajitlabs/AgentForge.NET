namespace AgentForge.Providers.OpenAI.Internal;

internal sealed record OpenAIProviderOptionsSnapshot(string ApiKey, Uri? Endpoint)
{
    public static OpenAIProviderOptionsSnapshot Create(OpenAIProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("An OpenAI API key is required.", nameof(options));

        var endpoint = options.Endpoint;
        if (endpoint is not null)
        {
            if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
                throw new ArgumentException("The OpenAI endpoint must be an absolute URI without user information or a fragment.", nameof(options));
            var secure = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            var allowedLoopback = options.AllowInsecureLoopbackEndpoint
                && endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && endpoint.IsLoopback;
            if (!secure && !allowedLoopback)
                throw new ArgumentException("The OpenAI endpoint must use HTTPS unless HTTP loopback is explicitly enabled.", nameof(options));
        }

        return new(options.ApiKey, endpoint);
    }
}
