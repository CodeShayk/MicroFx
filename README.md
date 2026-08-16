# <img src="https://github.com/NinjaRocks/MicroFx/blob/master/ninja-icon-16.png" alt="ninja" style="width:30px;"/> MicroFx

[![NuGet version](https://badge.fury.io/nu/MicroFx.svg)](https://badge.fury.io/nu/MicroFx) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/NinjaRocks/MicroFx/blob/master/LICENSE) [![build-master](https://github.com/NinjaRocks/MicroFx/actions/workflows/master.yml/badge.svg)](https://github.com/NinjaRocks/MicroFx/actions/workflows/master.yml) [![.NET 10](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

**A .NET microservice platform that makes a service production-grade before you write a line of platform code.**

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddMicroFx();

var app = builder.Build();
await app.RunMicroFxAsync();
```

That is a complete service. It has structured logging, distributed tracing, metrics, liveness and readiness probes on a separate management port, layered configuration with validated options, RFC 9457 error responses, correlation ids, security headers, rate limiting, and a startup banner telling you exactly what is switched on.

📖 **[Developer Guide](docs/developer-guide.md)** — start here. It goes from "what is this" to production, with no assumed background.

---

## Why it exists

Every microservice needs the same twenty things, and every team builds them slightly differently, slightly wrongly, and slightly late. MicroFx is those twenty things, built once, with the reasoning written down.

The distinctive part is **how** capability attaches. ASP.NET Core composes through two unordered bags — `services.Add*()` and `app.Use*()` — which gives you no ordering guarantees, no lifecycle, no identity, and no way to ask "is caching on, and who turned it off?". MicroFx replaces that with a **feature model**: every capability, built-in or yours, is an identified, ordered, introspectable object with a declared lifecycle.

```
+ microfx.core            kernel
+ microfx.observability   kernel  [otlp → localhost:4317, sample=0.10]
+ microfx.health          kernel  [:8081 live,ready,startup · 6 checks]
+ microfx.api                     [v1 · openapi · reference=/openapi/reference]
+ microfx.messaging               [transport=rabbitmq · 1 cmd, 2 evt, 1 sub]
+ acme.audit                      [sink=file]
✗ microfx.featureflags            disabled by config (MicroFx:Features:…)
```

---

## What you get

| | |
|---|---|
| **Composition** | Feature model, declared ordering, four override grains, introspectable catalog |
| **Observability** | OpenTelemetry logs, traces, metrics; per-feature startup attribution |
| **HTTP** | Problem Details, validation, versioning, OpenAPI + Scalar, rate limiting, idempotency |
| **Security** | JWT with algorithm allow-list, deny-by-default, audit stream, startup posture checks |
| **Data** | EF Core, ambient transactions, **transactional outbox**, durable inbox, migration gate |
| **Messaging** | Transport-neutral commands and events, CloudEvents envelope, dedupe, retry, dead-letter |
| **Resilience** | Polly pipelines on every `HttpClient`, tenant-scoped caching, typed service clients |
| **Jobs** | Cron and interval schedules, distributed locking, staleness alarms |
| **Testing** | Transport conformance suite, feature-graph assertions, in-memory transport |
| **Analyzers** | Compile-time enforcement of the conventions that matter |

---

## Three ideas worth knowing before you start

**Opt-out, not opt-in.** Cross-cutting capability is on by default. Turning something off is a named call or a config key — visible in review, visible in the catalog, never accidental.

**The platform never silently downgrades a guarantee.** It will emulate a missing *convenience* and tell you it did — a broker without delayed delivery gets a scheduled-message store. It will **refuse to start** rather than quietly weaken a *correctness* guarantee: ask for at-least-once delivery on a transport that cannot acknowledge, and startup fails with an explanation rather than silently delivering at-most-once.

**Escape hatches at every grain.** Disable a feature, configure it, replace it wholesale, or override one service inside it. Every built-in registration uses `TryAdd`, so your registration always wins. There is no cliff where you fall off the golden path permanently.

---

## Getting started

```bash
git clone https://github.com/NinjaRocks/MicroFx.git
cd MicroFx
dotnet run --project src/MicroFx.Host.Service
```

No database, no broker, no cloud account. The in-box defaults — SQLite, in-memory transport, in-memory cache — mean it just runs.

```bash
curl localhost:8080/v1/orders/abc123        # traffic port
curl localhost:8081/health/ready            # management port
curl localhost:8081/internal/features       # what is switched on, and why
open  localhost:8081/openapi/reference      # Scalar API reference
```

`src/MicroFx.Host.Service` is a real, deployable reference service — not a sample. CI builds it, containerises it, and end-to-end tests it. If the guide and the code ever disagree, the code is right, because it compiles.

---

## Repository layout

```
src/
  MicroFx/                      the platform — kernel plus every built-in feature
  MicroFx.Messaging.RabbitMq/   transport adapter
  MicroFx.Analyzers/            compile-time conventions
  MicroFx.Host.Service/         reference service, containerised and tested
test/
  MicroFx.Tests/                          unit
  MicroFx.Host.Service.E2E.Tests/         end-to-end
  MicroFx.Messaging.RabbitMq.Tests/       broker conformance
docs/
  developer-guide.md            start here
  microfx-design.md             the design and its reasoning
  microservice-template-requirements.md
  implementation-plan.md
  implementation-status.md      what is built, what is not, and what is unverified
```

**One project holds all platform functionality**, separated by namespace. Extra assemblies exist only where a technical constraint forces one (the analyzer must target `netstandard2.0`) or where the boundary is the point (an adapter keeping its third-party dependency off everyone else's graph).

---

## Documentation

| Document | For |
|---|---|
| [Developer Guide](docs/developer-guide.md) | Learning and using the framework, from first run to production |
| [Design](docs/microfx-design.md) | How it is built and why each decision went the way it did |
| [Requirements](docs/microservice-template-requirements.md) | The specification, with traceable requirement ids |
| [Implementation Status](docs/implementation-status.md) | What is built, what is deferred, what is unverified |

---

## Contributing

The bar is the same one the platform holds itself to: `TreatWarningsAsErrors`, security analyzers as errors, XML docs on public API, and a test that would have caught the bug. Comments explain **why**, not what.

## License

MIT. See [LICENSE](LICENSE).
