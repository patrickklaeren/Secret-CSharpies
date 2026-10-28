using Microsoft.EntityFrameworkCore;

namespace SecretSanta.Data;

public sealed class SantaDb(DbContextOptions<SantaDb> options) : DbContext(options)
{
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<SantaCycle> Cycles => Set<SantaCycle>();
    public DbSet<GiftMatch> Matches => Set<GiftMatch>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<GiftMatch>().HasIndex(m => m.SenderId).IsUnique();
        model.Entity<GiftMatch>().HasIndex(m => m.RecipientId).IsUnique();
        model.Entity<GiftMatch>().HasOne<Participant>().WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<GiftMatch>().HasOne<Participant>().WithMany().HasForeignKey(m => m.RecipientId).OnDelete(DeleteBehavior.Restrict);
    }
}
