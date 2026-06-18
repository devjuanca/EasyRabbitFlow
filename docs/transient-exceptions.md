## Transient Exceptions and Custom Retry Logic

EasyRabbitFlow distinguishes between **transient** failures (worth retrying) and **permanent** failures (drop straight to the dead-letter queue). The same set is honored by both the in-handler `RetryPolicy` and the [Dead-Letter Reprocessor](dead-letter.md#dead-letter-reprocessor).

### Exceptions auto-recognized as transient

| Exception type | When it fires |
|---|---|
| `RabbitFlowTransientException` **and any derived type** | Explicitly thrown by your consumer to signal a retryable error |
| `OperationCanceledException` / `TaskCanceledException` | Per-attempt timeout (`ConsumerSettings.Timeout`), cooperative cancellations, `HttpClient.Timeout` |
| `TimeoutException` | Classic timeout signal from drivers and clients |
| `HttpRequestException` with **no response** | Connection refused, DNS failure, socket reset — the request never reached a server |
| `HttpRequestException` with status **408, 429, 502, 503, 504** | Rate limiting and upstream/gateway unavailability — waiting helps |

Classification is **inheritance-aware** and also inspects the **inner-exception chain** (up to 10 levels): an `InvalidOperationException` wrapping an `HttpRequestException(429)` is still transient. Anything else is treated as **permanent** and routed to the dead-letter queue without further retry.

> **HTTP rate limits and gateway errors are covered for free.** A `429` or `503` thrown by `HttpClient` (e.g. via `EnsureSuccessStatusCode()`) is recognized automatically — no wrapping needed. A `404` or `400` stays permanent.

### Marking your own errors as transient

If a failure is transient but doesn't fall into the auto-recognized set (a database deadlock, a provider-specific error code…), catch the original exception and rethrow it wrapped in `RabbitFlowTransientException` — or throw your own subclass of it, which is equally recognized:

```csharp
using EasyRabbitFlow.Exceptions;

public class PaymentConsumer : IRabbitFlowConsumer<PaymentEvent>
{
    public async Task HandleAsync(PaymentEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessPaymentAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new RabbitFlowTransientException("Payment gateway temporarily unavailable (503).", ex);
        }
        // Any other exception → no retry, sent to dead-letter queue
    }
}
```

**Example — HTTP failures need no wrapping:**

```csharp
public class NotificationConsumer : IRabbitFlowConsumer<NotificationEvent>
{
    private readonly HttpClient _http;

    public NotificationConsumer(HttpClient http) => _http = http;

    public async Task HandleAsync(NotificationEvent message, RabbitFlowMessageContext context, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync("https://api.example.com/notify", message, cancellationToken);

        // 429 / 503 / 502 / 504 / 408 → HttpRequestException recognized as transient automatically.
        // Connection refused / DNS failure (no response) → also transient automatically.
        // 400 / 401 / 404 / 500 → permanent, goes to the dead-letter queue.
        response.EnsureSuccessStatusCode();
    }
}
```

**How the reprocessor decides:** when a message is dead-lettered, the failure is classified at capture time and recorded in the envelope as an `isTransient` flag — honoring inheritance and the HTTP rules above. The reprocessor re-enqueues only messages whose flag is `true`. Envelopes written by older library versions (without the flag) fall back to exact exception type-name matching (`RabbitFlowTransientException`, `OperationCanceledException`, `TaskCanceledException`).

### Exception types reference

| Exception | Purpose |
|-----------|---------|
| `RabbitFlowTransientException` | User-facing marker. Throw it from your handler to signal a retryable error to both the in-handler retry policy and the dead-letter reprocessor. |
| `RabbitFlowException` | General library error |
| `RabbitFlowOverRetriesException` | Thrown internally when all retry attempts are exhausted |

