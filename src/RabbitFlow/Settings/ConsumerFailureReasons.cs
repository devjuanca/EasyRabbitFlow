using System;
using EasyRabbitFlow.Exceptions;

namespace EasyRabbitFlow.Settings
{
    // Values of the 'reason' tag on easyrabbitflow.messages.consumed{outcome=failure} and of the consumer's
    // terminal failure log line. Kept pure so the mapping can be unit-tested without a broker.
    internal static class ConsumerFailureReasons
    {
        public const string HandlerError = "handler_error";
        public const string Timeout = "timeout";
        public const string Abandoned = "abandoned";
        public const string Poison = "poison";
        public const string Deserialize = "deserialize";

        // Type patterns with a discard: the netstandard2.1 target compiles as C# 8, which has no bare type patterns.
        public static string Of(Exception exception)
        {
            return exception switch
            {
                RabbitFlowHandlerAbandonedException _ => Abandoned,
                RabbitFlowPoisonMessageException _ => Poison,
                OperationCanceledException _ => Timeout,
                _ => HandlerError
            };
        }
    }
}
