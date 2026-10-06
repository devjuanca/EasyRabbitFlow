# Monitoring a fire-and-forget temporary run

`POST /run-monitoring/runs` starts an `IRabbitFlowTemporary` run and returns `202 Accepted` with a `runId` without
waiting for it. `GET /run-monitoring/runs/{runId}` reports how the run is going, from anywhere in the app, without
holding the run's `Task`.

How it works:

- The `runId` is the run's `CorrelationId`, so every progress snapshot carries it.
- `RunTemporaryOptions.OnProgress` writes a `TemporaryRunProgress` snapshot to `RunProgressStore` every second
  (`ProgressInterval`): total, published, processed, succeeded, failed and in-flight counters.
- `onCompletedAsync` replaces it with the final `TemporaryRunResult` (success, duration, errors).
- The status endpoint only reads the store.

Run the sample API and follow [RunMonitoring.http](RunMonitoring.http), or use Swagger. With the defaults (40 jobs of
500 ms, prefetch 2, every 7th job failing) the run takes about 10 seconds; polling shows the state go
`Starting` → `Running` → `Completed`, with `succeededMessages: 35`, `failedMessages: 5` and `success: false`.

States:

| State | Meaning |
|-------|---------|
| `Starting` | Accepted; the first progress report (after one interval) has not arrived yet |
| `Running` | Last report is recent |
| `Stale` | Still `Running`, but no report for three intervals: progress is a heartbeat, so the run or its process is gone. Only reachable with a shared store: here the dictionary dies with the process, so this demo never shows it |
| `Completed` | Final result stored by the completion callback. Stopping the broker mid-run also ends here: `success: false`, with the unprocessed messages counted as failed under a `ConnectionLost` error |
| `FailedToStart` | `RunAsync` threw before processing anything (e.g. the broker is unreachable) |

`GET /run-monitoring/runs/{runId}/broker-state` shows why a store is needed at all: it asks `IRabbitFlowState` for the
run's temporary queue and gets `405 RESOURCE_LOCKED`, because the queue is exclusive to the run's own connection.

The store is an in-memory dictionary, so this demo only works inside one process and loses its data on restart. With
several replicas, `OnProgress` would write to a shared store (Redis, a database) keyed by the `CorrelationId`, with a
TTL a few intervals long, and any replica could answer the status request. Entries are never evicted here; it is a
demo, not a pattern for long-lived processes.
