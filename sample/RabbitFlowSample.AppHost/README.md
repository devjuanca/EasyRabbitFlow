# RabbitFlowSample.AppHost

.NET Aspire orchestrator for the EasyRabbitFlow sample. Boots a RabbitMQ container (with the
management plugin), starts the sample API wired to it, and opens the Aspire dashboard where the
library's **traces** (`{destination} publish` / `{queue} process` spans, correlated end-to-end via
W3C trace context) and **metrics** (`easyrabbitflow.*` counters and the processing-duration
histogram) are visible live.

## Run

Requires Docker (or Podman) running.

```bash
dotnet run --project sample/RabbitFlowSample.AppHost
```

The console prints the dashboard URL (with a login token). From the dashboard:

- **Resources** — the RabbitMQ container (management UI exposed) and the sample API with its Swagger endpoint.
- **Traces** — fire any sample endpoint (e.g. `POST /orders`) and watch the HTTP request span
  continue into the `publish` span and, on the consumer side, the `process` span of each queue.
- **Metrics** — select the `rabbitflow-sample` resource and browse the `easyrabbitflow.*` instruments.

The sample API also exposes:

- `GET /health` — native EasyRabbitFlow health check (broker connectivity + key queues + consumer presence).
- `GET /diagnostics/queues` — unified `QueueState` snapshot of every sample queue in one broker connection.

## How the wiring works

- The AppHost passes `ConnectionStrings__rabbitmq` (an `amqp://` URI) to the sample, which parses it
  in `Program.cs` and falls back to `localhost:5672` + `guest/guest` when running standalone.
- The AppHost injects `OTEL_EXPORTER_OTLP_ENDPOINT` automatically; the sample only enables the OTLP
  exporter when that variable is present, so standalone runs don't retry against a dead endpoint.
