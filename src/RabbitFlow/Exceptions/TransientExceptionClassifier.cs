using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Exceptions
{
    /// <summary>
    /// Central transient-failure classification, shared by the in-process retry policy,
    /// the dead-letter envelope writer, and the dead-letter reprocessor — so all three
    /// always agree on what "transient" means.
    /// </summary>
    internal static class TransientExceptionClassifier
    {
        private const int MaxDepth = 10;

#if !NET8_0_OR_GREATER
        // HttpRequestException.StatusCode exists since .NET 5 but is invisible to the netstandard2.1
        // compilation; read it via reflection so .NET 6/7 apps consuming that build still get HTTP classification.
        private static readonly System.Reflection.PropertyInfo? HttpStatusCodeProperty = typeof(HttpRequestException).GetProperty("StatusCode");
#endif

        /// <summary>
        /// True when the exception — or any inner exception in its chain — represents a retryable failure:
        /// <see cref="RabbitFlowTransientException"/> (including derived types), cancellation/timeout,
        /// or an HTTP failure with no response or a transient status code (408, 429, 502, 503, 504).
        /// </summary>
        public static bool IsTransient(Exception? exception)
        {
            var current = exception;

            var depth = 0;

            while (current != null && depth < MaxDepth)
            {
                if (IsDirectlyTransient(current))
                {
                    return true;
                }

                current = current.InnerException;

                depth++;
            }

            return false;
        }

        /// <summary>
        /// Legacy fallback for dead-letter envelopes written before the <c>isTransient</c> flag existed:
        /// exact exception type-name matching, no inheritance.
        /// </summary>
        public static bool IsTransientTypeName(string? exceptionTypeName)
        {
            if (string.IsNullOrEmpty(exceptionTypeName))
            {
                return false;
            }

            return exceptionTypeName == nameof(OperationCanceledException)
                || exceptionTypeName == nameof(TaskCanceledException)
                || exceptionTypeName == nameof(RabbitFlowTransientException);
        }

        private static bool IsDirectlyTransient(Exception exception)
        {
            if (exception is RabbitFlowTransientException)
            {
                return true;
            }

            if (exception is OperationCanceledException)
            {
                return true;
            }

            if (exception is TimeoutException)
            {
                return true;
            }

            if (exception is HttpRequestException http)
            {
                return IsTransientHttp(http);
            }

            return false;
        }

        private static bool IsTransientHttp(HttpRequestException exception)
        {
            int? statusCode;

#if NET8_0_OR_GREATER
            statusCode = exception.StatusCode.HasValue ? (int)exception.StatusCode.Value : (int?)null;
#else
            var raw = HttpStatusCodeProperty?.GetValue(exception);

            statusCode = raw == null ? (int?)null : Convert.ToInt32(raw);
#endif

            if (statusCode == null)
            {
                // No response was received (DNS failure, connection refused/reset): network-level, transient.
                return true;
            }

            return statusCode == 408    // Request Timeout
                || statusCode == 429    // Too Many Requests
                || statusCode == 502    // Bad Gateway
                || statusCode == 503    // Service Unavailable
                || statusCode == 504;   // Gateway Timeout
        }
    }
}
