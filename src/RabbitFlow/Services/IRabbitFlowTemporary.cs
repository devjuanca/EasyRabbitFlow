using EasyRabbitFlow.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EasyRabbitFlow.Services
{

    public interface IRabbitFlowTemporary
    {
        /// <summary>
        /// Publishes and processes a single-pass asynchronous source incrementally. Completes only
        /// after the source ends and observed messages finish processing. Silence does not end the run.
        /// Cancellation, RunTimeout or a broker connection loss interrupt the source and return a partial
        /// result; handlers already running keep going, bounded only by Timeout / RunTimeout. Source errors
        /// are reported at the Enumeration stage, without invoking onError for an unknown message.
        /// The source must cooperate with cancellation; unfinished enumeration is disposed when it settles.
        /// </summary>
        Task<TemporaryRunResult> RunAsync<T>(
            IAsyncEnumerable<T> messages,
            Func<T, CancellationToken, Task> onMessageReceived,
            Action<TemporaryRunResult>? onCompleted = null,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class;

        /// <summary>
        /// Processes an asynchronous source incrementally and invokes an asynchronous completion callback,
        /// including for an empty source or a partial result. SourceCompleted indicates normal source termination.
        /// </summary>
        Task<TemporaryRunResult> RunAsync<T>(
            IAsyncEnumerable<T> messages,
            Func<T, CancellationToken, Task> onMessageReceived,
            Func<TemporaryRunResult, CancellationToken, Task> onCompletedAsync,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class;

        /// <summary>
        /// Processes an asynchronous source incrementally and collects successful handler results in memory.
        /// Results and error details grow with the run; the input source is never materialized.
        /// </summary>
        Task<TemporaryRunResult<TResult>> RunAsync<T, TResult>(
            IAsyncEnumerable<T> messages,
            Func<T, CancellationToken, Task<TResult>> onMessageReceived,
            Func<TemporaryRunResult<TResult>, CancellationToken, Task> onCompletedAsync,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class;

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
        /// Receives the <see cref="TemporaryRunResult"/> describing the whole run
        /// (counters, correlation id, queue name, duration, success flag, and errors).
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
            Action<TemporaryRunResult>? onCompleted = null,
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
        /// <li><b>First parameter:</b> The <see cref="TemporaryRunResult"/> describing the whole run (counters, correlation id, queue name, duration, success flag, and errors).</li>
        /// <li><b>Second parameter:</b> A <see cref="CancellationToken"/> propagated from the caller.</li>
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
            Func<TemporaryRunResult, CancellationToken, Task> onCompletedAsync,
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
        /// <li><b>First parameter:</b> The <see cref="TemporaryRunResult{TResult}"/> describing the whole run, including the <c>Results</c> collected from successful handlers.</li>
        /// <li><b>Second parameter:</b> A <see cref="CancellationToken"/> propagated from the caller.</li>
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
            Func<TemporaryRunResult<TResult>, CancellationToken, Task> onCompletedAsync,
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
            Action<TemporaryRunResult>? onCompleted = null,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
        {
            Func<TemporaryRunResult, CancellationToken, Task> wrapped = (result, _) =>
            {
                onCompleted?.Invoke(result);
                return Task.CompletedTask;
            };

            return RunAsync(messages, onMessageReceived, wrapped, onError, options, cancellationToken);
        }

        public async Task<TemporaryRunResult> RunAsync<T>(
            IReadOnlyList<T> messages,
            Func<T, CancellationToken, Task> onMessageReceived,
            Func<TemporaryRunResult, CancellationToken, Task> onCompletedAsync,
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
                (result, ct) => onCompletedAsync(result, ct),
                onError,
                options,
                cancellationToken).ConfigureAwait(false);
        }

        public Task<TemporaryRunResult<TResult>> RunAsync<T, TResult>(
               IReadOnlyList<T> messages,
               Func<T, CancellationToken, Task<TResult>> onMessageReceived,
               Func<TemporaryRunResult<TResult>, CancellationToken, Task> onCompletedAsync,
               Func<T, CancellationToken, Task>? onError = null,
               RunTemporaryOptions? options = null,
               CancellationToken cancellationToken = default) where T : class
        {
            return RunCoreAsync(
                messages,
                onMessageReceived,
                collectResults: true,
                onCompletedAsync,
                onError,
                options,
                cancellationToken);
        }

        public Task<TemporaryRunResult> RunAsync<T>(
            IAsyncEnumerable<T> messages,
            Func<T, CancellationToken, Task> onMessageReceived,
            Action<TemporaryRunResult>? onCompleted = null,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
            => RunAsync(messages, onMessageReceived, (result, _) =>
            {
                onCompleted?.Invoke(result);
                return Task.CompletedTask;
            }, onError, options, cancellationToken);

        public async Task<TemporaryRunResult> RunAsync<T>(
            IAsyncEnumerable<T> messages,
            Func<T, CancellationToken, Task> onMessageReceived,
            Func<TemporaryRunResult, CancellationToken, Task> onCompletedAsync,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
        {
            if (onMessageReceived is null) 
                throw new ArgumentNullException(nameof(onMessageReceived));
            
            if (onCompletedAsync is null) 
                throw new ArgumentNullException(nameof(onCompletedAsync));
            
            return await RunCoreAsync<T, object?>(messages, async (message, ct) =>
            {
                await onMessageReceived(message, ct).ConfigureAwait(false);
                
                return null;
            }, false, (result, ct) => onCompletedAsync(result, ct), onError, options, cancellationToken).ConfigureAwait(false);
        }

        public Task<TemporaryRunResult<TResult>> RunAsync<T, TResult>(
            IAsyncEnumerable<T> messages,
            Func<T, CancellationToken, Task<TResult>> onMessageReceived,
            Func<TemporaryRunResult<TResult>, CancellationToken, Task> onCompletedAsync,
            Func<T, CancellationToken, Task>? onError = null,
            RunTemporaryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
            => RunCoreAsync(messages, onMessageReceived, true, onCompletedAsync, onError, options, cancellationToken);

        private Task<TemporaryRunResult<TResult>> RunCoreAsync<T, TResult>(
            IReadOnlyList<T> messages,
            Func<T, CancellationToken, Task<TResult>> onMessageReceived,
            bool collectResults,
            Func<TemporaryRunResult<TResult>, CancellationToken, Task> onCompletedAsync,
            Func<T, CancellationToken, Task>? onError,
            RunTemporaryOptions? options,
            CancellationToken cancellationToken) where T : class
        {
            options ??= new RunTemporaryOptions();
            if (messages is null || messages.Count == 0)
            {
                return Task.FromResult(TemporaryRunResult<TResult>.Empty(options.CorrelationId, DateTime.UtcNow));
            }

            return RunCoreAsync(EnumerateList(messages), onMessageReceived, collectResults,
                onCompletedAsync, onError, options, cancellationToken, messages.Count);
        }

#pragma warning disable CS1998 // Synchronous adapter: a known list needs no awaits.
        private static async IAsyncEnumerable<T> EnumerateList<T>(IReadOnlyList<T> messages)
        {
            for (var i = 0; i < messages.Count; i++) yield return messages[i];
        }
#pragma warning restore CS1998

        private async Task<TemporaryRunResult<TResult>> RunCoreAsync<T, TResult>(
            IAsyncEnumerable<T> messages,
            Func<T, CancellationToken, Task<TResult>> onMessageReceived,
            bool collectResults,
            Func<TemporaryRunResult<TResult>, CancellationToken, Task> onCompletedAsync,
            Func<T, CancellationToken, Task>? onError,
            RunTemporaryOptions? options,
            CancellationToken cancellationToken,
            int? knownCount = null) where T : class
        {
            if (messages is null) throw new ArgumentNullException(nameof(messages));
            if (onMessageReceived is null) throw new ArgumentNullException(nameof(onMessageReceived));
            if (onCompletedAsync is null) throw new ArgumentNullException(nameof(onCompletedAsync));
            options ??= new RunTemporaryOptions();

            // Opt-in backpressure, asynchronous sources only: bounds the elements pulled from the source that have not
            // reached a terminal state. Collection runs ignore it — their input already lives in memory, so bounding
            // the broker-side backlog gains nothing.
            SemaphoreSlim? inFlight = null;
            if (!knownCount.HasValue && options.MaxInFlightMessages.HasValue)
            {
                if (options.MaxInFlightMessages.Value < options.PrefetchCount)
                {
                    throw new ArgumentException(
                        $"MaxInFlightMessages ({options.MaxInFlightMessages.Value}) must be greater than or equal to PrefetchCount ({options.PrefetchCount}); otherwise handlers could never reach their configured concurrency.",
                        nameof(options));
                }

                inFlight = new SemaphoreSlim(options.MaxInFlightMessages.Value, options.MaxInFlightMessages.Value);
            }

            var startedUtc = DateTime.UtcNow;

            var resultsQueue = new ConcurrentQueue<TResult>();

            var correlationId = options.CorrelationId;

            var prefetchCount = options.PrefetchCount;

            var timeout = options.Timeout;

            var queuePrefixName = options.QueuePrefixName;

            var executionId = Guid.NewGuid().ToString("N");

            var eventName = typeof(T).Name.ToLower();

            var _queue = $"{queuePrefixName ?? eventName}-temp-queue-{executionId}";

            // effectiveCt = caller token + optional whole-run timeout; governs handlers and every internal wait.
            using var runTimeoutCts = options.RunTimeout.HasValue ? new CancellationTokenSource(options.RunTimeout.Value) : null;

            using var effectiveCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, runTimeoutCts?.Token ?? CancellationToken.None);
            var effectiveCt = effectiveCts.Token;

            // producerCt = effectiveCt + broker shutdown. It governs only source enumeration and publication:
            // once the connection is gone nothing else can be admitted, so a source waiting for its next element
            // must be interrupted — while handlers that already own their (acked) message keep running, exactly
            // as in the collection overloads. They are bounded only by Timeout / RunTimeout.
            using var stopCts = new CancellationTokenSource();
            using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(effectiveCt, stopCts.Token);
            var producerCt = producerCts.Token;

            using var connection = await _connectionFactory.CreateConnectionAsync($"{_queue}", effectiveCt);

            using var channel = await connection.CreateChannelAsync(cancellationToken: effectiveCt);

            var maxMessages = knownCount ?? 0;
            var producerFinished = 0;
            var sourceCompleted = false;

            await channel.QueueDeclareAsync(_queue, durable: false, exclusive: true, autoDelete: true, cancellationToken: effectiveCt);

            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefetchCount, global: false, cancellationToken: effectiveCt);

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

            var activeTasks = new ConcurrentDictionary<ulong, Task>();
            var activeHandlers = 0;

            var connectionLost = 0;

            string? shutdownReason = null;

            void TryComplete()
            {
                var terminalCount = Volatile.Read(ref processed) + Volatile.Read(ref publishFailed);

                // On connection loss undelivered messages can never arrive: stop once in-flight handlers drain.
                var endedEarly = Volatile.Read(ref connectionLost) == 1;

                if (Volatile.Read(ref producerFinished) == 1 &&
                    (terminalCount >= Volatile.Read(ref maxMessages) || endedEarly) &&
                    Volatile.Read(ref activeHandlers) == 0)
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
                    stopCts.Cancel();
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
                // Terminal state reached: hand the in-flight permit back so the producer can pull the next element.
                inFlight?.Release();
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
                    Interlocked.Increment(ref activeHandlers);
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

                    activeTasks[ea.DeliveryTag] = processingTask;
                    _ = processingTask.ContinueWith(_ =>
                    {
                        activeTasks.TryRemove(ea.DeliveryTag, out var completedTask);
                        Interlocked.Decrement(ref activeHandlers);
                        TryComplete();
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

            var consumerTag = await channel.BasicConsumeAsync(_queue, autoAck: false, consumer, cancellationToken: effectiveCt);

            // Progress reporting runs beside the run, never inside a handler, and starts before publishing so a long
            // asynchronous source is observable while it is still being read. Canceled when the run starts closing.
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(effectiveCt);

            TemporaryRunProgress SnapshotProgress() => new TemporaryRunProgress(
                correlationId, _queue, startedUtc, DateTime.UtcNow,
                Volatile.Read(ref maxMessages), Volatile.Read(ref published), Volatile.Read(ref processed),
                Volatile.Read(ref succeeded), Volatile.Read(ref failed), Volatile.Read(ref activeHandlers));

            var progressLoop = options.OnProgress is { } onProgress
                ? ReportProgressAsync(onProgress, options.ProgressInterval, SnapshotProgress, correlationId, progressCts.Token)
                : null;

            try
            {
                var index = 0;
                await foreach (var msg in ReadSourceAsync(messages, inFlight, producerCt).ConfigureAwait(false))
                {
                    // Count the element as observed before publishing: if the run is interrupted mid-publish it is
                    // reported below as unaccounted (failed) rather than silently dropped from the source.
                    if (!knownCount.HasValue) Interlocked.Increment(ref maxMessages);
                    try
                    {
                        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, _jsonOptions));
                        await channelGate.WaitAsync(producerCt).ConfigureAwait(false);
                        try
                        {
                            await channel.BasicPublishAsync("", _queue, body, producerCt);
                            Interlocked.Increment(ref published);
                        }
                        finally
                        {
                            channelGate.Release();
                        }
                    }
                    catch (OperationCanceledException) when (producerCt.IsCancellationRequested)
                    {
                        // Run interruption, not a broken message: classified by the outer handler.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        Interlocked.Increment(ref publishFailed);
                        // Never published, so no handler will ever release this element's permit: do it here.
                        inFlight?.Release();
                        runErrors.Enqueue(TemporaryRunError.FromException(TemporaryRunErrorStage.Publish, ex, _queue, messageIndex: index));
                        _logger.LogError(ex, "[RabbitFlowTemporary] Error publishing message at index {index}. CorrelationId: {correlationId}", index, correlationId);
                        await InvokeOnErrorAsync(onError, msg, cancellationToken, _logger, correlationId);
                    }
                    index++;
                }
                sourceCompleted = true;
            }
            catch (OperationCanceledException) when (producerCt.IsCancellationRequested)
            {
                var stage = Volatile.Read(ref connectionLost) == 1 ? TemporaryRunErrorStage.ConnectionLost
                    : cancellationToken.IsCancellationRequested ? TemporaryRunErrorStage.Cancellation
                    : TemporaryRunErrorStage.Timeout;
                runErrors.Enqueue(TemporaryRunError.FromMessage(stage, "The input source was interrupted before completion.", _queue));
            }
            catch (Exception ex)
            {
                // A failed source cannot hang the run: every observed element was either published (and will reach a
                // terminal state) or counted as a publish failure, so TryComplete fires once admitted handlers finish.
                // Those handlers are bounded only by Timeout / RunTimeout, like every other run.
                runErrors.Enqueue(TemporaryRunError.FromException(TemporaryRunErrorStage.Enumeration, ex, _queue));
            }
            finally
            {
                Volatile.Write(ref producerFinished, 1);
                TryComplete();
            }

            using var ctr = effectiveCt.Register(() =>
            {
                tcs.TrySetCanceled(effectiveCt);
            });

            TemporaryRunResult<TResult> finalResult = null!;

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
                // No progress call starts once the run is closing; a call already running gets a bounded window,
                // so a callback that ignores its token cannot hold the completion callback back.
                progressCts.Cancel();

                if (progressLoop != null)
                {
                    await Task.WhenAny(progressLoop, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                }

                channel.ChannelShutdownAsync -= OnShutdownAsync;

                connection.ConnectionShutdownAsync -= OnShutdownAsync;

                // After an early end, give canceled in-flight handlers a bounded window to finish so counters settle.
                // Second pass catches a task dispatched concurrently with the early end.
                for (var pass = 0; pass < 2; pass++)
                {
                    var drainTasks = activeTasks.Values.Where(task => !task.IsCompleted).ToArray();

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

                // Build the result snapshot the callback receives. Counters are stable here
                // (all handlers drained); only Errors can still grow if the callback itself throws.
                var completedUtc = DateTime.UtcNow;

                finalResult = new TemporaryRunResult<TResult>(
                    maxMessages, published, processed, succeeded, failed,
                    correlationId, _queue, startedUtc, completedUtc,
                    runErrors.ToArray(), resultsQueue.ToArray(), knownCount.HasValue ? (bool?)null : sourceCompleted);

                // Always run the completion callback
                try
                {
                    _logger.LogDebug("[RabbitFlowTemporary] Executing completion callback. Processed: {processed}, Failed: {failed}, Results: {results}, CorrelationId: {correlationId}",
                        processed, failed, resultsQueue.Count, correlationId);

                    await onCompletedAsync(finalResult, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    runErrors.Enqueue(TemporaryRunError.FromException(TemporaryRunErrorStage.Completion, ex, _queue));
                    _logger.LogError(ex, "[RabbitFlowTemporary] Error in completion callback. CorrelationId: {correlationId}", correlationId);

                    // The callback couldn't see its own failure, but the returned result should: re-snapshot with the completion error.
                    finalResult = new TemporaryRunResult<TResult>(
                        maxMessages, published, processed, succeeded, failed,
                        correlationId, _queue, startedUtc, completedUtc,
                        runErrors.ToArray(), resultsQueue.ToArray(), knownCount.HasValue ? (bool?)null : sourceCompleted);
                }

                // Late handlers may still release their slots if they ignored cancellation.
                // Their primitives are left for GC instead of being disposed under running code.
            }

            return finalResult;
        }

        // One call at a time: the next interval starts only after the previous call returns, so a slow callback
        // delays reporting instead of piling calls up. A failing callback is logged and never affects the run.
        private async Task ReportProgressAsync(
            Func<TemporaryRunProgress, CancellationToken, Task> onProgress,
            TimeSpan interval,
            Func<TemporaryRunProgress> snapshot,
            string? correlationId,
            CancellationToken ct)
        {
            while (true)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await onProgress(snapshot(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[RabbitFlowTemporary] Error in progress callback. CorrelationId: {correlationId}", correlationId);
                }
            }
        }

        private async IAsyncEnumerable<T> ReadSourceAsync<T>(IAsyncEnumerable<T> source, SemaphoreSlim? inFlight,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var enumerator = source.GetAsyncEnumerator(ct);
            Task<bool>? pendingMove = null;
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    // Backpressure gate: a permit is taken BEFORE asking the source for its next element, so a full
                    // window means MoveNextAsync is simply not called and the producer stays parked at its yield.
                    if (inFlight != null)
                    {
                        await inFlight.WaitAsync(ct).ConfigureAwait(false);
                    }

                    pendingMove = enumerator.MoveNextAsync().AsTask();
                    await WaitForSourceOperationAsync(pendingMove, ct).ConfigureAwait(false);
                    var hasNext = await pendingMove.ConfigureAwait(false);
                    pendingMove = null;
                    if (!hasNext)
                    {
                        // The permit taken for this attempt was never matched by an element.
                        inFlight?.Release();
                        yield break;
                    }
                    yield return enumerator.Current;
                }
            }
            finally
            {
                if (pendingMove != null && !pendingMove.IsCompleted)
                {
                    // Never call DisposeAsync concurrently with MoveNextAsync. A source that ignores
                    // cancellation owns that unfinished operation until it eventually settles.
                    _ = DisposeSourceAfterMoveAsync(enumerator, pendingMove);
                }
                else
                {
                    var disposal = enumerator.DisposeAsync().AsTask();
                    await WaitForSourceOperationAsync(disposal, ct).ConfigureAwait(false);
                }
            }
        }

        private static async Task WaitForSourceOperationAsync(Task operation, CancellationToken ct)
        {
            if (!operation.IsCompleted)
            {
                var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => canceled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(operation, canceled.Task).ConfigureAwait(false) != operation)
                    {
                        _ = operation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        ct.ThrowIfCancellationRequested();
                    }
                }
            }
            await operation.ConfigureAwait(false);
        }

        private async Task DisposeSourceAfterMoveAsync<T>(IAsyncEnumerator<T> enumerator, Task pendingMove)
        {
            try { await pendingMove.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "[RabbitFlowTemporary] Abandoned source operation ended."); }
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "[RabbitFlowTemporary] Deferred source disposal failed."); }
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
