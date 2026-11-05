using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Settings;

namespace EasyRabbitFlow.Tests;

public class ConsumerFailureReasonsTests
{
    [Fact]
    public void AbandonedHandler_IsAbandoned()
    {
        Assert.Equal("abandoned", ConsumerFailureReasons.Of(new RabbitFlowHandlerAbandonedException("C", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30))));
    }

    [Fact]
    public void PoisonMessage_IsPoison()
    {
        Assert.Equal("poison", ConsumerFailureReasons.Of(new RabbitFlowPoisonMessageException(5, 5)));
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(TaskCanceledException))]
    public void CooperativeCancellation_IsTimeout(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal("timeout", ConsumerFailureReasons.Of(exception));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(RabbitFlowTransientException))]
    [InlineData(typeof(RabbitFlowException))]
    public void AnythingElse_IsHandlerError(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "x")!;

        Assert.Equal("handler_error", ConsumerFailureReasons.Of(exception));
    }
}
