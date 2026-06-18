# Queue Operations

## Queue State Inspection

Use `IRabbitFlowState` to query queue metadata at runtime. For health checks and dashboards, prefer `GetQueueStateAsync` — it returns everything in a **single broker round trip** (each individual method opens its own connection):

```csharp
public class HealthCheckService
{
    private readonly IRabbitFlowState _state;

    public HealthCheckService(IRabbitFlowState state)
    {
        _state = state;
    }

    public async Task<QueueState> GetQueueHealthAsync(string queueName)
    {
        // One connection, one round trip: Exists, MessageCount, ConsumerCount, IsEmpty, HasConsumers
        return await _state.GetQueueStateAsync(queueName);
    }

    public async Task<IReadOnlyList<QueueState>> GetAllQueuesHealthAsync(string[] queueNames)
    {
        // One shared connection for the whole batch
        return await _state.GetQueuesStateAsync(queueNames);
    }
}
```

`GetQueueStateAsync` does **not throw** when the queue is missing — it reports `Exists = false` with zero counts, which is what a health endpoint usually wants. The granular methods remain available and still throw on missing queues:

| Method | Returns | Description |
|--------|---------|-------------|
| `GetQueueStateAsync(queueName)` | `Task<QueueState>` | Full snapshot in one round trip: `Exists`, `MessageCount`, `ConsumerCount`, `IsEmpty`, `HasConsumers` |
| `GetQueuesStateAsync(queueNames)` | `Task<IReadOnlyList<QueueState>>` | Snapshots for several queues over a single connection, in input order |
| `IsEmptyQueueAsync(queueName)` | `Task<bool>` | Is the queue empty? |
| `GetQueueLengthAsync(queueName)` | `Task<uint>` | Number of messages in the queue |
| `GetConsumersCountAsync(queueName)` | `Task<uint>` | Number of active consumers |
| `HasConsumersAsync(queueName)` | `Task<bool>` | Does the queue have any consumers? |

---

## Queue Purging

Use `IRabbitFlowPurger` to remove all messages from queues:

```csharp
// Purge a single queue
await purger.PurgeMessagesAsync("orders-queue");

// Purge multiple queues at once
await purger.PurgeMessagesAsync(new[] { "orders-queue", "emails-queue", "notifications-queue" });
```
