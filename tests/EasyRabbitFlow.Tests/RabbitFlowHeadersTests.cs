using System.Text;
using EasyRabbitFlow.Settings;

namespace EasyRabbitFlow.Tests;

public class RabbitFlowHeadersTests
{
    [Fact]
    public void ReadDeliveryCount_MissingHeaders_IsNull()
    {
        Assert.Null(RabbitFlowHeaders.ReadDeliveryCount(null));
        Assert.Null(RabbitFlowHeaders.ReadDeliveryCount(new Dictionary<string, object?>()));
        Assert.Null(RabbitFlowHeaders.ReadDeliveryCount(new Dictionary<string, object?> { ["x-delivery-count"] = null }));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(3L)]
    [InlineData((short)3)]
    [InlineData((byte)3)]
    [InlineData("3")]
    public void ReadDeliveryCount_ReadsEveryNumericShape(object raw)
    {
        // RabbitMQ.Client surfaces header values as long (what the broker sends for x-delivery-count), but be as
        // defensive as ReadReprocessAttempts.
        var headers = new Dictionary<string, object?> { ["x-delivery-count"] = raw };

        Assert.Equal(3, RabbitFlowHeaders.ReadDeliveryCount(headers));
    }

    [Theory]
    [InlineData("x-delivery-count", true)]
    [InlineData("x-death", true)]
    [InlineData("x-first-death-reason", true)]
    [InlineData("x-last-death-queue", true)]
    [InlineData("x-reprocess-attempts", false)]
    [InlineData("tenant", false)]
    [InlineData("traceparent", false)]
    public void IsBrokerOwned_RecognisesTheBrokersDeliveryHistoryHeaders(string header, bool expected)
    {
        Assert.Equal(expected, RabbitFlowHeaders.IsBrokerOwned(header));
    }

    [Fact]
    public void ReadDeliveryCount_Bytes_AndGarbage()
    {
        Assert.Equal(7, RabbitFlowHeaders.ReadDeliveryCount(new Dictionary<string, object?> { ["x-delivery-count"] = Encoding.UTF8.GetBytes("7") }));
        Assert.Null(RabbitFlowHeaders.ReadDeliveryCount(new Dictionary<string, object?> { ["x-delivery-count"] = "seven" }));
        Assert.Null(RabbitFlowHeaders.ReadDeliveryCount(new Dictionary<string, object?> { ["x-delivery-count"] = new object() }));
        Assert.Equal(int.MaxValue, RabbitFlowHeaders.ReadDeliveryCount(new Dictionary<string, object?> { ["x-delivery-count"] = long.MaxValue }));
    }
}
