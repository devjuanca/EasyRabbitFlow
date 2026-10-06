# Webhook ingest: infinite source with backpressure

`POST /webhooks/{provider}` receives events whenever an external provider decides to send them:
a burst of hundreds after an outage, then hours of silence. There is no batch, no known total and
no end. The sample feeds those events into a single long-lived
`IRabbitFlowTemporary.RunAsync(IAsyncEnumerable<T>, ...)` and bounds the whole chain with
`RunTemporaryOptions.MaxInFlightMessages`.

```
HTTP request ──WriteAsync──▶ bounded Channel(64) ──ReadAllAsync──▶ RunAsync (MaxInFlightMessages = 16)
                                                                      └─▶ webhook-ingest-temp-queue-{guid} ──▶ 4 handlers
```

## Try it

Run the sample API and use [WebhookIngest.http](WebhookIngest.http) or Swagger.

1. `POST /webhooks/stripe` with one event: `202 Accepted` immediately, the log shows it processed
   250 ms later. Send another one a minute later: same thing. Silence in between costs nothing and
   does not end the run.
2. `POST /webhooks/shopify/burst?count=200&concurrency=32`: the provider replays a backlog from 32
   parallel senders. Handlers drain at about 16 events/s, so the 16-message in-flight window fills
   first, then the 64-slot buffer, and senders that wait more than 500 ms for a slot get
   `429 Too Many Requests`. Nothing is dropped silently: the sender is told to retry, which is how
   real providers behave on 429/5xx. With `concurrency=1` every event is accepted instead, because a
   single writer never waits long for a slot; backpressure still holds `buffered` at 64 while it drains.
3. `GET /webhooks/status` while the burst drains: `buffered` decreases, `processed` increases,
   `runs` stays at 1.

## What the pieces do

- **Endpoint** (`WebhookIngestModule`): validates, wraps the body in a `WebhookEvent` and writes it
  into a bounded `Channel`. Accepting means *buffered*, not *processed*. If the channel stays full
  for 500 ms the request is rejected with 429.
- **Hosted service** (`WebhookIngestPipeline`): owns the channel and keeps one temporary run alive
  with the application's stopping token. The source is `ReadAsync` on the channel, so while the
  in-flight window is full the run simply does not ask for the next event and the channel fills.
- **Rotation**: every 10 minutes the source ends on purpose. The run drains its handlers, completes
  with `SourceCompleted = true`, and a new run starts over the same channel. This keeps
  `TemporaryRunResult.Errors` from growing forever and never cancels a handler; a canceled
  `ReadAsync` never consumes an event, so rotation cannot lose one.
- **Reconnection**: if the broker drops the connection the run ends with `ConnectionLost`, the
  service waits two seconds and starts a new run. Events still in the channel survive.

## Guarantees and limits

- The temporary queue is exclusive, non-durable and auto-delete, and messages are acknowledged
  before the handler runs. Events already published when the connection drops are lost, and an
  event whose handler fails is not retried: `onError` is the last place to persist or alert on it.
  This sample is not durable webhook storage. For business events that cannot be lost, persist
  them first (or use a durable queue with a regular consumer) and use this pattern for the
  processing fan-out only.
- Backpressure is a chain of bounded waits: 16 in flight, 64 buffered, 500 ms accept wait. Tune the
  three numbers together; `MaxInFlightMessages` must be at least `PrefetchCount`. The sample uses
  4× `PrefetchCount` (4 handlers → 16 in flight), the upper end of the recommended 2×–4× range, because
  webhook events are small and arrive in bursts. See "Choosing a value" in
  [docs/temporary-processing.md](../../../../docs/temporary-processing.md).
- No `RunTimeout`: an infinite source has no "whole run" duration. The per-message `Timeout` of five
  seconds still applies.
- One instance owns the pipeline. Two replicas behind a load balancer each run their own buffer and
  queue; that is fine for independent events, not for ordered ones.
