using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;
using PFOH.Api.Models;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/admin/review")]
[Authorize(Policy = "AdminOnly")]
public class AdminReviewController(PfohDbContext db, IConfiguration configuration, IWebHostEnvironment environment) : ControllerBase
{
    private readonly HonoreeFileStorage fileStorage = new(configuration);

    [HttpGet("pending")]
    public async Task<ActionResult<IReadOnlyList<AdminReviewItemDto>>> GetPending(CancellationToken ct)
    {
        var submitted = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Include(r => r.FlagClaim)
            .Include(r => r.FlagGrid)
            .Include(r => r.ServiceBranch)
            .Where(r => r.RequestStatus == "Submitted")
            .OrderBy(r => r.SubmittedUtc ?? r.CreatedUtc)
            .Select(r => ToReviewDto(r))
            .ToListAsync(ct);

        return Ok(submitted);
    }

    [HttpGet("approved-reprint-queue")]
    public async Task<ActionResult<IReadOnlyList<AdminPrintQueueItemDto>>> GetApprovedReprintQueue(CancellationToken ct)
    {
        var approved = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Include(r => r.FlagClaim)
            .Include(r => r.FlagGrid)
            .Include(r => r.ServiceBranch)
            .Where(r =>
                r.RequestStatus == "Approved" &&
                r.RequiresCardReprint &&
                r.CardPrintedUtc == null)
            .OrderBy(r => r.ReviewedUtc ?? r.SubmittedUtc ?? r.CreatedUtc)
            .ToListAsync(ct);

        var honoreeIds = approved
            .Where(r => r.HonoreeId.HasValue)
            .Select(r => r.HonoreeId!.Value)
            .Distinct()
            .ToList();

        var pdfLookup = await db.HonoreeSearchResults
            .AsNoTracking()
            .Where(h => honoreeIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, h => h.PDFUrl, ct);

        var dtos = approved
            .Select(r => new AdminPrintQueueItemDto(
                r.Id,
                r.FlagClaimId,
                r.HonoreeId,
                BuildHonoreeName(r),
                r.FlagGrid?.FlagGridName ?? string.Empty,
                r.ServiceBranch?.ServiceBranchName,
                r.HonoreeId.HasValue && pdfLookup.TryGetValue(r.HonoreeId.Value, out var pdfUrl) ? pdfUrl : null,
                r.ReviewedUtc,
                r.CardPrintedUtc))
            .ToList();

        return Ok(dtos);
    }

    [HttpPost("{changeRequestId:int}/approve")]
    public async Task<ActionResult<AdminReviewItemDto>> Approve(
        int changeRequestId,
        [FromBody] ApproveChangeRequest request,
        CancellationToken ct)
    {
        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var change = await db.HonoreeChangeRequests
            .Include(r => r.FlagClaim)
            .Include(r => r.FlagGrid)
            .Include(r => r.ServiceBranch)
            .FirstOrDefaultAsync(r => r.Id == changeRequestId, ct);

        if (change is null)
        {
            return NotFound("Change request was not found.");
        }

        if (change.RequestStatus != "Submitted")
        {
            return Conflict("Only submitted change requests can be approved.");
        }

        var honoree = await ApplyApprovedChanges(change, adminName, ct);

        change.HonoreeId = honoree.Id;
        change.RequestStatus = "Approved";
        change.ReviewedUtc = DateTime.UtcNow;
        change.ReviewedBy = adminName;
        change.ReviewNotes = request.ReviewNotes;
        change.RequiresCardReprint = request.RequiresCardReprint;

        if (change.FlagClaim is not null)
        {
            change.FlagClaim.HonoreeId = honoree.Id;
            change.FlagClaim.ClaimStatus = "Claimed";
            change.FlagClaim.ApprovedUtc = DateTime.UtcNow;
            change.FlagClaim.ApprovedBy = adminName;
            change.FlagClaim.AdminNotes = request.ReviewNotes;
        }

        await db.SaveChangesAsync(ct);
        await RegenerateHonoreePdfAsync(honoree.Id, ct);
        await transaction.CommitAsync(ct);

        return Ok(ToReviewDto(change));
    }

    [HttpPost("{changeRequestId:int}/reject")]
    public async Task<ActionResult<AdminReviewItemDto>> Reject(
        int changeRequestId,
        [FromBody] RejectChangeRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        var change = await db.HonoreeChangeRequests
            .Include(r => r.FlagClaim)
            .Include(r => r.FlagGrid)
            .Include(r => r.ServiceBranch)
            .FirstOrDefaultAsync(r => r.Id == changeRequestId, ct);

        if (change is null)
        {
            return NotFound("Change request was not found.");
        }

        if (change.RequestStatus != "Submitted")
        {
            return Conflict("Only submitted change requests can be rejected.");
        }

        change.RequestStatus = "Rejected";
        change.ReviewedUtc = DateTime.UtcNow;
        change.ReviewedBy = adminName;
        change.ReviewNotes = request.ReviewNotes;
        change.RequiresCardReprint = false;

        if (change.FlagClaim is not null)
        {
            change.FlagClaim.ClaimStatus = "Claimed";
            change.FlagClaim.RejectedUtc = DateTime.UtcNow;
            change.FlagClaim.RejectedBy = adminName;
            change.FlagClaim.AdminNotes = request.ReviewNotes;
        }

        await db.SaveChangesAsync(ct);

        return Ok(ToReviewDto(change));
    }

    private async Task<Sponsor?> GetOrCreateSubmitterSponsorAsync(
        string email,
        string? displayName,
        CancellationToken ct)
    {
        var normalizedEmail = email.Trim();

        var existing = await db.Sponsors
            .FirstOrDefaultAsync(s =>
                s.DeletedDate == null &&
                s.EmailAddress == normalizedEmail,
                ct);

        if (existing is not null)
        {
            existing.IsActive = true;
            existing.ModifiedBy = normalizedEmail;
            existing.ModifiedDate = DateTime.UtcNow;
            return existing;
        }

        var sponsorCategoryId = await db.SponsorCategories
            .Where(c => c.DeletedDate == null)
            .OrderBy(c => c.Id)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync(ct);

        if (sponsorCategoryId is null)
        {
            return null;
        }

        var nameParts = (displayName ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var firstName = nameParts.Length > 0
            ? nameParts[0]
            : normalizedEmail.Split('@')[0];

        var lastName = nameParts.Length > 1
            ? nameParts[^1]
            : "Submitter";

        var sponsor = new Sponsor
        {
            SponsorCategoryId = sponsorCategoryId.Value,
            Description = "Submitted through the Plano Flags of Honor nomination form.",
            FirstName = firstName,
            MiddleName = nameParts.Length > 2 ? string.Join(" ", nameParts.Skip(1).Take(nameParts.Length - 2)) : null,
            LastName = lastName,
            Suffix = null,
            Nickname = null,
            Salutation = string.Empty,
            PhoneNumber = string.Empty,
            EmailAddress = normalizedEmail,
            StreetAddress = string.Empty,
            City = string.Empty,
            State = string.Empty,
            ZipCode = string.Empty,
            IsActive = true,
            CreatedBy = normalizedEmail,
            CreatedDate = DateTime.UtcNow,
            ModifiedBy = normalizedEmail,
            ModifiedDate = DateTime.UtcNow
        };

        db.Sponsors.Add(sponsor);
        return sponsor;
    }

    private async Task<Honoree> ApplyApprovedChanges(
        HonoreeChangeRequest change,
        string adminName,
        CancellationToken ct)
    {
        Honoree? honoree = null;

        if (change.HonoreeId.HasValue)
        {
            honoree = await db.Honorees
                .FirstOrDefaultAsync(h => h.Id == change.HonoreeId.Value, ct);
        }

        if (honoree is null)
        {
            honoree = new Honoree
            {
                CreatedBy = adminName,
                CreatedDate = DateTime.UtcNow,
                IsActive = true,
                Salutation = string.Empty,
                NameUserEntry = string.Empty,
                PhotoFileName = string.Empty,
                PhoneNumber = string.Empty,
                EmailAddress = string.Empty,
                Awards = string.Empty,
                ConflictsServed = string.Empty,
                DatesUserEntry = string.Empty,
                Description = string.Empty,
                Rank = string.Empty
            };

            db.Honorees.Add(honoree);
        }

        honoree.FirstName = change.FirstName.Trim();
        honoree.MiddleName = change.MiddleName?.Trim();
        honoree.LastName = change.LastName.Trim();
        honoree.Suffix = change.Suffix?.Trim();
        honoree.Nickname = change.Nickname?.Trim();
        honoree.Rank = change.Rank?.Trim() ?? string.Empty;
        honoree.ServiceBranchId = change.ServiceBranchId;
        honoree.ServiceBranchCategoryId = change.ServiceBranchCategoryId;
        honoree.StartYear = change.StartYear;
        honoree.EndYear = change.EndYear;
        honoree.DatesUserEntry = change.DatesUserEntry?.Trim() ?? string.Empty;
        honoree.ConflictsServed = change.ConflictsServed?.Trim() ?? string.Empty;
        honoree.Awards = change.Awards?.Trim() ?? string.Empty;
        honoree.Description = change.Description?.Trim() ?? string.Empty;
        honoree.KIA = change.KIA;
        honoree.PhoneNumber = change.SubmitterPhoneNumber?.Trim() ?? honoree.PhoneNumber;
        honoree.EmailAddress = change.SubmitterEmailAddress?.Trim() ?? honoree.EmailAddress;

        if (change.FlagClaim is not null &&
            !string.IsNullOrWhiteSpace(change.FlagClaim.ExternalUserEmail))
        {
            var submitterSponsor = await GetOrCreateSubmitterSponsorAsync(
                change.FlagClaim.ExternalUserEmail,
                change.FlagClaim.ExternalUserName,
                ct);

            if (submitterSponsor is not null)
            {
                honoree.SponsorId = submitterSponsor.Id;
            }
        }

        honoree.FlagGridId = change.FlagGridId;
        honoree.IsActive = true;
        honoree.ModifiedBy = adminName;
        honoree.ModifiedDate = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        var finalPhotoFileName = await fileStorage.PromoteImageAsync(change.PhotoFileName, honoree.Id, ct);
        if (!string.IsNullOrWhiteSpace(finalPhotoFileName))
        {
            honoree.PhotoFileName = finalPhotoFileName;
            change.PhotoFileName = finalPhotoFileName;
        }

        var flagGrid = await db.FlagGrids.FirstOrDefaultAsync(f => f.Id == change.FlagGridId, ct);
        if (flagGrid is not null)
        {
            flagGrid.HonoreeId = honoree.Id;
            flagGrid.ModifiedBy = adminName;
            flagGrid.ModifiedDate = DateTime.UtcNow;
        }

        return honoree;
    }

    private async Task RegenerateHonoreePdfAsync(int honoreeId, CancellationToken ct)
    {
        var honoree = await db.Honorees
            .AsNoTracking()
            .Include(h => h.FlagGrid)
            .Include(h => h.ServiceBranch)
            .Include(h => h.Sponsor)
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive && h.DeletedDate == null, ct);

        if (honoree is null)
        {
            return;
        }

        var searchResult = await db.HonoreeSearchResults
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive, ct);

        var photoBytes = await fileStorage.DownloadImageAsync(honoree.PhotoFileName, searchResult?.ImageUrl, ct);
        var reportAssets = await fileStorage.LoadReportAssetsAsync(honoree, searchResult, ct);
        var pdf = HonoreeReportPdfGenerator.Create(environment, honoree, searchResult, photoBytes, reportAssets);
        await fileStorage.UploadPdfAsync(honoree.Id, pdf, ct);
    }

    private static AdminReviewItemDto ToReviewDto(HonoreeChangeRequest r) => new(
        r.Id,
        r.FlagClaimId,
        r.FlagGridId,
        r.FlagGrid?.FlagGridName ?? string.Empty,
        r.HonoreeId,
        BuildHonoreeName(r),
        r.Rank,
        r.ServiceBranch?.ServiceBranchName,
        r.FlagClaim?.ExternalUserEmail ?? string.Empty,
        r.FlagClaim?.ExternalUserName,
        r.RequestStatus,
        r.CreatedUtc,
        r.SubmittedUtc,
        r.RequiresCardReprint,
        r.CardPrintedUtc,
        r.ReviewNotes);

    private static string BuildHonoreeName(HonoreeChangeRequest r)
    {
        return string.Join(
            " ",
            new[] { r.FirstName, r.MiddleName, r.LastName, r.Suffix }
                .Where(v => !string.IsNullOrWhiteSpace(v)));
    }
}
