# MicroFx — Phased Implementation Plan

**Document ID:** MFX-PLAN-001
**Version:** 1.0
**Date:** 2026-08-16
**Implements:** PLT-SPEC-001 v2.0 · MFX-TD-001 v1.0

---

## 0. Target repository layout

**One project for all MicroFx functionality.** Core capability is separated by **namespace**, never by assembly. The only projects outside `MicroFx` are transport/cloud **adapters** (which exist to keep their third-party dependencies off every consumer) and the Roslyn analyzer (which cannot physically live in the same assembly — see §0.1).

```
src/
  MicroFx/                              # THE core project — kernel + every built-in feature
    Features/                           #   MicroFx.Features        — kernel: descriptor, graph, contexts, lifecycle
    Hosting/                            #   MicroFx.Hosting         — AddMicroFx, RunMicroFxAsync, host-kind detection
    Core/                               #   MicroFx.Core            — ServiceMetadata, TimeProvider, serialization, ids
    Configuration/                      #   MicroFx.Configuration
    Observability/                      #   MicroFx.Observability
    Health/                             #   MicroFx.Health
    Diagnostics/                        #   MicroFx.Diagnostics
    Api/                                #   MicroFx.Api
    Validation/                         #   MicroFx.Validation
    RateLimiting/                       #   MicroFx.RateLimiting
    Idempotency/                        #   MicroFx.Idempotency
    Security/                           #   MicroFx.Security
    MultiTenancy/                       #   MicroFx.MultiTenancy
    Resilience/                         #   MicroFx.Resilience
    Caching/                            #   MicroFx.Caching         — HybridCache, in-memory L1 built in
    Persistence/                        #   MicroFx.Persistence     — EF Core, UoW/transactions, outbox, inbox
    Jobs/                               #   MicroFx.Jobs
    FeatureFlags/                       #   MicroFx.FeatureFlags
    Storage/                            #   MicroFx.Storage         — port + filesystem store
    ServiceClients/                     #   MicroFx.ServiceClients
    Messaging/                          #   MicroFx.Messaging       — envelope, pipeline, outbox, inbox, topology
      Transport/                        #   MicroFx.Messaging.Transport  — port, capabilities, negotiation
      Transport/InMemory/               #   MicroFx.Messaging.Transport.InMemory
    Testing/                            #   MicroFx.Testing         — graph assertions, transport control, conformance suite
  MicroFx.Messaging.RabbitMq/           # adapter — isolates RabbitMQ.Client
  MicroFx.Analyzers/                    # Roslyn rules MFX1xxx / MFX2xxx (see §0.1)
  MicroFx.Host.Service/                 # REFERENCE HOST — a real service with MicroFx enabled (§0.4)
    Program.cs                          #   the whole composition, ~15 lines
    Domain/                             #   Order aggregate, domain events, invariants
    Contracts/                          #   OrderPlacedV1, ReserveInventory — the published shapes
    Endpoints/                          #   IEndpointModule implementations
    Handlers/                           #   command + event handlers
    Features/                           #   ExampleCustomFeature — proves the extension contract
    Persistence/                        #   DbContext, entity config, migrations
    Jobs/                               #   a scheduled job
    Dockerfile                          #   multi-stage, chiselled, non-root
    appsettings*.json
test/
  MicroFx.Tests/                        # NUnit 4 — kernel + every built-in feature
  MicroFx.Messaging.RabbitMq.Tests/     # adapter + transport conformance (Testcontainers)
  MicroFx.Host.Service.E2E.Tests/       # end-to-end, two lanes (§0.5)
deploy/
  docker-compose.yml                    # host service + postgres + rabbitmq + otel collector
Directory.Build.props  Directory.Packages.props  global.json  .editorconfig
```

### 0.1 The two projects that are not `MicroFx`, and why

| Project | Why it cannot be a namespace inside `MicroFx` |
|---|---|
| `MicroFx.Analyzers` | **Hard technical constraint.** A Roslyn analyzer must target `netstandard2.0`, is loaded into the compiler process rather than the app, and ships under `analyzers/dotnet/cs/` in the package. It cannot be the same assembly as a `net10.0` runtime library. It is referenced by `MicroFx` as an analyzer asset, so consumers still get the rules from the single package reference. |
| `MicroFx.Messaging.RabbitMq` | **Dependency isolation, and your explicit instruction.** Folding it in would put `RabbitMQ.Client` on the dependency graph of every service, including ones with no messaging at all — and would defeat the point of the transport port existing. |

**`MicroFx.Testing` is folded into the core project** as the `MicroFx.Testing` namespace. It carries no third-party dependency — graph assertions, in-memory transport control, and the conformance suite are plain code over types the core already owns. Testcontainers-based fixtures, which *would* have dragged a heavy dependency in, live in the adapter's own test project where they belong.

### 0.2 Keeping one project from becoming a tangle

A single assembly removes the compiler's ability to enforce boundaries, so the boundaries are enforced by test instead — from phase 1, not retrofitted:

- **Internals stay internal.** Each feature namespace exposes only its ports, options, and public contracts; implementation types are `internal sealed`. `InternalsVisibleTo` is granted to `MicroFx.Tests` only.
- **An architecture test asserts the namespace graph**: a feature namespace may reference `MicroFx.Core`, `MicroFx.Features`, and other features' *public* surface — never another feature's internals. This is the same rule the multi-project layout would have given for free, expressed as a failing test rather than a failing build.
- **`PublicAPI.Shipped.txt`** tracks the whole assembly's public surface, so an accidental `public` on an implementation detail is a reviewed diff.

### 0.3 Future adapters (not in this plan's scope)

`MicroFx.Aws` (Secrets Manager, SSM, S3, DynamoDB lock) and `MicroFx.Caching.Redis` (distributed L2) follow the same rule as RabbitMQ: separate **only** to isolate a third-party dependency, never to split MicroFx's own functionality. Their ports and in-box defaults live in the core namespaces above.

Two capabilities that might have looked like adapters are deliberately **not**:

| Capability | Where it lives | Why not an adapter |
|---|---|---|
| **EF Core persistence + transactions** | Core, `MicroFx.Persistence` | The outbox is defined by atomicity; a port with an in-memory stand-in would let a service ship believing it had guarantees it did not. The core takes `EntityFrameworkCore.Relational` only — no driver — so the dependency cost is small and the service picks its own provider. |
| **In-memory cache (L1)** | Core, `MicroFx.Caching` | A complete, correct cache with no infrastructure. `MicroFx.Caching.Redis` adds a distributed L2 behind the same `HybridCache` surface — a capacity upgrade, not a correctness one, and no cache-consuming code changes. |

### 0.4 `MicroFx.Host.Service` — the reference host

A **real, runnable, deployable service with MicroFx enabled**, in `src` rather than a `samples` folder — because a sample is something that rots, while a project the CI builds, containerises, and end-to-end tests cannot.

It serves four purposes at once:

| Purpose | How |
|---|---|
| **Proof the platform composes** | It is the vehicle for PLT-SPEC-001 §8 acceptance criteria. AC-01…AC-50 are asserted against this service, not against a hypothetical one. |
| **Executable documentation** | `Program.cs` is the quickstart. If the README and this file disagree, the file is right, because it compiles. |
| **Dogfooding pressure** | Every awkwardness in the feature contract shows up here first. A platform whose own reference service needs an escape hatch has a design problem worth knowing about early. |
| **The e2e target** | The container it produces is what §0.5 exercises. |

**What it exercises** — deliberately one of everything, not a kitchen sink:

```csharp
// src/MicroFx.Host.Service/Program.cs — the whole composition
var builder = WebApplication.CreateBuilder(args);

builder.AddMicroFx(fx =>
{
    fx.Configure<PersistenceFeature>(p => p
        .UseDbContext<OrdersDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Orders")))
        .UseOutbox().UseInbox());

    fx.Configure<MessagingFeature>(m =>
    {
        m.PublishesEvent<OrderPlacedV1>();
        m.HandlesCommand<ReserveInventory, ReserveInventoryHandler>();
        m.SubscribesToEvent<OrderPlacedV1, OrderPlacedProjectionHandler>(
            s => s.WithConcurrency(4).WithPrefetch(16));
    });

    fx.AddFeature<ExampleCustomFeature>();     // proves the extension contract from outside the kernel
});

builder.Services.AddOrdersDomain();

var app = builder.Build();
await app.RunMicroFxAsync();
```

| Capability | What the host service does with it |
|---|---|
| Feature model | Registers `ExampleCustomFeature` with declared edges, a middleware stage, a lifecycle hook, and a validator |
| API | Versioned endpoints via `IEndpointModule`, validation, Problem Details, OpenAPI |
| Persistence + transactions | An `Order` aggregate, a migration, an ambient transaction spanning state change and outbox |
| Messaging | Publishes an event, handles a command, subscribes to its own event (round-trips the full path) |
| Caching | A read endpoint backed by `HybridCache` — L1 alone by default, L2 when Redis is configured |
| Jobs | One scheduled job with leader election |
| Health | Readiness reflecting database and transport |
| Roles | `MICROFX__ROLE` selects `api` / `consumer` / `relay` / `all` from the one image |

**Transport and store are configuration, not code.** The same binary runs on the in-memory transport + SQLite (default, no infrastructure) or RabbitMQ + PostgreSQL (compose/CI). That equivalence is itself an assertion — it is how AC-39 is demonstrated.

**Docker image** — multi-stage, chiselled, non-root, both ports exposed:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.*.props global.json ./
COPY . .
RUN dotnet restore --locked-mode
RUN dotnet publish src/MicroFx.Host.Service -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080 8081
ENTRYPOINT ["./MicroFx.Host.Service"]
```

`deploy/docker-compose.yml` brings up the service plus PostgreSQL, RabbitMQ (management UI on 15672), and an OTel collector, with topology applied and seed data loaded — the DEV-001 local stack, real rather than described.

### 0.5 End-to-end integration testing

`test/MicroFx.Host.Service.E2E.Tests` runs the **same test bodies** in two lanes. That is the design: a lane that proves the semantics fast, and a lane that proves the adapters honestly, sharing assertions so they cannot drift.

| Lane | Host | Infrastructure | Runtime | Runs on |
|---|---|---|---|---|
| **In-process** | `WebApplicationFactory<Program>` over the real middleware pipeline | In-memory transport, SQLite in-memory, L1 cache | seconds | Every commit |
| **Containerised** | The **built Docker image**, started by Testcontainers | PostgreSQL, RabbitMQ, Redis, OTel collector | ~2 min | PR + main |

The containerised lane starts the *image*, not the assembly — so it also covers the Dockerfile, the entrypoint, non-root permissions, port exposure, environment binding, and container-level graceful shutdown. Those break in ways no in-process test can see.

**Coverage — the scenarios worth the container cost:**

| Group | Scenarios |
|---|---|
| **Composition** | Bare `AddMicroFx()` starts and serves; feature graph resolves in expected order; `/internal/features` reports enabled/disabled/replaced; disabling via config takes effect; disabling a kernel feature fails startup; a cycle fails with the full path (AC-31…AC-37) |
| **HTTP** | Versioning; validation → 400 Problem Details with `traceId`; unmapped exception → 500 with no stack trace; rate limit → 429 + `Retry-After`; idempotent replay returns the original response; security headers present; OpenAPI served |
| **Security** | Unauthenticated → 401; under-scoped → 403; both emit audit events; cross-tenant read returns nothing and cross-tenant write is rejected (AC-11) |
| **Persistence + transactions** | Aggregate change and outbox row commit atomically; nested scope commits once; rollback discards inner work; explicit transaction under `EnableRetryOnFailure` succeeds; concurrency conflict → 409 (AC-45…AC-47) |
| **Messaging** | Publish → outbox → transport → subscriber → handler as one trace; duplicate delivery deduped; poison message dead-lettered after N attempts with history; retry backoff matches the policy with no in-process sleep; **kill the process between commit and publish, restart, event still delivered** (AC-06, AC-07, AC-24, AC-41) |
| **Transport equivalence** | The identical scenario set passes on in-memory and on RabbitMQ with only configuration differing (AC-39) |
| **Capability negotiation** | A transport configured without `ManualAcknowledgement` fails startup rather than degrading (AC-40); missing native delay engages the scheduled store and reports it |
| **Caching** | Hit/miss/invalidate with L1 only; add Redis and observe cross-instance sharing with **zero code change**; kill Redis mid-test and confirm requests still succeed (AC-49, AC-50) |
| **Health + lifecycle** | Liveness stays healthy while readiness fails on database loss and recovers (AC-04); SIGTERM to the container drains in-flight HTTP and in-flight messages with zero 5xx and zero loss (AC-12, AC-38) |
| **Observability** | One request produces correlated logs, a distributed trace spanning API → DB → transport → consumer, and RED metrics, all queryable by trace id (AC-03); startup duration attributable per feature (AC-43) |
| **Cloud-neutrality** | The whole solution restores, builds, and passes the in-process lane with **no cloud SDK package** (AC-44) |

**Two techniques that make the hard cases testable:**

- **Controllable time.** `FakeTimeProvider` is injected in the in-process lane, so retry ladders and job schedules are asserted in milliseconds rather than waited out. The containerised lane uses short real intervals for the same scenarios, confirming the fake did not lie.
- **Deliberate crashes.** The outbox crash-recovery test kills the container between commit and publish (`docker kill`, not a graceful stop) and restarts it. This is the single most valuable e2e test in the suite, because it is the one guarantee that cannot be demonstrated any other way — and the one that quietly regresses.

**Solution-wide settings** established in phase 0 and never revisited: `net10.0`, `nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, latest analysis level, deterministic builds, Central Package Management, `packages.lock.json` with `--locked-mode` restore (CD-008), source-linked symbols, XML docs on public API (QUA-007).

---

## 1. Phase table

Each phase is independently buildable, independently testable, and leaves the repository green. Phases 1–3 have **no external dependencies at all** — no broker, no database, no cloud — which is what makes them fast to build and fast to verify.

| # | Phase | Delivers | Verified by | External deps |
|---|---|---|---|---|
| **0** | Repository foundation | Retarget to `net10.0`, `Directory.Build.props`, CPM, analyzers, NUnit 4, CI workflow, solution restructure | Solution builds clean; CI green | none |
| **1** | Feature kernel | `IMicroFxFeature`, `FeatureDescriptor`, facets, contexts, discovery, graph resolution, four build passes, lifecycle with budgets, pipeline stages, catalog, banner | Graph resolution unit tests; AC-31…AC-37, AC-42, AC-43 | none |
| **2** | Kernel features | core, configuration, observability, health, diagnostics — the five non-disableable features | A bare `AddMicroFx()` host starts, serves `/health/*`, emits OTel; AC-01, AC-03, AC-44 | none |
| **3** | HTTP features | api (endpoint modules, versioning, RFC 9457, OpenAPI, headers, limits), validation, ratelimiting, idempotency | `WebApplicationFactory` end-to-end over the real pipeline; AC-14 | none |
| **4** | Security & tenancy | security (JWT, deny-by-default, policies, audit stream, field encryption), multitenancy (resolution, write guard) | AC-11; cross-tenant write rejection tests | none (test IdP in-proc) |
| **5** | Resilience, caching, clients | resilience (Polly pipelines, bulkheads, criticality, chaos hooks), **caching (HybridCache with in-memory L1 built in, `IDistributedCacheProvider` port for L2)**, serviceclients, storage (filesystem `IObjectStore`) | AC-05; CAC-001…007; cache fail-open and L1/L2-parity tests | none |
| **6** | Messaging core — part 1 | Envelope (CloudEvents), abstract topology, `IMessageTransport` + facets, `TransportCapabilities`, negotiation + startup report, **in-memory transport**, `ICommandSender`/`IEventPublisher` | AC-39 (in-memory half), AC-40; negotiation matrix tests | none |
| **7** | Messaging core — part 2 | Handler pipeline (11 middleware), inbox/dedupe, retry + dead-letter policy, scheduled-message store, claim-check, compression, message authorization, OTel messaging spans/metrics | AC-06, AC-17…AC-21, AC-24, AC-41 | none |
| **8** | Persistence & transactions | `MicroFx.Persistence` **with EF Core built in**: `IUnitOfWork`/`ITransactionScope` (ambient nesting, execution-strategy wrapping), durable outbox + relay, inbox, migration gate, audit/tenant/slow-query interceptors, domain-event dispatch, optimistic concurrency | AC-07; TXN-001…010; outbox crash-recovery tests | SQLite (in-proc) + PostgreSQL (Testcontainers) |
| **9** | Jobs & flags | jobs (background, scheduling, distributed lock, leader election, staleness), featureflags (OpenFeature, fallback, metrics) | AC-13; single-execution-across-replicas tests | none |
| **10** | RabbitMQ adapter | `MicroFx.Messaging.RabbitMq`: connections/channels, confirms, quorum queues, DLX/DLQ, TTL retry ladder, topology assertion, direct reply-to, broker metrics | Conformance suite; AC-25…AC-30, AC-39 (full) | RabbitMQ (Testcontainers) |
| **11** | Testing & analyzers | `MicroFx.Testing` namespace (graph assertions, transport control, conformance suite); `MicroFx.Analyzers` project (MFX1xxx/MFX2xxx) | Analyzer unit tests; MFX2001 clean across core | none |
| **12** | E2E hardening & docs | Containerised e2e lane complete: crash-recovery, drain, transport equivalence, capability negotiation, cloud-neutrality; `deploy/docker-compose.yml`; README rewrite; migration guide | Full AC matrix green in both lanes; README quickstart works verbatim | Postgres, RabbitMQ, Redis (Testcontainers) |

**`MicroFx.Host.Service` is not a phase — it grows with every phase.** Introducing it at the end would mean discovering the feature contract's awkwardness after the contract is frozen, which is precisely backwards. It is created in phase 2 as soon as there is something to host, and each phase adds the slice it just built plus the e2e tests for it:

| Phase | What the host service gains | E2E lane |
|---|---|---|
| 2 | Exists. Bare `AddMicroFx()`, health endpoints, startup banner, `/internal/features` | In-process |
| 3 | Versioned endpoints, validation, Problem Details, OpenAPI | In-process |
| 4 | Auth on endpoints, tenant-scoped data | In-process (test IdP) |
| 5 | A cached read endpoint, an outbound typed client | In-process |
| 6–7 | Publishes an event, handles a command, subscribes to its own event | In-process (in-memory transport) |
| 8 | `Order` aggregate, migration, ambient transaction spanning state + outbox | **Containerised** — Postgres enters |
| 9 | A scheduled job with leader election | Containerised |
| 10 | `Dockerfile`, compose stack, RabbitMQ configuration | **Containerised — full lane, transport equivalence** |
| 11 | `ExampleCustomFeature` exercising the full extension contract | Both |
| 12 | Crash-recovery, drain, negotiation, cloud-neutrality hardening | Both |

### 1.1 Why this order

**The kernel is phase 1, before any capability.** Every feature is written against the kernel's contracts. Building capabilities first and retrofitting a composition model is how a platform acquires two composition models and keeps both forever.

**Messaging splits across phases 6–8 and 10.** Semantics (6, 7) are proven against the in-memory transport with no container in CI — fan-out, retry curves, dedupe, dead-lettering, drain ordering all verified in milliseconds. The outbox (8) needs a real database because `FOR UPDATE SKIP LOCKED` and crash-recovery semantics cannot be honestly faked. RabbitMQ (10) arrives last, by which point its job is a mapping exercise with a conformance suite waiting for it.

**Phase 8 ships EF Core in the core project, and that resolves a real tension.** The outbox pattern is *defined* by atomicity with a state change (AC-07); a port with an in-memory stand-in cannot demonstrate it, and a service would discover the difference in production. The dependency objection that keeps RabbitMQ out does not apply with the same force here, because the core takes **`Microsoft.EntityFrameworkCore.Relational` only** — no database driver. The service brings its own EF provider, so switching engines needs no MicroFx adapter at all, and a non-EF store replaces the whole feature via `Replaces` (MFX-TD-001 §10.6).

The one detail that decides whether phase 8's tests are worth anything: the default store is **SQLite in-memory, not the EF `InMemory` provider**. `InMemory` has no transactions, so every outbox and inbox test would pass for the wrong reason.

**Analyzers are phase 11, not phase 1.** `MFX2001` (built-ins must use `TryAdd`) is the rule most likely to be violated during construction — but running it from phase 1 would mean churning it alongside an unstable API. Instead, phases 1–10 follow the convention by discipline, and phase 11 verifies retroactively across the whole core in one pass. If that pass finds violations, they are one-line fixes.

---

## 2. Phase detail — the load-bearing ones

### Phase 1 — Feature kernel

The kernel is ~1,500 lines and everything else depends on it, so it gets specified rather than sketched.

**Types to build**

| Area | Types |
|---|---|
| Contract | `IMicroFxFeature`, `FeatureDescriptor`, `HostKinds`, `BuiltIn` (id constants) |
| Facets | `IPipelineFeature`, `IEndpointFeature`, `IFeatureLifecycle`, `IConfigurationFeature`, `IFeatureValidator` |
| Contexts | `FeatureBuildContext`, `FeatureConfigurationContext`, `FeaturePipelineContext`, `FeatureEndpointContext`, `FeatureLifecycleContext`, `FeatureValidationContext` |
| Resolution | `FeatureRegistry`, `FeatureGraphResolver`, `ResolvedFeature`, `FeatureResolutionException` |
| Discovery | `MicroFxFeatureAttribute`, `MicroFxFeatureAssemblyAttribute`, `AssemblyFeatureScanner` |
| Catalog | `IFeatureCatalog`, `FeatureCatalogEntry`, `FeatureStartupBanner` |
| Hosting | `AddMicroFx`, `MicroFxBuilder`, `RunMicroFxAsync`, `MicroFxLifecycleHost`, `HostKindDetector` |
| Support | `ValidationReport`, `HealthContribution`, `ServiceMetadata` |

**Test coverage that matters** (the kernel's correctness is entirely about edge cases):

- Topological sort: linear chain, diamond, independent islands, deterministic tie-break under shuffled input.
- Cycle detection reports the **full path**, not just a boolean.
- Missing hard dependency distinguishes *absent* from *disabled*.
- Unknown id in `DependsOn` errors; unknown id in `Before`/`After` warns and proceeds.
- Replacement: edge inheritance, transitive chains, duplicate-replacement conflict, replacing a kernel feature.
- Disable: code, config, config-overrides-code, kernel refusal, disabling a depended-upon feature.
- Reverse-order shutdown observed via a recording feature.
- Per-feature lifecycle budget exceeded → named failure.
- Facets skipped for non-matching `HostKinds`.
- Reserved `microfx.` prefix rejected from a foreign assembly.
- Validators aggregate: three failing validators produce one report listing three problems.

**Definition of done:** a test host composed of three synthetic features with declared edges resolves, runs, and shuts down in the exact expected order, and every failure mode above produces a diagnosable message.

### Phase 6 — The transport port

The single most important design decision to get right in code, because every adapter and every messaging test depends on the shape.

**Build order within the phase**

1. `Envelope` + codec (CloudEvents 1.0 + platform extensions), `TransportMessage` with a header dictionary — the neutral wire shape.
2. Abstract topology: `MessageDestination`, `SubscriptionSpec`, `DeliveryGuarantee`, `OrderingScope`, `RetryPolicy`, `DeadLetterPolicy`.
3. `IMessageTransport`, `ITransportSubscription`, `DeliveryDisposition`, and the four optional facets.
4. `TransportCapabilities` + `CapabilityNegotiator` — the required-vs-advertised computation and its aggregated report.
5. `InMemoryTransport` over bounded `Channel<T>`: per-consumer-group fan-out, manual ack semantics, redelivery on abandon, a controllable clock for delay, and deliberate **capability toggles** so tests can simulate a transport that lacks confirms, or lacks native dead-lettering, and assert the core's emulation and refusal paths.
6. `ICommandSender`, `IEventPublisher`, `MessagingFeature` with its declaration lambda.

**The capability-toggle design in step 5 is the phase's real deliverable.** Without it, the negotiation logic in `CapabilityNegotiator` is untestable until a second real transport exists — which would be phase 10, far too late to discover the port is the wrong shape.

**Definition of done:** the negotiation matrix from MFX-TD-001 §9.4 is a parameterised test, with one case per row asserting satisfied / emulated-and-reported / startup-failure.

### Phase 10 — RabbitMQ adapter

Because phases 6–8 are complete, this phase writes no messaging semantics — only mapping.

| Adapter component | Maps |
|---|---|
| `RabbitMqTransport` | `PublishAsync` → confirmed, mandatory, persistent publish; `SubscribeAsync` → consumer channel with QoS |
| `RabbitMqTopologyMapper` | Abstract destinations/consumer groups → exchanges/quorum queues/bindings/DLX/DLQ/retry ladder |
| `RabbitMqTopologyProvisioner` | `TopologyMode.Assert` via passive declare (BRK-009); `Provision` allowed only in Development/test |
| `RabbitMqConnectionProvider` | Connection per role, channel per consumer, recovery, heartbeats, credential provider on reconnect |
| `RabbitMqRequestReply` | `amq.rabbitmq.reply-to` |
| `RabbitMqMetricsSource` | Management HTTP API → queue depth, oldest-message age, consumer count |
| `RabbitMqTransportFeature` | Assembly-attribute-discovered feature declaring `Replaces = null`, `DependsOn = [messaging]` |

Advertised capabilities: everything **except** `Transactions` and `NativeDelayedDelivery` — the latter deliberately absent (BRK-010), which engages the adapter's own TTL ladder as its `RetryLater` implementation.

**Definition of done:** the phase-6 conformance suite passes unmodified against RabbitMQ, and the phase-7 messaging semantics suite passes unmodified with only the transport swapped (AC-39).

---

## 3. Cross-cutting practices

| Practice | Applied from |
|---|---|
| Every built-in feature registers with `TryAdd*` only | Phase 1 |
| Every public type carries XML docs | Phase 1 |
| Every feature ships its `IFeatureValidator` where preconditions exist | Phase 2 |
| `[LoggerMessage]` source-generated logging; no interpolated log strings | Phase 2 |
| `TimeProvider` injected; no `DateTime.Now`/`UtcNow` | Phase 1 |
| Options bound through `ctx.AddValidatedOptions<T>()`; never `IConfiguration` reads at runtime | Phase 1 |
| `ConfigureAwait` not required (no sync-context library code), but no sync-over-async anywhere | Phase 1 |
| Public API tracked in a `PublicAPI.Shipped.txt` so breaking changes are a visible diff | Phase 2 |

---

## 4. Risks specific to implementation

| Risk | Mitigation |
|---|---|
| The transport port's shape is wrong, discovered at phase 10 | In-memory transport with **capability toggles** (phase 6) exercises every negotiation path before a real broker exists |
| Kernel API churn invalidates features built on it | Phase 1 ships with three synthetic features and a golden-order snapshot test; the contract is frozen before phase 2 |
| Feature graph becomes a hidden coupling web | `DependsOn` reserved for genuine hard dependencies (analyzer `MFX1004`); the golden snapshot makes every new edge a reviewed diff |
| One core project grows into an untestable tangle | Namespace-per-feature, implementation types `internal sealed`, and an architecture test asserting no feature reaches another feature's internals — only ports and the kernel (§0.2). The compiler cannot enforce this in one assembly, so a test does, from phase 1. |
| Cloud-neutral core silently regresses | A CI job restores and tests the core with cloud packages excluded (AC-44) |
| Messaging tests become slow and get skipped | Phases 6–7 run entirely in-memory in milliseconds; containers appear only in phases 8 and 10. The e2e in-process lane runs on every commit; the containerised lane on PR and main. |
| The reference service becomes a stale sample nobody runs | It lives in `src`, is built and containerised by CI, and is the vehicle for every acceptance criterion — a broken host service is a red build, not a stale folder |
| E2E suite becomes flaky and gets muted | `FakeTimeProvider` removes sleep-based waiting from the in-process lane; the containerised lane uses explicit readiness gates rather than fixed delays; any `Thread.Sleep`/`Task.Delay` in a test is an analyzer error |
| The two e2e lanes drift apart | Both lanes execute the **same test bodies** against a shared abstraction over the host; a scenario cannot be fixed in one lane without the other |

---

## 5. Suggested execution granularity

Phases 0–2 are best delivered together as one working increment (a service that starts, serves health, and emits telemetry is the first thing worth looking at). After that, each phase is a natural commit boundary.

| Increment | Phases | Outcome |
|---|---|---|
| **I** | 0, 1, 2 | `AddMicroFx()` composes a running, observable, healthy host with an introspectable feature graph — **and `MicroFx.Host.Service` runs it** |
| **II** | 3, 4, 5 | A complete HTTP service: versioned, validated, secured, tenanted, resilient, cached |
| **III** | 6, 7 | Full messaging semantics on the in-memory transport, no infrastructure |
| **IV** | 8, 9 | EF Core persistence with real transactions, durable outbox/inbox, jobs, flags — containerised e2e lane opens |
| **V** | 10 | RabbitMQ in production shape; Docker image and compose stack; transport equivalence proven |
| **VI** | 11, 12 | Test harness, analyzers, e2e hardening, documentation |
