## Temporary Batch Processing

`IRabbitFlowTemporary` processes a collection or an asynchronous source of messages through RabbitMQ
with automatic queue creation and cleanup. Await the result or use completion callbacks in background workflows.

**Ideal for:**

- Background jobs (PDF generation, email sending, report calculation)
- One-time batch processing (database cleanup, data migration)
- Parallel processing with configurable concurrency

### Basic Usage

```csharp
public class InvoiceService
{
    private readonly IRabbitFlowTemporary _temporary;

    public InvoiceService(IRabbitFlowTemporary temporary)
    {
        _temporary = temporary;
    }

    public async Task ProcessInvoiceBatchAsync(List<Invoice> invoices)
    {
        TemporaryRunResult run = await _temporary.RunAsync(
            invoices,
            onMessageReceived: async (invoice, ct) =>
            {
                Console.WriteLine($"Processing invoice {invoice.Id}...");
                await Task.Delay(500, ct); // Simulate work
            },
            onCompleted: run =>
            {
                Console.WriteLine($"Done! Processed: {run.ProcessedMessages}, Errors: {run.FailedMessages}");
            },
            options: new RunTemporaryOptions
            {
                PrefetchCount = 10,
                Timeout = TimeSpan.FromSeconds(30),
                CorrelationId = Guid.NewGuid().ToString()
            });

        Console.WriteLine($"Succeeded: {run.SucceededMessages}, Failed: {run.FailedMessages}");
    }
}
```

### Asynchronous Sources (`IAsyncEnumerable<T>`)

All three callback/result variants also accept `IAsyncEnumerable<T>`. The source is enumerated once,
without collecting its input into a list. Consumers start before publication, so handlers can process
earlier messages while the producer waits for its next page, cursor row or channel item.

```csharp
using System.Runtime.CompilerServices;

async IAsyncEnumerable<Invoice> ReadInvoicesAsync(
    [EnumeratorCancellation] CancellationToken ct = default)
{
    for (var page = 1; page <= 3; page++)
    {
        var invoices = await invoiceApi.GetPageAsync(page, ct);
        foreach (var invoice in invoices)
            yield return invoice;
    }
}

var run = await temporary.RunAsync(
    ReadInvoicesAsync(),
    onMessageReceived: (invoice, ct) => ProcessInvoiceAsync(invoice, ct),
    options: new RunTemporaryOptions
    {
        PrefetchCount = 4,
        RunTimeout = TimeSpan.FromMinutes(2)
    },
    cancellationToken: cancellationToken);
```

Completion requires **source exhaustion plus resolution of all observed messages**. A source waiting
for its next item keeps the run open, even if the queue is empty. For `ChannelReader.ReadAllAsync`,
the producer must complete its writer. There is no inactivity timeout, session registry or autoscaling.

- `SourceCompleted` is `true` when enumeration and enumerator disposal finish normally, `false` when
  interrupted or failed, and `null` for collection overloads. It does not indicate handler success.
- `TotalMessages` counts observed source elements. On interruption, it does not include unknown future
  elements. `Success` is false for an interrupted source, even if all observed messages succeeded.
- Source/enumerator errors appear as `Enumeration` errors with no message index. They do not invoke
  `onError`, because there is no failed message to supply. Handlers already admitted finish normally,
  bounded only by `Timeout` / `RunTimeout`, exactly as in a collection run; the run cannot hang because
  every observed element was either published or counted as a publish failure.
- Cancellation and `RunTimeout` apply while awaiting source elements as well as during processing.
  Sources should honor their enumerator token. If `MoveNextAsync` ignores cancellation, the run stops
  waiting and disposes that enumerator once its pending move settles; it never disposes concurrently
  with a pending move. Non-cooperative source work cannot be forcibly terminated. As with collection
  runs, connection/setup failures can throw before a run result is available.
- A broker connection loss interrupts a source waiting for its next element (nothing more can be
  admitted) and is reported as a `ConnectionLost` error, but it does **not** cancel handlers that are
  already running: they own an acknowledged message and their work usually does not depend on the broker.
  An element pulled from the source but not yet published when the run is interrupted is counted in
  `TotalMessages` and reported as failed, never silently dropped.
- An empty asynchronous source completes successfully and invokes the completion callback once. A null
  asynchronous source throws `ArgumentNullException`. The existing empty/null collection behavior is unchanged.
- Publication is sequential and completed handler tasks are released. `PrefetchCount` limits consumer
  processing, not the producer's broker backlog: without `MaxInFlightMessages` (below) the temporary
  queue absorbs the whole source at publish speed. Results and error details are retained in memory;
  avoid unbounded result collection and use finite sources or explicit cancellation/timeouts.

These overloads keep the existing exclusive, non-durable, best-effort temporary queue semantics,
including acknowledgement before handler execution. They do not turn webhook acceptance into durable
storage. See the runnable [catalog import sample](../sample/RabbitFlowSample/Samples/CatalogImport/README.md).

### Backpressure (`MaxInFlightMessages`)

For a source that fits comfortably in a list, the temporary queue is exactly the buffer you want and no
backpressure is needed: the run behaves like the collection overloads. The option matters when the
source is much larger than you would ever materialize, or never ends (a `Channel` fed by webhooks).
Without a bound, RabbitMQ becomes the place where the whole source is materialized, and its memory alarm
blocks every publisher on the broker, not just this run.

`MaxInFlightMessages` caps the elements pulled from the source that have not yet reached a terminal state
(processed, failed, or failed to publish). The permit is taken **before** `MoveNextAsync` is called, so
when the window is full the run simply stops asking the source for more. Because `IAsyncEnumerable<T>`
is pull-based, the producer stays parked at its `yield return`, and the pause propagates upstream: a
bounded `Channel` writer blocks, a database cursor stops advancing, a paged API is not called.

```csharp
var run = await temporary.RunAsync(
    channel.Reader.ReadAllAsync(stoppingToken),
    onMessageReceived: (evt, ct) => HandleAsync(evt, ct),
    options: new RunTemporaryOptions
    {
        PrefetchCount = 4,          // handler concurrency
        MaxInFlightMessages = 16,   // at most 16 messages pulled and not yet terminal
        Timeout = TimeSpan.FromSeconds(5)
        // no RunTimeout: an infinite source has no "whole run" duration
    },
    cancellationToken: stoppingToken);
```

- Opt-in. `null` (default) keeps the unbounded behavior. Must be at least `PrefetchCount`, otherwise
  the run throws `ArgumentException` before touching the source or the broker.
- Ignored by the collection overloads: their input already lives in memory.
- If the source is push-based and adapted through an unbounded `Channel`, the run stops pulling but the
  channel keeps growing in your process. Use `Channel.CreateBounded` so the whole chain is bounded.

#### Choosing a value

`PrefetchCount` is how many handlers work at once. `MaxInFlightMessages` is how many messages exist
between the source and the end of a handler. The difference is the reserve waiting in the temporary
queue so no handler idles while the producer fetches the next element. A good default is
**2× to 4× `PrefetchCount`**: enough reserve, negligible broker-side backlog.

| `PrefetchCount` | Reasonable `MaxInFlightMessages` |
|---|---|
| 1 | 4 |
| 4 | 8 – 16 |
| 8 | 16 – 32 |
| 16 | 32 – 64 |

- **Raise it** when the source has high latency per batch, for example a paged API where each page
  takes hundreds of milliseconds: a small window leaves handlers idle between pages. Size the window
  to cover one or two full pages (pages of 50 elements → 100 in flight). The practical ceiling is how
  much you are willing to lose on a connection drop, because everything in the temporary queue is lost.
- **Lower it** when messages are large, handlers are slow, or losing messages on a drop costs more
  than throughput. With `MaxInFlightMessages == PrefetchCount` there is no reserve: each message is
  published only when a handler is free, the maximum loss on a drop is `PrefetchCount` messages, and
  you pay a small gap between messages.
- **Never** set hundreds or thousands "just in case": that is the unbounded behavior with a false
  sense of safety. Never below `PrefetchCount`: the run rejects it.

Two signals tell you whether the value is right in production. If the temporary queue's message
count sits near `MaxInFlightMessages - PrefetchCount` for long periods, handlers are the bottleneck:
raise `PrefetchCount` or speed up the handler, a larger window will not help. If the queue is almost
always empty and handlers have gaps, the source is the bottleneck: a larger window only helps when
the source delivers in bursts.

### Infinite sources

A source that never ends keeps the run open indefinitely. Silence does not end it: while `MoveNextAsync`
is pending the run waits with no CPU cost, the connection stays alive through heartbeats, and the queue
stays declared. Practical consequences:

- Do not set `RunTimeout`; use the per-message `Timeout` only.
- Own the run in a hosted service with the application's stopping token, never inside an HTTP request.
  Endpoints only write into a bounded `Channel`; the run reads from it.
- A broker connection loss ends the run with `ConnectionLost`. Messages already in the temporary queue
  are lost (best-effort semantics); messages still in the in-process channel are not, and the next run
  picks them up. Restart the run in a loop with a short delay.
- `Errors` accumulates one entry per failed message for the whole run. For runs that live for days,
  rotate: end the source periodically (the run drains and completes normally with `SourceCompleted = true`),
  then start a new one over the same channel. Rotation ends the source, not the handlers, so nothing is lost.

See the runnable [webhook ingest sample](../sample/RabbitFlowSample/Samples/WebhookIngest/README.md).

### Error Handling with `onError`

The `onError` callback is invoked whenever a message fails to publish or fails during processing (timeout, cancellation, or exception). Use it to decide what to do with failed messages — log them, persist them, or republish to another queue:

```csharp
TemporaryRunResult run = await _temporary.RunAsync(
    invoices,
    onMessageReceived: async (invoice, ct) =>
    {
        await ProcessInvoiceAsync(invoice, ct);
    },
    onCompleted: run =>
    {
        Console.WriteLine($"Done! Processed: {run.ProcessedMessages}, Errors: {run.FailedMessages}");
    },
    onError: async (failedInvoice, ct) =>
    {
        // Store the failed message for later retry or manual review
        await _failedMessageStore.SaveAsync(failedInvoice, ct);

        // Or republish to a dead-letter queue
        await _publisher.PublishAsync(failedInvoice, "invoices-failed-queue");
    },
    options: new RunTemporaryOptions
    {
        PrefetchCount = 10,
        Timeout = TimeSpan.FromSeconds(30)
    });

if (!run.Success)
{
    Console.WriteLine($"Temporary batch completed with {run.FailedMessages} failures.");
}
```

> **Note:** If `onError` itself throws, the exception is caught and logged internally — it will not break the batch processing flow.

### Async Completion Callback

When the completion logic needs to `await` (e.g. flushing state to a database, publishing a follow-up message, calling another service), use the overload that takes `onCompletedAsync` instead of `onCompleted`. The callback receives the run's `TemporaryRunResult` and the operation's `CancellationToken`:

```csharp
TemporaryRunResult run = await _temporary.RunAsync(
    invoices,
    onMessageReceived: async (invoice, ct) =>
    {
        await ProcessInvoiceAsync(invoice, ct);
    },
    onCompletedAsync: async (result, ct) =>
    {
        await _metrics.RecordBatchAsync(result.ProcessedMessages, result.FailedMessages, ct);
        await _publisher.PublishAsync(new BatchCompleted { Total = result.ProcessedMessages, Errors = result.FailedMessages });
    });
```

Picking which overload to use:

| Use this | When |
|----------|------|
| `onCompleted: result => …` | Synchronous wrap-up (logging, in-memory counters) |
| `onCompletedAsync: async (result, ct) => …` | Wrap-up that needs to `await` I/O |

Both overloads coexist. The completion callback receives the same `TemporaryRunResult` that `RunAsync` returns, so it has access to the full counters, `CorrelationId`, `QueueName`, `Duration`, `Success`, and `Errors` — not just the processed/failed counts. (One exception: if the completion callback itself throws, `RunAsync` returns a rebuilt result that additionally carries a `Completion`-stage error entry the callback never saw.)

Note that with a `null` or empty `messages` collection, `RunAsync` returns `TemporaryRunResult.Empty` immediately and **no callback is invoked** — including `onCompleted` / `onCompletedAsync`.

### With Result Collection

```csharp
TemporaryRunResult<InvoiceResult> run = await _temporary.RunAsync<Invoice, InvoiceResult>(
    invoices,
    onMessageReceived: async (invoice, ct) =>
    {
        var result = await ProcessInvoiceAsync(invoice, ct);
        return new InvoiceResult { InvoiceId = invoice.Id, Status = "Completed" };
    },
    onCompletedAsync: async (run, ct) =>
    {
        // run.Results is an IReadOnlyList<InvoiceResult> with all collected results
        Console.WriteLine($"Processed {run.ProcessedMessages} invoices, collected {run.Results.Count} results");
        await SaveResultsAsync(run.Results);
    },
    onError: async (failedInvoice, ct) =>
    {
        await _failedMessageStore.SaveAsync(failedInvoice, ct);
    });

Console.WriteLine($"Collected {run.Results.Count} successful invoice results.");
```

### Progress Reporting (`OnProgress`)

A temporary queue is **exclusive** to the connection that declared it, so no other connection — another instance of
your service, or `IRabbitFlowState` itself — can inspect it (the broker answers `405 RESOURCE_LOCKED`). To observe a
run from outside, have the run report its own progress and store it where your other instances can read it.

The library does not care where the progress goes. The example below uses **Redis** (`StackExchange.Redis`, not a
dependency of EasyRabbitFlow) so that every replica of the service can answer a status request:

1. `POST /imports` stores a `Starting` snapshot, starts the run fire-and-forget and returns its id. The id is the run's `CorrelationId`, so every
   progress snapshot carries it.
2. `OnProgress` writes the snapshot under `runs:{id}` with a **short TTL**, renewed on every call.
3. `onCompletedAsync` overwrites the same key with the final figures and a **long TTL**.
4. `GET /imports/{id}`, on any replica, reads the key.

```csharp
// Program.cs
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("localhost:6379"));

// Any replica can start a run…
app.MapPost("/imports", async (
    IReadOnlyList<Product> products,
    IRabbitFlowTemporary temporary,
    IConnectionMultiplexer redis,
    IHostApplicationLifetime lifetime) =>
{
    var runId = $"import-{Guid.NewGuid():N}";
    var db = redis.GetDatabase();

    // The first progress report arrives one interval after the start: store a "Starting" snapshot now, so a status
    // request made right away finds the run instead of a 404.
    await db.StringSetAsync(RunKeys.For(runId), RunSnapshot.Starting(runId, products.Count), RunKeys.ProgressTtl);

    // Fire-and-forget: the response returns at once. The run is tied to the application's lifetime, not to the
    // request, so it keeps going after the response is sent.
    _ = temporary.RunAsync(
        products,
        ImportProductAsync,
        onCompletedAsync: (result, _) =>
            db.StringSetAsync(RunKeys.For(runId), RunSnapshot.From(result), RunKeys.ResultTtl),
        options: new RunTemporaryOptions
        {
            CorrelationId = runId,
            PrefetchCount = 4,
            ProgressInterval = RunKeys.ProgressInterval,
            OnProgress = (progress, _) =>
                db.StringSetAsync(RunKeys.For(runId), RunSnapshot.From(progress), RunKeys.ProgressTtl)
        },
        cancellationToken: lifetime.ApplicationStopping);

    return Results.Accepted($"/imports/{runId}", new { runId });
});

// …and any replica can report on it.
app.MapGet("/imports/{runId}", async (string runId, IConnectionMultiplexer redis) =>
{
    var stored = await redis.GetDatabase().StringGetAsync(RunKeys.For(runId));

    // Never started, finished long ago, or its progress stopped renewing the key (the run or its process died).
    if (stored.IsNullOrEmpty)
    {
        return Results.NotFound();
    }

    var snapshot = JsonSerializer.Deserialize<RunSnapshot>(stored.ToString())!;

    // A running snapshot whose heartbeat is late: still in Redis, but nothing has refreshed it.
    var stale = snapshot.State == "Running" && DateTime.UtcNow - snapshot.UpdatedUtc > RunKeys.ProgressInterval * 3;

    return Results.Ok(stale ? snapshot with { State = "Stale" } : snapshot);
});

static class RunKeys
{
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    // A few intervals: if the reports stop, the key expires on its own instead of showing "Running" forever.
    public static readonly TimeSpan ProgressTtl = ProgressInterval * 6;

    public static readonly TimeSpan ResultTtl = TimeSpan.FromHours(24);

    public static string For(string runId) => $"runs:{runId}";
}

// TemporaryRunProgress and TemporaryRunResult are built by the library (no public constructor), so store your own
// shape: it is also the contract your status endpoint returns.
sealed record RunSnapshot(
    string RunId, string State, int Total, int Published, int Processed, int Succeeded, int Failed, int InFlight,
    DateTime UpdatedUtc, bool? Success = null)
{
    public static string Starting(string runId, int total) => JsonSerializer.Serialize(new RunSnapshot(
        runId, "Starting", total, 0, 0, 0, 0, 0, DateTime.UtcNow));

    public static string From(TemporaryRunProgress p) => JsonSerializer.Serialize(new RunSnapshot(
        p.CorrelationId!, "Running", p.TotalMessages, p.PublishedMessages, p.ProcessedMessages,
        p.SucceededMessages, p.FailedMessages, p.InFlightMessages, p.TimestampUtc));

    public static string From(TemporaryRunResult r) => JsonSerializer.Serialize(new RunSnapshot(
        r.CorrelationId!, "Completed", r.TotalMessages, r.PublishedMessages, r.ProcessedMessages,
        r.SucceededMessages, r.FailedMessages, 0, r.CompletedUtc, r.Success));
}
```

Notes on the example:

- **No late overwrite.** No progress call starts once the run begins to close, so the final snapshot written by
  `onCompletedAsync` is never replaced by a late `Running` one.
- **Setup failures are swallowed by `_ =`.** If `RunAsync` cannot even start (for example, the broker is
  unreachable), no progress ever replaces the `Starting` snapshot: it expires after `ProgressTtl` and the status
  endpoint answers `404`. Await the task in a `Task.Run` with a
  `try/catch` if you need to store that failure too, as the [RunMonitoring sample](sample-project.md) does.
- **Asynchronous sources work the same.** The `IAsyncEnumerable<T>` overloads take the same options.
- **Runnable version.** The sample project has a version with an in-memory store instead of Redis:
  `sample/RabbitFlowSample/Samples/RunMonitoring`.

`OnProgress` receives a `TemporaryRunProgress` with `CorrelationId`, `QueueName`, `StartedUtc`, `TimestampUtc` and the
running counters: `TotalMessages` (for asynchronous sources, the elements pulled so far), `PublishedMessages`,
`ProcessedMessages`, `SucceededMessages`, `FailedMessages` and `InFlightMessages` (handlers running right now).

How it is called:

- **Every `ProgressInterval`** (default 5 s), measured from the end of the previous call, **even when nothing changed**.
  That makes it a heartbeat: store it with a short TTL, and a `TimestampUtc` that stops advancing (or a key that
  expires) means the run — or the process running it — is gone.
- **One call at a time.** A slow callback delays the next one; calls never overlap or pile up.
- **Beside the run, not inside it.** It never blocks message processing, and an exception it throws is logged as a
  warning without affecting the run or its result.
- **Stops when the run closes.** No call starts once the run begins to close, so it never runs after the completion
  callback, which carries the final figures. The token passed in is the run's own: honor it — a call still running
  when the run closes is waited for at most 5 seconds.
- Reporting starts with the run (before publishing), so a long asynchronous source is observable while it is still
  being read. A `null` or empty collection returns immediately and reports nothing.

`OnProgress = null` (default) starts no timer and adds no overhead.

> **Why an option and not a `RunAsync` parameter?** The callbacks passed to `RunAsync` (`onMessageReceived`,
> `onError`, `onCompleted` / `onCompletedAsync`) define what the run does and receive its outcome. `OnProgress` only
> observes the run and never changes it, and it is configured together with `ProgressInterval`, so it lives in
> `RunTemporaryOptions` with the other settings that shape a run without altering its result.

### How It Works

```text
  Your Code                   RabbitMQ (Temporary)            Handler
  ─────────                   ──────────────────              ───────

  RunAsync(messages) ─────►  Create temp queue  ───────────►  onMessageReceived()
       │                     Publish all msgs                      │
       │                           │                          ┌────┴────┐
       │                           │                        success   failure
       │                           │                          │         │
       │                     Consume & process  ◄─────────────┘    onError()
       │                           │
       │                     All processed?
       │                           │ yes
       ◄──────────────────── Call onCompleted()
       │                     Temp queue auto-removed
       │                     (exclusive + auto-delete,
       │                     gone when the connection closes)
  return TemporaryRunResult
```

`TemporaryRunResult` exposes `TotalMessages`, `PublishedMessages`, `ProcessedMessages`,
`SucceededMessages`, `FailedMessages`, `SourceCompleted`, `Success`, `StartedUtc`, `CompletedUtc`, `Duration`, and `Errors`. `RunTemporaryOptions` exposes `PrefetchCount`, `Timeout`, `RunTimeout`, `MaxInFlightMessages`, `OnProgress`, `ProgressInterval`, `QueuePrefixName` and `CorrelationId`. Each entry in
`Errors` records the run stage where the failure happened (`Publish`, `Deserialize`, `Process`,
`Timeout`, `Cancellation`, `Completion`, `ConnectionLost`, `Enumeration`); publish-stage errors also carry the `MessageIndex`
of the failed input message. The `RunAsync<T, TResult>` overload returns
`TemporaryRunResult<TResult>`, adding a `Results` collection with the values returned by
successful handlers. You can still ignore the returned task result in fire-and-forget flows
and rely on `onCompleted` / `onCompletedAsync` for background bookkeeping.

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `PrefetchCount` | ushort | `1` | Parallel message processing. Must be > 0 (`0` throws `ArgumentOutOfRangeException`) |
| `Timeout` | TimeSpan? | `null` | Per-message timeout. Must be positive when set |
| `RunTimeout` | TimeSpan? | `null` | Whole-run timeout: cancels in-progress handlers, reports pending messages as failed, and returns the partial result. It also bounds the setup (connection, queue declaration, consumer registration) and, for asynchronous sources, the wait for the next element. Must be positive when set |
| `MaxInFlightMessages` | int? | `null` | Asynchronous sources only: maximum elements pulled from the source that have not reached a terminal state; the run stops pulling until a handler finishes. Must be > 0 and ≥ `PrefetchCount`. Ignored by the collection overloads. See [Backpressure](#backpressure-maxinflightmessages) |
| `OnProgress` | `Func<TemporaryRunProgress, CancellationToken, Task>?` | `null` | Periodic progress snapshot while the run is in progress, for monitoring from outside. See [Progress Reporting](#progress-reporting-onprogress) |
| `ProgressInterval` | TimeSpan | `5 s` | How often `OnProgress` is called. Must be positive |
| `QueuePrefixName` | string? | `null` | Custom prefix for the temp queue name. The queue is named `{prefix}-temp-queue-{guid}`; when `null`, the prefix defaults to the lowercased event type name |
| `CorrelationId` | string? | `Guid` | Correlation ID for tracing/logging |

The run also ends early — instead of waiting forever — if the underlying connection or channel
is shut down mid-run (broker restart, network failure): in-flight handlers are drained, the
undelivered messages are reported as failed with a `ConnectionLost` error entry, and the
partial result is returned.

> **No telemetry:** temporary runs are not instrumented — they emit no `ActivitySource` spans and don't
> count toward the [`EasyRabbitFlow` metrics](observability.md). The `TemporaryRunResult` counters are the
> observability surface for these runs.
