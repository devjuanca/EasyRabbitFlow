using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
{

    public interface IRabbitFlowTemporary
    {
        /// <summary>
        /// Publishes a collection of messages to a temporary RabbitMQ exchange and consumes them asynchronously.
        /// Each message is processed using the provided handler function, with support for cancellation and per-message timeout.
        /// An optional completion callback can be invoked once all messages have been processed.
        /// </summary>
        /// <typeparam name="T">The type of messages to process. Must be a reference type.</typeparam>
        /// <param name="messages">The collection of messages to publish and process.</param>
        /// <param name="onMessageReceived">
        /// An asynchronous callback executed for each received message.
        /// Supports cancellation via the provided <see cref="CancellationToken"/>.
        /// </param>
        /// <param name="onCompleted">
        /// An optional callback executed after all messages have been processed.
        /// <br/>
        /// <ul>
        /// <li><b>First parameter:</b> The number of processed messages.</li>
        /// <li><b>Second parameter:</b> The number of messages that encountered errors.</li>
        /// </ul>
        /// </param>
        /// <param name="onError">
        /// An optional asynchronous callback executed when a message fails to publish or fails during processing (due to timeout, cancellation, or exception).
        /// Receives the failed message and a <see cref="CancellationToken"/>.
        /// Use this to decide what to do with failed messages — e.g., log them, store them in a persistence layer, or republish to another queue.
        /// <br/>
        /// If not provided, errors are only logged internally.
        /// </param>
        /// <param name="options">Optional configuration settings for the temporary queue behavior.</param>
        /// <param name="cancellationToken">
        /// A token that can be used to cancel the overall operation early. This will cancel the consumption process and stop waiting for message completion.
        /// <br/>
        /// <b>When triggered, it will:</b>
        /// <ul>
        ///   <li>Abort waiting for all messages to be processed.</li>
        ///   <li>Cancel any in-progress message handlers.</li>
        ///   <li>Stop consuming further messages from the temporary queue.</li>
        /// </ul>
        /// This token is typically used to propagate shutdown signals or client-side timeouts.
        /// <br/><br/>
        /// <b>If no cancellation token is provided:</b>
        /// <ul>
        ///   <li>The process will continue running until all messages are processed from the queue.</li>
        ///   <li>A per-message timeout (if configured via <see cref="RunTemporaryOptions.Timeout"/>) will still apply using an internal cancellation token for each message handler.</li>
        /// </ul>
        /// </param>

        /// <returns>A <see cref="TemporaryRunResult"/> with publish, processing, success, failure, and error counts.</returns>

        Task<TemporaryRunResult> RunAsync<T>(
            IReadOnlyList<T> messages,
            Func<T, CancellationToken, Task> onMessageReceived,
            Action<int, int>? onCompleted = null,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class;

        /// <summary>
        /// Publishes a collection of messages to a temporary RabbitMQ exchange and consumes them asynchronously.
        /// Each message is processed using the provided handler function, with support for cancellation and per-message timeout.
        /// An asynchronous completion callback is invoked once all messages have been processed.
        /// </summary>
        /// <typeparam name="T">The type of messages to process. Must be a reference type.</typeparam>
        /// <param name="messages">The collection of messages to publish and process.</param>
        /// <param name="onMessageReceived">
        /// An asynchronous callback executed for each received message.
        /// Supports cancellation via the provided <see cref="CancellationToken"/>.
        /// </param>
        /// <param name="onCompletedAsync">
        /// An asynchronous callback executed after all messages have been processed.
        /// <br/>
        /// <ul>
        /// <li><b>First parameter:</b> The number of processed messages.</li>
        /// <li><b>Second parameter:</b> The number of messages that encountered errors.</li>
        /// <li><b>Third parameter:</b> A <see cref="CancellationToken"/> propagated from the caller.</li>
        /// </ul>
        /// </param>
        /// <param name="onError">
        /// An optional asynchronous callback executed when a message fails to publish or fails during processing (due to timeout, cancellation, or exception).
        /// Receives the failed message and a <see cref="CancellationToken"/>.
        /// Use this to decide what to do with failed messages — e.g., log them, store them in a persistence layer, or republish to another queue.
        /// <br/>
        /// If not provided, errors are only logged internally.
        /// </param>
        /// <param name="options">Optional configuration settings for the temporary queue behavior.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the overall operation early.</param>
        /// <returns>A <see cref="TemporaryRunResult"/> with publish, processing, success, failure, and error counts.</returns>
        Task<TemporaryRunResult> RunAsync<T>(
            IReadOnlyList<T> messages,
            Func<T, CancellationToken, Task> onMessageReceived,
            Func<int, int, CancellationToken, Task> onCompletedAsync,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class;

        /// <summary>
        /// Publishes a collection of messages to a temporary RabbitMQ queue and processes them asynchronously.
        /// Each message is handled via a user-provided callback, and the results are safely collected into a concurrent queue.
        /// </summary>
        /// <typeparam name="T">The type of messages to be published. Must be a reference type.</typeparam>
        /// <typeparam name="TResult">The type of result produced for each message.</typeparam>
        /// <param name="messages">The list of messages to publish and process.</param>
        /// <param name="onMessageReceived">
        /// Asynchronous callback invoked for each received message.
        /// Returns a result that will be added to the result queue.
        /// </param>
        /// <param name="onCompletedAsync">
        /// Asynchronous callback executed once all messages have been processed.
        /// <br/>
        /// <ul>
        /// <li><b>First parameter:</b> The total number of messages processed.</li>
        /// <li><b>Second parameter:</b> A thread-safe queue containing the results of each message.</li>
        /// </ul>
        /// </param>
        /// <param name="onError">
        /// An optional asynchronous callback executed when a message fails to publish or fails during processing (due to timeout, cancellation, or exception).
        /// Receives the failed message and a <see cref="CancellationToken"/>.
        /// Use this to decide what to do with failed messages — e.g., log them, store them in a persistence layer, or republish to another queue.
        /// <br/>
        /// If not provided, errors are only logged internally.
        /// </param>
        /// <param name="options">Optional configuration settings for the temporary queue behavior.</param>
        /// <param name="cancellationToken">Token to cancel the operation prematurely.</param>
        /// <returns>A <see cref="TemporaryRunResult{TResult}"/> with aggregate counts and collected handler results.</returns>
        Task<TemporaryRunResult<TResult>> RunAsync<T, TResult>(
            IReadOnlyList<T> messages,
            Func<T, CancellationToken, Task<TResult>> onMessageReceived,
            Func<int, ConcurrentQueue<TResult>, Task> onCompletedAsync,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class;
    }

    internal sealed class RabbitFlowTemporary : IRabbitFlowTemporary
    {
        private readonly ConnectionFactory _connectionFactory;

        private readonly ILogger<RabbitFlowTemporary> _logger;

        private readonly JsonSerializerOptions _jsonOptions;

        public RabbitFlowTemporary(ConnectionFactory connectionFactory, ILogger<RabbitFlowTemporary> logger, [FromKeyedServices("RabbitFlowJsonSerializer")] JsonSerializerOptions? jsonOptions = null)
        {
            _connectionFactory = connectionFactory;

            _logger = logger;

            _jsonOptions = jsonOptions ?? JsonSerializerOptions.Web;
        }

        public Task<TemporaryRunResult> RunAsync<T>(
            IReadOnlyList<T> messages,
            Func<T, CancellationToken, Task> onMessageReceived,
            Action<int, int>? onCompleted = null,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
        {
            Func<int, int, CancellationToken, Task> wrapped = (p, e, _) =>
            {
                onCompleted?.Invoke(p, e);
                return Task.CompletedTask;
            };

            return RunAsync(messages, onMessageReceived, wrapped, onError, options, cancellationToken);
        }

        public async Task<TemporaryRunResult> RunAsync<T>(
            IReadOnlyList<T> messages,
            Func<T, CancellationToken, Task> onMessageReceived,
            Func<int, int, CancellationToken, Task> onCompletedAsync,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
        {
            return await RunCoreAsync<T, object?>(
                messages,
                async (message, ct) =>
                {
                    await onMessageReceived(message, ct).ConfigureAwait(false);
                    return null;
                },
                collectResults: false,
                (processed, failed, _, ct) => onCompletedAsync(processed, failed, ct),
                onError,
                options,
                cancellationToken).ConfigureAwait(false);
        }

        public Task<TemporaryRunResult<TResult>> RunAsync<T, TResult>(
               IReadOnlyList<T> messages,
               Func<T, CancellationToken, Task<TResult>> onMessageReceived,
               Func<int, ConcurrentQueue<TResult>, Task> onCompletedAsync,
               Func<T, CancellationToken, Task>? onError = null,
               RunTemporaryOptions? options = null,
               CancellationToken cancellationToken = default) where T : class
        {
            return RunCoreAsync(
                messages,
                onMessageReceived,
                collectResults: true,
                (processed, _, results, ct) => onCompletedAsync(processed, results),
                onError,
                options,
                cancellationToken);
        }

        private async Task<TemporaryRunResult<TResult>> RunCoreAsync<T, TResult>(
            IReadOnlyList<T> messages,
            Func<T, CancellationToken, Task<TResult>> onMessageReceived,
            bool collectResults,
            Func<int, int, ConcurrentQueue<TResult>, CancellationToken, Task> onCompletedAsync,
            Func<T, CancellationToken, Task>? onError,
            RunTemporaryOptions? options,
            CancellationToken cancellationToken) where T : class
        {
            options ??= new RunTemporaryOptions();

            var startedUtc = DateTime.UtcNow;

            if (messages is null || messages.Count == 0)
            {
                return TemporaryRunResult<TResult>.Empty(options.CorrelationId, startedUtc);
            }

            var resultsQueue = new ConcurrentQueue<TResult>();

            var correlationId = options.CorrelationId;

            var prefetchCount = options.PrefetchCount;

            var timeout = options.Timeout;

            var queuePrefixName = options.QueuePrefixName;

            var executionId = Guid.NewGuid().ToString("N");

            var eventName = typeof(T).Name.ToLower();

            var _queue = $"{queuePrefixName ?? eventName}-temp-queue-{executionId}";

            // effectiveCt = caller token + optional whole-run timeout; governs every internal wait
            using var runTimeoutCts = options.RunTimeout.HasValue ? new CancellationTokenSource(options.RunTimeout.Value) : null;

            using var effectiveCts = runTimeoutCts != null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runTimeoutCts.Token) : null;

            var effectiveCt = effectiveCts?.Token ?? cancellationToken;

            using var connection = await _connectionFactory.CreateConnectionAsync($"{_queue}", cancellationToken);

            using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            var maxMessages = messages.Count;

            await channel.QueueDeclareAsync(_queue, durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken);

            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefetchCount, global: false, cancellationToken: cancellationToken);

            var published = 0;

            var processed = 0;

            var succeeded = 0;

            var failed = 0;

            var publishFailed = 0;

            var runErrors = new ConcurrentQueue<TemporaryRunError>();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var consumer = new AsyncEventingBasicConsumer(channel);

            var semaphore = new SemaphoreSlim(prefetchCount);

            var channelGate = new SemaphoreSlim(1, 1);

            var activeTasks = new ConcurrentBag<Task>();

            var connectionLost = 0;

            string? shutdownReason = null;

            void TryComplete()
            {
                var terminalCount = Volatile.Read(ref processed) + Volatile.Read(ref publishFailed);

                // On connection loss undelivered messages can never arrive: stop once in-flight handlers drain.
                var endedEarly = Volatile.Read(ref connectionLost) == 1;

                if ((terminalCount >= maxMessages || endedEarly) && (activeTasks.Count == 0 || activeTasks.All(task => task.IsCompleted)))
                {
                    tcs.TrySetResult(true);
                }
            }

            Task OnShutdownAsync(object sender, ShutdownEventArgs ea)
            {
                if (!tcs.Task.IsCompleted && Interlocked.Exchange(ref connectionLost, 1) == 0)
                {
                    shutdownReason = ea.ReplyText;
                    _logger.LogError("[RabbitFlowTemporary] Connection or channel shut down while the run was in progress: {reason}. CorrelationId: {correlationId}", ea.ReplyText, correlationId);
                    TryComplete();
                }

                return Task.CompletedTask;
            }

            channel.ChannelShutdownAsync += OnShutdownAsync;

            connection.ConnectionShutdownAsync += OnShutdownAsync;

            void CompleteProcessed(bool success)
            {
                if (success)
                {
                    Interlocked.Increment(ref succeeded);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }

                Interlocked.Increment(ref processed);
                TryComplete();
            }

            async Task SafeAckAsync(ulong deliveryTag)
            {
                try
                {
                    if (!channel.IsOpen)
                    {
                        return;
                    }

                    await channelGate.WaitAsync(effectiveCt).ConfigureAwait(false);
                    try
                    {
                        await channel.BasicAckAsync(deliveryTag: deliveryTag, multiple: false);
                    }
                    finally
                    {
                        channelGate.Release();
                    }
                }
                catch (Exception ex)
                {
                    // Best-effort: the queue is exclusive and auto-delete, so a missed ack cannot leave a stuck message behind.
                    _logger.LogDebug(ex, "[RabbitFlowTemporary] Could not acknowledge message. DeliveryTag: {deliveryTag}, CorrelationId: {correlationId}", deliveryTag, correlationId);
                }
            }

            consumer.ReceivedAsync += async (model, ea) =>
            {
                await semaphore.WaitAsync(effectiveCt);

                var ownsSemaphore = true;

                try
                {
                    if (tcs.Task.IsCompleted || effectiveCt.IsCancellationRequested)
                    {
                        // The run already ended (timeout, cancellation, or connection loss): the message is accounted for as unprocessed.
                        return;
                    }

                    T? message;
                    try
                    {
                        message = JsonSerializer.Deserialize<T>(ea.Body.Span, _jsonOptions);
                    }
                    catch (Exception ex)
                    {
                        runErrors.Enqueue(TemporaryRunError.FromException(TemporaryRunErrorStage.Deserialize, ex, _queue, ea.DeliveryTag));
                        _logger.LogError(ex, "[RabbitFlowTemporary] Error deserializing message. CorrelationId: {correlationId}", correlationId);
                        await SafeAckAsync(ea.DeliveryTag).ConfigureAwait(false);
                        CompleteProcessed(false);
                        return;
                    }

                    if (message is null)
                    {
                        runErrors.Enqueue(TemporaryRunError.FromMessage(TemporaryRunErrorStage.Deserialize, "Deserialization returned null.", _queue, ea.DeliveryTag));
                        _logger.LogWarning("[RabbitFlowTemporary] Message is null. CorrelationId: {correlationId}", correlationId);
                        await SafeAckAsync(ea.DeliveryTag).ConfigureAwait(false);
                        CompleteProcessed(false);
                        return;
                    }

                    await SafeAckAsync(ea.DeliveryTag).ConfigureAwait(false);

                    // The processing task takes over the semaphore slot and releases it when done
                    var processingTask = Task.Run(async () =>
                    {
                        try
                        {
                            using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;

                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(effectiveCt, timeoutCts?.Token ?? CancellationToken.None);

                            try
                            {
                                var result = await onMessageReceived(message, linkedCts.Token).ConfigureAwait(false);

                                if (collectResults)
                                {
                                    resultsQueue.Enqueue(result);
                                }

                                CompleteProcessed(true);
                            }
                            catch (TaskCanceledException) when (timeoutCts != null && timeoutCts.Token.IsCancellationRequested)
                            {
                                runErrors.Enqueue(TemporaryRunError.FromMessage(TemporaryRunErrorStage.Timeout, $"Message processing timed out after {timeout}.", _queue, ea.DeliveryTag));
                                _logger.LogError("[RabbitFlowTemporary] Message processing timed out after {Timeout}. CorrelationId: {correlationId}", timeout, correlationId);
                                await InvokeOnErrorAsync(onError, message, cancellationToken, _logger, correlationId);
                                CompleteProcessed(false);
                            }
                            catch (OperationCanceledException) when (runTimeoutCts != null && runTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                            {
                                runErrors.Enqueue(TemporaryRunError.FromMessage(TemporaryRunErrorStage.Timeout, $"Message processing was canceled because the run timed out after {options.RunTimeout}.", _queue, ea.DeliveryTag));
                                _logger.LogError("[RabbitFlowTemporary] Message processing was canceled because the run timed out after {RunTimeout}. CorrelationId: {correlationId}", options.RunTimeout, correlationId);
                                await InvokeOnErrorAsync(onError, message, CancellationToken.None, _logger, correlationId);
                                CompleteProcessed(false);
                            }
                            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                runErrors.Enqueue(TemporaryRunError.FromMessage(TemporaryRunErrorStage.Cancellation, "Message processing was canceled by main Cancellation Token.", _queue, ea.DeliveryTag));
                                _logger.LogError("[RabbitFlowTemporary] Message processing was canceled by main Cancellation Token. CorrelationId: {correlationId}", correlationId);
                                await InvokeOnErrorAsync(onError, message, CancellationToken.None, _logger, correlationId);
                                CompleteProcessed(false);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                runErrors.Enqueue(TemporaryRunError.FromMessage(TemporaryRunErrorStage.Cancellation, "Message processing was canceled.", _queue, ea.DeliveryTag));
                                _logger.LogError("[RabbitFlowTemporary] Message processing was canceled. CorrelationId: {correlationId}", correlationId);
                                await InvokeOnErrorAsync(onError, message, CancellationToken.None, _logger, correlationId);
                                CompleteProcessed(false);
                            }
                            catch (Exception ex)
                            {
                                runErrors.Enqueue(TemporaryRunError.FromException(TemporaryRunErrorStage.Process, ex, _queue, ea.DeliveryTag));
                                _logger.LogError(ex, "[RabbitFlowTemporary] Error while processing the message. CorrelationId: {correlationId}", correlationId);
                                await InvokeOnErrorAsync(onError, message, cancellationToken, _logger, correlationId);
                                CompleteProcessed(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            runErrors.Enqueue(TemporaryRunError.FromException(TemporaryRunErrorStage.Process, ex, _queue, ea.DeliveryTag));
                            _logger.LogError(ex, "[RabbitFlowTemporary] Unhandled exception in processing task. CorrelationId: {correlationId}", correlationId);
                            CompleteProcessed(false);
                        }
                        finally
                        {
                            // Always release the semaphore when the task is done
                            semaphore.Release();
                        }
                    });

                    ownsSemaphore = false;

                    activeTasks.Add(processingTask);
                }
                catch (Exception ex)
                {
                    runErrors.Enqueue(TemporaryRunError.FromException(TemporaryRunErrorStage.Process, ex, _queue, ea.DeliveryTag));
                    _logger.LogError(ex, "[RabbitFlowTemporary] Error while preparing message processing. CorrelationId: {correlationId}", correlationId);
                    CompleteProcessed(false);
                }
                finally
                {
                    if (ownsSemaphore)
                    {
                        semaphore.Release();
                    }
                }
            };

            var consumerTag = await channel.BasicConsumeAsync(_queue, autoAck: false, consumer);

            for (var index = 0; index < messages.Count; index++)
            {
                var msg = messages[index];

                try
                {
                    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, _jsonOptions));

                    await channelGate.WaitAsync(effectiveCt).ConfigureAwait(false);
                    try
                    {
                        await channel.BasicPublishAsync("", _queue, body);
                        Interlocked.Increment(ref published);
                    }
                    finally
                    {
                        channelGate.Release();
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Interlocked.Increment(ref publishFailed);
                    runErrors.Enqueue(TemporaryRunError.FromException(TemporaryRunErrorStage.Publish, ex, _queue, messageIndex: index));

                    _logger.LogError(ex, "[RabbitFlowTemporary] Error publishing message at index {index} to temporary queue. CorrelationId: {correlationId}", index, correlationId);

                    await InvokeOnErrorAsync(onError, msg, cancellationToken, _logger, correlationId);

                    TryComplete();
                }
            }

            // Monitoring task: closes the completion gap between the last CompleteProcessed and pending active tasks
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!effectiveCt.IsCancellationRequested)
                    {
                        TryComplete();

                        if (tcs.Task.IsCompleted) break;

                        // Check periodically
                        await Task.Delay(100, effectiveCt);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RabbitFlowTemporary] Error in completion monitoring task. CorrelationId: {correlationId}", correlationId);
                }
            }, effectiveCt);

            using var ctr = effectiveCt.Register(() =>
            {
                tcs.TrySetCanceled(effectiveCt);
            });

            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                _logger.LogError("[RabbitFlowTemporary] Run was canceled before completion (caller cancellation or run timeout). CorrelationId: {correlationId}", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitFlowTemporary] Error while waiting for messages to be processed. CorrelationId: {correlationId}", correlationId);
            }
            finally
            {
                channel.ChannelShutdownAsync -= OnShutdownAsync;

                connection.ConnectionShutdownAsync -= OnShutdownAsync;

                // After an early end, give canceled in-flight handlers a bounded window to finish so counters settle.
                // Second pass catches a task dispatched concurrently with the early end.
                for (var pass = 0; pass < 2; pass++)
                {
                    var drainTasks = activeTasks.Where(task => !task.IsCompleted).ToArray();

                    if (drainTasks.Length == 0)
                    {
                        break;
                    }

                    await Task.WhenAny(Task.WhenAll(drainTasks), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                }

                try
                {
                    await channel.BasicCancelAsync(consumerTag, cancellationToken: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RabbitFlowTemporary] Error canceling consumer. CorrelationId: {correlationId}", correlationId);
                }

                // Messages that never reached a terminal state (lost connection, run timeout, cancellation)
                var unaccounted = maxMessages - Volatile.Read(ref processed) - Volatile.Read(ref publishFailed);

                if (unaccounted > 0)
                {
                    Interlocked.Add(ref failed, unaccounted);

                    if (Volatile.Read(ref connectionLost) == 1)
                    {
                        runErrors.Enqueue(TemporaryRunError.FromMessage(TemporaryRunErrorStage.ConnectionLost, $"{unaccounted} message(s) were never processed: the connection or channel was shut down ({shutdownReason ?? "unknown reason"}).", _queue));
                    }
                    else if (runTimeoutCts != null && runTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        runErrors.Enqueue(TemporaryRunError.FromMessage(TemporaryRunErrorStage.Timeout, $"{unaccounted} message(s) were never processed: the run timed out after {options.RunTimeout}.", _queue));
                    }
                    else
                    {
                        runErrors.Enqueue(TemporaryRunError.FromMessage(TemporaryRunErrorStage.Cancellation, $"{unaccounted} message(s) were never processed: the run was canceled.", _queue));
                    }
                }

                // Always run the completion callback
                try
                {
                    _logger.LogDebug("[RabbitFlowTemporary] Executing completion callback. Processed: {processed}, Failed: {failed}, Results: {results}, CorrelationId: {correlationId}",
                        processed, failed, resultsQueue.Count, correlationId);

                    await onCompletedAsync(processed, failed, resultsQueue, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    runErrors.Enqueue(TemporaryRunError.FromException(TemporaryRunErrorStage.Completion, ex, _queue));
                    _logger.LogError(ex, "[RabbitFlowTemporary] Error in completion callback. CorrelationId: {correlationId}", correlationId);
                }

                channelGate.Dispose();
            }

            return new TemporaryRunResult<TResult>(
                maxMessages,
                published,
                processed,
                succeeded,
                failed,
                correlationId,
                _queue,
                startedUtc,
                DateTime.UtcNow,
                runErrors.ToArray(),
                resultsQueue.ToArray());
        }

        private static async Task InvokeOnErrorAsync<T>(Func<T, CancellationToken, Task>? onError, T message, CancellationToken cancellationToken, ILogger logger, string? correlationId)
        {
            if (onError is null)
            {
                return;
            }

            try
            {
                await onError(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[RabbitFlowTemporary] Error in onError callback. CorrelationId: {correlationId}", correlationId);
            }
        }

    }
}
