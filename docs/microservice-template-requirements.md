# Requirement Specification — MicroFx Microservice Platform

**Document ID:** PLT-SPEC-001
**Version:** 2.0
**Date:** 2026-08-16 (v1.0: 2026-07-29)
**Target stack:** .NET 10 (LTS) / C# 14 · cloud-neutral core · AWS as the reference deployment

> **v2.0 changes.** The platform is now composed through an explicit **feature model** (§4A) rather than a
> single `AddPlatformDefaults()` call; the core is **cloud-neutral** with cloud services reached through
> ports and adapter packages; and **messaging is transport-neutral** (§5.6) with RabbitMQ demoted from a
> mandated broker to the reference transport adapter. Package naming moves from `Platform.*` to `MicroFx`.
> The composition mechanism is specified in full in **MFX-TD-001 — MicroFx Technical Design (docs/microfx-design.md)**.

---

## 1. Purpose and Scope

### 1.1 Purpose

Define the requirements for a **microservice template** — a `dotnet new` scaffold plus a set of versioned **platform packages** — that lets a team stand up a production-grade, cloud-native service on AWS without re-implementing cross-cutting concerns.

### 1.2 Scope

| In scope | Out of scope |
|---|---|
| Project scaffold (`dotnet new` template) | Business domain logic |
| Platform NuGet packages (`Platform.*`) for cross-cutting concerns | The service mesh / cluster build itself |
| Reference implementations of HTTP, messaging, and scheduled workloads | Org-wide IAM account topology |
| IaC modules for the service's own AWS resources | Shared network (VPC, TGW) provisioning |
| CI/CD pipeline template | Central observability backend provisioning |
| Local development experience | Data platform / analytics pipelines |

### 1.3 Design principles

1. **Convention over configuration** — a new service is production-ready with zero platform code written.
2. **Opt-out, not opt-in** — cross-cutting capabilities are on by default; disabling is explicit and auditable.
3. **Everything is a feature** — every capability, built-in or custom, is composed through the same identified,
   ordered, introspectable contract. The platform has no privileged registration path a service author cannot use.
4. **Replaceable at every grain** — a capability can be disabled, configured, replaced wholesale, or overridden
   one service at a time. There is no cliff at which a service falls off the golden path permanently.
5. **Cloud-neutral core, adapters at the edge** — the core references no cloud SDK. Cloud services are reached
   through ports (`ISecretStore`, `IObjectStore`, `IMessageTransport`, `IDistributedLock`) with working in-box
   defaults for local development and test, and adapter packages for production.
6. **Thin abstractions** — a port exists where it buys testability, substitutability, or policy enforcement.
   Where wrapping an SDK buys none of those, the SDK client is exposed directly. No leaky "write once, run on
   any cloud" facade.
7. **No silent downgrades** — the platform may emulate a missing convenience and say so; it never quietly
   weakens a correctness guarantee (delivery semantics, authorization, encryption) because a provider is limited.
8. **Standards first** — OpenTelemetry, CloudEvents, OpenAPI, Problem Details (RFC 9457), OAuth 2.1, OpenFeature.
   No bespoke protocols.
9. **Upgradable** — services must be able to take platform package upgrades without code changes (SemVer +
   template sync tooling).
10. **Secure and compliant by default** — least privilege, encryption, no plaintext secrets, auditable.

### 1.4 Definitions

| Term | Meaning |
|---|---|
| **Template** | The `dotnet new` scaffold producing a new service repository |
| **MicroFx** | The platform package (`MicroFx`) containing the feature kernel and all built-in features |
| **Feature** | A unit of cross-cutting capability implementing `IMicroFxFeature`, with an id, ordering metadata, and a lifecycle. Built-in and custom features are the same kind of thing. |
| **Kernel** | The feature-composition machinery, plus the five features that cannot be disabled |
| **Port** | An interface the core defines and an adapter implements (`IMessageTransport`, `ISecretStore`, …) |
| **Adapter package** | A versioned NuGet library implementing one or more ports (`MicroFx.Messaging.RabbitMq`, `MicroFx.Aws`) |
| **In-box default** | The zero-dependency implementation of a port shipped in the core, for local development and test |
| **Service** | An instance of a deployed unit created from the template |
| **Golden path** | The supported, paved-road configuration |
| **BFF** | Backend-for-frontend |

### 1.5 Requirement conventions

Requirements are identified as `<AREA>-<nnn>` and prioritised **MUST** (M1, template GA), **SHOULD** (M2), **MAY** (backlog).

---

## 2. Stakeholders and Personas

| Persona | Need |
|---|---|
| **Service developer** | Scaffold and ship a service in hours; write only domain code |
| **Platform engineer** | Roll out policy/capability changes across all services centrally |
| **SRE / on-call** | Consistent telemetry, health, runbooks, and failure semantics everywhere |
| **Security engineer** | Enforce authn/authz, secret handling, supply-chain integrity |
| **Architect** | Consistent integration contracts and evolution rules |
| **FinOps** | Cost attribution per service, per environment |

---

## 3. Architecture Overview

### 3.1 Solution layout (template output)

```
src/
  Acme.<Service>.Api/            # Host: Minimal API / gRPC / worker
  Acme.<Service>.Application/    # Use cases, ports, validators
  Acme.<Service>.Domain/         # Entities, value objects, domain events
  Acme.<Service>.Infrastructure/ # Adapters: persistence, AWS, HTTP clients
  Acme.<Service>.Contracts/      # Public DTOs + integration events (published pkg)
tests/
  ...UnitTests/ ...ArchitectureTests/ ...IntegrationTests/ ...ContractTests/
deploy/
  terraform/                     # Service-owned AWS resources
  helm/ or ecs/                  # Workload manifests
.github/workflows/ (or .buildkite/)
docs/  (ADRs, runbook, C4 diagrams)
```

Hexagonal / ports-and-adapters. Dependency rule enforced by architecture tests (`ARCH-001`).

### 3.2 Package inventory

**One project.** `MicroFx` contains the feature kernel, every built-in feature, and the test harness. Cross-cutting concerns are separated by **namespace**, not by assembly — a distinction that costs nothing at runtime (an unused feature registers nothing) and removes fifteen versioning surfaces, fifteen CI surfaces, and a packaging decision per concern.

| Package | Contents |
|---|---|
| **`MicroFx`** | Feature kernel (§4A) + all built-in features, one namespace each: `Core`, `Configuration`, `Observability`, `Health`, `Diagnostics`, `Api`, `Validation`, `RateLimiting`, `Idempotency`, `Security`, `MultiTenancy`, `Resilience`, `Caching`, `Persistence`, `Messaging` (+ `Messaging.Transport`, `Messaging.Transport.InMemory`), `Jobs`, `FeatureFlags`, `Storage`, `ServiceClients`, `Testing`. Zero cloud SDK references. |
| `MicroFx.Analyzers` | Roslyn analyzers enforcing platform conventions at compile time (Appendix B of MFX-TD-001). Separate **only** because an analyzer must target `netstandard2.0` and ship under `analyzers/dotnet/cs/`; it is referenced by `MicroFx` as an analyzer asset, so one package reference delivers the rules. |

**Adapter packages** implement ports the core defines. They exist for exactly one reason — to keep a third-party dependency off the graph of every service that does not use it — and never to split MicroFx's own functionality. A service references only the adapters it deploys with.

| Package | Ports implemented |
|---|---|
| `MicroFx.Messaging.RabbitMq` | `IMessageTransport`, `ITransportTopologyProvisioner`, `ITransportRequestReply`, `ITransportMetricsSource` |
| `MicroFx.Aws` | `ISecretStore` (Secrets Manager), `IConfigurationSourceProvider` (SSM, AppConfig), `IObjectStore` (S3), `IDistributedLock` (DynamoDB lease), `IInboxStore` (DynamoDB) |
| `MicroFx.Caching.Redis` | `IDistributedCacheProvider` (adds distributed L2 behind the built-in `HybridCache`), `IDistributedLock` |

**Persistence and caching are built in, not adapters.** EF Core ships in `MicroFx.Persistence` (DAT-000) because the transactional outbox is defined by atomicity and cannot be honestly stood in for; the core takes `EntityFrameworkCore.Relational` only, so the service picks its own EF provider and no MicroFx adapter is needed to change database engine. In-memory L1 caching ships in `MicroFx.Caching` (CAC-001) as a complete, correct cache; Redis adds a distributed tier behind the same surface. Non-EF stores replace the persistence feature outright (DAT-002).

Packages are independently versioned but released as a coherent **bill of materials (BOM)** consumed via `Directory.Packages.props` (Central Package Management).

**Every port has a working in-box default** in the core — environment-variable secrets, filesystem object store, in-memory message transport, in-memory outbox/inbox, in-process lock — so the core builds, runs, and tests with no external dependency. Outside the `Development` environment, each in-box default still in use emits a startup **warning** naming the adapter package that should replace it (a service running the in-memory transport in production is an incident, not a configuration choice).

### 3.3 Supported workload archetypes

The template MUST support selection via `dotnet new acme-service --type <x>`:

| Type | Host | Typical AWS runtime |
|---|---|---|
| `http-api` | ASP.NET Core Minimal API | ECS Fargate / EKS |
| `grpc` | ASP.NET Core gRPC | EKS |
| `worker` | Generic Host `BackgroundService` | ECS Fargate / EKS |
| `consumer` | RabbitMQ consumer host | ECS Fargate / EKS |
| `lambda` | AWS Lambda (Native AOT preferred) | Lambda |
| `bff` | Minimal API + YARP + session | ECS Fargate |

---

## 4. Functional Requirements — Template & Scaffolding

| ID | Priority | Requirement |
|---|---|---|
| TPL-001 | M | Distributed as a NuGet template package (`Acme.Templates.Microservice`) installable via `dotnet new install`. |
| TPL-002 | M | Parameterised by: service name, workload type, persistence choice (`postgres`/`dynamodb`/`none`), messaging (`on`/`off`), auth mode, AWS region, owning team, cost centre. |
| TPL-003 | M | Generated solution MUST build, pass all tests, and run locally with a single command (`make up` / `dotnet run --project ...`) with **zero** manual edits. |
| TPL-004 | M | Generated repo includes: README, runbook skeleton, ADR-0001 (bootstrap), `CODEOWNERS`, PR template, `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`. |
| TPL-005 | M | Generated repo includes a working CI pipeline and IaC that provisions the service's own AWS resources. |
| TPL-006 | S | `service.yaml` catalog descriptor (Backstage-compatible) emitted with ownership, tier, dependencies, SLOs. |
| TPL-007 | S | Template sync tooling allows an existing service to pull forward template changes with a reviewable diff/PR. |
| TPL-008 | S | Template exposed through the internal developer portal (Backstage scaffolder) in addition to the CLI. |
| TPL-009 | M | Scaffolded code contains no TODO placeholders required for the service to run. |
| TPL-010 | C | Optional Native AOT profile for `lambda` and `worker` types. |

---

## 4A. Functional Requirements — Feature Model (Composition)

The mechanism by which capability attaches to a service host. Every requirement in §5 is delivered *as a feature* through this model. Specified in full in MFX-TD-001 §4–§7.

### 4A.1 The feature contract

| ID | Pri | Requirement |
|---|---|---|
| FEA-001 | M | A capability is a **feature**: a type implementing `IMicroFxFeature`, exposing a `FeatureDescriptor` (stable string id, ordering metadata, activation metadata) and a `Configure` method that runs during host build. |
| FEA-002 | M | Built-in and custom features MUST use the identical contract. The platform has no privileged registration path unavailable to a service author. |
| FEA-003 | M | Additional behaviour is contributed through **optional facets** — `IPipelineFeature`, `IEndpointFeature`, `IFeatureLifecycle`, `IConfigurationFeature`, `IFeatureValidator` — so a feature carries only the concepts it uses. |
| FEA-004 | M | Feature ids are strings so they can be referenced from configuration and from packages that do not reference each other. The `microfx.` prefix is reserved; a non-platform assembly using it fails startup (and is flagged by analyzer `MFX1001`). |
| FEA-005 | M | `Configure` MUST be free of I/O and blocking calls; startup work belongs in `IFeatureLifecycle.StartingAsync`, where it is ordered, budgeted, traced, and attributable. Enforced by analyzer `MFX1003`. |

### 4A.2 Composition and ordering

| ID | Pri | Requirement |
|---|---|---|
| FEA-010 | M | A single entry point composes the service: `builder.AddMicroFx()` (optionally with a configuration lambda) plus `app.RunMicroFxAsync()`. With no lambda this yields a complete, production-grade service. |
| FEA-011 | M | Composition runs in ordered passes — discover → resolve → configuration sources → build → validate → starting → pipeline/endpoints → started — so a feature can contribute configuration sources that another feature's options bind from. |
| FEA-012 | M | Features declare ordering by **dependency**, not by call sequence: `DependsOn` (hard), `Before`/`After` (soft), with `Order` then id as deterministic tie-breaks. The resolved order MUST be identical across runs and machines. |
| FEA-013 | M | The graph is topologically sorted. A cycle is a startup error reporting the **full cycle path**. A missing or disabled hard dependency is a startup error naming the dependent feature and distinguishing "absent" from "explicitly disabled". |
| FEA-014 | M | HTTP middleware order is declared via a fixed `PipelineStage` enum owned by the platform, not by statement sequence in `Program.cs`. A service author cannot reorder authentication relative to authorization. |
| FEA-015 | M | Management and diagnostic endpoints are mapped onto a **separate endpoint builder** bound to the management port, so exposing them publicly requires deliberately using the wrong object (HLT-001). |
| FEA-016 | M | Lifecycle shutdown runs in **reverse** dependency order within the drain budget, so "cancel consumers → drain in-flight → close connections → flush telemetry" is guaranteed rather than coincidental (HLT-004). |
| FEA-017 | M | Each lifecycle phase is budgeted per feature; a feature exceeding its budget fails startup naming itself. |
| FEA-018 | S | Features declare `SupportedHosts` (Web / Worker / Serverless). Facets that do not apply to the current host kind are **skipped with a debug log**, not failed — the same feature set composes an API host and a consumer host. |

### 4A.3 Discovery

| ID | Pri | Requirement |
|---|---|---|
| FEA-020 | M | Built-in features are registered from a static in-code registry — no reflection, deterministic, AOT- and trim-safe. |
| FEA-021 | M | External features are discovered by **assembly-level attribute** (`[assembly: MicroFxFeatureAssembly]` + `[assembly: MicroFxFeature(typeof(T))]`), scanning only opted-in assemblies from the entry assembly's dependency context. Full type scanning is prohibited. |
| FEA-022 | M | Adding a package reference to an adapter MUST be sufficient to make it available; no registration call required. |
| FEA-023 | S | Assembly scanning is disableable (`fx.DisableAssemblyScanning()`) for services requiring a fully explicit, auditable composition. |

### 4A.4 Override, replace, disable

| ID | Pri | Requirement |
|---|---|---|
| FEA-030 | M | Four override grains MUST be supported: **disable** a feature, **configure** its options, **replace** it wholesale, **override** a single DI service within it. |
| FEA-031 | M | Disabling is possible from code (`fx.Disable(id)`) **and** from configuration (`MicroFx:Features:{id}:Enabled=false`), so an operator can kill a capability without a rebuild. Configuration wins over code, and the catalog records which source disabled it. |
| FEA-032 | M | **Kernel features** (core, configuration, observability, health, diagnostics) cannot be disabled. An attempt fails startup with a message naming the feature and the configuration path that tried. |
| FEA-033 | M | A replacement feature declares `Replaces = "{id}"` and **inherits the replaced feature's graph edges**, so unrelated features that ordered themselves against the original continue to work without knowing a substitution occurred. |
| FEA-034 | M | Two features replacing the same id is a startup error naming both. Replacement chains (A replaces B replaces C) resolve transitively and are recorded. |
| FEA-035 | M | **Every** DI registration made by a built-in feature uses `TryAdd*`, so a service can substitute any single platform service by registering its own. Enforced across the platform source by analyzer `MFX2001`. |
| FEA-036 | M | Every override — disable, replace, DI substitution — MUST be visible at `/internal/features` and in the startup banner. No silent divergence from the golden path. |

### 4A.5 Introspection and self-observability

| ID | Pri | Requirement |
|---|---|---|
| FEA-040 | M | An `IFeatureCatalog` exposes the resolved graph: enabled, disabled (with cause), replaced (with chain), resolved order, contributing assembly and version. |
| FEA-041 | M | A structured **startup banner** logs the resolved feature set once, with each feature's key facts (transport in use, exporter endpoint, sampling rate, endpoint counts). |
| FEA-042 | M | `GET /internal/features` (management port, protected) returns the catalog as JSON including per-feature options snapshots with secrets redacted, graph edges, and last-startup lifecycle timings. |
| FEA-043 | M | Startup emits `microfx.feature.startup.duration` (histogram, tagged by feature and phase) and a span per lifecycle call under a `microfx.startup` root, so slow cold starts are attributable to a named feature. |
| FEA-044 | S | Validation failures across all features are **aggregated into one report**, so a misconfigured service surfaces every problem in a single startup rather than one per restart. |
| FEA-045 | S | An architecture test asserts the feature graph resolves, is acyclic, and matches a golden snapshot — making an accidental ordering change a failing test rather than a production surprise. |

---

## 5. Functional Requirements — Cross-Cutting Platform Capabilities

### 5.1 Configuration Management

| ID | Pri | Requirement |
|---|---|---|
| CFG-001 | M | Layered provider precedence: `appsettings.json` → `appsettings.{Env}.json` → AWS Systems Manager Parameter Store → AWS Secrets Manager → environment variables → command line. |
| CFG-002 | M | Parameter Store hierarchy convention: `/{org}/{env}/{service}/...`; shared platform config at `/{org}/{env}/_shared/...`. |
| CFG-003 | M | All configuration bound to strongly-typed POCOs via `IOptions<T>` with DataAnnotations/FluentValidation and `ValidateOnStart()` — invalid config fails startup, never at first request. |
| CFG-004 | M | Secrets resolved at runtime from Secrets Manager; **never** committed, baked into images, or written to logs. Secret values MUST be `redacted` in any config dump endpoint. |
| CFG-005 | S | Hot reload of non-secret configuration within 60 s without restart, via polling or AppConfig; changes emit a structured log + metric. |
| CFG-006 | S | AWS AppConfig integration for gradual, validated configuration rollout with automatic rollback on alarm. |
| CFG-007 | M | Secret rotation supported without downtime: cached credentials refresh on `AccessDenied`/expiry, bounded by TTL ≤ 15 min. |
| CFG-008 | M | A `GET /internal/config` endpoint (protected, non-prod only) shows effective config with secrets redacted and provider provenance per key. |
| CFG-009 | S | Config drift between deployed value and IaC-declared value is detectable and alertable. |

### 5.2 Observability

#### 5.2.1 Logging

| ID | Pri | Requirement |
|---|---|---|
| LOG-001 | M | Structured JSON logging via `Microsoft.Extensions.Logging` (Serilog or OTel log pipeline) to stdout. No file sinks in containers. |
| LOG-002 | M | Every log record carries: `timestamp` (UTC, ISO-8601), `level`, `message`, `template`, `service.name`, `service.version`, `deployment.environment`, `trace_id`, `span_id`, `host`, `correlation_id`, `tenant_id` (if applicable). |
| LOG-003 | M | High-performance logging via source-generated `[LoggerMessage]`; analyzer flags interpolated-string logging. |
| LOG-004 | M | Automatic PII/secret redaction using `Microsoft.Extensions.Compliance.Redaction` with `[PersonalData]`/`[SensitiveData]` taxonomy attributes on DTO properties. |
| LOG-005 | M | Per-namespace log level overridable at runtime without redeploy (config hot reload / AppConfig). |
| LOG-006 | S | Sampling for high-volume `Debug`/`Information` logs, with 100% retention of logs on sampled-in traces and all `Warning`+ records. |
| LOG-007 | M | Shipped to CloudWatch Logs (via awslogs/FireLens) with retention and subscription filters defined in IaC. |

#### 5.2.2 Tracing

| ID | Pri | Requirement |
|---|---|---|
| TRC-001 | M | OpenTelemetry tracing enabled by default with auto-instrumentation for ASP.NET Core, `HttpClient`, EF Core / Npgsql, AWS SDK, **RabbitMQ** (publish/consume spans with messaging semantic conventions), Redis. |
| TRC-002 | M | W3C Trace Context propagation (`traceparent`/`tracestate`) inbound and outbound, including across **AMQP message headers**. Consumer spans link to the producer span via span links where a batch is processed. |
| TRC-003 | M | Export via OTLP to a collector sidecar/daemonset; backend (X-Ray, Grafana Tempo, Datadog, etc.) is a deployment concern, not a code concern. |
| TRC-004 | M | Tail-based or parent-based sampling configurable; errors and slow requests always sampled. Default head sampling 10% in prod, 100% in non-prod. |
| TRC-005 | S | Business-meaningful spans easily added via `IPlatformActivitySource` with enforced attribute naming (OTel semantic conventions). |
| TRC-006 | S | Trace ID surfaced in error responses (`traceId` in Problem Details) and in the `X-Correlation-Id`/`traceresponse` header. |

#### 5.2.3 Metrics

| ID | Pri | Requirement |
|---|---|---|
| MET-001 | M | `System.Diagnostics.Metrics` based; OTLP export plus Prometheus scrape endpoint on the management port. |
| MET-002 | M | RED metrics emitted automatically for all inbound HTTP/gRPC and message-consumer operations (rate, errors, duration histogram). |
| MET-003 | M | USE metrics for runtime: GC, thread pool, heap, CPU, allocation rate, connection pool utilisation (`System.Runtime` + `Microsoft.AspNetCore.Hosting` meters). |
| MET-004 | M | Dependency metrics: outbound HTTP, database, cache, queue publish/consume — latency, errors, retries, circuit-breaker state. |
| MET-005 | M | Mandatory resource attributes on all metrics: `service.name`, `service.version`, `deployment.environment`, `team`, `cost_center`. |
| MET-006 | S | Exemplars linking metric buckets to trace IDs. |
| MET-007 | S | Custom business metric registration API with cardinality guardrails (analyzer + runtime cap on tag value cardinality). |

#### 5.2.4 Health, readiness, and diagnostics

| ID | Pri | Requirement |
|---|---|---|
| HLT-001 | M | Three endpoints on a **separate management port** (not internet-exposed): `/health/live`, `/health/ready`, `/health/startup`. |
| HLT-002 | M | Liveness MUST NOT check dependencies. Readiness MUST check critical dependencies (DB, required queues, mandatory downstreams) with individual timeouts. |
| HLT-003 | M | Health checks auto-registered by the platform for every registered dependency; adding a DB or queue requires no health-check code. |
| HLT-004 | M | Graceful shutdown: on SIGTERM, fail readiness immediately, drain in-flight requests and in-flight messages, then exit. Drain window configurable (default 30 s), must be ≤ container `stopTimeout`. |
| HLT-005 | S | `/internal/info` returns build SHA, version, build time, .NET version, feature flag snapshot, dependency versions. |
| HLT-006 | S | On-demand diagnostics: dump GC/thread state and capture a `dotnet-counters`-style snapshot via a protected endpoint or signal. |

### 5.3 API Layer

| ID | Pri | Requirement |
|---|---|---|
| API-001 | M | Minimal APIs with typed results; endpoints grouped and registered via `IEndpointModule` discovery. Controllers permitted only for legacy migration. |
| API-002 | M | URL-path API versioning (`/v{n}/...`) via `Asp.Versioning`; deprecation signalled with `Sunset`/`Deprecation` headers. |
| API-003 | M | All error responses conform to **RFC 9457 Problem Details**, including `type`, `title`, `status`, `detail`, `instance`, `traceId`, and `errors[]` for validation failures. No stack traces in non-dev environments. |
| API-004 | M | Global exception handling maps domain/application exception taxonomy → HTTP status codes; unmapped exceptions become 500 with a trace ID and are logged at `Error`. |
| API-005 | M | Request validation via FluentValidation with automatic 400 Problem Details; validation runs before any handler logic. |
| API-006 | M | OpenAPI 3.1 document generated at build time, published as a CI artifact, and served at `/openapi/v{n}.json`. Swagger UI/Scalar in non-prod only. |
| API-007 | M | Breaking-change detection on the OpenAPI document in CI (e.g. `oasdiff`); breaking changes fail the build unless the version is bumped. |
| API-008 | M | Standard headers handled: `X-Correlation-Id` (accept or generate, always echo), `X-Request-Id`, `Accept-Language`, `traceparent`. |
| API-009 | M | Request size limits, timeout middleware, and `RequestTimeouts` policies applied by default. |
| API-010 | M | Rate limiting via `Microsoft.AspNetCore.RateLimiting` with per-client/per-tenant partitioning; `429` responses include `Retry-After`. |
| API-011 | S | Idempotency for unsafe methods via `Idempotency-Key` header, backed by the distributed cache with configurable TTL; replays return the original response. |
| API-012 | S | Conditional requests (`ETag`, `If-Match`, `If-None-Match`) supported for resource endpoints; optimistic concurrency conflicts return `412`. |
| API-013 | S | Cursor-based pagination, filtering, and sorting conventions with a shared `PagedResult<T>` contract. |
| API-014 | M | CORS, HSTS, `X-Content-Type-Options`, `X-Frame-Options`, `Content-Security-Policy`, and `Referrer-Policy` configured by default. |
| API-015 | C | gRPC parity: interceptors for the same cross-cutting concerns; `.proto` contracts linted and breaking-change checked (Buf). |

### 5.4 Security

| ID | Pri | Requirement |
|---|---|---|
| SEC-001 | M | OAuth 2.1 / OIDC bearer token authentication; JWT validation with issuer, audience, lifetime, signature, and JWKS caching with rotation. |
| SEC-002 | M | Policy-based authorization; endpoints deny by default (`RequireAuthorization()` applied globally, opt-out via explicit `AllowAnonymous`). |
| SEC-003 | M | Machine-to-machine auth via client credentials **or** AWS SigV4/IAM; token acquisition, caching, and refresh handled by the platform HTTP client. |
| SEC-004 | M | Support for scope-, role-, and claim-based authorization plus a pluggable external decision point (OPA/Cedar/AVP) for fine-grained rules. |
| SEC-005 | M | Tenant isolation: `ITenantContext` resolved from token claim or header; persistence and cache keys automatically tenant-scoped; cross-tenant access attempts logged and rejected. |
| SEC-006 | M | All AWS access via IAM roles (IRSA for EKS, task roles for ECS). **No** static AWS access keys anywhere, enforced by CI secret scanning. Broker credentials are the one documented exception (BRK-012). |
| SEC-007 | M | TLS 1.2+ for all in-transit traffic; mTLS supported for service-to-service where the mesh does not provide it. |
| SEC-008 | M | Encryption at rest with customer-managed KMS keys for RDS, S3, DynamoDB, **Amazon MQ**, EBS, and CloudWatch Logs. |
| SEC-009 | S | Application-level field encryption for designated sensitive columns via KMS data keys with envelope encryption and key caching. |
| SEC-010 | M | Security audit events (authn failure, authz denial, privileged action, config/secret change) emitted to a dedicated, immutable audit stream — separate from application logs. |
| SEC-011 | M | Anti-automation: rate limiting (API-010) plus optional WAF integration at the ALB/API Gateway. |
| SEC-012 | M | Dependency scanning (`dotnet list package --vulnerable`, Dependabot/Renovate), container image scanning (ECR enhanced scanning/Trivy), SAST, and IaC scanning in CI; **critical/high findings block deployment to prod**. |
| SEC-013 | M | SBOM (CycloneDX) generated per build and stored as an attestation; build provenance signed (Sigstore/cosign, SLSA L3 target). |
| SEC-014 | S | Container runs as a non-root user, read-only root filesystem, no privileged capabilities, distroless or chiselled base image. |
| SEC-015 | S | Outbound egress restricted by security group / network policy to an explicit allowlist. |

### 5.5 Resilience and Fault Tolerance

| ID | Pri | Requirement |
|---|---|---|
| RES-001 | M | All outbound HTTP calls go through named/typed `HttpClient`s registered with a **Polly v8 resilience pipeline** — no raw `new HttpClient()` (analyzer-enforced). |
| RES-002 | M | Default pipeline order: total request timeout → retry (exponential backoff + jitter, idempotent methods only) → circuit breaker → per-attempt timeout. |
| RES-003 | M | Retry policy MUST NOT retry non-idempotent operations unless an idempotency key is present. |
| RES-004 | M | Circuit breaker state changes emit logs, metrics, and (in prod) alerts. |
| RES-005 | M | Bulkhead/concurrency limits per downstream dependency to prevent thread-pool and connection-pool exhaustion. |
| RES-006 | S | Fallback/degraded-mode responses configurable per dependency (cached value, empty result, or fail-fast) with an explicit decision recorded per dependency in the service README. |
| RES-007 | S | Request hedging for latency-sensitive, idempotent reads. |
| RES-008 | M | Every dependency classified as **critical** or **non-critical**; non-critical failures MUST NOT fail the request or readiness. |
| RES-009 | S | Chaos/fault-injection hooks (latency, error, throttle) toggleable via feature flags in non-prod. |
| RES-010 | M | Backpressure: bounded queues/channels everywhere; `503` + `Retry-After` when saturated rather than unbounded memory growth. |

### 5.6 Messaging — Commands, Events, and Request/Reply

The platform MUST support three distinct asynchronous messaging patterns as first-class, separately-modelled capabilities. Conflating them is the single most common source of coupling in event-driven estates, so the template treats them as different types with different rules.

**Messaging is transport-neutral (v2.0 change).** The messaging feature is a **generic adapter**: the envelope, handler pipeline, outbox, inbox, dedupe, retry policy, dead-letter policy, claim-check, tracing, and abstract topology model live in the core and are written once. A concrete broker is reached through the `IMessageTransport` port plus optional facets. The core ships an **in-memory transport** for local development and test; **`MicroFx.Messaging.RabbitMq` is the reference production adapter** and remains the default deployment (Amazon MQ for RabbitMQ, 3-AZ cluster). Requirements are therefore split:

| Prefix | Scope | Binds |
|---|---|---|
| `MSG`, `CMD`, `EVT`, `REQ`, `ORC` | Transport-neutral. Implemented once in the core. | Every transport |
| `TRN` | The transport port and capability negotiation (§5.6.0) | Every adapter |
| `BRK` | **RabbitMQ adapter only** (§5.6.1) | `MicroFx.Messaging.RabbitMq` |

| Pattern | Semantics | Intent | Naming | Abstract topology | RabbitMQ mapping |
|---|---|---|---|---|---|
| **Command** | Point-to-point, exactly one logical consumer, sender expects it to be *done* | Imperative — "do this" | Imperative verb: `ReserveInventory` | Command destination, one consumer group | `direct` exchange → single durable quorum queue |
| **Event** | Publish/subscribe, 0..N independent subscribers, publisher indifferent to who listens | Declarative fact — "this happened" | Past tense: `OrderPlaced` | Event destination, one consumer group **per subscriber** | `topic` exchange → one durable quorum queue per subscriber |
| **Request/Reply** | Point-to-point with a correlated response | Query or command needing a result | `GetQuoteRequest` / `GetQuoteReply` | Request destination + reply destination | request queue + `direct reply-to` pseudo-queue |

#### 5.6.0 Transport port and capability negotiation

| ID | Pri | Requirement |
|---|---|---|
| TRN-001 | M | A transport implements `IMessageTransport` (publish, subscribe, delivery disposition) plus optional facets: `ITransportTopologyProvisioner`, `ITransportRequestReply`, `ITransportScheduler`, `ITransportMetricsSource`. A transport implements only what it can do. |
| TRN-002 | M | Every transport advertises a `TransportCapabilities` flag set: publisher confirms, manual acknowledgement, native dead-letter, native delayed delivery, native request/reply, ordered delivery, priority, topology provisioning, consumer cancellation, broker-side filtering, message TTL, transactions. |
| TRN-003 | M | At startup the messaging feature computes **required capabilities** (from the declared subscriptions and delivery guarantees) against **advertised capabilities**, and produces one aggregated report naming each unmet requirement, the subscription that asked for it, and the transport that lacks it. |
| TRN-004 | M | The core MUST emulate a missing **convenience** and record that it did so: absent native delayed delivery → scheduled-message store drained by a job (never in-process `Task.Delay`); absent native dead-letter → core-published dead-letter destination with delivery history preserved in the envelope; absent broker-side filtering → consumer-side filtering with a `messaging.filtered.count` metric so the waste is visible. |
| TRN-005 | M | The core MUST NOT silently downgrade a **correctness** guarantee. A transport lacking manual acknowledgement cannot satisfy at-least-once and MUST fail startup. A transport lacking publisher confirms MUST fail startup unless `AllowUnconfirmedPublish` is explicitly set, which requires an ADR reference recorded in the feature catalog. Ordering-per-key requested of a transport that cannot provide it MUST fail startup. |
| TRN-006 | M | Service and handler code MUST NOT reference transport types. Enforced by analyzer `MFX1020`. Swapping the transport MUST require no change to handlers, publishers, contracts, or tests. |
| TRN-007 | M | The topology model is transport-neutral: `MessageDestination` (kind, owner, name, version) and `SubscriptionSpec` (consumer group, source, filter, guarantee, concurrency, prefetch, retry, dead-letter, ordering scope). The **consumer group** is the abstraction that expresses "queue per subscriber" without naming a queue. |
| TRN-008 | M | The core ships an **in-memory transport** (bounded `System.Threading.Channels`) advertising full capability emulation, used by `MicroFx.Testing` and by local development. Outside `Development` its use emits a startup warning. |
| TRN-009 | S | A transport adapter ships a conformance test suite provided by `MicroFx.Testing` that exercises every capability it advertises, so "advertises confirms" is a tested claim rather than a flag. |
| TRN-010 | S | Multiple transports may be registered concurrently, with destinations routed per message kind or per destination, to support migration between brokers without a big-bang cutover. |

#### 5.6.1 RabbitMQ adapter — broker, connection, and topology management

> Scope: `MicroFx.Messaging.RabbitMq` only. These requirements do not constrain the core or any other adapter.

| ID | Pri | Requirement |
|---|---|---|
| BRK-001 | M | Broker is **RabbitMQ 3.13+** on **Amazon MQ**, deployed as a `CLUSTER_MULTI_AZ` (3-node) instance in private subnets, with automatic minor-version upgrades in a defined maintenance window. Single-instance deployments permitted **only** in dev. |
| BRK-002 | M | All queues are **quorum queues** (Raft-replicated) by default. Classic mirrored queues are prohibited (deprecated and removed upstream). Non-replicated classic queues permitted only for transient reply/scratch queues. |
| BRK-003 | M | **Publisher confirms** enabled on every publishing channel. A publish is not considered successful until the broker acks it; unconfirmed publishes are retried by the outbox relay (EVT-004). Fire-and-forget publishing without confirms is analyzer-flagged. |
| BRK-004 | M | Publishes use the `mandatory` flag with an **alternate exchange** configured on every exchange, so unroutable messages land in a monitored `unroutable` queue rather than being silently dropped. An unroutable message raises an alert — it always indicates a topology or naming defect. |
| BRK-005 | M | **Consumer acknowledgements are manual and explicit** (`autoAck: false`). Ack after successful handling; `nack`/`reject` with `requeue: false` on terminal failure so the message is dead-lettered rather than looping. |
| BRK-006 | M | **QoS prefetch** configured per consumer (default 10, tuned per handler duration). Unbounded prefetch is prohibited — it defeats fair dispatch and inflates memory. |
| BRK-007 | M | Connection management: **one connection per process per role** (publisher/consumer), **one channel per consumer**, and channels are never shared across threads. Enforced by the platform's connection factory; direct `IConnection`/`IModel` use is analyzer-flagged. |
| BRK-008 | M | Automatic connection and **topology recovery** enabled, with heartbeats (default 60 s), bounded reconnect backoff, and a `messaging.broker.connected` gauge. Connection loss degrades readiness (HLT-002) but never liveness. |
| BRK-009 | M | **Topology is declared as code and provisioned by IaC/migration**, not by the application at startup. Exchanges, queues, bindings, policies, users, and permissions are versioned artefacts; the app asserts (passive-declares) the topology it expects and fails startup on mismatch. |
| BRK-010 | M | Amazon MQ **does not support custom plugins**. The platform MUST NOT depend on `rabbitmq_delayed_message_exchange`, `rabbitmq_stream`, or any other non-bundled plugin on the golden path; features needing them are provided another way (MSG-014, EVT-013) or require moving to self-managed RabbitMQ with an ADR. |
| BRK-011 | M | **vhost per environment-and-domain** (`/{env}-{domain}`) with per-service users scoped by least-privilege `configure`/`write`/`read` regex permissions. The `guest` user is deleted. Cross-vhost access requires an explicit shovel. |
| BRK-012 | M | Broker credentials stored in **Secrets Manager**, rotated without downtime (CFG-007). This is an accepted, documented exception to SEC-006 (IAM-only auth) — RabbitMQ does not support IAM authentication; compensating controls are short rotation intervals, per-service users, and vhost isolation. |
| BRK-013 | M | TLS (AMQPS, 1.2+) enforced for all client connections; plaintext AMQP listeners disabled. Encryption at rest via Amazon MQ's KMS-backed volume encryption with a customer-managed key. |
| BRK-014 | M | **Queue-level policies** applied via IaC: `max-length` / `max-length-bytes` with `overflow: reject-publish` (never `drop-head` for business messages), `message-ttl` where applicable, `delivery-limit` for quorum-queue poison protection, and `dead-letter-exchange`. |
| BRK-015 | M | Broker capacity guardrails: alarms on memory high-watermark, disk free-space alarm, file-descriptor and connection/channel counts, and per-queue depth. A broker-side flow-control (blocked connection) event is a paging alert. |
| BRK-016 | S | Broker observability via the **RabbitMQ Prometheus plugin** (self-managed) or Amazon MQ CloudWatch metrics + the Management HTTP API, feeding a standard broker dashboard (OPS-002). |
| BRK-017 | S | **Federation or Shovel** for cross-region or cross-vhost bridging, declared in IaC; never ad-hoc application-level bridging. |
| BRK-018 | S | Broker capacity and instance-type sizing documented per environment, with a load-tested messages/sec and connection-count ceiling per tier. |

#### 5.6.2 Common messaging plumbing (applies to all patterns)

| ID | Pri | Requirement |
|---|---|---|
| MSG-001 | M | A single unified abstraction — `ICommandSender`, `IEventPublisher`, `IRequestClient<TReq,TRes>` — over any transport. Destinations are resolved from **topology conventions** (TRN-007), never named in handler or caller code, and never expressed in transport vocabulary (exchange, routing key, partition, ARN). |
| MSG-002 | M | All messages, regardless of pattern, share a **CloudEvents 1.0** envelope (`id`, `source`, `type`, `subject`, `time`, `datacontenttype`, `dataschema`, `data`) plus platform extensions: `correlationid`, `causationid`, `traceparent`, `tenantid`, `messagekind` (`command`\|`event`\|`request`\|`reply`), `replyto`, `expiresat`. |
| MSG-003 | M | `messagekind` is set by the platform, not the caller, and is validated on receipt: a consumer registered for events MUST reject a message stamped as a command, and vice versa. |
| MSG-004 | M | **Inbox / idempotent consumption**: every consumer deduplicates by envelope `id` against a persisted dedup store (the service database or DynamoDB) with configurable retention (default 7 days). Applies to commands, events, and replies alike. |
| MSG-005 | M | Consumer concurrency (consumers per queue), prefetch (BRK-006), and handler timeout configured declaratively per consumer. Long-running handlers MUST NOT rely on broker-side extension — RabbitMQ has no visibility timeout; the delivery is held until ack, so handler timeouts are enforced client-side. |
| MSG-006 | M | Poison-message handling: bounded retries with backoff (MSG-018), then dead-lettering. **Every** platform-provisioned subscription MUST have a dead-letter destination and an alarm on dead-letter depth > 0 — enforced by the core regardless of whether the transport dead-letters natively (TRN-004). A transport-native redelivery backstop is configured where one exists. |
| MSG-007 | M | DLQ inspection and **replay/requeue tooling** (shovel-based or CLI) plus a runbook; replay MUST be idempotent-safe and preserve the original envelope, `x-death` history, and trace context. |
| MSG-008 | M | Trace context propagated in **transport message headers** so producer and consumer spans join one trace, with OTel messaging semantic-convention attributes (`messaging.system`, `messaging.operation`, `messaging.destination.name`), plus transport-specific attributes contributed by the adapter (TRC-002). |
| MSG-009 | M | Schema registry for **both** command and event contracts, provided by the platform rather than assumed from the broker: contracts published as versioned NuGet packages plus JSON Schema artefacts in a central registry, with CI failing on incompatible evolution. Where a transport offers a native registry, the adapter may additionally register there; the platform registry remains authoritative. |
| MSG-010 | M | Handler pipeline with ordered, composable middleware — deserialize → validate → dedupe → authorize → tenant scope → trace/log/meter → retry → handle → ack — mirroring the HTTP pipeline so behaviour is consistent across entry points. |
| MSG-011 | M | Message-level authorization: the envelope carries the caller's identity/claims; handlers can require scopes or roles exactly as HTTP endpoints do (SEC-002, SEC-004). |
| MSG-012 | S | Claim-check pattern: payloads above a configurable threshold (default 128 KB) are offloaded via `IObjectStore` with a reference in the envelope; transparent to handlers on both sides. The threshold is a deliberate platform policy, not a broker limit — it defaults below every supported transport's hard cap and above the size at which large messages start degrading broker replication and memory. |
| MSG-013 | S | Message payload compression (gzip/brotli) above a configurable threshold, negotiated via `datacontentencoding`. |
| MSG-014 | S | **Delayed delivery** is a core capability, satisfied three ways in order of preference: the transport's native scheduler (`ITransportScheduler`) where it exists and covers the required delay; the adapter's emulation within its own primitives (for RabbitMQ, the TTL holding-queue ladder — BRK-010 forbids the plugin); otherwise the core's persisted **scheduled-message store** drained by a job (JOB-002). In-process `Task.Delay` is prohibited in all three cases. |
| MSG-015 | M | Per-consumer metrics: received, succeeded, failed, retried, dead-lettered, handler duration, plus per-queue `messages_ready`, `messages_unacknowledged`, consumer count, and **oldest-message age**, with alerting thresholds (MET-002, OPS-003). |
| MSG-016 | M | Consumers honour graceful shutdown: on SIGTERM stop accepting deliveries (via `ITransportSubscription.CancelAsync`, mapping to the transport's cancellation primitive), finish in-flight handlers and acknowledge them within the drain window, then close. Unacknowledged messages are redelivered by the transport — no message loss (HLT-004). Ordered by the feature model's reverse-order shutdown (FEA-016). |
| MSG-017 | S | Poison-pill circuit breaker: if a consumer's failure rate exceeds a threshold, `basic.cancel` the consumer and alert rather than draining the queue into the DLQ. |
| MSG-018 | M | **Retry policy is core; the retry mechanism is the transport's.** The core decides attempt count, backoff curve, and jitter, and tracks the attempt count in the envelope. The delay is realised by MSG-014's three-way resolution — never by an in-process sleep, which holds the delivery, consumes a prefetch slot, and stalls the consumer. |
| MSG-019 | M | Message durability: all business messages published with the transport's durable/persistent mode to durable destinations. A transport unable to offer durable publish fails the at-least-once capability check (TRN-005). Transient messages require an explicit opt-in and an ADR. |
| MSG-020 | S | **Priority** support where the transport offers it (`TransportCapabilities.Priority`); requested-but-unavailable priority is a startup warning and the subscription proceeds unprioritised. Transport-specific trade-offs (e.g. RabbitMQ priority forcing classic queues) are documented by the adapter and require an ADR. |

#### 5.6.3 Commands (point-to-point)

| ID | Pri | Requirement |
|---|---|---|
| CMD-001 | M | `ICommandSender.SendAsync<TCommand>` delivers to **exactly one** logical consumer — one consumer group on a command destination. Registering a second consumer group for the same command type is a topology error and MUST fail both IaC validation and application startup assertion. |
| CMD-002 | M | Commands are addressed by **destination convention**, not by the sender naming a queue or topic: the abstract destination is `(Command, owner: {owning-service}, name: {command-name}, version)`. The sender declares the command type; the concrete address is resolved by the adapter from the topology registry. The sender never writes a transport address. |
| CMD-003 | M | Commands MUST be validated (FluentValidation) before the handler executes. Validation failure is **non-retryable** — the message goes straight to the DLQ with a structured rejection reason, never into a retry loop. |
| CMD-004 | M | Explicit distinction between **retryable** (transient: timeout, throttle, 5xx) and **non-retryable** (validation, authorization, business-rule rejection) failures. Only retryable failures consume retry attempts. |
| CMD-005 | M | Commands are owned by the **receiving** service. The receiver publishes the command contract in its `Contracts` package; senders take a dependency on it. The reverse (a sender defining commands for others) is prohibited. |
| CMD-006 | M | Command handlers are idempotent by contract and enforced by the inbox (MSG-004); at-least-once delivery is assumed and documented. |
| CMD-007 | S | Per-aggregate ordering, where required, via **single-active-consumer** (`x-single-active-consumer`) on the command queue, or consistent-hash routing to per-partition queues. Note this caps throughput at one consumer — it requires an ADR. |
| CMD-008 | S | Command outcome notification: a handler may emit a correlated **event** (`...Succeeded`/`...Rejected`) carrying the originating `causationid`, giving the sender an audit trail without coupling to a reply channel. |
| CMD-009 | S | Command expiry (`expiresat` in the envelope, plus AMQP per-message `expiration` where appropriate): a command received after its deadline is dead-lettered with an `expired` reason rather than executed late. |
| CMD-010 | C | Priority handling via separate high/normal priority **queues** per command type with weighted consumer allocation (preferred over `x-max-priority`, which forces classic queues — MSG-020). |

#### 5.6.4 Events (publish/subscribe)

| ID | Pri | Requirement |
|---|---|---|
| EVT-001 | M | `IEventPublisher.PublishAsync<TEvent>` is fire-and-forget with respect to subscribers. The publisher MUST NOT know, enumerate, or depend on its subscribers. |
| EVT-002 | M | Topology is an **event destination per owning service** with a **consumer group per subscriber**. Every subscriber gets its own consumer group, hence its own retry, dead-letter, backlog, and consumption rate. Two services sharing one consumer group is prohibited — it turns pub/sub into competing consumers. A transport that cannot express independent consumer groups on a shared destination cannot host the event pattern (TRN-005). |
| EVT-003 | M | Naming convention at the abstract layer: event destination `(Event, owner: {owning-service})`; event name `{aggregate}.{event-name}.{version}` (e.g. `order.placed.v1`); consumer group `{subscribing-service}.{owning-service}.{event-name}`. Each adapter documents its deterministic mapping to concrete transport objects (the RabbitMQ mapping is in §5.6.1). |
| EVT-004 | M | **Transactional outbox**: events raised inside a database transaction are persisted atomically with the state change and published by a relay dispatcher, guaranteeing at-least-once delivery with per-aggregate ordering. Direct publish inside a transaction is analyzer-flagged. |
| EVT-005 | M | The outbox relay is crash-safe, runs with leader election or partitioned claim (JOB-003), and exposes lag metrics (`outbox.pending.count`, `outbox.oldest.age`) with alerts. |
| EVT-006 | M | Clear separation between **domain events** (in-process, never leave the service) and **integration events** (published, versioned, in the public `Contracts` package). Analyzer prevents publishing a domain event to a transport. |
| EVT-007 | M | Integration event contracts are **additive-only** within a major version: new optional fields allowed; removing or retyping a field requires a new versioned event type (`OrderPlaced.v2`) published in parallel during a documented migration window. |
| EVT-008 | M | Subscribers declare a transport-neutral filter pattern (`order.*.v1`, `order.#`). Where the transport advertises `BrokerSideFiltering`, the adapter MUST push the filter to the broker rather than receiving-and-discarding. Where it does not, the core filters consumer-side and emits `messaging.filtered.count` so the wasted delivery volume is visible and quantified (TRN-004). |
| EVT-009 | M | Events carry a **fact, not a command**: the platform's naming analyzer rejects imperative event names, and review guidance forbids subscriber-specific fields in an event payload. |
| EVT-010 | S | Events are self-contained enough to be processed without a callback to the publisher for common cases (state-transfer where payload size permits), with the claim-check escape hatch (MSG-012) for large state. |
| EVT-011 | S | **Event replay is a platform capability, not a broker feature** — assuming a replayable log is what makes a design non-portable. An **archive consumer group** on every event destination persists the raw envelope via `IObjectStore` (partitioned by date/type), plus tooling to republish a time range or filtered set **to a single consumer group**. Replayed messages are flagged (`replayed: true`) so consumers can suppress side effects. Where the transport is natively replayable (a log), the adapter may satisfy replay directly; the platform contract is identical either way. |
| EVT-012 | S | Ordered delivery per aggregate, where a subscriber requires it, via single-active-consumer or consistent-hash-exchange partitioning (CMD-007), with the throughput trade-off documented per subscription. |
| EVT-013 | S | **High-throughput / re-readable streams** are out of scope for the RabbitMQ adapter (BRK-010 rules out the stream plugin on Amazon MQ). Workloads needing > ~20k msg/s sustained, long retention, or replayable offsets are served by writing a **log transport adapter** (Kafka/MSK, Kinesis) against the same `IMessageTransport` port — which the v2.0 inversion makes an adapter-sized task rather than a rewrite. Choosing one still requires an ADR. |
| EVT-014 | S | Subscriber registry: the platform catalog records who publishes and who consumes each event type, generating an estate-wide dependency graph and warning on events with zero subscribers. |
| EVT-015 | C | Consumer-driven contract tests for events (Pact message pacts); a publisher change that breaks a registered consumer fails the publisher's CI. |

#### 5.6.5 Request/Reply over messaging

| ID | Pri | Requirement |
|---|---|---|
| REQ-001 | S | `IRequestClient<TRequest,TResponse>` provides correlated request/reply with a **mandatory** timeout. Where the transport advertises `NativeRequestReply` the adapter uses it (RabbitMQ: `direct reply-to`, avoiding orphaned exclusive reply queues); otherwise the core uses a per-instance reply destination it creates and reaps. A durable reply destination is the fallback where the reply must survive a caller restart. |
| REQ-002 | S | Correlation via the envelope `id`/`correlationid` mapped onto the transport's correlation primitive; late or unmatched replies are discarded and counted, never delivered to a stale waiter. The pending-request map is bounded and evicted on timeout. |
| REQ-003 | S | Replies MUST be able to carry a fault: a typed error response distinct from a transport failure, so a caller can distinguish "rejected" from "no answer". |
| REQ-004 | M | Guidance and analyzer warning: request/reply over messaging is **not** the default for synchronous needs — prefer HTTP/gRPC (S2S-001). It exists for long-running work, load levelling, and crossing a network boundary that forbids direct calls. Its use requires an ADR. |

#### 5.6.6 Orchestration and long-running flows

| ID | Pri | Requirement |
|---|---|---|
| ORC-001 | C | Saga / process-manager support with correlated state, timeouts, and compensating actions — durable state in **AWS Step Functions** (preferred for cross-service flows) or an in-service state machine persisted with the aggregate. |
| ORC-002 | C | Saga state is queryable and its timeouts observable; a stuck saga raises an alert with the correlation ID. |

### 5.7 Data Persistence

| ID | Pri | Requirement |
|---|---|---|
| DAT-000 | M | **EF Core is the built-in, out-of-the-box persistence implementation**, shipped in the core `MicroFx.Persistence` namespace — not an adapter. It provides a real, durable, transactional store so the outbox (EVT-004) and inbox (MSG-004) can be demonstrated rather than simulated. |
| DAT-000a | M | The core references **`Microsoft.EntityFrameworkCore.Relational` only** — no database driver. The service supplies its own EF provider (`UseNpgsql`, `UseSqlServer`, `UseSqlite`, …). Changing database engine requires **no** MicroFx adapter; the EF provider *is* the adapter. |
| DAT-000b | M | The zero-configuration default is **SQLite in-memory**, explicitly **not** the EF `InMemory` provider — the latter does not support transactions and would make every outbox and inbox test pass for the wrong reason. Local runs and CI exercise the real transactional code path. |
| DAT-001 | M | Primary relational target: **Amazon Aurora PostgreSQL** via the Npgsql provider, using **IAM database authentication** (no stored DB passwords) with token refresh. This is a deployment choice, not a platform coupling. |
| DAT-002 | M | Non-EF stores (DynamoDB, Mongo, Dapper) are supported by **replacing the persistence feature** (`Replaces = "microfx.persistence"`), implementing `IUnitOfWork`, `ITransactionScope`, `IOutboxStore`, `IInboxStore`, `IMigrationGate`. The replacement inherits the built-in's graph edges, so messaging continues unchanged (FEA-033). |
| DAT-003 | M | Schema migrations version-controlled and applied by an explicit pipeline stage (init container or migration job) — **never** on application startup in prod. The platform's `IMigrationGate` **asserts** applied migrations match expectations and fails startup on drift; it migrates only in `Development`. |
| DAT-004 | M | Migrations MUST be backwards-compatible (expand/contract), enabling rolling deployments and rollback of the previous app version. |
| DAT-005 | M | Connection pooling configured (RDS Proxy or Npgsql pool) with tuned max pool size, timeouts, and pool-exhaustion metrics. |
| DAT-006 | M | Read/write splitting supported via a reader endpoint for explicitly marked read-only operations. |
| DAT-007 | M | Optimistic concurrency via `xmin`/rowversion or DynamoDB conditional writes; conflicts surface as a typed domain exception → `409`/`412`. |
| DAT-008 | M | Unit of work / transaction scope aligned with the outbox (MSG-003); no distributed transactions across services. |
| DAT-009 | S | Multi-tenancy strategies supported: shared schema with tenant discriminator (default, with global query filter), schema-per-tenant, or database-per-tenant. |
| DAT-010 | S | Query performance guardrails: command timeout, slow-query logging above a threshold, and analyzer warning on unbounded queries (`ToListAsync` without a limit). |
| DAT-011 | S | Soft delete, audit columns (`CreatedAt/By`, `ModifiedAt/By`), and temporal/audit history conventions available as opt-in. |
| DAT-012 | M | Backup, PITR, and restore requirements defined per service tier; restore procedure exercised at least annually. |
| DAT-013 | C | Change Data Capture via DMS/Debezium for services requiring outbound replication. |

#### 5.7.1 Transaction handling

Three subsystems must commit together — the aggregate's state change, the inbox dedupe record, and the outbox rows — and none of them should have to know about the others. The platform owns the unit of work so a service never assembles this by hand.

| ID | Pri | Requirement |
|---|---|---|
| TXN-001 | M | `IUnitOfWork.BeginAsync()` returns an `ITransactionScope`. `SaveChangesAsync` persists changes, dispatches domain events, and enlists integration events into the outbox **within the same transaction**. |
| TXN-002 | M | **Ambient nesting**: an inner `BeginAsync` joins the enclosing scope rather than opening a second transaction; commit on an ambient scope is a no-op and the outermost scope decides the outcome. Without this, a handler calling a shared application service silently commits half its work. |
| TXN-003 | M | **Execution strategy is handled by the platform.** EF Core throws when connection retry (`EnableRetryOnFailure`) meets a user-initiated transaction, because a retry cannot safely replay a partial transaction. `BeginAsync` MUST wrap the whole transaction in `IExecutionStrategy.ExecuteAsync`. A service MUST NOT have to know this. |
| TXN-004 | M | **Transaction per message handler is on by default**: inbox dedupe insert + handler work + outbox rows commit as one unit, making "did the work" and "recorded that we did the work" inseparable (MSG-004). |
| TXN-005 | M | **Transaction per HTTP request is off by default** and opt-in per endpoint. A request-scoped transaction held across an outbound call is a connection-pool exhaustion incident; the platform declines to make it the default. |
| TXN-006 | M | `IEventPublisher.PublishAsync` inside an ambient scope writes an outbox row; outside one it publishes directly. Handler code is identical either way (EVT-004). Direct publish inside a transaction is analyzer-flagged. |
| TXN-007 | M | `SaveChangesAsync` runs one ordered sequence inside the transaction: drain domain events from tracked aggregates → dispatch in-process handlers (which may mutate further state) → repeat until quiescent → project `IIntegrationEvent`s into outbox rows → persist. |
| TXN-008 | M | Domain events that are not integration events never reach a transport (EVT-006), enforced at the projection step and by analyzer `MFX1022`. |
| TXN-009 | S | Savepoints available for genuine partial rollback within an ambient scope, where a provider supports them. |
| TXN-010 | M | Transaction scope, duration, retry count, and rollback cause are traced and metered, so a long-held transaction is attributable rather than inferred from connection-pool starvation. |

### 5.8 Caching

| ID | Pri | Requirement |
|---|---|---|
| CAC-001 | M | **In-memory (L1) caching is built in and works with zero configuration** — `HybridCache` over an in-process store, shipped in the core `MicroFx.Caching` namespace with no external dependency. A service gets stampede protection, key conventions, tenant scoping, and metrics without deploying any infrastructure. |
| CAC-001a | M | A **distributed L2 is opt-in via the `IDistributedCacheProvider` port**, satisfied by `MicroFx.Caching.Redis` (Redis / ElastiCache for Valkey, TLS, IAM/RBAC auth). Adding the package reference and configuring a connection is the whole change — no cache-consuming code is touched, because `HybridCache` already fronts both tiers. |
| CAC-001b | M | The L1↔L1+L2 transition MUST NOT change semantics a caller can observe beyond latency and cross-instance visibility. Key construction, serialization, TTL, jitter, and invalidation behave identically with and without L2. |
| CAC-002 | M | Key naming convention `{service}:{env}:{tenant}:{entity}:{version}:{id}` applied by the platform; manual key construction discouraged by analyzer. |
| CAC-003 | M | Stampede protection (single-flight) and jittered TTLs by default, in both tiers. |
| CAC-004 | M | Cache must be a strict optimisation — L2 unavailability degrades to L1/origin, never fails the request. L2 health is **degraded-only** and never contributes to readiness. |
| CAC-005 | S | Explicit invalidation API plus tag-based invalidation; cache version prefix allows bulk invalidation on deploy. |
| CAC-006 | M | Hit/miss/eviction/latency metrics per cache region. |
| CAC-007 | S | Output caching and HTTP response caching for suitable GET endpoints. |

### 5.9 Background Work and Scheduling

| ID | Pri | Requirement |
|---|---|---|
| JOB-001 | M | Long-running work hosted as `BackgroundService` with cooperative cancellation honouring `IHostApplicationLifetime`. |
| JOB-002 | M | Scheduled work via **EventBridge Scheduler** publishing a command to the service's queue (preferred, externalised) or an in-process scheduler with distributed locking. Also drains the scheduled-message store for long delays (MSG-014). |
| JOB-003 | M | Distributed lock / leader election (DynamoDB conditional-write lease or Redis lock) so scheduled work runs once across N replicas; leases must auto-expire. |
| JOB-004 | M | Every job execution is traced, timed, and emits success/failure metrics plus a "job did not run" staleness alarm. |
| JOB-005 | S | Job runs are idempotent and safe to re-execute; overlapping-run prevention configurable. |
| JOB-006 | S | Long-running orchestration delegated to **AWS Step Functions** where retries, timeouts, and human steps are needed. |

### 5.10 Feature Flags

| ID | Pri | Requirement |
|---|---|---|
| FLG-001 | M | **OpenFeature**-based abstraction with a pluggable provider (AWS AppConfig Feature Flags default; LaunchDarkly/Flagsmith supported). |
| FLG-002 | M | Evaluation context automatically enriched with tenant, user, environment, and service version. |
| FLG-003 | M | Local evaluation with a cached ruleset; provider outage falls back to last-known-good, then to code defaults. Never fails the request. |
| FLG-004 | M | Flag evaluations emit metrics and (sampled) trace events for A/B and rollout analysis. |
| FLG-005 | S | Kill switches for each external dependency and each expensive code path, documented in the runbook. |
| FLG-006 | S | Flag lifecycle governance: creation date, owner, expected removal date; stale-flag report in CI. |

### 5.11 Service-to-Service Communication

| ID | Pri | Requirement |
|---|---|---|
| S2S-001 | M | Typed HTTP clients generated from the provider's OpenAPI document (NSwag/Kiota), published as a client NuGet package by the provider's pipeline. |
| S2S-002 | M | All clients preconfigured with resilience (RES-001), auth token acquisition (SEC-003), tracing, and metrics. |
| S2S-003 | M | Service discovery via DNS (Cloud Map / Kubernetes Service) — endpoints from configuration, never hard-coded. |
| S2S-004 | S | Consumer-driven contract tests (Pact) run in CI; provider verification gates provider deploys. |
| S2S-005 | S | `Platform.Contracts` package convention for shared DTOs and events, with additive-only evolution rules. |

### 5.12 File and Object Storage

| ID | Pri | Requirement |
|---|---|---|
| STO-001 | M | S3 access wrapped with bucket/prefix conventions, SSE-KMS, versioning, and blocked public access. |
| STO-002 | M | Large uploads/downloads via presigned URLs with short expiry; the service never proxies large payloads. |
| STO-003 | S | Lifecycle policies (transition/expiry) declared in IaC per bucket. |
| STO-004 | S | Virus/malware scanning hook for user-uploaded content before it becomes readable. |

### 5.13 Localisation, Time, and Miscellany

| ID | Pri | Requirement |
|---|---|---|
| MSC-001 | M | All timestamps stored and transmitted as UTC ISO-8601; `TimeProvider` injected everywhere (no `DateTime.Now`, analyzer-enforced) to keep code testable. |
| MSC-002 | S | Localisation via resource files with `Accept-Language` negotiation; error `title`s localisable, `type` URIs stable. |
| MSC-003 | M | Culture-invariant parsing/formatting for machine interfaces. |
| MSC-004 | S | Money handled as a value object with currency; never `double`. |

---

## 6. Non-Functional Requirements

### 6.1 Performance

| ID | Pri | Requirement |
|---|---|---|
| PRF-001 | M | Template overhead: platform middleware adds ≤ 5 ms p99 to a no-op request. |
| PRF-002 | M | Cold start: containerised service ready in ≤ 15 s; Lambda (AOT) ≤ 500 ms cold start. |
| PRF-003 | M | Baseline reference service sustains ≥ 1,000 rps per 1 vCPU / 1 GiB container for a trivial endpoint. |
| PRF-004 | S | Load and soak test harness (NBomber/k6) included in the template with a CI performance-regression gate (> 10% p99 regression fails). |
| PRF-005 | M | Memory: no unbounded caches, channels, or collections; container memory limit set and a `dotnet-gcdump` runbook provided. |

### 6.2 Scalability and Availability

| ID | Pri | Requirement |
|---|---|---|
| SCA-001 | M | Services are stateless; any state lives in a database, cache, or object store. Sticky sessions prohibited. |
| SCA-002 | M | Horizontal autoscaling driven by CPU, memory, RPS, or queue depth (KEDA / ECS target tracking); scaling policy in IaC. |
| SCA-003 | M | Minimum 2 replicas across ≥ 2 AZs in prod; PodDisruptionBudget / ECS deployment circuit breaker configured. |
| SCA-004 | M | Availability target per service tier: Tier 1 = 99.95%, Tier 2 = 99.9%, Tier 3 = 99.5% monthly. |
| SCA-005 | S | Multi-region strategy documented per service (active-passive default); RTO/RPO stated per tier. |

### 6.3 Maintainability and Quality

| ID | Pri | Requirement |
|---|---|---|
| QUA-001 | M | Nullable reference types, `TreatWarningsAsErrors`, latest analysis level, and `.editorconfig` enforced solution-wide. |
| QUA-002 | M | Unit test line coverage ≥ 80% on Application and Domain projects; CI enforces the threshold. |
| QUA-003 | M | Architecture tests (NetArchTest/ArchUnitNET) enforce layering, naming, sealed-by-default, and forbidden references. |
| QUA-004 | M | Integration tests run against **Testcontainers** (PostgreSQL, Valkey, **RabbitMQ** with the management plugin) and **LocalStack** (S3/DynamoDB/Secrets Manager) — no shared cloud environment or shared broker required for PR builds. Each test class gets an isolated vhost. |
| QUA-005 | M | `WebApplicationFactory`-based end-to-end tests over the real middleware pipeline, including auth. |
| QUA-006 | S | Mutation testing (Stryker.NET) on the Domain project with a minimum mutation score. |
| QUA-007 | M | Platform packages ship with XML docs, a migration guide per major version, and samples. |

### 6.4 Operability

| ID | Pri | Requirement |
|---|---|---|
| OPS-001 | M | Every service ships a runbook covering: purpose, dependencies, SLOs, alert→action mapping, common failures, rollback, escalation. |
| OPS-002 | M | Default dashboards (Grafana/CloudWatch) auto-provisioned per service from a shared template: RED, USE, dependencies, queues, errors. |
| OPS-003 | M | Default alerts auto-provisioned: SLO burn rate (multi-window multi-burn-rate), error rate, latency, DLQ depth, queue age, health-check failure, restart loops, cert expiry. |
| OPS-004 | M | Alerts route to the owning team via the catalog descriptor; no unowned alerts. |
| OPS-005 | S | Cost attribution: mandatory AWS tags (`service`, `team`, `cost_center`, `environment`, `tier`) enforced by IaC policy (tag policies / Checkov rule). |
| OPS-006 | S | Deployment markers emitted to the observability backend to correlate deploys with regressions. |

### 6.5 Compliance and Governance

| ID | Pri | Requirement |
|---|---|---|
| GOV-001 | M | Audit trail for data mutations of regulated entities: who, what, when, before/after, correlation ID — retained per the data-retention policy. |
| GOV-002 | M | Data classification attributes on DTO/entity properties drive redaction (LOG-004) and encryption (SEC-009). |
| GOV-003 | S | GDPR support hooks: data-subject export and erasure interfaces implementable per service, with a platform-provided contract. |
| GOV-004 | M | Log/data retention configured per classification in IaC. |
| GOV-005 | M | All platform and service dependencies licence-scanned; copyleft licences blocked by policy. |

---

## 7. AWS Platform Requirements

### 7.1 Runtime and Infrastructure

| ID | Pri | Requirement |
|---|---|---|
| AWS-001 | M | Primary runtime: **ECS on Fargate** or **EKS**; the template must support both with a single flag and no application code change. |
| AWS-002 | M | Container images built multi-stage, published to **ECR** with immutable tags (`{semver}-{gitsha}`), scanned on push. |
| AWS-003 | M | All service-owned AWS resources declared in **Terraform** modules within the service repo; the platform publishes reusable modules (queue-with-dlq, aurora-cluster, s3-bucket, service-workload). |
| AWS-004 | M | IAM policies scoped to least privilege and generated from the resources the service declares; wildcard resource ARNs rejected by IaC policy checks. |
| AWS-005 | M | Ingress via ALB (or API Gateway for Lambda/public edge) with WAF, TLS from ACM, and access logs to S3. |
| AWS-006 | M | Private subnets only for workloads; VPC endpoints for S3, DynamoDB, Secrets Manager, SSM, ECR, and CloudWatch to avoid NAT egress. Amazon MQ brokers are deployed **without public accessibility**, reachable only from workload security groups on 5671/AMQPS and 443 (management). |
| AWS-010 | M | Amazon MQ for RabbitMQ provisioned by a shared Terraform module: 3-node cluster across AZs, CMK encryption, private-only, maintenance window, CloudWatch log export, and a broker-level alarm set (BRK-015). |
| AWS-007 | S | AWS Distro for OpenTelemetry (ADOT) collector deployed as a sidecar/daemonset for OTLP ingestion. |
| AWS-008 | M | All resources tagged per OPS-005. |
| AWS-009 | S | Lambda archetype uses Native AOT, SnapStart where applicable, and Powertools for AWS Lambda (.NET) for logging/tracing/metrics parity with the container archetype. |

### 7.2 CI/CD

| ID | Pri | Requirement |
|---|---|---|
| CD-001 | M | Pipeline stages: restore → build → analyze (SAST/lint) → unit test → integration test → package → SBOM + sign → scan → publish → deploy(dev) → smoke → deploy(staging) → test → **manual gate** → deploy(prod). |
| CD-002 | M | Trunk-based development; every commit to `main` is a release candidate. Conventional Commits drive SemVer. |
| CD-003 | M | Zero-downtime deployment: rolling (ECS/EKS) with health-gated rollout; **blue/green or canary** for Tier 1 services via CodeDeploy or Argo Rollouts. |
| CD-004 | M | Automatic rollback on health-check failure or CloudWatch alarm breach during the bake window. |
| CD-005 | M | OIDC federation from the CI provider to AWS IAM roles; no long-lived cloud credentials in CI. |
| CD-006 | M | Migration stage runs before the app deploy and is idempotent and rollback-aware (DAT-004). |
| CD-007 | S | Ephemeral PR preview environments for `http-api` services. |
| CD-008 | M | Build reproducibility: pinned SDK via `global.json`, Central Package Management, lock files (`packages.lock.json`) with `--locked-mode` restore. |
| CD-009 | S | Deployment metadata (version, SHA, actor, time) published to the service catalog and emitted as a deployment marker (OPS-006). |

### 7.3 Local Developer Experience

| ID | Pri | Requirement |
|---|---|---|
| DEV-001 | M | `docker compose up` (or **.NET Aspire** AppHost) starts the service plus PostgreSQL, Valkey, **RabbitMQ (with the management UI on :15672)**, and LocalStack, with the service's topology declared and seeded data loaded. |
| DEV-002 | M | No AWS account access required to run or test the service locally. |
| DEV-003 | M | Hot reload (`dotnet watch`) works for the primary host project. |
| DEV-004 | S | .NET Aspire dashboard available locally for traces, logs, and metrics without external infrastructure. |
| DEV-005 | M | Pre-commit hooks: format, lint, secret scan (gitleaks). |
| DEV-006 | M | `.http` / Bruno collection with sample requests, including a local token issuer for auth flows. |
| DEV-007 | S | Devcontainer definition for a reproducible toolchain. |

---

## 8. Acceptance Criteria

The template is accepted when a **reference service** built from it demonstrates all of the following, verified in a staging environment:

| # | Criterion |
|---|---|
| AC-01 | Scaffold → running locally with full dependency stack in ≤ 10 minutes, no manual edits. |
| AC-02 | Scaffold → deployed to a dev AWS environment via the generated pipeline in ≤ 60 minutes, including provisioned infrastructure. |
| AC-03 | A single request produces correlated logs, a distributed trace spanning API → DB → RabbitMQ → consumer, and RED metrics, all queryable by trace ID. |
| AC-04 | Killing the database makes readiness fail while liveness stays healthy; the pod is not restart-looped and recovers automatically when the DB returns. |
| AC-05 | A downstream returning 500s trips the circuit breaker, emits an alert, and the service degrades per its documented fallback rather than cascading. |
| AC-06 | A duplicate message is consumed exactly once (idempotency), and a poison message lands in the DLQ after the configured attempts and raises an alarm. |
| AC-07 | An outbox event survives an application crash between DB commit and publish, and is published on recovery. |
| AC-17 | A **command** sent to a service is handled exactly once by exactly one consumer; attempting to register a second consumer for the same command type fails at startup. |
| AC-18 | A command failing validation goes straight to the DLQ with a structured rejection reason and **zero** retry attempts, while a command failing on a transient timeout exhausts its retries first. |
| AC-19 | An **event** published once is delivered independently to three subscribers; one subscriber failing permanently fills only its own DLQ and does not affect the other two or the publisher. |
| AC-20 | Adding a new subscriber to an existing event requires no change or redeploy of the publishing service. |
| AC-21 | A subscriber's routing-key binding pattern prevents non-matching events from reaching its queue at all (verified by `messages_ready` on the queue, not handler counts). |
| AC-22 | An archived event range is replayed to a single rebuilt subscriber without re-delivering to any other subscriber, and replayed messages are flagged as such. |
| AC-23 | A request/reply call times out cleanly when no reply arrives; a reply arriving after the timeout is discarded and counted, not delivered to a stale waiter. |
| AC-24 | A trace spans HTTP request → outbox → topic exchange → subscriber queue → handler → downstream command, as one connected trace. |
| AC-25 | Killing the active RabbitMQ node fails over to another cluster node; publishers and consumers reconnect and recover topology automatically with **zero message loss** (publisher confirms + quorum queues + unacked redelivery) and no manual intervention. |
| AC-26 | A message published with a routing key that matches no binding lands in the `unroutable` queue and raises an alert — it is never silently dropped. |
| AC-27 | A transient handler failure is retried via the delay-queue ladder with the expected backoff, **without** blocking the consumer's prefetch or holding the channel; after the attempt cap it is dead-lettered with its `x-death` history intact. |
| AC-28 | A message delayed by 10 minutes is delivered within tolerance using only bundled RabbitMQ features (no `delayed_message_exchange` plugin), proving Amazon MQ compatibility (BRK-010). |
| AC-29 | An attempt to bind a second queue to a command routing key fails IaC validation, and a service whose declared topology diverges from the provisioned topology fails startup rather than auto-creating it. |
| AC-30 | Broker credential rotation in Secrets Manager completes with no dropped messages and no restart (BRK-012, CFG-007). |
| AC-08 | A rolling deploy across a backwards-compatible migration completes with zero failed requests under sustained load. |
| AC-09 | An automatic rollback triggers when a deliberately broken build breaches the bake-window alarm. |
| AC-10 | Secrets are absent from the image, logs, config endpoint, and environment inspection; rotation completes with no downtime. |
| AC-11 | An unauthenticated and an under-scoped request are both rejected, and both produce audit events. |
| AC-12 | SIGTERM drains in-flight HTTP requests and in-flight RabbitMQ deliveries with zero message loss and zero 5xx; undelivered messages are redelivered to a surviving replica. |
| AC-13 | A feature flag kill switch disables a dependency path in < 60 s without a deploy. |
| AC-14 | A breaking OpenAPI change and an incompatible event-schema change each fail CI. |
| AC-15 | The pipeline produces a signed SBOM and blocks on an injected critical CVE. |
| AC-16 | A running service can consume a new platform BOM version and redeploy with no source changes. |
| AC-31 | A service composed with `builder.AddMicroFx()` and **no** configuration lambda starts, serves, reports healthy, and emits correlated logs/traces/metrics — with zero platform code written (FEA-010). |
| AC-32 | A custom feature added by package reference alone (assembly attribute, no registration call) appears in the startup banner, in `/internal/features`, and in the resolved order at its declared graph position (FEA-021, FEA-022). |
| AC-33 | A feature disabled via configuration (`MicroFx:Features:{id}:Enabled=false`) is absent at runtime, and the catalog reports it as disabled **with the configuration path that disabled it** (FEA-031, FEA-036). |
| AC-34 | Attempting to disable a kernel feature fails startup with a message naming the feature; the service does not start in a partially-observable state (FEA-032). |
| AC-35 | A feature declaring `Replaces` displaces the built-in and inherits its edges: an unrelated feature ordered `After` the original still runs after the replacement, without modification (FEA-033). |
| AC-36 | Introducing a dependency cycle between two features fails startup with the **full cycle path** printed, not a generic error (FEA-013). |
| AC-37 | A service registering its own implementation of any platform interface keeps it — the built-in feature's `TryAdd` does not overwrite it — and the substitution is reported (FEA-035, FEA-036). |
| AC-38 | Shutdown ordering is observably reverse-dependency: consumers cancel before the transport connection closes, and telemetry flushes last, with no message loss (FEA-016, MSG-016). |
| AC-39 | The **identical** handler, publisher, contract, and test code passes end-to-end against the in-memory transport and against RabbitMQ, with only the adapter package and configuration differing (TRN-006). |
| AC-40 | A transport that does not advertise `ManualAcknowledgement` fails startup when a subscription requires at-least-once — it does not start and silently deliver at-most-once (TRN-005). |
| AC-41 | A transport lacking native delayed delivery still performs the full retry backoff curve via the core's scheduled-message store, with no in-process sleep holding a delivery, and the emulation is reported in the catalog (TRN-004, MSG-018). |
| AC-42 | A misconfigured service reports **every** validation failure (bad connection string, missing destination, unbound option) in a single startup run, not one per restart (FEA-044). |
| AC-43 | A cold start's duration is attributable per feature from `microfx.feature.startup.duration` and from the `microfx.startup` trace (FEA-043). |
| AC-44 | The core solution builds, runs its full test suite, and starts a working service with **no cloud SDK package** restored (§9.2 constraint). |
| AC-45 | A handler that mutates an aggregate and publishes an integration event commits both atomically; a crash between commit and publish still delivers the event on recovery — demonstrated on the **built-in** EF Core store with no adapter package (DAT-000, TXN-001). |
| AC-46 | A nested `BeginAsync` inside an outer scope commits once, not twice; rolling back the outer scope discards the inner work (TXN-002). |
| AC-47 | An explicit transaction on a connection configured with `EnableRetryOnFailure` succeeds — the platform's execution-strategy wrapping prevents the EF Core exception a hand-rolled transaction would hit (TXN-003). |
| AC-48 | Switching database engine (SQLite → PostgreSQL) requires only a different EF provider and connection string: no MicroFx package change, no platform code change (DAT-000a). |
| AC-49 | A service caches, reads, invalidates, and observes hit/miss metrics with **no cache infrastructure deployed**; adding `MicroFx.Caching.Redis` and a connection string introduces cross-instance sharing with **zero changes to cache-calling code** (CAC-001, CAC-001a, CAC-001b). |
| AC-50 | Killing the L2 cache degrades to L1/origin with no failed requests and no readiness impact (CAC-004). |

---

## 9. Assumptions, Constraints, Risks

### 9.1 Assumptions

- A shared AWS Landing Zone (accounts, VPCs, TGW, org SCPs) already exists.
- A central identity provider issuing OIDC/OAuth tokens exists.
- A central observability backend accepting OTLP exists.
- A single container orchestration platform (ECS Fargate **or** EKS) is chosen as the org default; the other is supported but not the golden path.

### 9.2 Constraints

- .NET 10 LTS is the minimum runtime; services must upgrade within 6 months of a new LTS.
- **The `MicroFx` core references no cloud SDK.** Cloud services are reached through ports with in-box defaults and adapter packages (§3.2). Ports exist primarily for testability and policy enforcement; cross-cloud portability is a by-product, not a commitment — the platform does not promise that every AWS adapter has an equivalent elsewhere.
- AWS is the reference deployment. Terraform is the mandated IaC tool.
- **RabbitMQ is the default message transport** (via `MicroFx.Messaging.RabbitMq`, deployed on Amazon MQ) for service-to-service asynchronous messaging, and the reference adapter against which the port is validated. AWS-native messaging (SNS/SQS/EventBridge) is used for AWS-service integration (S3 notifications, EventBridge Scheduler) and may be adopted as a transport where a service justifies it; adopting a non-default transport requires an ADR but no longer requires platform work.
- Message ordering is **not** globally guaranteed; ordering requirements must be narrowed to a per-aggregate scope, cost a throughput trade-off (CMD-007, EVT-012), and are refused at startup by transports that cannot honour them (TRN-005).
- Feature ids under the `microfx.` prefix are reserved to the platform and are not available to service or third-party features (FEA-004).

### 9.3 Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Platform abstraction becomes a bottleneck for teams with unusual needs | High | Escape hatches: all platform registrations replaceable via DI; documented "off-road" process |
| Version skew — services stuck on old platform packages | High | Template sync tooling (TPL-007), platform version reported to the catalog, drift dashboard, deprecation windows |
| Over-abstraction of AWS SDKs | Medium | Thin-abstraction principle; expose the underlying client where wrapping adds nothing |
| Template becomes a fork-and-forget copy | High | Platform capabilities live in NuGet packages, not scaffolded source, wherever possible |
| Cross-cutting middleware cost creep | Medium | PRF-001 budget enforced by a benchmark in platform CI |
| **Broker is a shared single point of failure** — unlike SQS, RabbitMQ is a stateful cluster the org now operates | High | Multi-AZ quorum queues (BRK-001/002), publisher confirms, capacity alarms (BRK-015), per-domain broker split (open question 12), documented failover runbook, AC-25 as a recurring game-day |
| **Amazon MQ plugin restriction** discovered late, forcing a migration to self-managed mid-programme | High | BRK-010 makes the constraint explicit up front; MSG-014 and EVT-013 designed around it from day one |
| **Messaging framework licensing** (MassTransit v9 commercial) changes cost or forces a rewrite | High | Open question 9 resolved before M2; platform abstraction (MSG-001) keeps handler code framework-agnostic so the transport layer stays replaceable |
| RabbitMQ operational expertise is scarce in the org | Medium | Managed Amazon MQ by default, platform-owned topology (BRK-009) so teams never hand-craft exchanges, broker runbook and dashboards shipped with the template |

---

## 10. Delivery Plan

| Milestone | Contents | Exit criteria |
|---|---|---|
| **M0 — Feature kernel** | Descriptor, discovery, graph resolution, four build passes, lifecycle, pipeline stages, catalog + banner + `/internal/features`, override/replace/disable | FEA-001…045; AC-31 – AC-37, AC-42, AC-43 |
| **M1 — Foundations** | Kernel features: core, configuration, observability, health, diagnostics; `http-api` archetype; local dev stack | AC-01, AC-03, AC-44 |
| **M2 — Production Ready (GA)** | `api`, `validation`, `security`, `multitenancy`, `resilience`, `caching`, `serviceclients`, `ratelimiting`, `idempotency`; graceful shutdown; CI/CD; IaC modules | All **MUST** requirements outside messaging; AC-01 – AC-05, AC-08 – AC-12, AC-14 – AC-16 |
| **M3 — Generic messaging** | `microfx.messaging`: envelope, handler pipeline, outbox, inbox, retry/dead-letter policy, claim-check, abstract topology, capability negotiation, **in-memory transport**; `persistence`; `jobs`; `consumer`/`worker` archetypes | TRN-001…008; AC-06, AC-07, AC-13, AC-17 – AC-21, AC-24, AC-38, AC-40 – AC-42 |
| **M4 — RabbitMQ adapter** | `MicroFx.Messaging.RabbitMq`: connections/channels, confirms, quorum queues, DLX/DLQ, TTL retry ladder, topology assertion, direct reply-to, broker metrics; conformance suite | AC-25 – AC-30, AC-39 |
| **M5 — Scale & Polish** | Request/reply (REQ), event archive + replay, subscriber registry, `featureflags` GA, `storage`, cloud adapters (`MicroFx.Aws`), Lambda + gRPC archetypes, contract testing, chaos hooks, perf gates | All **SHOULD** requirements; AC-22, AC-23 |
| **M6 — Ecosystem** | Backstage integration, template sync at scale, multi-region, sagas (ORC), additional transport adapters | Adoption target: 80% of new services on the golden path |

> **Sequencing rationale.** The feature kernel moves to M0 because every subsequent milestone ships *as features*; building capabilities first and retrofitting a composition model second is how a platform ends up with two composition models. Messaging splits into M3 (generic, testable against the in-memory transport with no broker in CI) and M4 (RabbitMQ), which means the messaging semantics are proven before any broker is involved — and the adapter's job is reduced to a mapping exercise with a conformance suite to pass.

---

## Appendix A — Requirement Index

| Area | Prefix | Count |
|---|---|---|
| Template | TPL | 10 |
| **Feature model (composition)** | **FEA** | **24** |
| **Messaging — transport port** | **TRN** | **10** |
| Configuration | CFG | 9 |
| Logging / Tracing / Metrics / Health | LOG, TRC, MET, HLT | 7 / 6 / 7 / 6 |
| API | API | 15 |
| Security | SEC | 15 |
| Resilience | RES | 10 |
| Messaging — RabbitMQ adapter | BRK | 18 |
| Messaging — common (transport-neutral) | MSG | 20 |
| Messaging — commands | CMD | 10 |
| Messaging — events (pub/sub) | EVT | 15 |
| Messaging — request/reply | REQ | 4 |
| Messaging — orchestration | ORC | 2 |
| Data | DAT | 16 |
| **Transactions** | **TXN** | **10** |
| Caching | CAC | 9 |
| Jobs | JOB | 6 |
| Feature flags | FLG | 6 |
| Service-to-service | S2S | 5 |
| Storage | STO | 4 |
| Misc | MSC | 4 |
| Performance | PRF | 5 |
| Scalability | SCA | 5 |
| Quality | QUA | 7 |
| Operability | OPS | 6 |
| Governance | GOV | 5 |
| AWS | AWS | 9 |
| CI/CD | CD | 9 |
| Developer experience | DEV | 7 |

## Appendix B — Open Questions

### Closed by v2.0

| # | Question | Resolution |
|---|---|---|
| 8 | Amazon MQ vs. self-managed RabbitMQ | **De-risked, not decided.** Both are adapter configuration, not platform architecture. Default remains Amazon MQ with BRK-010 designed around; switching no longer touches core code. |
| 9 | MassTransit vs. a thin in-house abstraction | **Resolved: thin in-house layer over the `IMessageTransport` port.** No licence exposure, and §5.6.0 shows the port surface is small. MassTransit could itself become an adapter if ever wanted. |
| 13 | Is event replay needed at GA? | **Defer the tooling, ship the archive consumer group from day one** — events not captured cannot be retroactively archived. Now transport-neutral (EVT-011). |

### Still open

1. ECS Fargate or EKS as the single golden path? (drives AWS-001 effort)
2. Observability backend: CloudWatch/X-Ray native, Grafana Cloud, or Datadog? (drives cost and TRC-003 config)
3. Aurora PostgreSQL vs. DynamoDB as the *default* in the template when the team has no strong preference.
4. Feature flag provider: AWS AppConfig (cheap, coarse) vs. a commercial provider (richer targeting).
5. Is multi-tenancy a first-class platform concern for all services, or only for the SaaS-facing subset? (drives SEC-005, DAT-009)
6. Authorization: centralised PDP (Cedar/AVP/OPA) or in-service policies? (drives SEC-004)
7. Required audit retention period per data classification (drives GOV-001, GOV-004).
8. **Amazon MQ vs. self-managed RabbitMQ on EKS.** Amazon MQ removes operational burden but blocks all custom plugins (BRK-010), caps tuning, and lags upstream versions. Self-managed via the Cluster Operator gives streams, delayed-message exchange, and full control at the cost of owning upgrades, quorum rebalancing, and on-call. Drives BRK-001, MSG-014, EVT-013.
9. **Messaging framework: MassTransit vs. a thin in-house abstraction over `RabbitMQ.Client`.** MassTransit delivers most of section 5.6 out of the box, but v9 moved to a commercial licence — v8 remains free but its long-term support horizon must be confirmed. Alternatives: Rebus, Wolverine, or building on `RabbitMQ.Client` v7. This is the single highest-leverage decision in the messaging workstream; it should be settled before M2 starts.
10. Do commands cross team boundaries at all, or are inter-team integrations events-only with commands reserved for intra-team calls? A governance decision, not a technical one; it shapes CMD-005.
11. Is request/reply over messaging (REQ-001…003) needed at GA, or can it wait for a proven use case? Building it early tends to invite misuse where HTTP would do.
12. **One broker per environment, or one per domain/bounded context?** A shared broker is cheaper and simpler but couples blast radius across teams; per-domain brokers isolate failure and noisy neighbours at the cost of federation for cross-domain events. Drives BRK-011, BRK-017.
13. Is event replay (EVT-011) needed at GA? On RabbitMQ it is a platform-built archive-and-republish capability rather than a broker feature, so it carries real cost that SNS/EventBridge would have absorbed.
