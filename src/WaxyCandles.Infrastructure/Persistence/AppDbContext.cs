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