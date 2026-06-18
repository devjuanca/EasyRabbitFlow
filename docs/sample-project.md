## Sample Project

`sample/RabbitFlowSample` is a runnable ASP.NET Core API that demonstrates every library feature with self-contained modules under `Samples/` (each has its own README, `.http` file, and topology):

| Module | What it demonstrates |
|--------|----------------------|
| `Notifications/` | Fanout exchange, retry policies, dead-letter envelope + reprocessor, DI lifetimes |
| `Orders/` | Topic exchange with `*` / `#` wildcard bindings |
| `Payments/` | Dead-letter replicas (audit + alerting feeds from the same DLX) |
| `SupportTickets/` | Priority queues (`MaxPriority` + `PublishOptions.Priority`) |
| `Thumbnails/` | Temporary queues: `TemporaryRunResult`, per-job `Timeout`, whole-run `RunTimeout`, fire-and-forget |

It also exposes `GET /health` (the native health check, monitoring queues + consumer presence) and `GET /diagnostics/queues` (unified `QueueState` snapshot of all sample queues).

### Option A — Aspire (recommended): full observability

With Docker (or Podman) running:

```bash
dotnet run --project sample/RabbitFlowSample.AppHost
```

The Aspire AppHost starts a RabbitMQ container (management plugin included), launches the sample API wired to it, and prints the **dashboard URL** in the console. From the dashboard:

- **Resources** — RabbitMQ (with its management UI link) and the sample API (with its Swagger link).
- **Traces** — fire any endpoint from Swagger (e.g. `POST /orders`) and watch the HTTP request span flow into the `publish` span and then into each queue's `process` span, stitched by the W3C trace context traveling in AMQP headers.
- **Metrics** — select the `rabbitflow-sample` resource and browse the `easyrabbitflow.*` instruments (publish/consume counters, processing-duration histogram).

The AppHost injects the RabbitMQ connection string and the OTLP endpoint automatically — no configuration needed.

### Option B — Standalone

Start a broker manually and run the API:

```bash
docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management
dotnet run --project sample/RabbitFlowSample
```

The sample falls back to `localhost:5672` with `guest`/`guest`, and skips the OTLP exporter when no collector endpoint is configured. Swagger is served at the root URL printed in the console; the RabbitMQ management UI lives at `http://localhost:15672`.
