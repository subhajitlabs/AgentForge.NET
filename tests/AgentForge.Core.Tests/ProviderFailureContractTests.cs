using AgentForge.Abstractions;

namespace AgentForge.Core.Tests;

public sealed class ProviderFailureContractTests
{
    [Theory]
    [InlineData(ModelProviderErrorKind.Authentication, false)]
    [InlineData(ModelProviderErrorKind.Authorization, false)]
    [InlineData(ModelProviderErrorKind.InvalidRequest, false)]
    [InlineData(ModelProviderErrorKind.ModelNotFound, false)]
    [InlineData(ModelProviderErrorKind.RateLimited, true)]
    [InlineData(ModelProviderErrorKind.Timeout, true)]
    [InlineData(ModelProviderErrorKind.ServiceUnavailable, true)]
    [InlineData(ModelProviderErrorKind.Transport, true)]
    [InlineData(ModelProviderErrorKind.Protocol, false)]
    [InlineData(ModelProviderErrorKind.Unknown, false)]
    public void Constructor_StableClassification_PreservesEvidenceBasedTransience(
        ModelProviderErrorKind kind,
        bool isTransient)
    {
        var exception = new ModelProviderException(kind, isTransient, "Safe provider failure.");

        Assert.Equal(kind, exception.Kind);
        Assert.Equal(isTransient, exception.IsTransient);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86401)]
    public void Constructor_OutOfBoundsRetryAfter_DiscardsMetadata(int seconds)
    {
        var exception = new ModelProviderException(
            ModelProviderErrorKind.RateLimited,
            true,
            "Safe provider failure.",
            retryAfter: TimeSpan.FromSeconds(seconds));

        Assert.Null(exception.RetryAfter);
    }

    [Fact]
    public void Constructor_BoundedRetryAfterAndSafeRequestId_PreservesMetadata()
    {
        var exception = new ModelProviderException(
            ModelProviderErrorKind.RateLimited,
            true,
            "Safe provider failure.",
            retryAfter: TimeSpan.FromHours(24),
            providerRequestId: "req.Safe_123:part");

        Assert.Equal(TimeSpan.FromHours(24), exception.RetryAfter);
        Assert.Equal("req.Safe_123:part", exception.ProviderRequestId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unsafe request id")]
    [InlineData("unsafe\r\nheader")]
    public void Constructor_UnsafeRequestId_DiscardsMetadata(string requestId)
    {
        var exception = new ModelProviderException(
            ModelProviderErrorKind.Unknown,
            false,
            "Safe provider failure.",
            providerRequestId: requestId);

        Assert.Null(exception.ProviderRequestId);
    }

    [Fact]
    public void Constructor_SensitiveInnerMessage_IsNotCopiedToDiagnosticMessage()
    {
        const string secret = "credential-and-response-body-secret";
        var exception = new ModelProviderException(
            ModelProviderErrorKind.Unknown,
            false,
            "Safe provider failure.",
            new InvalidOperationException(secret));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.Contains(secret, exception.InnerException!.Message, StringComparison.Ordinal);
    }
}
