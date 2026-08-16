# MicroFx — Implementation Status

**Document ID:** MFX-STATUS-001
**Last updated:** 2026-08-16
**Tracks:** MFX-PLAN-001 (implementation plan) · MFX-TD-001 (design) · PLT-SPEC-001 (requirements)

---

## 1. Summary

| | |
|---|---|
| **Phases complete** | **0–12 — all planned phases** |
| **Solution state** | Release build clean, zero warnings, `TreatWarningsAsErrors` across six projects |
| **Tests** | **258 passing, 2 skipped, 0 failing** — stable across repeated runs |
| **Features implemented** | 19 of 19 built-ins, plus the RabbitMQ transport adapter |
| **Verified running** | Yes — HTTP, messaging, outbox crash recovery, jobs, Scalar, port isolation |
| **Not verified** | **The RabbitMQ adapter and the container image have never been executed** — see §8 |

```
Increment I    ██████████  phases 0,1,2    complete
Increment II   ██████████  phases 3,4,5    complete
Increment III  ██████████  phases 6,7      complete
Increment IV   ██████████  phases 8,9      complete
Increment V    ██████████  phase 10        complete (unverified — no broker available)
Increment VI   ██████████  phases 11,12    complete
```

---

## 2. What exists

```
src/
  MicroFx/                       kernel + 19 built-in features + MicroFx.Testing
  MicroFx.Messaging.RabbitMq/    transport adapter
  MicroFx.Analyzers/             6 Roslyn rules, netstandard2.0
  MicroFx.Host.Service/          reference service, containerised
test/
  MicroFx.Tests/                          189 tests
  MicroFx.Host.Service.E2E.Tests/          67 tests
  MicroFx.Messaging.RabbitMq.Tests/         2 tests (skip without a broker)
docs/
  developer-guide.md             ← new: novice-to-production guide
  microfx-design.md
  microservice-template-requirements.md
  implementation-plan.md
  implementation-status.md
deploy/
  docker-compose.yml             host + PostgreSQL + RabbitMQ + collector
  otel-collector.yaml
```

---

## 3. Phase 10 — RabbitMQ adapter ✅ (built, **not executed**)

| Component | Delivered |
|---|---|
| `RabbitMqOptions` | Validated; refuses plaintext AMQP, requires an ascending retry ladder |
| `RabbitMqConnectionProvider` | Connection per role, channel per consumer, recovery, cluster failover |
| `RabbitMqTopologyMapper` | Destinations → exchanges, consumer groups → queues, filters → routing keys |
| `RabbitMqTransport` | Confirmed mandatory publish, QoS, dispositions, TTL retry ladder, passive assertion |
| `RabbitMqTransportFeature` | Assembly-attribute discovered; a package reference is the whole installation |

### One design correction

The design document (§13.1) said the adapter would advertise **`NativeDelayedDelivery = false`**, on the grounds that Amazon MQ forbids the delayed-message plugin, and that the core's scheduled store would then supply the delay.

That is the wrong reading of the flag. It means *"this transport handles delay"*, not *"the broker has a plugin"*. The adapter implements `ITransportScheduler` with a TTL holding-queue ladder, so it **does** handle delay — and advertising otherwise would force every service using RabbitMQ to enable persistence purely to retry a message.

The adapter therefore advertises the capability and implements the facet. **MFX-TD-001 §13.1 should be amended.**

### The retry ladder, and why it is shaped the way it is

Each rung is a **fanout** exchange in front of a TTL queue. Fanout matters: a message entering a rung carries the *target queue name* as its routing key, so on expiry the default exchange routes it straight back. A direct exchange would try to route on that key and the message would never reach the rung at all.

---

## 4. Phase 11 — Test harness and analyzers ✅

### `MicroFx.Testing` (in the core project)

| Component | Purpose |
|---|---|
| `TransportConformanceSuite` | Eight checks that a transport does what its flags claim |
| `ConformanceReport` | Reads as a diagnosis, not a boolean |
| `FeatureGraphAssertions` | `AssertOrder`, `AssertEnabled`, `AssertDisabled`, `AssertReports`, `Snapshot` |

The conformance suite is itself tested against the in-memory transport, including a deliberately **dishonest** transport that advertises confirms without honouring them. A suite that has never run against a transport known to be correct cannot distinguish "the adapter is broken" from "the suite is broken".

### `MicroFx.Analyzers`

| Rule | Severity | |
|---|---|---|
| `MFX1001` | Error | Reserved `microfx.` prefix from a foreign assembly |
| `MFX1003` | Warning | Blocking call in `Configure` |
| `MFX1010` | Warning | `HttpClient` constructed directly |
| `MFX1011` | Warning | Ambient clock instead of `TimeProvider` |
| `MFX1022` | Error | Domain event published to a transport |
| `MFX2001` | Error | *(platform-internal)* Built-in feature must use `TryAdd` |

Shipped as an analyzer asset of the `MicroFx` package, with release tracking so a new or changed rule is a reviewed diff rather than a surprise build break for consumers.

---

## 5. Phase 12 and additions ✅

| Item | State |
|---|---|
| **Developer guide** | 21 sections, novice to production, written for community adoption |
| **README** | Rewritten around what the framework does and why |
| **Scalar API reference** | Served on the management port, Development-gated, "try it" hidden outside Development |
| **Lock files** | `packages.lock.json` per project; CI restores with `--locked-mode` |
| **CI** | Cloud-neutrality gate, **transport-isolation gate**, container build, broker conformance job |
| **Compose stack** | Host + PostgreSQL + RabbitMQ + collector, hardened (`read_only`, `cap_drop: ALL`) |

---

## 6. Two bugs the tooling found in its own platform

Both were found by the tools built in phase 11, within minutes of those tools existing.

### 6.1 The conformance suite caught a dishonest capability flag — on the in-memory transport

The in-memory transport **advertised `NativeDelayedDelivery`** but never implemented `ITransportScheduler`. The delayed-delivery check skipped rather than passing, which surfaced the inconsistency immediately.

That flag drives a real decision: the core reads it to decide whether to emulate delay with a scheduled store. A transport claiming it without providing the facet would have left retries silently unimplemented on that path. The transport now implements the facet, and the claim is true.

**The first thing the conformance suite did was catch a lie in the platform's own reference transport.** That is the strongest argument for its existence.

### 6.2 The analyzer caught an untestable clock read — in the distributed lock

`MFX1011` fired on `InProcessDistributedLock.Handle.RenewAsync`, which read `DateTimeOffset.UtcNow` instead of the injected `TimeProvider`. Lease renewal was therefore **untestable without waiting in real time** — and lease expiry is precisely the behaviour worth testing in a lock.

The same method also had a pointless conditional whose branches were identical, which the fix removed.

---

## 7. Security posture — phases 10–12

| Control | Implementation |
|---|---|
| **Plaintext AMQP refused** | Startup error outside Development. Credentials and every message body would otherwise cross the network in the clear. |
| **Broker credentials warned in config** | Anything readable from a config endpoint, a crash dump, or a deployment manifest should not hold a password. |
| **Quorum queues by default** | A non-replicated classic queue loses every message it holds when its node dies; mirrored queues are removed upstream. |
| **Topology asserted, not created** | Passive declare in production. Application-side declaration is how estates acquire drifted, undocumented objects nobody dares delete. |
| **Unroutable messages captured** | Every exchange has an alternate exchange feeding a monitored queue. An unroutable message is always a defect; dropping it silently is how the defect survives for months. |
| **`reject-publish`, never `drop-head`** | Discarding the oldest business message to make room is data loss disguised as back-pressure. |
| **AMQP headers bounded and type-checked** | Header values arrive from a shared broker; a hostile frame must not become an unbounded allocation. |
| **Broker names sanitised and hash-suffixed** | Truncation alone could make two destinations collide on one queue, silently merging two subscribers' traffic. |
| **Credentials stripped from diagnostics** | The catalog reports host and port, never the URI. |
| **Scalar gated like the document** | Protecting the schema and then serving a UI that reads it would achieve nothing. "Try it" is hidden outside Development. |
| **Compose hardened** | `read_only`, `cap_drop: ALL`, `no-new-privileges`, management port unpublished. |
| **Lock files + `--locked-mode`** | A build cannot silently acquire a different transitive dependency than the one reviewed. |
| **Transport-isolation CI gate** | Fails if `RabbitMQ.Client` reaches the core graph — the separation the adapter exists to provide. |

---

## 8. What is **not** verified

This section is the one to read before deploying anything.

### The RabbitMQ adapter has never been executed

**Docker was unavailable throughout development.** The adapter compiles, is reviewed, and is covered by a conformance suite — but no message has ever passed through it. Specifically unverified:

- Publishing, confirmations, and the `mandatory` / alternate-exchange path
- Consumption, QoS, acknowledgement, and rejection semantics
- The TTL retry ladder — including the fanout-plus-default-exchange routing trick, which is the subtlest part of the adapter and the most likely to be wrong
- Passive topology assertion and the provisioning path
- Connection recovery and cluster failover

CI contains a `broker-conformance` job with a RabbitMQ service container that will run the suite on the first push. **Treat the adapter as unproven until that job is green.**

#### First CI run — two defects found, fixed, still unverified locally

The first `broker-conformance` run failed exactly where this section predicted, and the failure was real rather than environmental:

1. **The conformance suite never provisioned the topology it used.** It invents destinations and consumer groups at run time and subscribed to them directly. The in-memory transport conjures a destination on first use, so the suite passed there; a broker does not, and every subscribe returned `404 NOT_FOUND — no queue`. The suite now provisions each destination and subscription through `ITransportTopologyProvisioner` before subscribing, which also means it now exercises the topology facet rather than only the messaging one. This was a defect in the *suite*, and it had been masking itself: a certification suite that only ever passed against the transport that needs no certification.

2. **`ScheduleAsync` could not express a delayed publish.** It required a `microfx-target-queue` header and threw without one. That header exists for a *redelivery*, which must return to one consumer group's queue. A delayed publish has not reached anyone yet and must fan out to every subscriber when it matures — so it now waits in a per-destination holding queue whose dead-letter settings expire it onto the destination's exchange, with `x-dead-letter-routing-key` set explicitly because dead-lettering otherwise preserves the holding queue's own name as the key and the message would land in the unroutable queue.

Both fixes are **compiled and reviewed but not executed** — Docker remained unavailable, so the 256 local tests still exercise only the in-memory path. The same caveat as above applies unchanged: treat the adapter as unproven until `broker-conformance` is green.

### The container image has never been built

The Dockerfile and compose stack are written and reviewed; `docker build` has never run locally.

The first CI run failed here too, and the cause is worth recording because it fails quietly: the restore layer copied every project manifest **except** the analyzer's. NuGet does not treat a missing project as an error — it logs `Skipping project ... because it was not found` and carries on, producing an incomplete assets file that only fails later, during publish, as an unrelated-looking missing-package error. The layer now copies all three manifests and their lock files, and restores with `--locked-mode` so the image is built from exactly the dependency graph CI reviewed.

### The containerised e2e lane does not exist

Durable-store tests run against **SQLite** rather than PostgreSQL in a container. The outbox semantics under test are provider-independent, and the crash-recovery test genuinely restarts the host — but a PostgreSQL run is still outstanding.

---

## 9. Remaining deferrals

| Gap | Reason |
|---|---|
| Request/reply over messaging (`REQ-*`) | Facet defined, no implementation; deferred until a real use case. Its own requirement warns against using it where HTTP would do. |
| Event archive and replay (`EVT-011`) | Archive consumer group not wired. The design's advice — ship the archive from day one, defer the tooling — is not yet followed. |
| Claim-check offload, payload compression | Envelope carries the fields; no codec wired |
| Message-level authorization | Envelope carries the token; validation deferred until the outbox has a signing story |
| `MicroFx.Aws`, `MicroFx.Caching.Redis` | Ports exist with in-box defaults; adapters unwritten |
| URL versioning via `Asp.Versioning` | Package referenced, host uses literal route groups |
| ETags, pagination, bulkheads, hedging, chaos hooks | API-012/013, RES-005/007/009 |
| External PDP, field encryption, mTLS | SEC-004 partial, SEC-009, SEC-007 |
| Prometheus scrape endpoint | No stable package release; OTLP satisfies the MUST |
| `/internal/dump` | An unauthenticated heap dump is an exfiltration primitive; needs an authorization story first |
| Automatic domain-event draining on `SaveChanges` | The projector is explicit today |
| Leader election as a long-running primitive | `ILeaderElector` defined; singleton jobs use the lock directly |
| Golden-snapshot feature-graph test | `Snapshot()` exists; no golden file checked in yet |

---

## 10. Cumulative test inventory — 258 passing, 2 skipped

| Suite | Count | Covers |
|---|---|---|
| `FeatureGraphResolverTests` | 24 | Ordering, determinism, cycles, replacement, identity |
| `CompositionTests` | 9 | Composition, `TryAdd` substitution, lifecycle order, aggregated validation |
| `SecretRedactorTests` | 14 | Key-name and value-shape redaction |
| `FileSystemObjectStoreTests` | 23 | Path traversal, size bounds, atomic writes |
| `SecurityValidationTests` | 8 | Ways of shipping an unintentionally open service |
| `CacheKeyBuilderTests` | 16 | Tenant scoping, separator forgery |
| `TenantResolverTests` | 17 | Claim/header/route sources, injection-shaped values |
| `EnvelopeCodecTests` | 24 | Malformed input, forged attempts, header forgery |
| `CapabilityNegotiationTests` | 14 | The full negotiation matrix |
| `MessageTypeRegistryTests` | 7 | Unregistered names never resolve |
| `RetryPolicyTests` | 7 | Growth, capping, overflow, clamping, jitter |
| `InboxStoreTests` | 7 | Atomic admission under 64-way contention |
| `OutboxStoreTests` | 11 | Claiming, lease expiry, concurrency, rollback invisibility |
| `EfInboxStoreTests` | 5 | Durable dedupe across contexts |
| **`ConformanceSuiteTests`** | **4** | **The suite itself, including a dishonest transport** |
| `CompositionEndToEndTests` | 15 | Startup, probes, catalog, config gating |
| `HttpPipelineEndToEndTests` | 25 | Validation, headers, correlation, idempotency, caching, limits |
| `MessagingEndToEndTests` | 10 | Round trip, dedupe, retry ladder, dead-letter, unknown type |
| `PersistenceEndToEndTests` | 9 | Atomicity, **crash recovery**, durable dedupe, transactions, jobs |
| `ManagementPortIsolationTests` | 8 | Real sockets; endpoints unreachable from traffic |
| `RabbitMqConformanceTests` | 2 | **Skipped** — no broker available locally |

---

## 11. Where to go next

**Immediately, on first CI run:** confirm the `broker-conformance` job and the container build both pass. Until then, §8 stands.

**Then, in rough order of value:**

1. **`MicroFx.Aws`** — Secrets Manager and SSM first, since they unblock the secret-store warnings that currently fire in every production deployment.
2. **The containerised e2e lane** — the outbox tests against PostgreSQL.
3. **`MicroFx.Caching.Redis`** — the L2 tier, which needs no core change.
4. **Event archive** — the design is right that you cannot retroactively archive events you never captured, so the archive consumer group should land before the replay tooling.
5. **The remaining API polish** — versioning, ETags, pagination.

The platform is feature-complete against its specification. What remains is adapters, verification, and polish — none of which changes the shape of anything already built.
