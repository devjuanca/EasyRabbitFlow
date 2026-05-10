using EasyRabbitFlow.Exceptions;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;
using EasyRabbitFlow.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace EasyRabbitFlow.Tests;

public class NameValidationTests
{
    private static void AddTestConsumer(string queueName, Action<ConsumerSettings<TestConsumer>>? configure = null)
    {
        var configurator = new RabbitFlowConfigurator(new ServiceCollection());

        configurator.AddConsumer<TestConsumer>(queueName, cfg => configure?.Invoke(cfg));
    }

    [Theory]
    [InlineData("orders-deadletter")]
    [InlineData("orders-deadletter-extra")]
    [InlineData("my-exchange")]
    [InlineData("my-exchange-suffix")]
    [InlineData("my-routing-key")]
    [InlineData("queue-routing-key-extra")]
    public void ConsumerSettings_QueueName_WithReservedSubstring_Throws(string queueName)
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer(queueName));

        Assert.Contains(queueName, ex.Message);
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("orders-DEADLETTER")]
    [InlineData("Orders-DeadLetter")]
    [InlineData("queue-Exchange")]
    [InlineData("QUEUE-ROUTING-KEY")]
    public void ConsumerSettings_QueueName_ReservedSubstring_IsCaseInsensitive(string queueName)
    {
        Assert.Throws<RabbitFlowException>(() => AddTestConsumer(queueName));
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("orders-created")]
    [InlineData("user.signed-up")]
    public void ConsumerSettings_QueueName_ValidName_DoesNotThrow(string queueName)
    {
        AddTestConsumer(queueName);
    }

    [Theory]
    [InlineData("orders-deadletter")]
    [InlineData("custom-exchange")]
    [InlineData("custom-routing-key")]
    public void AutoGenerateSettings_ExchangeName_WithReservedSubstring_Throws(string exchangeName)
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag => ag.ExchangeName = exchangeName);
        }));

        Assert.Contains(exchangeName, ex.Message);
    }

    [Theory]
    [InlineData("orders-deadletter")]
    [InlineData("rk-exchange")]
    [InlineData("rk-routing-key")]
    public void AutoGenerateSettings_RoutingKey_WithReservedSubstring_Throws(string routingKey)
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag => ag.RoutingKey = routingKey);
        }));

        Assert.Contains(routingKey, ex.Message);
    }

    [Fact]
    public void AutoGenerateSettings_NullOrWhitespace_DoesNotThrow()
    {
        AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.ExchangeName = null;
                ag.RoutingKey = null;
            });
        });

        AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.ExchangeName = "   ";
                ag.RoutingKey = "";
            });
        });
    }

    [Fact]
    public void AutoGenerateSettings_ValidNames_DoNotThrow()
    {
        AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.ExchangeName = "orders.events";
                ag.RoutingKey = "orders.created";
            });
        });
    }

    [Theory]
    [InlineData("orders-deadletter")]
    [InlineData("my-exchange")]
    [InlineData("my-routing-key")]
    public void DisableNameValidation_WithAutoGenerateOff_AllowsReservedSubstring(string queueName)
    {
        AddTestConsumer(queueName, cfg =>
        {
            cfg.DisableNameValidation = true;
            cfg.AutoGenerate = false;
        });
    }

    [Fact]
    public void DisableNameValidation_IsIgnored_WhenAutoGenerateIsOn()
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders-deadletter", cfg =>
        {
            cfg.DisableNameValidation = true;
            cfg.AutoGenerate = true;
        }));

        Assert.Contains("orders-deadletter", ex.Message);
    }

    [Fact]
    public void DisableNameValidation_DefaultsToFalse_StillValidatesQueueName()
    {
        Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders-deadletter"));
    }
}
