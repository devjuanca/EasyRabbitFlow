using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EasyRabbitFlow.Services;
using EasyRabbitFlow.Settings;

namespace RabbitFlowSample.Samples.WebhookIngest;

// Owns the infinite source and the long-lived temporary run.
//
//   HTTP request ──WriteAsync──▶ bounded Channel(64) ──ReadAllAsync──▶ IRabbitFlowTemporary.RunAsync
//                                                                        │  MaxInFlightMessages = 16
//                                                                        └─▶ temporary queue ──▶ 4 handlers
//
// Backpressure chain: when 16 events are pulled and not yet processed, the run stops reading the
// channel; when the channel holds 64 events, WriteAsync blocks; when a request has waited 500 ms,
// the endpoint answers 429. Nothing is silently dropped anywhere in the chain.
public sealed class WebhookIngestPipeline : BackgroundService
{
    public const int BufferCapacity = 64;
    public const int MaxInFlightMessages = 16;
    private const int HandlerConcurrency = 4;

    // How long a single temporary run lives before the source is ended on purpose. The run then drains
    // and completes normally (SourceCompleted = true) and a fresh one starts over the same channel.
    // Rotation keeps TemporaryRunResult.Errors from growing forever; it never cancels handlers.
    private static readonly TimeSpan SessionLength = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AcceptWait = TimeSpan.FromMilliseconds(500);

    private readonly Channel<WebhookEvent> _buffer = Channel.CreateBounded<WebhookEvent>(new BoundedChannelOptions(BufferCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true
    });

    private readonly IRabbitFlowTemporary _temporary;
    private readonly ILogger<WebhookIngestPipeline> _logger;

    private long _accepted, _rejected, _processed, _failed, _runs;
    private DateTime? _currentRunStartedUtc;
    private WebhookRunSummary? _lastCompletedRun;

    public WebhookIngestPipeline(IRabbitFlowTemporary temporary, ILogger<WebhookIngestPipeline> logger)
    {
        _temporary = temporary;
        _logger = logger;
    }

    // Called by the HTTP endpoint. Returns false when the buffer stayed full for AcceptWait: the caller
    // gets a 429 and retries later, which is how backpressure reaches the webhook sender.
    public async Task<bool> TryAcceptAsync(WebhookEvent evt, CancellationToken requestCt)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(requestCt);
        wait.CancelAfter(AcceptWait);

        try
        {
            await _buffer.Writer.WriteAsync(evt, wait.Token);
            Interlocked.Increment(ref _accepted);
            return true;
        }
        catch (OperationCanceledException) when (!requestCt.IsCancellationRequested)
        {
            Interlocked.Increment(ref _rejected);
            return false;
        }
    }

    public WebhookIngestStatus GetStatus() => new(
        Interlocked.Read(ref _accepted),
        Interlocked.Read(ref _rejected),
        _buffer.Reader.Count,
        BufferCapacity,
        MaxInFlightMessages,
        Interlocked.Read(ref _processed),
        Interlocked.Read(ref _failed),
        Interlocked.Read(ref _runs),
        _currentRunStartedUtc,
        _lastCompletedRun);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _runs);
            _currentRunStartedUtc = DateTime.UtcNow;
            var sessionEndsUtc = _currentRunStartedUtc.Value + SessionLength;

            try
            {
                var result = await _temporary.RunAsync(
                    ReadSessionAsync(sessionEndsUtc, stoppingToken),
                    onMessageReceived: HandleAsync,
                    onError: (evt, _) =>
                    {
                        // The event was acknowledged before its handler ran, so this is the last place to
                        // persist or alert on it. The sample only logs.
                        _logger.LogWarning("[WebhookIngest] Event {EventId} ({Type}) from {Provider} failed and will not be retried.", evt.EventId, evt.Type, evt.Provider);
                        return Task.CompletedTask;
                    },
                    options: new RunTemporaryOptions
                    {
                        QueuePrefixName = "webhook-ingest",
                        PrefetchCount = HandlerConcurrency,
                        MaxInFlightMessages = MaxInFlightMessages,
                        Timeout = TimeSpan.FromSeconds(5)
                        // No RunTimeout: an infinite source has no "whole run" duration.
                    },
                    cancellationToken: stoppingToken);

                var endReason = stoppingToken.IsCancellationRequested ? "application shutdown"
                    : result.SourceCompleted == true ? "session rotation"
                    : result.Errors.FirstOrDefault()?.Stage.ToString() ?? "unknown";

                _lastCompletedRun = new WebhookRunSummary(result.QueueName, result.StartedUtc, result.CompletedUtc, result.SourceCompleted,
                    result.TotalMessages, result.SucceededMessages, result.FailedMessages, result.Errors.Count, endReason);

                _logger.LogInformation("[WebhookIngest] Run ended ({Reason}). Observed={Observed}, Succeeded={Succeeded}, Failed={Failed}, Errors={Errors}",
                    endReason, result.TotalMessages, result.SucceededMessages, result.FailedMessages, result.Errors.Count);

                if (result.SourceCompleted != true && !stoppingToken.IsCancellationRequested)
                {
                    // Typically ConnectionLost: events still in the channel survive; events already in the
                    // temporary queue did not (best-effort semantics). Back off briefly before reconnecting.
                    await Task.Delay(RestartDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Connection/setup failures throw before a run result exists (broker down at startup, etc.).
                _logger.LogError(ex, "[WebhookIngest] Run could not start; retrying in {Delay}.", RestartDelay);
                await Task.Delay(RestartDelay, stoppingToken);
            }
        }
    }

    // The infinite source, sliced into sessions. Reading is pull-based: with the in-flight window full,
    // the run does not call MoveNextAsync, this method stays parked at its await, and the channel fills.
    private async IAsyncEnumerable<WebhookEvent> ReadSessionAsync(DateTime sessionEndsUtc, [EnumeratorCancellation] CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.CancelAfter(sessionEndsUtc - DateTime.UtcNow);

        while (true)
        {
            WebhookEvent evt;
            try
            {
                // A canceled ReadAsync never consumes an item: rotation cannot lose an event.
                evt = await _buffer.Reader.ReadAsync(session.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Session over: end the source normally. The run drains its handlers and completes.
                yield break;
            }

            yield return evt;
        }
    }

    private async Task HandleAsync(WebhookEvent evt, CancellationToken ct)
    {
        // Simulate the real work (verify signature, upsert, notify). Events whose type ends with ".fail"
        // throw so the onError path and the failure counters can be observed.
        await Task.Delay(250, ct);

        if (evt.Type.EndsWith(".fail", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _failed);
            throw new InvalidOperationException($"Simulated processing failure for {evt.EventId}.");
        }

        Interlocked.Increment(ref _processed);
        _logger.LogInformation("[WebhookIngest] Processed {EventId} ({Type}) from {Provider}", evt.EventId, evt.Type, evt.Provider);
    }
}
