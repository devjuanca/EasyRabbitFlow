# Thumbnails — Temporary queues (`IRabbitFlowTemporary`)

Illustrates **on-demand worker pools backed by a temporary RabbitMQ queue**. A request arrives with N image-resize jobs; EasyRabbitFlow creates a queue on the fly, fans the jobs out to internal workers, drains the queue, and tears the topology down when the batch finishes. Nothing about the queue, exchange, or consumer survives the request.

## What it demonstrates

- **`IRabbitFlowTemporary.RunAsync<T, TResult>`** — returns a rich run summary and collects per-job results once the batch finishes (await-completion endpoint).
- **`IRabbitFlowTemporary.RunAsync<T>`** — fires the batch and returns immediately while the workers keep draining (fire-and-forget endpoint).
- **Bounded concurrency** — `PrefetchCount` controls how many jobs run in parallel.
- **Per-job timeout** — `Timeout` cancels a stuck worker without killing the whole batch.
- **Error callback** — failed jobs are surfaced to `onError` so the endpoint can log / persist them.

## When to reach for this

- One-shot bursts of work that does **not** justify a hosted consumer or pre-declared topology (image processing, on-demand report rendering, ad-hoc data validation).
- Workloads where the caller needs to know the batch finished before responding.
- Pipelines that already persist their output elsewhere and just need parallel execution.

## Flow

```
HTTP request                                   Temporary queue
[job1, job2, ..., jobN]  ─────►  RunAsync   ─►  thumbnails_<guid>  ─►  workers (PrefetchCount)
                                                                          │
                                                                          ▼
                                                                  per-job onMessageReceived
                                                                          │
                                                                          ▼
                                                                  TemporaryRunResult collected
                                                                          │
                                                                          ▼
HTTP response  ◄─────  onCompletedAsync  ◄───────────────────────────────┘
```

## Endpoints

| Method | Path | What it does |
|--------|------|--------------|
| POST | `/thumbnails` | Run the batch and **await** completion; returns the per-job result list. |
| POST | `/thumbnails/fire-and-forget` | Kick off the batch and return 202 immediately; results are logged only. |

See [Thumbnails.http](Thumbnails.http) for ready-to-run requests.
