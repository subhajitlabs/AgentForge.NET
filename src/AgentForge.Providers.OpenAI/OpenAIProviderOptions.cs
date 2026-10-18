using System.Diagnostics;

namespace AgentForge.Providers.OpenAI;

/// <summary>Configures an AgentForge-owned OpenAI Responses client.</summary>
[DebuggerDisplay("OpenAI provider options (credentials redacted)")]
public sealed class OpenAIProviderOptions
{
    /// <summary>Gets or sets the OpenAI API key.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets an optional Responses API endpoint.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Gets or sets whether explicit HTTP loopback endpoints are permitted for development.</summary>
    public bool AllowInsecureLoopbackEndpoint { get; set; }
}
