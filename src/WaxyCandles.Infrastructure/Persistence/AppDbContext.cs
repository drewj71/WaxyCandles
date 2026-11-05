using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.Infrastructure.Persistence;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Watchlist> Watchlists => Set<Watchlist>();
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();
    public DbSet<StockPrice> StockPrices => Set<StockPrice>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<OutboxMessage> OutboxMessages { get; set; }
    public DbSet<StockCandle> StockCandles { get; set; }
    public DbSet<Notification> Notifications => Set<Notification>();

    public const int OutboxMessageTypeMaxLength = 200;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // SQL Server's default decimal(18,2) silently truncates prices and
        // percentages (Alpha Vantage sends 4 decimals, e.g. "0.8119%", and alert
        // targets can be sub-penny). Applies to every decimal, including new ones.
        configurationBuilder
            .Properties<decimal>()
            .HavePrecision(18, 6);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // =======================
        // Indexes
        // =======================
        builder.Entity<StockPrice>()
            .HasIndex(x => new { x.Symbol, x.Timestamp });

        builder.Entity<Alert>()
            .HasIndex(x => new { x.UserId, x.Symbol });

        // Evaluation looks alerts up by symbol for every price update. Without
        // this it scans the table, and under locking READ COMMITTED that scan
        // waits on every other transaction's uncommitted alert writes.
        builder.Entity<Alert>()
            .HasIndex(x => x.Symbol);

        builder.Entity<Watchlist>(entity =>
        {
            // No foreign key to AspNetUsers, as before the lists existed: rows
            // from before then may belong to users that are gone.
            entity.Property(x => x.UserId)
                .IsRequired();

            entity.Property(x => x.Name)
                .HasMaxLength(Watchlist.NameMaxLength)
                .IsRequired();

            entity.HasIndex(x => new { x.UserId, x.Name })
                .IsUnique();

            entity.HasMany(x => x.Items)
                .WithOne()
                .HasForeignKey(x => x.WatchlistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WatchlistItem>(entity =>
        {
            entity.Property(x => x.Symbol)
                .IsRequired();

            entity.HasIndex(x => new { x.WatchlistId, x.Symbol })
                .IsUnique();

            // The price poller reads the distinct symbols across every list.
            entity.HasIndex(x => x.Symbol);
        });

        builder.Entity<StockCandle>()
            .HasIndex(x => new
            {
                x.Symbol,
                x.Interval,
                x.Timestamp
            })
            .IsUnique();

        builder.Entity<Notification>(entity =>
        {
            entity.Property(x => x.UserId)
                .IsRequired();

            entity.Property(x => x.Title)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(x => x.Body)
                .HasMaxLength(1000)
                .IsRequired();

            // Makes creating a notification from a message idempotent.
            entity.HasIndex(x => x.SourceMessageId)
                .IsUnique();

            // A user's most recent notifications.
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<OutboxMessage>(entity =>
        {
            // Bounded so it can be included in the pending index below. Values
            // are the contracts' MessageType constants (~25 characters).
            entity.Property(x => x.Type)
                .HasMaxLength(OutboxMessageTypeMaxLength);

            // The outbox publisher polls every 5s for the oldest pending rows
            // (and counts pending rows with no route). Filtered to pending rows,
            // so it stays small however many processed rows the table holds,
            // and covers both queries with Type included.
            entity.HasIndex(x => x.CreatedAt)
                .HasDatabaseName("IX_OutboxMessages_Pending")
                .HasFilter("[ProcessedAt] IS NULL")
                .IncludeProperties(x => x.Type);

            // Retention deletes processed rows by age (Retention/DataRetentionService).
            entity.HasIndex(x => x.ProcessedAt)
                .HasDatabaseName("IX_OutboxMessages_Processed")
                .HasFilter("[ProcessedAt] IS NOT NULL");

            entity.Property(x => x.TraceParent)
                .HasMaxLength(MessageTracing.TraceParentMaxLength);

            entity.Property(x => x.TraceState)
                .HasMaxLength(MessageTracing.TraceStateMaxLength);
        });

        // Retention deletes idempotency claims by age.
        builder.Entity<ProcessedMessage>()
            .HasIndex(x => x.ProcessedAt);

        builder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(x => x.Id);

            // binary(32): a fixed-length SHA-256 of the refresh token.
            entity.Property(x => x.TokenHash)
                .IsRequired()
                .HasMaxLength(Authentication.TokenService.RefreshTokenHashLength)
                .IsFixedLength();

            entity.HasIndex(x => x.TokenHash)
                .IsUnique();

            entity.HasIndex(x => x.UserId);

            // Family revocation and logout update by family.
            entity.HasIndex(x => x.FamilyId);

            // Retention deletes by expiry.
            entity.HasIndex(x => x.ExpiresAt);

            entity.Property(x => x.UserId)
                .IsRequired();

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}