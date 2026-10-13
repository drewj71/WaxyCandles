using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Identity;

namespace WaxyCandles.Infrastructure.Persistence;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Watchlist> Watchlists => Set<Watchlist>();
    public DbSet<StockPrice> StockPrices => Set<StockPrice>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<OutboxMessage> OutboxMessages { get; set; }
    public DbSet<StockCandle> StockCandles { get; set; }
    public DbSet<Notification> Notifications => Set<Notification>();

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

        builder.Entity<Watchlist>()
            .HasIndex(x => new { x.UserId, x.Symbol }).IsUnique();

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

        builder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Token)
                .IsRequired();

            entity.HasIndex(x => x.Token)
                .IsUnique();

            entity.HasIndex(x => x.UserId);

            entity.Property(x => x.UserId)
                .IsRequired();

            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}