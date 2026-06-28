## Temporary Batch Processing

`IRabbitFlowTemporary` is designed for **fire-and-forget batch workflows** — process a collection of messages through RabbitMQ with automatic queue creation and cleanup.

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

Both overloads coexist. The completion callback receives the **same** `TemporaryRunResult` that `RunAsync` returns, so it has access to the full counters, `CorrelationId`, `QueueName`, `Duration`, `Success`, and `Errors` — not just the processed/failed counts.

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
       ◄──────────────────── Delete temp queue
       │                     Call onCompleted()
       │
  return TemporaryRunResult
```

`TemporaryRunResult` exposes `TotalMessages`, `PublishedMessages`, `ProcessedMessages`,
`SucceededMessages`, `FailedMessages`, `Success`, `Duration`, and `Errors`. Each entry in
`Errors` records the run stage where the failure happened (`Publish`, `Deserialize`, `Process`,
`Timeout`, `Cancellation`, `Completion`); publish-stage errors also carry the `MessageIndex`
of the failed input message. The `RunAsync<T, TResult>` overload returns
`TemporaryRunResult<TResult>`, adding a `Results` collection with the values returned by
successful handlers. You can still ignore the returned task result in fire-and-forget flows
and rely on `onCompleted` / `onCompletedAsync` for background bookkeeping.

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `PrefetchCount` | ushort | `1` | Parallel message processing (>0) |
| `Timeout` | TimeSpan? | `null` | Per-message timeout |
| `RunTimeout` | TimeSpan? | `null` | Whole-run timeout: cancels in-progress handlers, reports pending messages as failed, and returns the partial result |
| `QueuePrefixName` | string? | `null` | Custom prefix for the temp queue name |
| `CorrelationId` | string? | `Guid` | Correlation ID for tracing/logging |

The run also ends early — instead of waiting forever — if the underlying connection or channel
is shut down mid-run (broker restart, network failure): in-flight handlers are drained, the
undelivered messages are reported as failed with a `ConnectionLost` error entry, and the
partial result is returned.
