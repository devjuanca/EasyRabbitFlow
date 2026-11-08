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

    [Fact]
    public void ConsumerSettings_QuorumQueueType_WithExclusiveQueue_Throws()
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.QueueType = QueueType.Quorum;
                ag.ExclusiveQueue = true;
            });
        }));

        Assert.Contains("exclusive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(QueueType.Quorum, null)]
    [InlineData(null, QueueType.Quorum)]
    public void ConsumerSettings_QuorumQueueType_WithNonDurableQueue_Throws(QueueType? queueType, QueueType? deadLetterQueueType)
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.QueueType = queueType;
                ag.DeadLetterQueueType = deadLetterQueueType;
                ag.DurableQueue = false;
            });
        }));

        Assert.Contains("durable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(QueueType.Quorum, null)]
    [InlineData(null, QueueType.Quorum)]
    public void ConsumerSettings_QuorumQueueType_WithAutoDeleteQueue_Throws(QueueType? queueType, QueueType? deadLetterQueueType)
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.QueueType = queueType;
                ag.DeadLetterQueueType = deadLetterQueueType;
                ag.AutoDeleteQueue = true;
            });
        }));

        Assert.Contains("auto-delete", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsumerSettings_QuorumThroughArgsOnly_IsValidatedLikeTheTypedProperty()
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.Args = new Dictionary<string, object?> { ["x-queue-type"] = "quorum" };
                ag.ExclusiveQueue = true;
            });
        }));

        Assert.Contains("exclusive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConsumerSettings_QuorumDeadLetter_WithoutDeadLetterQueue_IsNotValidated()
    {
        // DeadLetterQueueType only matters when a dead-letter queue is generated.
        AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.GenerateDeadletterQueue = false;
                ag.DeadLetterQueueType = QueueType.Quorum;
                ag.DurableQueue = false;
                ag.ExclusiveQueue = true;
            });
        });
    }

    [Fact]
    public void ConsumerSettings_QueueType_ContradictingArgs_Throws()
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.QueueType = QueueType.Classic;
                ag.Args = new Dictionary<string, object?> { ["x-queue-type"] = "quorum" };
            });
        }));

        Assert.Contains("x-queue-type", ex.Message);
    }

    [Fact]
    public void ConsumerSettings_QueueType_AgreeingWithArgs_IsAccepted()
    {
        AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.QueueType = QueueType.Quorum;
                ag.Args = new Dictionary<string, object?> { ["x-queue-type"] = "quorum" };
            });
        });
    }

    [Fact]
    public void ConsumerSettings_NoQueueType_LeavesBrokerDefaultsAlone()
    {
        // null is the backward-compatible default: nothing to validate, nothing sent.
        AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.ExclusiveQueue = true;
                ag.DurableQueue = false;
            });
        });
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
    [InlineData("orders-deadletter-exchange")]
    [InlineData("Custom-DeadLetter")]
    public void AutoGenerateSettings_ExchangeName_WithDeadletter_Throws(string exchangeName)
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
    [InlineData("orders-deadletter-routing-key")]
    [InlineData("RK-DEADLETTER")]
    public void AutoGenerateSettings_RoutingKey_WithDeadletter_Throws(string routingKey)
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag => ag.RoutingKey = routingKey);
        }));

        Assert.Contains(routingKey, ex.Message);
    }

    [Theory]
    [InlineData("orders-exchange")]
    [InlineData("snapshots-exchange")]
    [InlineData("custom-routing-key")]
    [InlineData("Orders-Exchange")]
    public void AutoGenerateSettings_ExchangeName_WithExchangeOrRoutingKeySuffix_DoesNotThrow(string exchangeName)
    {
        AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag => ag.ExchangeName = exchangeName);
        });
    }

    [Theory]
    [InlineData("orders-routing-key")]
    [InlineData("rk-exchange")]
    [InlineData("ORDERS-ROUTING-KEY")]
    public void AutoGenerateSettings_RoutingKey_WithExchangeOrRoutingKeySuffix_DoesNotThrow(string routingKey)
    {
        AddTestConsumer("orders", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag => ag.RoutingKey = routingKey);
        });
    }

    [Fact]
    public void AutoGenerateSettings_ConventionalSuffixes_DoNotRelaxQueueNameRule()
    {
        var ex = Assert.Throws<RabbitFlowException>(() => AddTestConsumer("orders-exchange", cfg =>
        {
            cfg.AutoGenerate = true;
            cfg.ConfigureAutoGenerate(ag =>
            {
                ag.ExchangeName = "snapshots-exchange";
                ag.RoutingKey = "orders-routing-key";
            });
        }));

        Assert.Contains("orders-exchange", ex.Message);
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
