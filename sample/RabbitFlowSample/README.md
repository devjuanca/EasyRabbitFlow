# EasyRabbitFlow Sample API

ASP.NET Core minimal-API host that exercises the main features of [EasyRabbitFlow](../../README.md). Each scenario lives in its own folder under [`Samples/`](./Samples) with a dedicated README, a self-contained `*.http` file, the consumers it needs, and a `*Module.cs` that wires the consumers and endpoints into the host.

## Samples

| Folder | Pattern | Highlights |
|--------|---------|------------|
| [Notifications](Samples/Notifications/README.md) | Fanout exchange | Pub/sub broadcast, retry policy, dead-letter envelope + reprocessor, DI lifetimes |
| [Orders](Samples/Orders/README.md) | Topic exchange | `*` and `#` routing wildcards, multiple bindings sharing one exchange |
| [Payments](Samples/Payments/README.md) | Dead-letter replicas | Same DLX, multiple replica queues (audit + live alerting) |
| [SupportTickets](Samples/SupportTickets/README.md) | Priority queues | `MaxPriority` on declaration + `PublishOptions.Priority` per message |
| [Thumbnails](Samples/Thumbnails/README.md) | Temporary queues (`IRabbitFlowTemporary`) | On-demand worker pool, await-completion vs. fire-and-forget |

## Project layout

```
sample/RabbitFlowSample/
├── Program.cs                          # composition root: OpenAPI + RabbitFlow + module wiring
├── Common/                             # cross-sample helpers (DI demo services, fire-and-forget extension)
├── Samples/
│   ├── Notifications/
│   ├── Orders/
│   ├── Payments/
│   ├── SupportTickets/
│   └── Thumbnails/
└── README.md
```

Each `Samples/<feature>` folder ships:
- A README describing the pattern and the topology it produces.
- The events, consumers, and a `*Module` that exposes `RegisterConsumers` + `MapEndpoints`.
- A `*.http` file with realistic requests covering happy paths and failure modes.

`Program.cs` only owns shared concerns: OpenAPI, RabbitMQ host/publisher settings, DI for the demo services, and a sequence of `Module.RegisterConsumers / MapEndpoints` calls.

## Running

Prerequisites:
- .NET 8 SDK
- A local RabbitMQ broker at `localhost:5672` (default `guest`/`guest`). The simplest way:

```sh
docker run --rm -d --name rabbit -p 5672:5672 -p 15672:15672 rabbitmq:3-management
```

Then:

```sh
dotnet run --project sample/RabbitFlowSample
```

The launch profile binds the API to `https://localhost:7097` (HTTP fallback on `http://localhost:5132`). Swagger UI is available at [`https://localhost:7097/swagger`](https://localhost:7097/swagger), and the RabbitMQ management UI at [`http://localhost:15672`](http://localhost:15672) (login `guest`/`guest`) lets you watch queues fill up and dead-letter as you fire requests.

## Adding a new sample

1. Create `Samples/<NewSample>/` with the events, consumers, and a `<NewSample>Module.cs` exposing `RegisterConsumers(RabbitFlowConfigurator)` and `MapEndpoints(IEndpointRouteBuilder)`.
2. Add a `README.md` describing the pattern and a `<NewSample>.http` covering its endpoints.
3. Wire it into `Program.cs` by calling `Register` + `MapEndpoints` for the new module.
