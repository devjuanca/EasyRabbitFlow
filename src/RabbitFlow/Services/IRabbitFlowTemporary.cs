using EasyRabbitFlow.Settings;
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

    /// <returns>The number of successfully processed messages.</returns>

    Task<int> RunAsync<T>(IReadOnlyList<T> messages, Func<T, CancellationToken, Task> onMessageReceived, Action<int, int>? onCompleted = null, RunTemporaryOptions? options = null, CancellationToken cancellationToken = default) where T : class;

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
    /// <param name="options">Optional configuration settings for the temporary queue behavior.</param>
    /// <param name="cancellationToken">Token to cancel the operation prematurely.</param>
    /// <returns>The total number of messages successfully processed.</returns>
    Task<int> RunAsync<T, TResult>(
        IReadOnlyList<T> messages,
        Func<T, CancellationToken, Task<TResult>> onMessageReceived,
        Func<int, ConcurrentQueue<TResult>, Task> onCompletedAsync,
        RunTemporaryOptions? options = null,
        CancellationToken cancellationToken = default) where T : class;
}

internal sealed class RabbitFlowTemporary : IRabbitFlowTemporary
{
    private readonly ConnectionFactory _connectionFactory;

    private readonly ILogger<RabbitFlowTemporary> _logger;

    public RabbitFlowTemporary(ConnectionFactory connectionFactory, ILogger<RabbitFlowTemporary> logger)
    {
        _connectionFactory = connectionFactory;

        _logger = logger;
    }

    public async Task<int> RunAsync<T>(
        IReadOnlyList<T> messages,
        Func<T, CancellationToken, Task> onMessageReceived,
        Action<int, int>? onCompleted = null,
        RunTemporaryOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        if (messages is null || messages.Count == 0)
        {
            return 0;
        }

        options ??= new RunTemporaryOptions();

        var correlationId = options.CorrelationId;

        var prefetchCount = options.PrefetchCount;

        var timeout = options.Timeout;

        var queuePrefixName = options.QueuePrefixName;

        var executionId = Guid.NewGuid().ToString("N");

        var eventName = typeof(T).Name.ToLower();

        var _queue = $"{queuePrefixName ?? eventName}-temp-queue-{executionId}";

        using var connection = await _connectionFactory.CreateConnectionAsync($"{_queue}", cancellationToken);

        using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        var maxMessages = messages.Count;

        await channel.QueueDeclareAsync(_queue, durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken);

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefetchCount, global: false, cancellationToken: cancellationToken);

        var processed = 0;

        var errors = 0;

        var tcs = new TaskCompletionSource<bool>();

        var consumer = new AsyncEventingBasicConsumer(channel);

        var semaphore = new SemaphoreSlim(prefetchCount);

        var activeTasks = new ConcurrentBag<Task>();

        consumer.ReceivedAsync += async (model, ea) =>
        {
            await semaphore.WaitAsync(cancellationToken);

            var json = Encoding.UTF8.GetString(ea.Body.Span);

            var message = JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web);

            if (message is null)
            {
                _logger.LogWarning("[RabbitFlowTemporary] Message is null. CorrelationId: {correlationId}", correlationId);
                semaphore.Release();
                return;
            }

            try
            {
                // Acknowledge the message to RabbitMQ
                if (channel.IsOpen)
                {
                    await channel.BasicAckAsync(deliveryTag: ea.DeliveryTag, multiple: false);
                }

                // Create the processing task that will manage its own semaphore release
                var processingTask = Task.Run(async () =>
                {
                    try
                    {
                        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;

                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts?.Token ?? CancellationToken.None);

                        try
                        {
                            await onMessageReceived(message, linkedCts.Token).ConfigureAwait(false);
                        }
                        catch (TaskCanceledException) when (timeoutCts != null && timeoutCts.Token.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref errors);
                            _logger.LogError("[RabbitFlowTemporary] Message processing timed out after {Timeout}. CorrelationId: {correlationId}", timeout, correlationId);
                        }
                        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref errors);
                            _logger.LogError("[RabbitFlowTemporary] Message processing was canceled by main Cancellation Token. CorrelationId: {correlationId}", correlationId);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref errors);
                            _logger.LogError("[RabbitFlowTemporary] Message processing was canceled. CorrelationId: {correlationId}", correlationId);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref errors);
                            _logger.LogError(ex, "[RabbitFlowTemporary] Error while processing the message. CorrelationId: {correlationId}", correlationId);
                        }
                        finally
                        {
                            // Check if we're done with all messages
                            if (Interlocked.Increment(ref processed) >= maxMessages && activeTasks.All(task => task.IsCompleted))
                            {
                                tcs.TrySetResult(true);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RabbitFlowTemporary] Unhandled exception in processing task. CorrelationId: {correlationId}", correlationId);

                        if (Interlocked.Increment(ref processed) >= maxMessages)
                        {
                            tcs.TrySetResult(true);
                        }
                    }
                    finally
                    {
                        // Always release the semaphore when the task is done
                        semaphore.Release();
                    }
                });

                activeTasks.Add(processingTask);
            }
            catch (Exception ex)
            {
                // Release semaphore on any error in the handler
                semaphore.Release();

                _logger.LogError(ex, "[RabbitFlowTemporary] Error while preparing message processing. CorrelationId: {correlationId}", correlationId);

                if (Interlocked.Increment(ref processed) >= maxMessages)
                {
                    tcs.TrySetResult(true);
                }
            }
        };

        var consumerTag = await channel.BasicConsumeAsync(_queue, autoAck: false, consumer);

        foreach (var msg in messages)
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));

                await channel.BasicPublishAsync("", _queue, body);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errors);

                _logger.LogError(ex, "[RabbitFlowTemporary] Error publishing message to exchange. CorrelationId: {correlationId}", correlationId);
            }
        }
        var _ = Task.Run(async () =>
         {
             try
             {
                 while (!cancellationToken.IsCancellationRequested)
                 {
                     // If we've processed all messages and all tasks are complete
                     if (processed >= maxMessages && (activeTasks.Count == 0 || activeTasks.All(t => t.IsCompleted)))
                     {
                         tcs.TrySetResult(true);
                         break;
                     }

                     // Check periodically
                     await Task.Delay(100, cancellationToken);
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
         }, cancellationToken);

        using var ctr = cancellationToken.Register(() =>
        {
            tcs.TrySetCanceled(cancellationToken);
        });

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            _logger.LogError("[RabbitFlowTemporary] Task was canceled by main Cancellation Token. CorrelationId: {correlationId}", correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RabbitFlowTemporary] Error while waiting for messages to be processed. CorrelationId: {correlationId}", correlationId);
        }
        finally
        {
            try
            {
                await channel.BasicCancelAsync(consumerTag, cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitFlowTemporary] Error canceling consumer. CorrelationId: {correlationId}", correlationId);
            }

            // Always run the completion callback
            try
            {
                _logger.LogDebug("[RabbitFlowTemporary] Executing completion callback. Processed: {processed}, Errors: {errors}, CorrelationId: {correlationId}",
                    processed, errors, correlationId);

                onCompleted?.Invoke(processed, errors);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitFlowTemporary] Error in completion callback. CorrelationId: {correlationId}", correlationId);
            }
        }

        return processed;
    }

    public async Task<int> RunAsync<T, TResult>(
           IReadOnlyList<T> messages,
           Func<T, CancellationToken, Task<TResult>> onMessageReceived,
           Func<int, ConcurrentQueue<TResult>, Task> onCompletedAsync,
           RunTemporaryOptions? options = null,
           CancellationToken cancellationToken = default) where T : class
    {
        if (messages is null || messages.Count == 0)
        {
            return 0;
        }

        var resultsQueue = new ConcurrentQueue<TResult>();

        options ??= new RunTemporaryOptions();

        var correlationId = options.CorrelationId;

        var prefetchCount = options.PrefetchCount;

        var timeout = options.Timeout;

        var queuePrefixName = options.QueuePrefixName;

        var executionId = Guid.NewGuid().ToString("N");

        var eventName = typeof(T).Name.ToLower();

        var _queue = $"{queuePrefixName ?? eventName}-temp-queue-{executionId}";

        using var connection = await _connectionFactory.CreateConnectionAsync($"{_queue}", cancellationToken);

        using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        var maxMessages = messages.Count;

        await channel.QueueDeclareAsync(_queue, durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken);

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefetchCount, global: false, cancellationToken: cancellationToken);

        var processed = 0;

        var tcs = new TaskCompletionSource<bool>();

        var consumer = new AsyncEventingBasicConsumer(channel);

        var semaphore = new SemaphoreSlim(prefetchCount);

        var activeTasks = new ConcurrentBag<Task>();

        consumer.ReceivedAsync += async (model, ea) =>
        {
            await semaphore.WaitAsync(cancellationToken);

            var json = Encoding.UTF8.GetString(ea.Body.Span);

            var message = JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web);

            if (message is null)
            {
                _logger.LogWarning("[RabbitFlowTemporary] Message is null. CorrelationId: {correlationId}", correlationId);
                semaphore.Release();
                return;
            }

            try
            {
                // Acknowledge the message to RabbitMQ
                if (channel.IsOpen)
                {
                    await channel.BasicAckAsync(deliveryTag: ea.DeliveryTag, multiple: false);
                }

                // Create the processing task that will manage its own semaphore release
                var processingTask = Task.Run(async () =>
                {
                    try
                    {
                        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts?.Token ?? CancellationToken.None);

                        try
                        {
                            var result = await onMessageReceived(message, linkedCts.Token).ConfigureAwait(false);
                            resultsQueue.Enqueue(result);
                        }
                        catch (TaskCanceledException) when (timeoutCts != null && timeoutCts.Token.IsCancellationRequested)
                        {
                            _logger.LogError("[RabbitFlowTemporary] Message processing timed out after {Timeout}. CorrelationId: {correlationId}", timeout, correlationId);
                        }
                        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogError("[RabbitFlowTemporary] Message processing was canceled by main Cancellation Token. CorrelationId: {correlationId}", correlationId);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogError("[RabbitFlowTemporary] Message processing was canceled. CorrelationId: {correlationId}", correlationId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[RabbitFlowTemporary] Error while processing the message. CorrelationId: {correlationId}", correlationId);
                        }
                        finally
                        {
                            // Check if we're done with all messages
                            if (Interlocked.Increment(ref processed) >= maxMessages && activeTasks.All(task => task.IsCompleted))
                            {
                                tcs.TrySetResult(true);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RabbitFlowTemporary] Unhandled exception in processing task. CorrelationId: {correlationId}", correlationId);

                        if (Interlocked.Increment(ref processed) >= maxMessages)
                        {
                            tcs.TrySetResult(true);
                        }
                    }
                    finally
                    {
                        // Always release the semaphore when the task is done
                        semaphore.Release();
                    }
                });

                activeTasks.Add(processingTask);
            }
            catch (Exception ex)
            {
                // Release semaphore on any error in the handler
                semaphore.Release();

                _logger.LogError(ex, "[RabbitFlowTemporary] Error while preparing message processing. CorrelationId: {correlationId}", correlationId);

                if (Interlocked.Increment(ref processed) >= maxMessages)
                {
                    tcs.TrySetResult(true);
                }
            }
        };

        var consumerTag = await channel.BasicConsumeAsync(_queue, autoAck: false, consumer);

        foreach (var msg in messages)
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, JsonSerializerOptions.Web));

                await channel.BasicPublishAsync("", _queue, body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitFlowTemporary] Error publishing message to exchange. CorrelationId: {correlationId}", correlationId);
            }
        }

        // After publishing all messages, start a monitoring task
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // If we've processed all messages and all tasks are complete
                    if (processed >= maxMessages && (activeTasks.Count == 0 || activeTasks.All(t => t.IsCompleted)))
                    {
                        tcs.TrySetResult(true);
                        break;
                    }

                    // Check periodically
                    await Task.Delay(100, cancellationToken);
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
        }, cancellationToken);

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            _logger.LogError("[RabbitFlowTemporary] Task was canceled by main Cancellation Token. CorrelationId: {correlationId}", correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RabbitFlowTemporary] Error while waiting for messages to be processed. CorrelationId: {correlationId}", correlationId);
        }
        finally
        {
            try
            {
                await channel.BasicCancelAsync(consumerTag, cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitFlowTemporary] Error canceling consumer. CorrelationId: {correlationId}", correlationId);
            }

            // Always run the completion callback
            try
            {
                _logger.LogDebug("[RabbitFlowTemporary] Executing async completion callback. Results: {count}, CorrelationId: {correlationId}", resultsQueue.Count, correlationId);

                await onCompletedAsync(messages.Count, resultsQueue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RabbitFlowTemporary] Error in async completion callback. CorrelationId: {correlationId}", correlationId);
            }
        }

        return processed;
    }

}
}