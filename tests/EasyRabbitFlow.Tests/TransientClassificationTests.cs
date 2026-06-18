using System.Net;
using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Tests.Helpers;

namespace EasyRabbitFlow.Tests;

public class TransientClassificationTests
{
    [Fact]
    public void RabbitFlowTransientException_IsTransient()
    {
        Assert.True(TransientExceptionClassifier.IsTransient(new RabbitFlowTransientException("boom")));
    }

    [Fact]
    public void DerivedTransientException_IsTransient_ByInheritance()
    {
        Assert.True(TransientExceptionClassifier.IsTransient(new DerivedTransientException("boom")));
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(TimeoutException))]
    public void CancellationAndTimeout_AreTransient(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.True(TransientExceptionClassifier.IsTransient(exception));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]        // 408
    [InlineData(HttpStatusCode.TooManyRequests)]       // 429
    [InlineData(HttpStatusCode.BadGateway)]            // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]    // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]        // 504
    public void HttpRequestException_TransientStatusCodes(HttpStatusCode statusCode)
    {
        var exception = new HttpRequestException("http failure", inner: null, statusCode);

        Assert.True(TransientExceptionClassifier.IsTransient(exception));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]            // 400
    [InlineData(HttpStatusCode.Unauthorized)]          // 401
    [InlineData(HttpStatusCode.NotFound)]              // 404
    [InlineData(HttpStatusCode.InternalServerError)]   // 500
    public void HttpRequestException_NonTransientStatusCodes(HttpStatusCode statusCode)
    {
        var exception = new HttpRequestException("http failure", inner: null, statusCode);

        Assert.False(TransientExceptionClassifier.IsTransient(exception));
    }

    [Fact]
    public void HttpRequestException_WithoutResponse_IsTransient()
    {
        // No status code: the request never produced a response (DNS, connection refused/reset)
        Assert.True(TransientExceptionClassifier.IsTransient(new HttpRequestException("connection refused")));
    }

    [Fact]
    public void WrappedTransient_IsDetected_ThroughInnerChain()
    {
        var wrapped = new InvalidOperationException(
            "outer",
            new HttpRequestException("rate limited", inner: null, HttpStatusCode.TooManyRequests));

        Assert.True(TransientExceptionClassifier.IsTransient(wrapped));
    }

    [Fact]
    public void PlainExceptions_AreNotTransient()
    {
        Assert.False(TransientExceptionClassifier.IsTransient(new InvalidOperationException("boom")));
        Assert.False(TransientExceptionClassifier.IsTransient(new ArgumentNullException("param")));
        Assert.False(TransientExceptionClassifier.IsTransient(null));
    }

    [Fact]
    public void LegacyTypeNameFallback_MatchesExactNamesOnly()
    {
        Assert.True(TransientExceptionClassifier.IsTransientTypeName(nameof(RabbitFlowTransientException)));
        Assert.True(TransientExceptionClassifier.IsTransientTypeName(nameof(OperationCanceledException)));
        Assert.True(TransientExceptionClassifier.IsTransientTypeName(nameof(TaskCanceledException)));

        Assert.False(TransientExceptionClassifier.IsTransientTypeName(nameof(DerivedTransientException)));
        Assert.False(TransientExceptionClassifier.IsTransientTypeName(nameof(InvalidOperationException)));
        Assert.False(TransientExceptionClassifier.IsTransientTypeName(null));
    }
}
