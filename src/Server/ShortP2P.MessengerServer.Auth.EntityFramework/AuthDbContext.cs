using Microsoft.EntityFrameworkCore;
using ShortP2P.MessengerServer.Auth.EntityFramework.Entities;

namespace ShortP2P.MessengerServer.Auth.EntityFramework;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<AuthAccountEntity> Accounts => Set<AuthAccountEntity>();
    public DbSet<BotAccountEntity> Bots => Set<BotAccountEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthAccountEntity>(e =>
        {
            e.ToTable("auth_accounts");
            e.HasKey(x => x.NetworkId);
            e.HasIndex(x => x.Nick).IsUnique();
            e.Property(x => x.NetworkId).HasMaxLength(64);
            e.Property(x => x.Nick).HasMaxLength(256);
            e.Property(x => x.PasswordSalt).HasMaxLength(128);
            e.Property(x => x.PasswordHash).HasMaxLength(128);
            e.Property(x => x.VisibleForSearch).HasDefaultValue(true);
        });

        modelBuilder.Entity<BotAccountEntity>(e =>
        {
            e.ToTable("bot_accounts");
            e.HasKey(x => x.NetworkId);
            e.HasIndex(x => x.BotName).IsUnique();
            e.Property(x => x.NetworkId).HasMaxLength(64);
            e.Property(x => x.BotName).HasMaxLength(256);
            e.Property(x => x.BotReadableName).HasMaxLength(100);
            e.Property(x => x.BotDescription).HasMaxLength(500);
            e.Property(x => x.KeySalt).HasMaxLength(256);
            e.Property(x => x.KeyHash).HasMaxLength(256);
            e.Property(x => x.CertificateFingerprintSha256).HasMaxLength(64);
            e.Property(x => x.MessagePublicKey).HasMaxLength(16384);
            e.Property(x => x.CommandsJson).HasMaxLength(32768);
        });
    }
}
