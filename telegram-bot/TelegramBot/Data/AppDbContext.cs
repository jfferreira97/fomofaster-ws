using Microsoft.EntityFrameworkCore;
using TelegramBot.Models;

namespace TelegramBot.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<Trader> Traders { get; set; }
    public DbSet<UserTrader> UserTraders { get; set; }
    public DbSet<Notification> Notifications { get; set; }
    public DbSet<SentMessage> SentMessages { get; set; }
    public DbSet<PendingPayment> PendingPayments { get; set; }
    public DbSet<AppConfig> AppConfigs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ChatId).IsUnique();
            entity.Property(e => e.ChatId).IsRequired();
            entity.Property(e => e.JoinedAt).IsRequired();
            entity.Property(e => e.IsActive).IsRequired();
        });

        modelBuilder.Entity<Trader>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Handle).IsUnique();
            entity.Property(e => e.Handle).IsRequired().HasMaxLength(100);
            entity.Property(e => e.FirstSeenAt).IsRequired();
            entity.Property(e => e.LastSeenAt).IsRequired();
        });

        modelBuilder.Entity<UserTrader>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Composite index for fast lookups: O(log n)
            entity.HasIndex(e => new { e.UserId, e.TraderId }).IsUnique();

            // Index for trader -> users lookup: O(log n)
            entity.HasIndex(e => e.TraderId);

            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.TraderId).IsRequired();
            entity.Property(e => e.FollowedAt).IsRequired();

            // Configure relationships
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Trader)
                .WithMany()
                .HasForeignKey(e => e.TraderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Message).IsRequired();
            entity.Property(e => e.Ticker).HasMaxLength(50);
            entity.Property(e => e.Trader).HasMaxLength(100);
            entity.Property(e => e.ContractAddress).HasMaxLength(100);
            entity.Property(e => e.Chain).HasConversion<string>();
            entity.Property(e => e.Type).HasConversion<string>().HasDefaultValue(NotificationType.Unknown);
            entity.Property(e => e.SentAt).IsRequired();

            entity.Property(e => e.FomoWsTradeId).HasMaxLength(100);
            entity.HasIndex(e => e.FomoWsTradeId).IsUnique().HasFilter("\"FomoWsTradeId\" IS NOT NULL");

            // Index for cleanup queries (delete old notifications)
            entity.HasIndex(e => e.SentAt);
        });

        modelBuilder.Entity<SentMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NotificationId).IsRequired();
            entity.Property(e => e.ChatId).IsRequired();
            entity.Property(e => e.MessageId).IsRequired();
            entity.Property(e => e.SentAt).IsRequired();
            entity.Property(e => e.IsManuallyEdited).IsRequired();
            entity.Property(e => e.IsSystemEdited).IsRequired();

            // Index for finding messages by NotificationId (for editing)
            entity.HasIndex(e => e.NotificationId);

            // Index for cleanup queries
            entity.HasIndex(e => e.SentAt);

            // Configure relationship
            entity.HasOne(e => e.Notification)
                .WithMany()
                .HasForeignKey(e => e.NotificationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Key).IsUnique();
            entity.Property(e => e.Key).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Value).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
        });

        modelBuilder.Entity<PendingPayment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WalletPublicKey);
            entity.HasIndex(e => new { e.IsConfirmed, e.ExpiresAt });
            entity.Property(e => e.ChatId).IsRequired();
            entity.Property(e => e.WalletPublicKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.WalletPrivateKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AmountSol).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.ExpiresAt).IsRequired();
            entity.Property(e => e.IsConfirmed).IsRequired();
        });
    }
}
