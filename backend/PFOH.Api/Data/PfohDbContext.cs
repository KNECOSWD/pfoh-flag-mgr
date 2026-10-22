using Microsoft.EntityFrameworkCore;
using PFOH.Api.Models;
namespace PFOH.Api.Data;

public class PfohDbContext(DbContextOptions<PfohDbContext> options) : DbContext(options)
{
    public DbSet<FlagGrid> FlagGrids => Set<FlagGrid>();
    public DbSet<Honoree> Honorees => Set<Honoree>();
    public DbSet<Sponsor> Sponsors => Set<Sponsor>();
    public DbSet<SponsorCategory> SponsorCategories => Set<SponsorCategory>();
    public DbSet<ServiceBranch> ServiceBranches => Set<ServiceBranch>();
    public DbSet<ServiceBranchCategory> ServiceBranchCategories => Set<ServiceBranchCategory>();

    public DbSet<AvailableFlagGrid> AvailableFlagGrids => Set<AvailableFlagGrid>();

    public DbSet<FlagClaim> FlagClaims => Set<FlagClaim>();
    public DbSet<HonoreeClaim> HonoreeClaims => Set<HonoreeClaim>();
    public DbSet<ReprintDuplicateReview> ReprintDuplicateReviews => Set<ReprintDuplicateReview>();
    public DbSet<HonoreeNotDuplicatePair> HonoreeNotDuplicatePairs => Set<HonoreeNotDuplicatePair>();
    public DbSet<HonoreeMergeAudit> HonoreeMergeAudits => Set<HonoreeMergeAudit>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<HonoreeSearchResult> HonoreeSearchResults => Set<HonoreeSearchResult>();
    public DbSet<HonoreeChangeRequest> HonoreeChangeRequests => Set<HonoreeChangeRequest>();
    public DbSet<CardPrintEvent> CardPrintEvents => Set<CardPrintEvent>();
    public DbSet<FlagRemovalQueueItem> FlagRemovalQueueItems => Set<FlagRemovalQueueItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FlagGrid>(entity =>
        {
            entity.ToTable("FlagGrids", "dbo");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FlagGridName).IsRequired();
            entity.Property(e => e.Notes).IsRequired();
            entity.Property(e => e.CreatedBy).IsRequired();
            entity.Property(e => e.ModifiedBy).IsRequired();

            entity.HasOne(e => e.Honoree)
                .WithMany()
                .HasForeignKey(e => e.HonoreeId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Honoree>(entity =>
        {
            entity.ToTable("Honorees", "dbo");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.FirstName).IsRequired();
            entity.Property(e => e.LastName).IsRequired();
            entity.Property(e => e.Rank).IsRequired();
            entity.Property(e => e.Awards).IsRequired();
            entity.Property(e => e.PhoneNumber).IsRequired();
            entity.Property(e => e.EmailAddress).IsRequired();
            entity.Property(e => e.PhotoFileName).IsRequired();
            entity.Property(e => e.Salutation).IsRequired();
            entity.Property(e => e.ConflictsServed).IsRequired();
            entity.Property(e => e.DatesUserEntry).IsRequired();
            entity.Property(e => e.NameUserEntry).IsRequired();
            entity.Property(e => e.CreatedBy).IsRequired();
            entity.Property(e => e.ModifiedBy).IsRequired();

            entity.HasOne(e => e.FlagGrid)
                .WithMany(e => e.AssignedHonorees)
                .HasForeignKey(e => e.FlagGridId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.Sponsor)
                .WithMany(e => e.Honorees)
                .HasForeignKey(e => e.SponsorId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.ServiceBranch)
                .WithMany(e => e.Honorees)
                .HasForeignKey(e => e.ServiceBranchId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.ServiceBranchCategory)
                .WithMany(e => e.Honorees)
                .HasForeignKey(e => e.ServiceBranchCategoryId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Sponsor>(entity =>
        {
            entity.ToTable("Sponsors", "dbo");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.FirstName).IsRequired();
            entity.Property(e => e.LastName).IsRequired();
            entity.Property(e => e.PhoneNumber).IsRequired();
            entity.Property(e => e.EmailAddress).IsRequired();
            entity.Property(e => e.StreetAddress).IsRequired();
            entity.Property(e => e.City).IsRequired();
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.ZipCode).IsRequired();
            entity.Property(e => e.Salutation).IsRequired();
            entity.Property(e => e.CreatedBy).IsRequired();
            entity.Property(e => e.ModifiedBy).IsRequired();

            entity.HasOne(e => e.SponsorCategory)
                .WithMany(e => e.Sponsors)
                .HasForeignKey(e => e.SponsorCategoryId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<SponsorCategory>(entity =>
        {
            entity.ToTable("SponsorCategories", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SponsorCategoryName).IsRequired();
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.CreatedBy).IsRequired();
            entity.Property(e => e.ModifiedBy).IsRequired();
        });

        modelBuilder.Entity<ServiceBranch>(entity =>
        {
            entity.ToTable("ServiceBranches", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ServiceBranchName).IsRequired();
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.LogoFileName).IsRequired();
            entity.Property(e => e.CreatedBy).IsRequired();
            entity.Property(e => e.ModifiedBy).IsRequired();

            entity.HasOne(e => e.ServiceBranchCategory)
                .WithMany(e => e.ServiceBranches)
                .HasForeignKey(e => e.ServiceBranchCategoryId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<ServiceBranchCategory>(entity =>
        {
            entity.ToTable("ServiceBranchCategories", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ServiceBranchCategoryName).IsRequired();
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.CreatedBy).IsRequired();
            entity.Property(e => e.ModifiedBy).IsRequired();
        });

        modelBuilder.Entity<AvailableFlagGrid>(entity =>
        {
            entity.ToView("vw_AvailableFlagGrids", "dbo");
            entity.HasNoKey();
        });

        modelBuilder.Entity<FlagClaim>(entity =>
        {
            entity.ToTable("FlagClaims", "dbo");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ClientRequestId);
            entity.HasIndex(e => e.ClientRequestId)
                .HasDatabaseName("UX_FlagClaims_ClientRequestId")
                .IsUnique()
                .HasFilter("[ClientRequestId] IS NOT NULL");

            entity.Property(e => e.ExternalUserObjectId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ExternalUserEmail).HasMaxLength(255).IsRequired();
            entity.Property(e => e.ExternalUserName).HasMaxLength(255);
            entity.Property(e => e.ClaimStatus).HasMaxLength(30).IsRequired();
            entity.Property(e => e.ApprovedBy).HasMaxLength(255);
            entity.Property(e => e.RejectedBy).HasMaxLength(255);

            entity.HasIndex(e => e.ExternalUserObjectId);
            entity.HasIndex(e => e.FlagGridId);
            entity.HasIndex(e => new { e.FlagGridId, e.ExternalUserObjectId })
                .HasDatabaseName("UX_FlagClaims_ActiveFlagGrid")
                .IsUnique()
                .HasFilter("[ClaimStatus] IN ('Claimed', 'Submitted', 'Approved') AND [FlagGridId] IS NOT NULL");

            entity.HasOne(e => e.FlagGrid)
                .WithMany(e => e.FlagClaims)
                .HasForeignKey(e => e.FlagGridId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.Honoree)
                .WithMany()
                .HasForeignKey(e => e.HonoreeId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.ToTable("UserProfiles", "dbo");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.OwnerObjectId).HasMaxLength(UserProfileLimits.OwnerObjectIdMaxLength).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(UserProfileLimits.DisplayNameMaxLength).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(UserProfileLimits.EmailMaxLength).IsRequired();
            entity.Property(e => e.Phone).HasMaxLength(UserProfileLimits.PhoneMaxLength).IsRequired();
            entity.Property(e => e.Organization).HasMaxLength(UserProfileLimits.OrganizationMaxLength);
            entity.Property(e => e.DisplayNameSource).HasMaxLength(UserProfileLimits.FieldSourceMaxLength);
            entity.Property(e => e.PhoneSource).HasMaxLength(UserProfileLimits.FieldSourceMaxLength);
            entity.Property(e => e.OrganizationSource).HasMaxLength(UserProfileLimits.FieldSourceMaxLength);

            entity.HasIndex(e => e.OwnerObjectId).IsUnique();
        });

        modelBuilder.Entity<HonoreeSearchResult>(entity =>
        {
            entity.ToView("vwHonorees", "dbo");
            entity.HasNoKey();
            entity.Property(e => e.PDFUrl).HasColumnName("PDFUrl");
        });

        modelBuilder.Entity<HonoreeChangeRequest>(entity =>
        {
            entity.ToTable("HonoreeChangeRequests", "dbo");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FirstName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.MiddleName).HasMaxLength(200);
            entity.Property(e => e.LastName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Suffix).HasMaxLength(100);
            entity.Property(e => e.Nickname).HasMaxLength(200);
            entity.Property(e => e.Rank).HasMaxLength(200);
            entity.Property(e => e.DatesUserEntry).HasMaxLength(500);
            entity.Property(e => e.SubmitterPhoneNumber).HasMaxLength(50);
            entity.Property(e => e.SubmitterEmailAddress).HasMaxLength(255);
            entity.Property(e => e.RequestStatus).HasMaxLength(30).IsRequired();
            entity.Property(e => e.ReviewedBy).HasMaxLength(255);

            entity.HasIndex(e => e.FlagClaimId);
            entity.HasIndex(e => e.FlagGridId);

            entity.HasOne(e => e.FlagClaim)
                .WithMany(e => e.HonoreeChangeRequests)
                .HasForeignKey(e => e.FlagClaimId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.FlagGrid)
                .WithMany()
                .HasForeignKey(e => e.FlagGridId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.Honoree)
                .WithMany()
                .HasForeignKey(e => e.HonoreeId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.ServiceBranch)
                .WithMany()
                .HasForeignKey(e => e.ServiceBranchId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.ServiceBranchCategory)
                .WithMany()
                .HasForeignKey(e => e.ServiceBranchCategoryId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<CardPrintEvent>(entity =>
        {
            entity.ToTable("CardPrintEvents", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PrintedBy).HasMaxLength(CardPrintEventLimits.PrintedByMaxLength).IsRequired();
            entity.Property(e => e.Comment).HasMaxLength(CardPrintEventLimits.CommentMaxLength);
            entity.HasIndex(e => e.CardPrintBatchId).IsUnique();
            entity.HasIndex(e => e.PrintedUtc);
        });

        modelBuilder.Entity<HonoreeClaim>(entity =>
        {
            entity.ToTable("HonoreeClaims", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(255).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(255);
            entity.Property(e => e.ClaimStatus).HasMaxLength(30).IsRequired();
            entity.HasIndex(e => new { e.HonoreeId, e.UserId }).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasOne(e => e.Honoree)
                .WithMany()
                .HasForeignKey(e => e.HonoreeId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<ReprintDuplicateReview>(entity =>
        {
            entity.ToTable("ReprintDuplicateReviews", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
            entity.Property(e => e.ResolvedBy).HasMaxLength(255);
            entity.HasIndex(e => e.DuplicateChangeRequestId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasOne(e => e.Honoree)
                .WithMany()
                .HasForeignKey(e => e.HonoreeId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<HonoreeNotDuplicatePair>(entity =>
        {
            entity.ToTable("HonoreeNotDuplicatePairs", "dbo", table =>
                table.HasCheckConstraint("CK_HonoreeNotDuplicatePairs_Ordered", "[LowHonoreeId] < [HighHonoreeId]"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MarkedBy).HasMaxLength(255).IsRequired();
            entity.HasIndex(e => new { e.LowHonoreeId, e.HighHonoreeId }).IsUnique();
            entity.HasOne(e => e.LowHonoree)
                .WithMany()
                .HasForeignKey(e => e.LowHonoreeId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(e => e.HighHonoree)
                .WithMany()
                .HasForeignKey(e => e.HighHonoreeId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<HonoreeMergeAudit>(entity =>
        {
            entity.ToTable("HonoreeMergeAudits", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ClientRequestId);
            entity.HasIndex(e => e.ClientRequestId)
                .HasDatabaseName("UX_HonoreeMergeAudits_ClientRequestId")
                .IsUnique()
                .HasFilter("[ClientRequestId] IS NOT NULL");

            entity.Property(e => e.AbsorbedHonoreeIds).IsRequired();
            entity.Property(e => e.BeforeState).IsRequired();
            entity.Property(e => e.AfterSurvivorState).IsRequired();
            entity.Property(e => e.AfterState).IsRequired();
            entity.Property(e => e.CreatedHonoreeClaimIds).IsRequired();
            entity.Property(e => e.MergedBy).HasMaxLength(255).IsRequired();
            entity.Property(e => e.UndoneBy).HasMaxLength(255);
            entity.HasIndex(e => e.MergedUtc);
        });

        modelBuilder.Entity<FlagRemovalQueueItem>(entity =>
        {
            entity.ToTable("FlagRemovalQueueItems", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.HonoreeName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.FlagGridName).HasMaxLength(50).IsRequired();
            entity.Property(e => e.NewFlagGridName).HasMaxLength(50);
            entity.Property(e => e.Source).HasMaxLength(20).IsRequired();
            entity.Property(e => e.SourceDetail).HasMaxLength(200);
            entity.Property(e => e.CreatedBy).HasMaxLength(256).IsRequired();
            entity.Property(e => e.DoneBy).HasMaxLength(256);
            entity.Property(e => e.CanceledBy).HasMaxLength(256);
            entity.Property(e => e.CancelReason).HasMaxLength(200);
            entity.HasIndex(e => new { e.FlagGridName, e.Id })
                .HasDatabaseName("IX_FlagRemovalQueueItems_Open")
                .HasFilter("[DoneUtc] IS NULL AND [CanceledUtc] IS NULL");
            entity.HasIndex(e => e.MergeAuditId)
                .HasDatabaseName("IX_FlagRemovalQueueItems_MergeAuditId");
            entity.HasOne(e => e.MergeAudit)
                .WithMany()
                .HasForeignKey(e => e.MergeAuditId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}
