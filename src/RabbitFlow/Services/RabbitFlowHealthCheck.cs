using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
{
    /// <summary>
    /// Options for the EasyRabbitFlow health check.
    /// </summary>
    public sealed class RabbitFlowHealthCheckOptions
    {
        /// <summary>
        /// Queues that must exist on the broker. When empty, the check only verifies broker connectivity.
        /// A missing queue reports the registration's failure status (unhealthy by default).
        /// </summary>
        public IList<string> Queues { get; } = new List<string>();

        /// <summary>
        /// When set, the check reports <see cref="HealthStatus.Degraded"/> if any monitored queue
        /// holds more ready messages than this threshold.
        /// </summary>
        public uint? MaxReadyMessages { get; set; }

        /// <summary>
        /// When <c>true</c>, the check reports <see cref="HealthStatus.Degraded"/> if any monitored
        /// queue has no consumers attached.
        /// </summary>
        public bool RequireConsumers { get; set; }
    }

    internal sealed class RabbitFlowHealthCheck : IHealthCheck
    {
        private readonly IRabbitFlowState _state;

        private readonly RabbitFlowHealthCheckOptions _options;

        public RabbitFlowHealthCheck(IRabbitFlowState state, RabbitFlowHealthCheckOptions options)
        {
            _state = state;

            _options = options;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                // With no queues configured this still opens a connection, acting as a pure connectivity probe.
                var states = await _state.GetQueuesStateAsync(_options.Queues, cancellationToken).ConfigureAwait(false);

                var data = new Dictionary<string, object>();

                var missing = new List<string>();

                var degraded = new List<string>();

                foreach (var queue in states)
                {
                    data[$"{queue.QueueName}.exists"] = queue.Exists;
                    data[$"{queue.QueueName}.messages"] = queue.MessageCount;
                    data[$"{queue.QueueName}.consumers"] = queue.ConsumerCount;

                    if (!queue.Exists)
                    {
                        missing.Add($"Queue '{queue.QueueName}' does not exist.");
                        continue;
                    }

                    if (_options.RequireConsumers && queue.ConsumerCount == 0)
                    {
                        degraded.Add($"Queue '{queue.QueueName}' has no consumers.");
                    }

                    if (_options.MaxReadyMessages.HasValue && queue.MessageCount > _options.MaxReadyMessages.Value)
                    {
                        degraded.Add($"Queue '{queue.QueueName}' backlog is {queue.MessageCount} (threshold {_options.MaxReadyMessages.Value}).");
                    }
                }

                if (missing.Count > 0)
                {
                    return new HealthCheckResult(context.Registration.FailureStatus, string.Join(" ", missing), data: data);
                }

                if (degraded.Count > 0)
                {
                    return HealthCheckResult.Degraded(string.Join(" ", degraded), data: data);
                }

                var description = _options.Queues.Count == 0
                    ? "RabbitMQ broker is reachable."
                    : $"All {states.Count} monitored queue(s) are healthy.";

                return HealthCheckResult.Healthy(description, data);
            }
            catch (Exception ex)
            {
                return new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ broker is unreachable.", ex);
            }
        }
    }

    /// <summary>
    /// Registration extensions for the EasyRabbitFlow health check.
    /// </summary>
    public static class RabbitFlowHealthCheckBuilderExtensions
    {
        /// <summary>
        /// Adds a health check that verifies RabbitMQ broker connectivity and, optionally, the existence,
        /// backlog, and consumer presence of specific queues — all in a single broker connection.
        /// Requires <c>AddRabbitFlow</c> to be registered.
        /// </summary>
        /// <param name="builder">The health checks builder.</param>
        /// <param name="name">The health check name. Defaults to <c>rabbitflow</c>.</param>
        /// <param name="configure">Optional callback to configure monitored queues and thresholds.</param>
        /// <param name="failureStatus">Status reported on hard failures (unreachable broker, missing queue). Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
        /// <param name="tags">Optional tags for filtering health check endpoints.</param>
        public static IHealthChecksBuilder AddRabbitFlow(
            this IHealthChecksBuilder builder,
            string name = "rabbitflow",
            Action<RabbitFlowHealthCheckOptions>? configure = null,
            HealthStatus? failureStatus = null,
            IEnumerable<string>? tags = null)
        {
            var options = new RabbitFlowHealthCheckOptions();

            configure?.Invoke(options);

            builder.Add(new HealthCheckRegistration(
                name,
                serviceProvider => new RabbitFlowHealthCheck(serviceProvider.GetRequiredService<IRabbitFlowState>(), options),
                failureStatus,
                tags));

            return builder;
        }
    }
}
