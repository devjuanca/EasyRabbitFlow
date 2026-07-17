using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
{
    /// <summary>
    /// Declares the application-owned exchanges registered via <c>DeclareExchange</c> at startup,
    /// independently of any consumer. One-shot: it opens a connection, declares everything, and closes it.
    /// <para>
    /// A broker that is unreachable at startup does not prevent the host from starting: when
    /// <see cref="HostSettings.AutomaticRecoveryEnabled"/> is true (the default) the service keeps retrying
    /// in the background using <see cref="HostSettings.NetworkRecoveryInterval"/> until it succeeds or the
    /// application shuts down. An exchange that already exists with a different type or arguments is adopted
    /// with a warning instead of failing the start — a running publisher beats one that won't boot.
    /// </para>
    /// </summary>
    internal sealed class TopologyHostedService : IHostedService
    {
        private readonly IServiceProvider _root;

        private readonly ILogger<TopologyHostedService> _logger;

        private CancellationTokenSource? _retryCts;

        private Task? _retryTask;

        public TopologyHostedService(IServiceProvider root, ILogger<TopologyHostedService> logger)
        {
            _root = root;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var declarations = _root.GetServices<ExchangeDeclaration>().ToList();

            if (declarations.Count == 0)
            {
                return;
            }

            var connectionFactory = _root.GetRequiredService<ConnectionFactory>();

            var hostSettings = _root.GetService<HostSettings>() ?? new HostSettings();

            try
            {
                await DeclareAllAsync(connectionFactory, declarations, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Same availability contract as consumers: a broker that is down at startup must not take the
                // whole host down with it. Publishes to the missing exchanges will fail (and surface through
                // PublishResult) until the background retry succeeds.
                _logger.LogError(ex,
                    "[RABBIT-FLOW]: Could not declare the configured topology ({Count} exchange(s)) at startup. The host will continue to start. {Recovery}",
                    declarations.Count,
                    hostSettings.AutomaticRecoveryEnabled
                        ? "Recovery is enabled; declaration will keep retrying in the background."
                        : "Recovery is disabled (AutomaticRecoveryEnabled = false); the topology stays undeclared until restart.");

                if (hostSettings.AutomaticRecoveryEnabled)
                {
                    // Floored at 1s so a misconfigured tiny interval never spins the loop (same rule as consumers).
                    var retryDelay = TimeSpan.FromMilliseconds(Math.Max(1000, hostSettings.NetworkRecoveryInterval.TotalMilliseconds));

                    _retryCts = new CancellationTokenSource();

                    _retryTask = RetryLoopAsync(connectionFactory, declarations, retryDelay, _retryCts.Token);
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _retryCts?.Cancel();

            if (_retryTask != null)
            {
                try
                {
                    await _retryTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _retryCts?.Dispose();
        }

        private async Task RetryLoopAsync(ConnectionFactory connectionFactory, List<ExchangeDeclaration> declarations, TimeSpan delay, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);

                    await DeclareAllAsync(connectionFactory, declarations, ct).ConfigureAwait(false);

                    _logger.LogInformation("[RABBIT-FLOW]: Configured topology declared successfully after retry ({Count} exchange(s)).", declarations.Count);

                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[RABBIT-FLOW]: Topology declaration retry failed; next attempt in {Delay}.", delay);
                }
            }
        }

        private async Task DeclareAllAsync(ConnectionFactory connectionFactory, List<ExchangeDeclaration> declarations, CancellationToken ct)
        {
            using var connection = await connectionFactory.CreateConnectionAsync($"topology-{Guid.NewGuid():N}", ct).ConfigureAwait(false);

            foreach (var declaration in declarations)
            {
                // Each declare runs on its own throwaway channel so a PRECONDITION_FAILED (406) closes only
                // that channel and the remaining exchanges still get declared.
                using var channel = await connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);

                var exchangeType = declaration.ExchangeType.ToString().ToLowerInvariant();

                try
                {
                    await channel.ExchangeDeclareAsync(
                        declaration.ExchangeName,
                        exchangeType,
                        declaration.Durable,
                        declaration.AutoDelete,
                        declaration.Args,
                        cancellationToken: ct).ConfigureAwait(false);

                    _logger.LogDebug("[RABBIT-FLOW]: Exchange '{Exchange}' ({Type}) declared.", declaration.ExchangeName, exchangeType);
                }
                catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 406)
                {
                    // The 406 itself proves the exchange exists (inequivalent type/args) — adopt it and keep
                    // running rather than crashing the host over a topology mismatch. Bindings and routing keep
                    // working against whatever the broker already has.
                    _logger.LogWarning(
                        "[RABBIT-FLOW]: Exchange '{Exchange}' already exists with different settings; adopting the existing exchange. " +
                        "Broker reason: {Reason}. To apply the configured settings, delete the exchange (draining bound queues first) and restart.",
                        declaration.ExchangeName, ex.ShutdownReason?.ReplyText);
                }
            }
        }
    }
}
