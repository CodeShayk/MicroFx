# MicroFx Developer Guide

**For .NET developers of every level.** No prior knowledge of MicroFx is assumed, and the concepts it builds on — hosting, dependency injection, middleware — are explained where they matter.

---

## Contents

**Getting started**
1. [What MicroFx is, and what it is not](#1-what-microfx-is-and-what-it-is-not)
2. [Your first service in five minutes](#2-your-first-service-in-five-minutes)
3. [What just happened](#3-what-just-happened)

**Core concepts**
4. [Features: the one idea to understand](#4-features-the-one-idea-to-understand)
5. [Configuration and options](#5-configuration-and-options)
6. [Turning things on and off](#6-turning-things-on-and-off)

**Building a service**
7. [HTTP endpoints](#7-http-endpoints)
8. [Validation and errors](#8-validation-and-errors)
9. [Security](#9-security)
10. [Data and transactions](#10-data-and-transactions)
11. [Messaging](#11-messaging)
12. [Caching](#12-caching)
13. [Background jobs](#13-background-jobs)
14. [Feature flags](#14-feature-flags)
15. [Calling other services](#15-calling-other-services)

**Going further**
16. [Writing your own feature](#16-writing-your-own-feature)
17. [Testing](#17-testing)
18. [Observability](#18-observability)
19. [Going to production](#19-going-to-production)
20. [Troubleshooting](#20-troubleshooting)
21. [Reference](#21-reference)

---

## 1. What MicroFx is, and what it is not

### The problem

Every microservice needs roughly the same twenty things: structured logging, health probes, configuration, error responses, authentication, retries, caching, messaging, background jobs. None of it is your business logic. All of it is fiddly, and most of it is subtly wrong the first time.

Worse, the mistakes are quiet. A health probe that checks the database looks fine until an outage restarts every replica. A retry that fires on a POST looks fine until it charges a customer twice. A cache key that forgets the tenant looks fine until one customer sees another's data.

### What MicroFx does

It provides those twenty things, built once, with the reasoning written down — and it makes the *dangerous* defaults hard to reach.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddMicroFx();                    // ← everything
var app = builder.Build();
await app.RunMicroFxAsync();
```

### What MicroFx is not

- **Not an ORM, web framework, or DI container.** It sits on ASP.NET Core, EF Core, and `Microsoft.Extensions.*`. You keep every skill you already have.
- **Not cloud-specific.** The core references no cloud SDK. AWS, Redis, and RabbitMQ are adapter packages you add if you want them.
- **Not all-or-nothing.** Any capability can be disabled, configured, replaced, or overridden one service at a time.

### Who it is for

Teams running more than one .NET service who are tired of the twentieth slightly-different `Program.cs`. It is equally usable on a single service — you simply get a very good starting point.

---

## 2. Your first service in five minutes

### Prerequisites

.NET 10 SDK. Nothing else — no database, no message broker, no Docker, no cloud account.

### Create the project

```bash
mkdir HelloMicroFx && cd HelloMicroFx
dotnet new web
dotnet add package MicroFx
```

### Write `Program.cs`

```csharp
using MicroFx.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddMicroFx();

var app = builder.Build();

app.MapGet("/hello", () => new { message = "Hello from MicroFx" });

await app.RunMicroFxAsync();
```

### Run it

```bash
dotnet run
```

You will see a banner like this:

```
MicroFx composed: HelloMicroFx 1.0.0 [Development] host=Web role=all
  10 enabled, 9 disabled, 0 replaced
  + microfx.core                 kernel [clock=system, instance=a3f91c4d2e88]
  + microfx.configuration        kernel [sources=default]
  + microfx.observability        kernel [otlp=not configured, sampleRatio=1]
  + microfx.health               kernel [probes=live,ready,startup]
  + microfx.diagnostics          kernel
  + microfx.api                         [version=v1, openapi=True]
  + microfx.validation                  [validators=0]
  + microfx.ratelimiting                [limit=100/60s]
  + microfx.security                    [auth=jwt, policy=deny-by-default]
  + microfx.caching                     [l1=in-memory, l2=none]
  - microfx.messaging                   (EnabledByDefault = false)
  ...
```

### Try it

```bash
curl localhost:8080/hello                 # your endpoint
curl localhost:8081/health/live           # is the process alive?
curl localhost:8081/health/ready          # can it serve traffic?
curl localhost:8081/internal/features     # what is switched on, and why
curl localhost:8081/internal/info         # version, commit, environment
```

Open `http://localhost:8081/openapi/reference` for browsable, executable API documentation.

> **Two ports.** Application traffic is on **8080**. Health and diagnostics are on **8081**. This is deliberate — see [§3](#3-what-just-happened).

---

## 3. What just happened

`AddMicroFx()` composed ten features. Here is what each bought you.

### Structured logging with correlation

Every log record carries a correlation id, trace id, and service identity. Send a request with `X-Correlation-Id: my-trace` and it comes back on the response and appears on every log line for that request.

The header is **validated** before use — length-capped and character-restricted. An unvalidated header that reaches your logs is a log-injection vector.

### Health probes that will not hurt you

| Probe | Checks | Why |
|---|---|---|
| `/health/live` | Only that the process responds | A liveness probe that checks the database restarts every replica during a database outage — turning one outage into an outage *plus* a restart storm. The restarts do not help; the database is still down. |
| `/health/ready` | Every dependency | Stops traffic being routed here while a dependency is down, without killing the process. |
| `/health/startup` | One-time preconditions | Lets an orchestrator wait for a slow start without a long liveness timeout. |

Probes are also **anonymous** even when your service denies by default — otherwise your orchestrator would get 401 and kill every pod.

### Errors that say enough and no more

An unhandled exception becomes an RFC 9457 problem response:

```json
{
  "type": "https://problems.microfx.dev/500",
  "title": "An unexpected error occurred",
  "status": 500,
  "traceId": "a3d0332f451af2916385fa2a0dceff38"
}
```

No stack trace, no exception message, no internal type names — those go to the log, where they belong. The caller gets a trace id, which is useless to an attacker and sufficient for an operator.

In Development you also get the exception type and message, gated on the *environment* rather than a flag, so an accidental `true` in production config cannot start leaking stack traces.

### Security headers and a closed CORS default

CSP, `X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy`, and `Permissions-Policy` are applied. `Server` and `X-Powered-By` are stripped. CORS permits nothing until you list origins — there is no implicit wildcard.

### Rate limiting before authentication

An unauthenticated flood costs a dictionary lookup, not a signature validation. Partitioning uses the authenticated subject when present and the connection's remote IP otherwise — **never** `X-Forwarded-For`, which a caller can forge into unlimited distinct identities.

### The management port

Health, diagnostics, and API documentation are on a **different port**, mapped onto a different route builder. Exposing them publicly requires deliberately using the wrong object. Do not add 8081 to a public load balancer.

---

## 4. Features: the one idea to understand

Everything else follows from this.

### The problem with `Add` and `Use`

ASP.NET Core composes through two lists:

```csharp
builder.Services.AddX();   // order rarely matters
app.UseY();                // order matters enormously
```

Middleware order is the *sequence of statements in your file*. Move `UseAuthentication()` below `UseAuthorization()` and you have a silently unauthenticated service. There is no lifecycle hook for "check this before serving traffic", no way to ask "is caching on?", and no way to express "tenancy must run after authentication" other than a comment.

### What a feature is

A **feature** is a capability with an identity, declared ordering, and a lifecycle:

```csharp
public interface IMicroFxFeature
{
    FeatureDescriptor Descriptor { get; }     // id, ordering, activation
    void Configure(FeatureBuildContext context);  // register services
}
```

Built-in and custom features use the **identical** contract. There is no privileged path you cannot use.

### Ordering is declared, not positional

```csharp
public FeatureDescriptor Descriptor => new()
{
    Id = "acme.audit",
    DependsOn = [BuiltIn.Core, BuiltIn.Security],  // cannot work without these
    After = [BuiltIn.MultiTenancy],                // prefer to run after, if present
    Before = [BuiltIn.Api],                        // prefer to run before, if present
};
```

The kernel topologically sorts these. Ties break deterministically, so the order is identical on your machine and in CI. A cycle fails startup with the **full path** (`a → b → c → a`), not a vague "cycle detected".

### Middleware order is a fixed enum

A feature names a **stage**, never a position:

```csharp
public void UsePipeline(FeaturePipelineContext context) =>
    context.Use(PipelineStage.Telemetry, app => app.UseMiddleware<AuditMiddleware>());
```

| Order | Stage | |
|---|---|---|
| 1 | `Exception` | Nothing above it can throw unhandled |
| 2 | `Diagnostics` | Correlation id, activity, log scope |
| 3 | `ForwardedHeaders` | Real client IP, before anything decides on it |
| 4 | `SecurityHeaders` | |
| 5 | `Management` | Health and diagnostics short-circuit |
| 6 | `Timeout` | |
| 7 | `RateLimiting` | **Before** auth — cheap rejection first |
| 8 | `Authentication` | |
| 9 | `Tenancy` | **After** auth — tenant comes from a verified claim |
| 10 | `Authorization` | |
| 11 | `Telemetry` | Records the authenticated, tenanted request |
| 12 | `PreEndpoint` | Idempotency |
| 13 | `Endpoint` | |

You cannot reorder authentication relative to authorization. That is the point.

### The lifecycle

```csharp
ValueTask StartingAsync(...)   // before traffic — preflight, warm-up. Throwing aborts startup.
ValueTask StartedAsync(...)    // after listening
ValueTask StoppingAsync(...)   // on SIGTERM, in REVERSE order
```

Reverse-order shutdown is what makes "cancel consumers → drain in-flight → close connections → flush telemetry" correct rather than coincidental. Each phase is budgeted per feature, so a hanging feature fails startup **naming itself** rather than hanging your deployment anonymously.

### Introspection

The feature graph is operational data:

```bash
curl localhost:8081/internal/features
```

Returns every feature with its resolved order, enabled state, **why** it is disabled and which config key did it, its declared edges, the facts it reported, and its last-startup timings.

---

## 5. Configuration and options

### Where settings come from

Standard .NET layering — `appsettings.json`, `appsettings.{Environment}.json`, environment variables, command line — plus any secret store you add through an adapter.

Everything MicroFx reads lives under `MicroFx:`:

```json
{
  "MicroFx": {
    "Service":  { "Name": "orders", "Team": "commerce" },
    "Host":     { "TrafficPort": 8080, "ManagementPort": 8081 },
    "Api":      { "MaxRequestBodyBytes": 1048576 },
    "Security": { "Authority": "https://idp.example.com" }
  }
}
```

### Options are validated at startup

Every options class is bound, validated, and **checked at startup** — not at first use. Bad configuration fails your deployment, not your first customer request.

A misconfigured service reports **every** problem in one startup:

```
MicroFx startup validation failed with 3 error(s):
  - [microfx.security] Authentication is disabled outside Development.
  - [microfx.persistence] OutboxLeaseDuration must exceed OutboxPollInterval.
  - [microfx.messaging] at-least-once delivery [orders.shipping]: the transport does not
    support explicit acknowledgement, so a crash while handling loses the message.
```

Three restarts' worth of information in one.

### Seeing what is actually in effect

```bash
curl localhost:8081/internal/config
```

Shows every key, its value, and which provider supplied it — with **secrets redacted** by both key name and value shape, so a connection string under an innocuous key is still caught.

This endpoint is **double-gated**: off outside Development unless you also set `AllowConfigurationOutsideDevelopment`. Values are redacted, but key names alone reveal your topology.

---

## 6. Turning things on and off

Four grains, coarsest to finest. All four are visible at `/internal/features`.

### Disable a whole feature

```csharp
builder.AddMicroFx(fx => fx.Disable(BuiltIn.Caching));
```
```json
{ "MicroFx": { "Features": { "microfx.caching": { "Enabled": false } } } }
```

Configuration wins over code, so an operator can kill a capability without a rebuild — and the catalog records which key did it.

**Five features cannot be disabled**: core, configuration, observability, health, diagnostics. Each is a precondition for diagnosing the failure of anything else. A service that has disabled observability cannot tell you why it is broken.

### Configure a feature

```csharp
builder.AddMicroFx(fx => fx.Configure<CachingFeature>(c => /* ... */));
builder.Services.PostConfigure<ObservabilityOptions>(o => o.SampleRatio = 0.5);
```

### Replace a feature

```csharp
builder.AddMicroFx(fx => fx.Replace<CachingFeature, MyCachingFeature>());
```

The replacement **inherits the original's graph edges**, so features that ordered themselves against the original keep working without knowing a substitution happened.

### Override one service

Every built-in registration uses `TryAdd`, so yours always wins:

```csharp
builder.Services.AddSingleton<ICacheKeyBuilder, MyKeyBuilder>();
builder.AddMicroFx();

// or, more visibly, after:
builder.Services.Replace(ServiceDescriptor.Singleton<ICacheKeyBuilder, MyKeyBuilder>());
```

An analyzer (`MFX2001`) enforces `TryAdd` across the platform source, because one stray `AddSingleton` would silently remove this escape hatch for that interface and nobody would notice until they needed it.

### Opt-in features

Some features are **off by default**, because most services do not need them: `microfx.messaging`, `microfx.persistence`, `microfx.jobs`, `microfx.featureflags`, `microfx.multitenancy`, `microfx.storage`.

```csharp
builder.AddMicroFx(fx => fx.Enable(BuiltIn.Persistence));
```

---

## 7. HTTP endpoints

### Endpoint modules

Rather than one growing `Program.cs`, group endpoints into modules. They are discovered automatically:

```csharp
public sealed class OrderEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/orders").WithTags("Orders");

        group.MapGet("/{id}", (string id) => Results.Ok(new { id }));
        group.MapPost("/", (PlaceOrder request) => Results.Created($"/v1/orders/1", request));
    }
}
```

Discovery is scoped to **your application assembly** — a transitively-referenced package cannot contribute routes to a service that never asked for them.

### API documentation

OpenAPI and the Scalar reference UI are served on the **management port**:

```
http://localhost:8081/openapi/v1.json
http://localhost:8081/openapi/reference
```

Both are Development-only unless you explicitly opt in. A full endpoint and schema inventory is a reconnaissance aid; publishing it should be a decision. The "try it" button is also hidden outside Development, so a documentation page cannot be used to exercise a live service.

### Request limits

Body size, request timeout, and form limits are applied by default and enforced at the **server**, so an oversized body is rejected before it is buffered into memory.

---

## 8. Validation and errors

### Validating a request

Write a FluentValidation validator — it is discovered automatically:

```csharp
public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrder>
{
    public PlaceOrderValidator()
    {
        RuleFor(o => o.Sku).NotEmpty().MaximumLength(64).Matches("^[A-Z0-9-]+$");
        RuleFor(o => o.Quantity).InclusiveBetween(1, 1000);
    }
}
```

Attach it to an endpoint:

```csharp
group.MapPost("/", (PlaceOrder request) => Results.Created(...))
     .Validate<PlaceOrder>();
```

A failure returns 400 with per-field errors:

```json
{
  "type": "https://problems.microfx.dev/validation",
  "title": "One or more validation errors occurred",
  "status": 400,
  "traceId": "25954985c489521efa7b174fbf98dd54",
  "errors": {
    "Sku": ["'Sku' must match the required pattern."],
    "Quantity": ["'Quantity' must be between 1 and 1000. You entered 0."]
  }
}
```

Only the property name and the validator's own message cross the boundary. The *attempted value* never does — it may be the credential the caller got wrong.

### Mapping your own exceptions

```csharp
public sealed class DomainExceptionMapper : IExceptionMapper
{
    public ExceptionMapping? Map(Exception exception) => exception switch
    {
        OrderNotFoundException => new(404, "Order not found"),
        InsufficientStockException => new(409, "Insufficient stock"),
        _ => null,   // defer to the next mapper
    };
}
```

Register it before `AddMicroFx()`. **The title and detail are public output** — never put an exception message there unless you authored the exception deliberately.

### Idempotency

Send `Idempotency-Key` on an unsafe request and a retry replays the original response instead of doing the work twice:

```bash
curl -X POST localhost:8080/v1/orders -H 'Idempotency-Key: abc-123' -d '...'
```

The key is scoped by tenant, caller, method, and path, so one caller cannot read back another's recorded response. It is also **fingerprinted against the request body**: reusing a key with different content returns 409 rather than the wrong answer. Failed responses are never recorded — that would pin a transient failure for the whole retention window.

---

## 9. Security

### Turning on authentication

```json
{
  "MicroFx": {
    "Security": {
      "Authority": "https://idp.example.com",
      "Audiences": ["orders-api"]
    }
  }
}
```

That is it. JWT bearer validation is configured with issuer, audience, lifetime, signature, and an **algorithm allow-list** — `none` and symmetric algorithms are rejected outright. Clock skew is 30 seconds, not the framework's five minutes, which would extend the usable life of a revoked token.

### Deny by default

Every endpoint requires an authenticated caller. Opting out is explicit and greppable:

```csharp
group.MapGet("/public", () => "anyone").AllowAnonymous();
```

### Scope policies

```json
{ "MicroFx": { "Security": { "ScopePolicies": { "orders.write": "orders:write" } } } }
```
```csharp
group.MapPost("/", ...).RequireAuthorization("orders.write");
```

Scope matching is **exact-segment**. A substring check would let `orders:read-only` satisfy `orders:read`, which is a quietly wrong authorization decision.

### The audit stream

Authentication failures, authorization denials, and cross-tenant attempts are written to a **separate** logger category (`MicroFx.Audit`), so you can route them to a different sink with a different retention policy — and they survive a service raising its log level to reduce noise.

Records carry the exception **type**, never its message: token-validation messages can echo token contents into your audit stream.

### Startup refuses an open service

Outside Development, startup **fails** if authentication is disabled, no authority is configured, or HTTPS metadata is turned off (which would let an attacker supply their own signing keys). It warns if you have no audience configured or generous clock skew.

### Multi-tenancy

```csharp
builder.AddMicroFx(fx => fx.Enable(BuiltIn.MultiTenancy));
```

The tenant comes from a **verified token claim** by default — never a header, which any caller can forge. Inject `ITenantContext` to read it. Cache keys, log scopes, and query filters are scoped automatically.

The identifier is sanitised to a restricted alphabet, because it flows into cache keys and storage prefixes where an unconstrained value would be a key-injection vector.

---

## 10. Data and transactions

### Setting up

```csharp
builder.AddMicroFx(fx =>
{
    fx.Enable(BuiltIn.Persistence);
    fx.Configure<PersistenceFeature>(p => p.Configure(c => c
        .UseDbContext<OrdersDbContext>(db => db.UseNpgsql(connectionString))
        .UseOutbox()
        .UseInbox()));
});
```

**You supply the EF provider.** MicroFx references `EntityFrameworkCore.Relational` only — no driver — so switching from PostgreSQL to SQL Server needs no MicroFx package.

Register the platform's tables in your context:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyMicroFxPersistence();   // outbox and inbox tables
}
```

### Transactions

```csharp
await unitOfWork.ExecuteAsync(async token =>
{
    database.Orders.Add(order);
    await unitOfWork.SaveChangesAsync(token);
});
```

Use `ExecuteAsync`, not `BeginAsync`. EF Core refuses to combine connection retry with a user-started transaction, because a retry cannot replay a transaction it did not start. Passing your work as a delegate lets the execution strategy own the whole unit and replay it correctly.

`BeginAsync` exists for work that cannot be a delegate, and throws with an actionable message when a retrying strategy is configured — rather than failing later with a cryptic one, or silently not retrying.

**Nesting joins.** An inner `BeginAsync` joins the enclosing transaction; its commit is a no-op and the outermost scope decides. Without this, a shared service called from inside a handler would commit half your work.

### The transactional outbox

The problem: you save an order and publish `OrderPlaced`. If the process dies between them, the order exists and nobody knows.

The outbox writes the *intent to publish* into the same transaction as the state change:

```csharp
await unitOfWork.ExecuteAsync(async token =>
{
    database.Orders.Add(order);

    await outbox.EnqueueAsync(
        projector.Project(new OrderPlacedV1(order.Id), aggregateId: order.Id, tenantId: null),
        token);

    await unitOfWork.SaveChangesAsync(token);   // one commit for both
});
```

A background relay claims pending rows, publishes them, waits for the broker's confirmation, and only then marks them dispatched. A crash between the confirm and the mark **republishes** rather than loses — and duplicates are the consumer inbox's problem, which it already solves.

### Audit and tenant guards

Implement `IAuditable` and `CreatedAt`/`CreatedBy`/`ModifiedAt`/`ModifiedBy` fill themselves. Creation facts are marked immutable on update, so a write cannot rewrite who created a record.

Implement `ITenantOwned` and a write crossing a tenant boundary is **refused** at `SaveChanges`, logged `Critical`, and audited. A query filter protects reads only; the write guard is what closes the other half.

### Migrations

The **migration gate** asserts that applied migrations match what your code expects and fails startup on drift. It does not migrate — that belongs to a pipeline stage, because migrating from N racing replicas is how a rollback stops working.

For local development, `CreateSchemaOnStartup: true` creates the schema. It is refused outside Development.

---

## 11. Messaging

### Commands and events

| | Command | Event |
|---|---|---|
| Consumers | Exactly one | Zero or more, independent |
| Intent | "Do this" | "This happened" |
| Naming | Imperative — `ReserveInventory` | Past tense — `OrderPlaced` |
| Owned by | The **receiver** | The publisher |

```csharp
public sealed record OrderPlacedV1(string OrderId, string Sku) : IIntegrationEvent;
public sealed record ReserveInventory(string OrderId, string Sku) : ICommand;
```

### Declaring what you publish and handle

```csharp
fx.Enable(BuiltIn.Messaging);
fx.Configure<MessagingFeature>(m => m.Configure(c =>
{
    c.PublishesEvent<OrderPlacedV1>();
    c.HandlesCommand<ReserveInventory, ReserveInventoryHandler>();
    c.SubscribesToEvent<OrderPlacedV1, ShippingHandler>(
        owner: "orders",
        configure: s => s.WithConcurrency(4).WithPrefetch(16));
}));
```

You never name a queue, exchange, topic, or ARN. Destinations are resolved from your declarations, which is what lets the same code run on a different broker.

### Handling a message

```csharp
public sealed class ReserveInventoryHandler : IHandleCommand<ReserveInventory>
{
    public Task<HandlerResult> HandleAsync(
        ReserveInventory command, MessageContext context, CancellationToken cancellationToken)
    {
        if (OutOfStock(command.Sku))
        {
            // Will never succeed by waiting — straight to the dead letter, zero retries.
            return Task.FromResult(HandlerResult.Permanent("insufficient-stock"));
        }

        if (!InventoryServiceReachable())
        {
            // Might succeed later — earns the retry ladder.
            return Task.FromResult(HandlerResult.Transient("inventory-unavailable"));
        }

        return Task.FromResult(HandlerResult.Success());
    }
}
```

**Return a result, do not throw.** "Should this be retried?" is a decision, and decisions read better where they are made than as an exception type caught three layers up. An unhandled exception still works — it maps to `Transient`.

### What you get for free

Every delivery passes through a pipeline: envelope decode → kind check → type resolution → expiry → deserialize → context → timeout → **deduplication** → your handler.

- **At-least-once with dedupe.** The same message delivered twice runs your handler once.
- **Retry with backoff.** Exponential, jittered, capped, and never an in-process sleep — a sleeping handler holds its delivery and stalls the consumer.
- **Dead-lettering** with the failure history preserved.
- **Tracing** joins publisher and consumer into one trace.

### Running with no broker at all

The default transport is **in-memory**, so your messaging tests run in milliseconds with no infrastructure. It implements real acknowledgement, redelivery, fan-out, ordering, delay, and dead-lettering — it is not a stub.

It is **refused in production**: messages exist only inside one process, so a restart loses everything in flight. That is silent data loss, not a degraded mode.

### Using RabbitMQ

```bash
dotnet add package MicroFx.Messaging.RabbitMq
```

That is the whole installation — the adapter is discovered by assembly attribute. Configure it:

```json
{
  "MicroFx": {
    "Messaging": {
      "RabbitMq": {
        "Uri": "amqps://broker.example.com:5671/",
        "VirtualHost": "/prod-commerce"
      }
    }
  }
}
```

Your handlers, publishers, contracts, and tests do not change.

### Capability negotiation

Transports differ. MicroFx handles that explicitly rather than hoping:

> **It will emulate a missing convenience and tell you. It will refuse to start rather than silently weaken a correctness guarantee.**

| Missing | Outcome |
|---|---|
| Delayed delivery | Emulated with a scheduled-message store, reported |
| Dead-lettering | Emulated by republishing, reported |
| Broker-side filtering | Emulated consumer-side, waste counted |
| **Acknowledgement** | **Startup fails** — at-least-once cannot be faked |
| **Ordering** | **Startup fails** — order cannot be restored after delivery |
| **Publisher confirms** | **Startup fails** unless you accept the risk explicitly |

---

## 12. Caching

In-memory caching is on by default and needs no infrastructure:

```csharp
var order = await cache.GetOrCreateAsync(
    keys.Build("order", id),
    id,
    async (key, token) => await LoadOrderAsync(key, token),
    cancellationToken: cancellationToken);
```

`ICacheKeyBuilder` produces `{service}:{env}:{tenant}:{entity}:{version}:{id}`. **Tenant scoping is applied by the platform**, because one forgotten prefix leaks one tenant's data to another through a cache hit — a bug with no exception, no stack trace, and no obvious symptom.

Adding a distributed tier is a package reference and a connection string. Key construction, expiration, jitter, and stampede protection behave identically — no cache-calling code changes. The cache is always a strict optimisation: if the distributed tier is unavailable, requests still succeed.

---

## 13. Background jobs

```csharp
fx.Enable(BuiltIn.Jobs);
fx.Configure<JobsFeature>(j => j.Configure(c => c
    .AddCronJob<NightlyReconciliation>("reconcile", "0 2 * * *", job => job
        .AsSingleton()
        .WithTimeout(TimeSpan.FromMinutes(30))
        .WithLease(TimeSpan.FromMinutes(45))
        .WithStalenessThreshold(TimeSpan.FromHours(26)))));
```

```csharp
public sealed class NightlyReconciliation(OrdersDbContext database) : IJob
{
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        // Must be idempotent: a lease can expire mid-run and another replica may start the
        // same job. "Runs exactly once" is not a guarantee any scheduler can make.
    }
}
```

**Cron is evaluated in UTC.** A local-time schedule silently shifts — or runs twice, or not at all — across a daylight-saving transition.

**Singleton by default.** A scheduled job that runs on every replica is the most common way a nightly task becomes a nightly incident. The lease must exceed the timeout, or it expires mid-run and a second replica starts the same work; startup refuses that combination.

**Staleness is a readiness check.** A job that stops firing produces no errors at all, so silence has to be made observable.

The in-box lock is **in-process**, so with multiple replicas each runs the job independently. Startup warns about this outside Development, because the failure is silent — the job appears to work, just N times.

---

## 14. Feature flags

```csharp
fx.Enable(BuiltIn.FeatureFlags);
```
```json
{ "MicroFx": { "FeatureFlags": { "Flags": { "new-pricing": "true" } } } }
```
```csharp
if (await flags.IsEnabledAsync("new-pricing", defaultValue: false, cancellationToken))
{
    // ...
}
```

Built on **OpenFeature**, so swapping the configuration provider for LaunchDarkly or AWS AppConfig is a provider registration rather than a change at every call site.

**Evaluation never fails a request.** A provider outage, a timeout, or a malformed value all resolve to your code default. A flag system that can take the service down adds a dependency to every code path it touches, which is worse than having no flags.

Because the in-box provider reads through `IOptionsMonitor`, changing a flag in configuration takes effect **without a redeploy** — which is what makes a kill switch useful during an incident rather than after one.

---

## 15. Calling other services

```csharp
builder.Services.AddServiceClient<IInventoryClient, InventoryClient>("inventory");
```
```json
{ "MicroFx": { "ServiceClients": { "Endpoints": { "inventory": "https://inventory.internal" } } } }
```

Every client gets a Polly pipeline: total timeout → retry → circuit breaker → per-attempt timeout, plus connection recycling so a DNS change is picked up through a failover.

**Retries are restricted to idempotent methods.** A timeout means the response was lost, not that the work did not happen — retrying a POST on that basis duplicates it. A POST carrying `Idempotency-Key` is safe and is retried.

**Redirects are not followed**, because a redirect from an upstream could send a bearer token to a host you never intended to call.

Token forwarding is **off by default** and gated by a per-service allow list. Forwarding a token sends the caller's credential to another host, which is a decision about trust rather than a convenience.

---

## 16. Writing your own feature

Everything the platform does, you can do.

```csharp
[assembly: MicroFxFeatureAssembly]
[assembly: MicroFxFeature(typeof(Acme.Audit.AuditTrailFeature))]

public sealed class AuditTrailFeature
    : IMicroFxFeature, IPipelineFeature, IFeatureLifecycle, IFeatureValidator
{
    public FeatureDescriptor Descriptor => new()
    {
        Id = "acme.audit",                          // your prefix, not microfx.
        DependsOn = [BuiltIn.Core, BuiltIn.Security],
        After = [BuiltIn.MultiTenancy],
        ConfigurationSection = "Acme:Audit",
    };

    public void Configure(FeatureBuildContext context)
    {
        context.AddValidatedOptions<AuditOptions>()
               .Validate(o => o.RetentionDays >= 30, "Retention must be at least 30 days.");

        context.Services.TryAddSingleton<IAuditSink, FileAuditSink>();   // TryAdd: overridable

        context.AddHealthContribution(HealthContribution.Ready(
            "audit-sink", (sp, ct) => sp.GetRequiredService<IAuditSink>().CheckAsync(ct)));

        context.Report("sink", "file");   // appears in the banner and /internal/features
    }

    public void UsePipeline(FeaturePipelineContext context) =>
        context.Use(PipelineStage.Telemetry, app => app.UseMiddleware<AuditMiddleware>());

    public async ValueTask<ValidationReport> ValidateAsync(
        FeatureValidationContext context, CancellationToken cancellationToken) =>
        await context.Services.GetRequiredService<IAuditSink>().IsWritableAsync(cancellationToken)
            ? ValidationReport.Ok()
            : ValidationReport.Error("The audit sink is not writable; records would be lost.");

    public async ValueTask StoppingAsync(FeatureLifecycleContext context, CancellationToken ct) =>
        await context.Services.GetRequiredService<IAuditSink>().FlushAsync(ct);
}
```

Consumers add a package reference. Nothing else.

### Rules to follow

| Rule | Why |
|---|---|
| Prefix ids with your organisation | `microfx.` is reserved; the kernel and an analyzer both reject it |
| Use `TryAdd*` for everything | Preserves the override escape hatch for *your* consumers |
| Prefer `After`/`Before` over `DependsOn` | A hard dependency means you *cannot function* without the other feature |
| No I/O in `Configure` | It is not cancellable, traced, or budgeted — use `StartingAsync` |
| Return reports from `ValidateAsync`, throw from `StartingAsync` | Validation aggregates; lifecycle aborts |
| Never `Report()` a secret | Facts reach logs and diagnostics — report presence or host, not value |

---

## 17. Testing

### Unit-testing a handler

Handlers are ordinary classes. Inject a `FakeTimeProvider` and assert the result:

```csharp
var result = await handler.HandleAsync(command, context, CancellationToken.None);
Assert.That(result.Outcome, Is.EqualTo(HandlerOutcome.Permanent));
```

### End-to-end over the real pipeline

```csharp
using var factory = new WebApplicationFactory<Program>();
using var client = factory.CreateClient();

var response = await client.PostAsJsonAsync("/v1/orders", new PlaceOrder("ABC-1", 2, "GBP"));
Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
```

Everything runs: middleware, validation, security, messaging on the in-memory transport, persistence on SQLite. No infrastructure.

> **One gotcha.** With minimal hosting, `ConfigureWebHost` callbacks run *after* `WebApplication.CreateBuilder`, so settings supplied there are invisible to composition-time code. Configuration your test needs `AddMicroFx()` to see must come from **environment variables**.

### Asserting the feature graph

```csharp
catalog.AssertEnabled("microfx.security");
catalog.AssertOrder("microfx.security", "microfx.multitenancy", "acme.audit");
catalog.AssertDisabled("microfx.caching", DisabledReason.DisabledByConfiguration);
catalog.AssertReports("microfx.messaging", "transport", "in-memory");
```

Or snapshot it against a checked-in golden file, so an accidental ordering change is a reviewed diff rather than a production surprise:

```csharp
Assert.That(catalog.Snapshot(), Is.EqualTo(File.ReadAllText("graph.approved.txt")));
```

### Testing a custom transport

If you write an adapter, run the conformance suite:

```csharp
var report = await new TransportConformanceSuite(myTransport).RunAsync();
Assert.That(report.Passed, Is.True, report.ToString());
```

It checks that your transport actually does what its capability flags claim. A transport advertising publisher confirms without honouring them would make the outbox mark rows dispatched that never arrived — and nothing would notice until messages went missing.

---

## 18. Observability

### Traces, metrics, logs

OpenTelemetry is configured out of the box. Point it at a collector:

```json
{ "MicroFx": { "Observability": { "OtlpEndpoint": "http://collector:4317", "SampleRatio": 0.1 } } }
```

With no endpoint configured, no exporter is registered — an exporter with nowhere to send produces a connection failure on every export interval, which is noisier than not exporting.

Health and diagnostics endpoints are excluded from traces; otherwise most spans in a quiet service are probes.

### Metrics worth alerting on

| Metric | Why |
|---|---|
| `outbox.oldest.age` | The leading indicator that events have stopped flowing. Depth can be healthy under load; **age cannot**. |
| `messaging.deadletter.count` | Messages nobody could handle |
| `messaging.filtered.count` | Non-zero means your transport cannot filter broker-side and you are paying for it |
| `microfx.feature.startup.duration` | Slow cold starts, attributable to a named feature |
| `jobs.skipped.count` | Overlapping runs, or a job that never gets the lock |

### Debugging startup

Every `StartingAsync` gets a span under a `microfx.startup` root, so a twelve-second cold start renders as a flame graph rather than a mystery.

---

## 19. Going to production

### Checklist

- [ ] `MicroFx:Security:Authority` and `Audiences` configured
- [ ] Secrets from a secret store, not configuration
- [ ] `MicroFx:Observability:OtlpEndpoint` pointing at a real collector
- [ ] A real transport adapter referenced, not the in-memory default
- [ ] Migrations applied by a pipeline stage; `CreateSchemaOnStartup` **off**
- [ ] Management port (8081) **not** on any public load balancer
- [ ] Sampling ratio set for your volume
- [ ] Drain timeout shorter than the orchestrator's termination grace period

Startup validation checks most of this and refuses to start if the answer would be an open or lossy service. Read the banner on your first production deploy — it tells you what is in force.

### Container

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
EXPOSE 8081
USER $APP_UID
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["./YourService"]
```

Chiselled: no shell, no package manager, non-root, minimal CVE surface. Because there is no shell, health-check the container by re-executing the binary:

```yaml
healthcheck:
  test: ["CMD", "/app/YourService", "--health-check"]
```

### Graceful shutdown

On SIGTERM the platform fails readiness immediately, drains in-flight requests and messages, then exits — in reverse dependency order, so consumers stop before the transport closes and telemetry flushes last. Set your drain timeout below the orchestrator's grace period, or the process is killed mid-drain.

---

## 20. Troubleshooting

### "Why isn't my feature running?"

```bash
curl localhost:8081/internal/features
```

It tells you enabled state, the reason for disablement, and **the configuration key responsible**.

### "Startup failed with a validation error"

Read all of it. Validation aggregates, so the list is every problem, not the first. Each message says what is wrong and what to do.

### "Cycle detected in the feature graph"

The message contains the full path (`a → b → c → a`). Break it by demoting one edge from `DependsOn` to `After`, or by extracting the shared concern into a third feature.

### "My endpoints return 404"

Endpoint modules are discovered from your **application assembly**. Under an unusual host, check that `IHostEnvironment.ApplicationName` names your assembly.

### "Health probes return 401 in production"

They should not — probes are explicitly anonymous. If you replaced the health feature, ensure you kept `.AllowAnonymous()`; otherwise deny-by-default will challenge your orchestrator and it will kill every pod.

### "My messages retry forever"

Check that your transport propagates the headers the platform supplies on redelivery. The attempt count lives in the envelope; a transport that redelivers the *original* headers replays attempt 1 forever and the retry policy never exhausts. The conformance suite covers this.

### "Configuration in my test is ignored"

See the gotcha in [§17](#17-testing) — use environment variables for anything composition-time.

---

## 21. Reference

### Built-in features

| Id | Default | Purpose |
|---|---|---|
| `microfx.core` | **kernel** | Metadata, `TimeProvider`, serialization |
| `microfx.configuration` | **kernel** | Layered config, validated options, redaction |
| `microfx.observability` | **kernel** | OpenTelemetry |
| `microfx.health` | **kernel** | Probes on the management port |
| `microfx.diagnostics` | **kernel** | `/internal/*` |
| `microfx.api` | on | Problem details, OpenAPI, headers, limits |
| `microfx.validation` | on | FluentValidation |
| `microfx.ratelimiting` | on | Partitioned limiting |
| `microfx.idempotency` | on | `Idempotency-Key` replay |
| `microfx.security` | on | JWT, deny-by-default, audit |
| `microfx.resilience` | on | Polly on every `HttpClient` |
| `microfx.caching` | on | L1, optional L2 |
| `microfx.serviceclients` | on | Typed clients |
| `microfx.multitenancy` | **opt-in** | Tenant resolution and scoping |
| `microfx.persistence` | **opt-in** | EF Core, transactions, outbox, inbox |
| `microfx.messaging` | **opt-in** | Commands and events |
| `microfx.jobs` | **opt-in** | Scheduled work |
| `microfx.featureflags` | **opt-in** | OpenFeature |
| `microfx.storage` | **opt-in** | Object storage |

### Analyzer rules

| Rule | Severity | |
|---|---|---|
| `MFX1001` | Error | Feature id uses the reserved `microfx.` prefix |
| `MFX1003` | Warning | Blocking call in `Configure` |
| `MFX1010` | Warning | `HttpClient` constructed directly |
| `MFX1011` | Warning | Ambient clock instead of `TimeProvider` |
| `MFX1022` | Error | Domain event published to a transport |
| `MFX2001` | Error | *(platform)* Built-in feature must use `TryAdd` |

### Endpoints

| Path | Port | |
|---|---|---|
| `/health/live` | management | Liveness — checks nothing external |
| `/health/ready` | management | Readiness — checks dependencies |
| `/health/startup` | management | One-time preconditions |
| `/internal/info` | management | Version, commit, environment |
| `/internal/features` | management | The resolved feature graph |
| `/internal/config` | management | Effective config, redacted, double-gated |
| `/openapi/v1.json` | management | OpenAPI document |
| `/openapi/reference` | management | Scalar API reference |

### Further reading

- [Design](microfx-design.md) — how it is built and why
- [Requirements](microservice-template-requirements.md) — the specification
- [Implementation Status](implementation-status.md) — what is built, deferred, and unverified

---

**Found something confusing?** That is a documentation bug. Please open an issue — the guide is meant to work for someone meeting the framework for the first time.
