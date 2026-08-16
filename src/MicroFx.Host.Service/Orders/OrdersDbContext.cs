using System.ComponentModel.DataAnnotations;
using MicroFx.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MicroFx.Host.Service.Orders;

/// <summary>A persisted order.</summary>
public sealed class OrderEntity : IAuditable
{
    /// <summary>Order identifier.</summary>
    [MaxLength(64)]
    public string Id { get; set; } = string.Empty;

    /// <summary>Stock keeping unit.</summary>
    [MaxLength(64)]
    public string Sku { get; set; } = string.Empty;

    /// <summary>How many units.</summary>
    public int Quantity { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = "GBP";

    /// <summary>
    /// Concurrency token. Two callers updating the same order concurrently produce a conflict
    /// rather than a silent last-write-wins.
    /// </summary>
    public byte[]? RowVersion { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public string? CreatedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset? ModifiedAt { get; set; }

    /// <inheritdoc />
    public string? ModifiedBy { get; set; }
}

/// <summary>The reference host's database context.</summary>
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    /// <summary>Orders.</summary>
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OrderEntity>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.RowVersion).IsRowVersion();
        });

        // Explicit rather than magical: the service owns its schema and should be able to see every
        // table in it, including the platform's outbox and inbox.
        modelBuilder.ApplyMicroFxPersistence();
    }
}
