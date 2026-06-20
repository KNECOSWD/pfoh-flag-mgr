using Microsoft.EntityFrameworkCore;
using PFOH.Api.Models;

namespace PFOH.Api.Data;

public class PfohDbContext(DbContextOptions<PfohDbContext> options) : DbContext(options)
{
    public DbSet<FlagRecord> Flags => Set<FlagRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FlagRecord>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.Property(f => f.OwnerObjectId).HasMaxLength(128).IsRequired();
            entity.Property(f => f.HonoreeName).HasMaxLength(200).IsRequired();
            entity.Property(f => f.ServiceBranch).HasMaxLength(100);
            entity.Property(f => f.RankOrTitle).HasMaxLength(100);
            entity.Property(f => f.FlagNumber).HasMaxLength(50);
            entity.Property(f => f.GridLocation).HasMaxLength(50);
            entity.Property(f => f.TributeText).HasMaxLength(2000);
            entity.Property(f => f.Status).HasMaxLength(30).HasDefaultValue("Draft");
            entity.HasIndex(f => new { f.OwnerObjectId, f.FlagNumber });
        });
    }
}
