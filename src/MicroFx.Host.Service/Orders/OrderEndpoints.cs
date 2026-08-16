using FluentValidation;
using MicroFx.Api;
using MicroFx.Caching;
using MicroFx.Messaging;
using MicroFx.Persistence;
using Microsoft.EntityFrameworkCore;
using MicroFx.Validation;
using Microsoft.Extensions.Caching.Hybrid;

namespace MicroFx.Host.Service.Orders;

/// <summary>Request to place an order.</summary>
/// <param name="Sku">Stock keeping unit.</param>
/// <param name="Quantity">How many units.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
public sealed record PlaceOrder(string Sku, int Quantity, string Currency);

/// <summary>An order as returned to callers.</summary>
/// <param name="Id">Order identifier.</param>
/// <param name="Sku">Stock keeping unit.</param>
/// <param name="Quantity">How many units.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
/// <param name="PlacedAt">When the order was placed.</param>
public sealed record Order(string Id, string Sku, int Quantity, string Currency, DateTimeOffset PlacedAt);

/// <summary>Validates <see cref="PlaceOrder"/>.</summary>
/// <remarks>
/// Discovered by the validation feature's entry-assembly scan; nothing registers it explicitly.
/// </remarks>
public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrder>
{
    /// <summary>Builds the rules.</summary>
    public PlaceOrderValidator()
    {
        RuleFor(order => order.Sku)
            .NotEmpty()
            .MaximumLength(64)
            .Matches("^[A-Z0-9-]+$")
            .WithMessage("SKU must be uppercase letters, digits, or hyphens.");

        RuleFor(order => order.Quantity)
            .InclusiveBetween(1, 1000);

        RuleFor(order => order.Currency)
            .NotEmpty()
            .Length(3)
            .Matches("^[A-Z]{3}$")
            .WithMessage("Currency must be a three-letter ISO 4217 code.");
    }
}

/// <summary>Order endpoints. Discovered by the API feature; no central registration list.</summary>
public sealed class OrderEndpoints : IEndpointModule
{
    /// <inheritdoc />
    public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/orders").WithTags("Orders");

        // AllowAnonymous is explicit and greppable. Without it the deny-by-default fallback policy
        // would require an authenticated caller, which is the intended posture.
        group.MapPost("/", (PlaceOrder request, TimeProvider clock) =>
            {
                var order = new Order(
                    Guid.NewGuid().ToString("N")[..12],
                    request.Sku,
                    request.Quantity,
                    request.Currency,
                    clock.GetUtcNow());

                return Results.Created($"/v1/orders/{order.Id}", order);
            })
            .Validate<PlaceOrder>()
            .AllowAnonymous()
            .WithName("place-order");

        // Exercises the cache: L1 only by default, L2 when a distributed provider is configured,
        // with no change to this code either way.
        group.MapGet("/{id}", async (
                string id,
                HybridCache cache,
                ICacheKeyBuilder keys,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                if (id.Length is 0 or > 64 || !id.All(char.IsAsciiLetterOrDigit))
                {
                    return Results.NotFound();
                }

                var order = await cache.GetOrCreateAsync(
                    keys.Build("order", id),
                    id,
                    (key, _) => ValueTask.FromResult(
                        new Order(key, "SKU-DEMO", 1, "GBP", clock.GetUtcNow())),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return Results.Ok(order);
            })
            .AllowAnonymous()
            .WithName("get-order");

        // Publishing the event and sending the command from an endpoint is what lets the
        // end-to-end suite drive the full messaging path over HTTP.
        group.MapPost("/{id}/publish", async (
                string id,
                IEventPublisher publisher,
                CancellationToken cancellationToken) =>
            {
                if (id.Length is 0 or > 64 || !id.All(char.IsAsciiLetterOrDigit))
                {
                    return Results.NotFound();
                }

                await publisher.PublishAsync(
                    new OrderPlacedV1(id, "SKU-DEMO", 1), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return Results.Accepted();
            })
            .AllowAnonymous()
            .WithName("publish-order-placed");

        group.MapPost("/{id}/reserve", async (
                string id,
                string sku,
                int quantity,
                ICommandSender sender,
                CancellationToken cancellationToken) =>
            {
                if (id.Length is 0 or > 64 || !id.All(char.IsAsciiLetterOrDigit) ||
                    sku.Length is 0 or > 64 || quantity < 1)
                {
                    return Results.BadRequest();
                }

                await sender.SendAsync(
                    new ReserveInventory(id, sku, quantity), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return Results.Accepted();
            })
            .AllowAnonymous()
            .WithName("reserve-inventory");

        // The outbox path: the order row and the intent to publish commit in one transaction, so
        // a crash between them cannot leave one without the other.
        group.MapPost("/durable", async (
                PlaceOrder request,
                IUnitOfWork unitOfWork,
                OrdersDbContext database,
                IOutboxStore outbox,
                OutboxDomainEventProjector projector,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var orderId = Guid.NewGuid().ToString("N")[..12];

                await unitOfWork.ExecuteAsync(async token =>
                {
                    database.Orders.Add(new OrderEntity
                    {
                        Id = orderId,
                        Sku = request.Sku,
                        Quantity = request.Quantity,
                        Currency = request.Currency,
                    });

                    await outbox.EnqueueAsync(
                        projector.Project(
                            new OrderPlacedV1(orderId, request.Sku, request.Quantity),
                            aggregateId: orderId,
                            tenantId: null),
                        token).ConfigureAwait(false);

                    // One commit for both. This is the whole point of the outbox.
                    await unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);

                return Results.Created($"/v1/orders/{orderId}",
                    new Order(orderId, request.Sku, request.Quantity, request.Currency, clock.GetUtcNow()));
            })
            .Validate<PlaceOrder>()
            .AllowAnonymous()
            .WithName("place-order-durable");

        group.MapGet("/durable/{id}", async (
                string id, OrdersDbContext database, CancellationToken cancellationToken) =>
            {
                if (id.Length is 0 or > 64 || !id.All(char.IsAsciiLetterOrDigit))
                {
                    return Results.NotFound();
                }

                var order = await database.Orders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
                    .ConfigureAwait(false);

                return order is null
                    ? Results.NotFound()
                    : Results.Ok(new { order.Id, order.Sku, order.Quantity, order.CreatedAt });
            })
            .AllowAnonymous()
            .WithName("get-durable-order");

        group.MapGet("/sweep", (OrderSweepReport report) =>
                Results.Ok(new { runs = report.Runs, lastCount = report.LastCount, lastRunAt = report.LastRunAt }))
            .AllowAnonymous()
            .WithName("order-sweep-report");

        group.MapGet("/{id}/projection", (string id, OrderProjection projection) =>
            {
                if (id.Length is 0 or > 64 || !id.All(char.IsAsciiLetterOrDigit))
                {
                    return Results.NotFound();
                }

                return Results.Ok(new { orderId = id, handled = projection.CountFor(id) });
            })
            .AllowAnonymous()
            .WithName("order-projection");
    }
}
