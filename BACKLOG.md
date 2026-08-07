# Backlog

Deferred work items, ordered by priority. Each entry records why it was deferred and the agreed
shape if it ever gets picked up, so the context survives between development cycles. Priority
reflects estimated value-to-effort for library users — none of these is a commitment.

---

## 1. Parked-message handler hook (`IParkedMessageHandler`) — **Priority: Medium**

*Deferred 2026-06-27 (8.0.0-rc.4 cycle).*

A callback abstraction fired by the dead-letter reprocessor when a message is parked or discarded.

**Why deferred:** the common case is covered without new API surface — clients consume the parking
queue (`{queue}-deadletter-parking`) with a normal consumer. What shipped instead:
`DeadLetterFinalAction` (`Park`/`Discard`), `ParkingMessageTtl`, lazy parking-queue creation, and the
`easyrabbitflow.messages.discarded` metric.

**Why it ranks highest here:** the workaround only covers `Park`. On `Discard`, the message is gone
and the only trace is a counter — a handler firing on discard would be the last chance to log,
persist, or alert on the dropped payload. That is a genuine observability gap for production users,
not a convenience.

**Agreed constraints if picked up:**
- The handler MUST also fire on `Discard` (it is the only remaining trace of the message).
- Decide global handler vs per-consumer `IParkedMessageHandler<TConsumer>` before implementing.

---

## 2. Graceful publisher shutdown (`IAsyncDisposable` on `RabbitFlowPublisher`) — **Priority: Low**

*Deferred 2026-07-29 (v8.2.0 cycle).*

Implement `IAsyncDisposable`: drain the confirm-channel pool (dispose each pooled channel), then
close the global publisher connection with a clean AMQP `close-ok`.

**Why deferred:** it is cosmetic. Host shutdown currently drops the connection abruptly; the broker
cleans it up via TCP close, with no functional impact — at most a connection closed "unexpectedly"
line in broker logs on each deploy. Explicitly NOT a DI-safety guard: `RabbitFlowPublisher` is
`internal sealed` and registered singleton by `AddRabbitFlow`, so consumers cannot re-register it
scoped, and third-party `IRabbitFlowPublisher` implementations manage their own resources.

**Why above the TTL item:** trivial to implement, no API surface change, and it establishes the
disposable lifecycle that item 3 needs for its timer. Do this first if either is picked up.

**Agreed shape:** `DisposeAsync` drains `confirmChannelPool` (`TryTake` loop, dispose each, swallow
errors), then closes/disposes `globalConnection` under the existing semaphore; idempotent via a
disposed flag. Ship in a minor release.

---

## 3. Idle-TTL eviction for the publisher channel pool (`PooledChannelIdleTimeout`) — **Priority: Low**

*Deferred 2026-07-29 (v8.2.0 cycle).*

Opt-in idle timeout that closes pooled confirm-channels after a period of publisher inactivity.

**Why deferred:** with the default cap of 8, lingering idle channels cost under ~1 MB broker-side per
app instance and generate zero traffic (heartbeats are per-connection). Lazy eviction (timestamp on
return, discard on rent) cannot work — no publishes means no rents, so nothing evicts; the only
correct design is a timer sweep, which needs the disposable lifecycle from item 2. Precedent:
Spring AMQP's `CachingConnectionFactory` caches channels with no idle eviction by default.

**Revisit when:** users combine a raised cap (32–64), spiky traffic, and many app instances per
broker.

**Agreed shape:** opt-in `PooledChannelIdleTimeout` on `PublisherConnectionOptions`, default infinite
(current behavior), implemented as a timer sweep. Depends on item 2. Ship in a minor release.
