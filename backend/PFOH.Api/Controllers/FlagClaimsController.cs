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
[Route("api/flag-claims")]
[Authorize]
public class FlagClaimsController(
    PfohDbContext db,
    IWebHostEnvironment environment,
    UserProfileService profiles,
    HonoreeFileStorage fileStorage,
    PendingReviewMaintenance pendingReviews,
    ReprintQueueMaintenance reprintQueue) : ControllerBase
{

    // A flag claim represents semi-ownership by a user.
    // These statuses keep the flag associated with the claimant.
    private static readonly string[] OwnershipClaimStatuses = ["Claimed", "Submitted", "Approved", "Rejected"];

    // These statuses mean there is a submitted or in-progress change cycle.
    private static readonly string[] OpenChangeStatuses = ["Submitted"];

    [HttpGet("/api/flagclaims/change-requests/{id:int}/photo")]
    public async Task<IActionResult> GetChangeRequestPhoto(int id, CancellationToken ct)
    {
        var change = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Include(r => r.FlagClaim)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (change is null)
        {
            return NotFound("Honoree change request was not found.");
        }

        var isAdmin = User.IsInRole("PFOH.Admin") || User.IsInRole("Admin");
        var isOwner = change.FlagClaim is not null &&
            string.Equals(change.FlagClaim.ExternalUserObjectId, User.GetExternalObjectId(), StringComparison.Ordinal);

        if (!isAdmin && !isOwner)
        {
            return Forbid();
        }

        if (!IsPendingHonoreePhoto(change.PhotoFileName))
        {
            return NotFound("Pending honoree photo was not found.");
        }

        try
        {
            var photoFileName = change.PhotoFileName!.Trim().Replace('\\', '/');
            var image = await fileStorage.OpenHonoreeImageAsync(photoFileName, ct);
            if (image is null)
            {
                return NotFound("Pending honoree photo was not found.");
            }

            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            return File(image.Stream, image.ContentType);
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = "The pending honoree photo could not be read from honoreeimages.",
                    detail = ex.Message
                });
        }
    }

    [HttpGet("{claimId:int}/card")]
    public async Task<IActionResult> DownloadHonoreeCard(int claimId, CancellationToken ct)
    {
        var claim = await db.FlagClaims
            .AsNoTracking()
            .Include(item => item.Honoree)!.ThenInclude(honoree => honoree!.FlagGrid)
            .Include(item => item.Honoree)!.ThenInclude(honoree => honoree!.ServiceBranch)
            .Include(item => item.HonoreeChangeRequests).ThenInclude(request => request.FlagGrid)
            .Include(item => item.HonoreeChangeRequests).ThenInclude(request => request.ServiceBranch)
            .FirstOrDefaultAsync(item => item.Id == claimId, ct);

        if (claim is null)
        {
            return NotFound("Flag claim was not found.");
        }

        var isAdmin = User.IsInRole("PFOH.Admin") || User.IsInRole("Admin");
        var isOwner = string.Equals(claim.ExternalUserObjectId, User.GetExternalObjectId(), StringComparison.Ordinal);
        if (!isAdmin && !isOwner)
        {
            return Forbid();
        }

        var latest = claim.HonoreeChangeRequests
            .OrderByDescending(request => request.CreatedUtc)
            .ThenByDescending(request => request.Id)
            .FirstOrDefault();
        var usePendingData = latest is not null &&
            !string.Equals(latest.RequestStatus, "Approved", StringComparison.OrdinalIgnoreCase);

        try
        {
            byte[] pdf;
            if (usePendingData)
            {
                pdf = await HonoreeCardPdf.RenderAsync(
                    environment,
                    fileStorage,
                    HonoreeCardPdf.FromChangeRequest(latest!),
                    null,
                    latest!.PhotoFileName,
                    null,
                    ct);
            }
            else if (claim.Honoree is { DeletedDate: null })
            {
                var searchResult = await db.HonoreeSearchResults
                    .AsNoTracking()
                    .FirstOrDefaultAsync(honoree => honoree.Id == claim.Honoree.Id && honoree.IsActive, ct);
                pdf = await HonoreeCardPdf.RenderAsync(
                    environment,
                    fileStorage,
                    claim.Honoree,
                    searchResult,
                    claim.Honoree.PhotoFileName,
                    searchResult?.ImageUrl,
                    ct);
            }
            else
            {
                return NotFound("Honoree card was not found.");
            }

            return File(pdf, "application/pdf", $"honoree-card-{claimId}.pdf");
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = "The honoree card could not be created.",
                    detail = ex.Message
                });
        }
    }

    [HttpGet("my")]
    public async Task<ActionResult<IReadOnlyList<FlagClaimDto>>> GetMyClaims(CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();

        var claims = await db.FlagClaims
            .AsNoTracking()
            .Include(c => c.FlagGrid)
            .Include(c => c.Honoree)
            .Include(c => c.HonoreeChangeRequests)
            .Where(c =>
                c.ExternalUserObjectId == userObjectId &&
                c.ClaimStatus != "AdminDirectEdit" &&
                c.ClaimStatus != "AdminDirectEditCompleted" &&
                c.ClaimStatus != "Unclaimed" &&
                c.ClaimStatus != "Cancelled")
            .OrderByDescending(c => c.CreatedUtc)
            .ToListAsync(ct);

        var honoreeIds = claims
            .Where(c => c.HonoreeId.HasValue)
            .Select(c => c.HonoreeId!.Value)
            .Distinct()
            .ToList();

        var imageUrlByHonoreeId = honoreeIds.Count == 0
            ? new Dictionary<int, string?>()
            : await db.HonoreeSearchResults
                .AsNoTracking()
                .Where(h => honoreeIds.Contains(h.Id))
                .Select(h => new { h.Id, h.ImageUrl })
                .ToDictionaryAsync(h => h.Id, h => h.ImageUrl, ct);

        return Ok(claims.Select(claim =>
        {
            var imageUrl =
                claim.HonoreeId is int honoreeId &&
                imageUrlByHonoreeId.TryGetValue(honoreeId, out var foundImageUrl)
                    ? foundImageUrl
                    : null;

            return ToDto(claim, imageUrl);
        }).ToList());
    }



    [HttpGet("admin/honoree/{honoreeId:int}/claimants")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<IReadOnlyList<object>>> GetHonoreeClaimants(int honoreeId, CancellationToken ct)
    {
        var claims = await db.FlagClaims
            .AsNoTracking()
            .Where(c =>
                c.HonoreeId == honoreeId &&
                c.ClaimStatus != "Unclaimed" &&
                c.ClaimStatus != "Cancelled" &&
                c.ClaimStatus != "AdminDirectEditCompleted")
            .OrderByDescending(c => c.CreatedUtc)
            .Select(c => new
            {
                claimId = c.Id,
                claimantName = c.ExternalUserName,
                claimantEmail = c.ExternalUserEmail,
                ownerObjectId = c.ExternalUserObjectId,
                claimStatus = c.ClaimStatus,
                createdUtc = c.CreatedUtc,
                submittedUtc = c.SubmittedUtc,
                latestRequestStatus = c.HonoreeChangeRequests
                    .OrderByDescending(r => r.CreatedUtc)
                    .Select(r => r.RequestStatus)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var contacts = await profiles.GetAccountContactsAsync(claims.Select(claim => claim.ownerObjectId), ct);

        return Ok(claims.Select(claim =>
        {
            contacts.TryGetValue(claim.ownerObjectId, out var contact);
            return new
            {
                claim.claimId,
                claim.claimantName,
                claim.claimantEmail,
                claimantPhone = contact?.Phone,
                claimantOrganization = contact?.Organization,
                claim.claimStatus,
                claim.createdUtc,
                claim.submittedUtc,
                claim.latestRequestStatus
            };
        }));
    }

    [HttpPost("{claimId:int}/unclaim")]
    public async Task<ActionResult> Unclaim(int claimId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();
        var isAdmin = User.IsInRole("PFOH.Admin");

        var claim = await db.FlagClaims
            .Include(c => c.HonoreeChangeRequests)
            .Include(c => c.Honoree)
            .FirstOrDefaultAsync(c => c.Id == claimId, ct);

        if (claim is null)
        {
            return NotFound(new { message = "Flag claim was not found." });
        }

        if (!isAdmin && claim.ExternalUserObjectId != userObjectId)
        {
            return Forbid();
        }

        if (claim.ClaimStatus == "AdminDirectEdit")
        {
            return BadRequest(new { message = "Admin direct-edit claims cannot be unclaimed from My claimed flags." });
        }

        var now = DateTime.UtcNow;
        var honoree = claim.Honoree;
        var approvedOnField = honoree is { IsActive: true, DeletedDate: null } && claim.HonoreeId is > 0;
        claim.ClaimStatus = "Unclaimed";
        claim.AdminNotes = string.IsNullOrWhiteSpace(claim.AdminNotes)
            ? $"Withdrawn on {now:u}."
            : $"{claim.AdminNotes}\nWithdrawn on {now:u}.";

        if (!approvedOnField)
        {
            claim.FlagGridId = null;
        }

        foreach (var request in claim.HonoreeChangeRequests.Where(r => r.RequestStatus is "Submitted" or "Draft"))
        {
            request.RequestStatus = PendingReviewMaintenance.Withdrawn;
            request.ReviewNotes = string.IsNullOrWhiteSpace(request.ReviewNotes)
                ? "Withdrawn by the submitter."
                : $"{request.ReviewNotes}\nWithdrawn by the submitter.";

            if (!approvedOnField || request.HonoreeId is not > 0)
            {
                request.FlagGridId = null;
            }

            await fileStorage.DeletePendingImageAsync(request.PhotoFileName, ct);
            if (HonoreeFileStorage.IsPendingPhotoName(request.PhotoFileName))
            {
                request.PhotoFileName = null;
            }
        }

        if (approvedOnField && honoree is not null)
        {
            var spotId = honoree.FlagGridId ?? claim.FlagGridId;
            if (honoree.FlagGridId is null && spotId is not null)
            {
                honoree.FlagGridId = spotId;
            }

            if (claim.FlagGridId is null && spotId is not null)
            {
                claim.FlagGridId = spotId;
            }

            var alreadyQueued = claim.HonoreeChangeRequests.Any(request =>
                request.RequestStatus == PendingReviewMaintenance.Withdrawal);
            if (!alreadyQueued)
            {
                claim.HonoreeChangeRequests.Add(new HonoreeChangeRequest
                {
                    FlagClaimId = claim.Id,
                    FlagGridId = spotId,
                    HonoreeId = honoree.Id,
                    FirstName = honoree.FirstName,
                    MiddleName = honoree.MiddleName,
                    LastName = honoree.LastName,
                    Suffix = honoree.Suffix,
                    Nickname = honoree.Nickname,
                    Rank = honoree.Rank,
                    ServiceBranchId = honoree.ServiceBranchId,
                    ServiceBranchCategoryId = honoree.ServiceBranchCategoryId,
                    StartYear = honoree.StartYear,
                    EndYear = honoree.EndYear,
                    ConflictsServed = honoree.ConflictsServed,
                    Awards = honoree.Awards,
                    Description = honoree.Description,
                    KIA = honoree.KIA,
                    RequestStatus = PendingReviewMaintenance.Withdrawal,
                    CreatedUtc = now,
                    SubmittedUtc = now,
                    ReviewNotes = "Withdrawn by the submitter. The flag spot stays assigned until an administrator confirms it was removed from the field."
                });
            }
        }

        await db.SaveChangesAsync(ct);

        return Ok(new { message = "Flag unclaimed." });
    }


    [HttpPost("{flagGridId:int}/claim")]
    public async Task<ActionResult<FlagClaimDto>> ClaimFlagGrid(int flagGridId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();
        var email = User.GetEmail();
        var name = await profiles.ResolveDisplayNameAsync(User, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var flagGrid = await db.FlagGrids
            .FirstOrDefaultAsync(f => f.Id == flagGridId && !f.Reserved && f.DeletedDate == null, ct);

        if (flagGrid is null)
        {
            return NotFound("Flag grid was not found or is reserved.");
        }

        var activeHonoreeExists = await db.Honorees
            .AnyAsync(h => h.IsActive && h.FlagGridId == flagGridId && h.DeletedDate == null, ct);

        if (activeHonoreeExists)
        {
            return Conflict("This flag grid is already assigned to an active honoree. Search for the honoree and claim the existing honoree record instead.");
        }

        var existingOwnershipClaims = await db.FlagClaims
            .Include(c => c.FlagGrid)
            .Include(c => c.Honoree)
            .Include(c => c.HonoreeChangeRequests)
            .Where(c => c.FlagGridId == flagGridId && OwnershipClaimStatuses.Contains(c.ClaimStatus))
            .ToListAsync(ct);

        var existingUserClaim = existingOwnershipClaims
            .FirstOrDefault(c => c.ExternalUserObjectId == userObjectId);

        var otherClaimantCount = existingOwnershipClaims
            .Count(c => c.ExternalUserObjectId != userObjectId);

        if (existingUserClaim is not null)
        {
            return Ok(ToDto(
                existingUserClaim,
                null,
                otherClaimantCount,
                BuildOtherClaimantsNotice(otherClaimantCount)));
        }

        var claim = new FlagClaim
        {
            FlagGridId = flagGridId,
            HonoreeId = flagGrid.HonoreeId,
            ExternalUserObjectId = userObjectId,
            ExternalUserEmail = email,
            ExternalUserName = name,
            ClaimStatus = "Claimed",
            CreatedUtc = DateTime.UtcNow,
            FlagGrid = flagGrid
        };

        db.FlagClaims.Add(claim);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Ok(ToDto(
            claim,
            null,
            otherClaimantCount,
            BuildOtherClaimantsNotice(otherClaimantCount)));
    }

    [HttpPost("honoree/{honoreeId:int}/claim")]
    public async Task<ActionResult<FlagClaimDto>> ClaimExistingHonoree(int honoreeId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();
        var email = User.GetEmail();
        var name = await profiles.ResolveDisplayNameAsync(User, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var honoree = await db.Honorees
            .Include(h => h.FlagGrid)
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive && h.DeletedDate == null, ct);

        if (honoree is null)
        {
            return NotFound("Honoree was not found.");
        }

        if (honoree.FlagGridId is null)
        {
            return BadRequest("This honoree is not assigned to a flag grid.");
        }

        var existingOwnershipClaims = await db.FlagClaims
            .Include(c => c.FlagGrid)
            .Include(c => c.Honoree)
            .Include(c => c.HonoreeChangeRequests)
            .Where(c =>
                c.FlagGridId == honoree.FlagGridId.Value &&
                OwnershipClaimStatuses.Contains(c.ClaimStatus))
            .ToListAsync(ct);

        var existingUserClaim = existingOwnershipClaims
            .FirstOrDefault(c => c.ExternalUserObjectId == userObjectId);

        var otherClaimantCount = existingOwnershipClaims
            .Count(c => c.ExternalUserObjectId != userObjectId);

        if (existingUserClaim is not null)
        {
            var existingImageUrl = await db.HonoreeSearchResults
                .AsNoTracking()
                .Where(h => h.Id == honoree.Id)
                .Select(h => h.ImageUrl)
                .FirstOrDefaultAsync(ct);

            return Ok(ToDto(
                existingUserClaim,
                existingImageUrl,
                otherClaimantCount,
                BuildOtherClaimantsNotice(otherClaimantCount)));
        }

        var claim = new FlagClaim
        {
            FlagGridId = honoree.FlagGridId.Value,
            HonoreeId = honoree.Id,
            ExternalUserObjectId = userObjectId,
            ExternalUserEmail = email,
            ExternalUserName = name,
            ClaimStatus = "Claimed",
            CreatedUtc = DateTime.UtcNow,
            FlagGrid = honoree.FlagGrid,
            Honoree = honoree
        };

        claim.HonoreeChangeRequests.Add(BuildDraftFromHonoree(claim, honoree, email));

        db.FlagClaims.Add(claim);
        await db.SaveChangesAsync(ct);
        await RegenerateHonoreePdfAsync(honoree.Id, ct);
        await transaction.CommitAsync(ct);

        var imageUrl = await db.HonoreeSearchResults
            .AsNoTracking()
            .Where(h => h.Id == honoree.Id)
            .Select(h => h.ImageUrl)
            .FirstOrDefaultAsync(ct);

        return Ok(ToDto(
            claim,
            imageUrl,
            otherClaimantCount,
            BuildOtherClaimantsNotice(otherClaimantCount)));
    }

    [HttpPost("nominate")]
    public async Task<ActionResult<FlagClaimDto>> NominateHonoree(
        [FromForm] NominateHonoreeRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var userObjectId = User.GetExternalObjectId();
        var email = User.GetEmail();
        var name = await profiles.ResolveDisplayNameAsync(User, ct);

        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Your signed-in account must have an email address before you can nominate an honoree.");
        }

        await PendingSubmissionGridSchema.EnsureAsync(db, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var submitter = await GetOrCreateSubmitterSponsorAsync(email, name, ct);
        if (submitter is null)
        {
            return Conflict("No sponsor category exists for creating the submitter record.");
        }

        var claim = new FlagClaim
        {
            FlagGridId = null,
            HonoreeId = null,
            ExternalUserObjectId = userObjectId,
            ExternalUserEmail = email,
            ExternalUserName = name,
            ClaimStatus = "Submitted",
            CreatedUtc = DateTime.UtcNow,
            SubmittedUtc = DateTime.UtcNow
        };

        var change = new HonoreeChangeRequest
        {
            FlagGridId = null,
            HonoreeId = null,
            RequestStatus = "Submitted",
            CreatedUtc = DateTime.UtcNow,
            SubmittedUtc = DateTime.UtcNow,
            SubmitterPhoneNumber = request.SubmitterPhoneNumber,
            SubmitterEmailAddress = request.SubmitterEmailAddress ?? email
        };

        ApplyRequest(change, request);

        if (string.IsNullOrWhiteSpace(change.SubmitterEmailAddress))
        {
            change.SubmitterEmailAddress = email;
        }

        claim.HonoreeChangeRequests.Add(change);

        db.FlagClaims.Add(claim);
        await db.SaveChangesAsync(ct);

        var pendingPhotoFileName = await fileStorage.UploadPendingImageAsync(request.Photo, change.Id, ct);
        if (!string.IsNullOrWhiteSpace(pendingPhotoFileName))
        {
            change.PhotoFileName = pendingPhotoFileName;
            await db.SaveChangesAsync(ct);
        }

        await pendingReviews.SupersedeOlderPendingAsync(change, ct);
        await transaction.CommitAsync(ct);

        return Ok(ToDto(claim, null));
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("admin/honoree/{honoreeId:int}/edit")]
    public async Task<ActionResult<FlagClaimDto>> StartAdminDirectHonoreeEdit(int honoreeId, CancellationToken ct)
    {
        var adminObjectId = User.GetExternalObjectId();
        var adminEmail = User.GetEmail();
        var adminName = await profiles.ResolveDisplayNameAsync(User, ct);

        var honoree = await db.Honorees
            .Include(h => h.FlagGrid)
            .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive && h.DeletedDate == null, ct);

        if (honoree is null)
        {
            return NotFound("Honoree was not found.");
        }

        if (honoree.FlagGridId is null)
        {
            return BadRequest("This honoree is not assigned to a flag grid.");
        }

        var claim = new FlagClaim
        {
            FlagGridId = honoree.FlagGridId.Value,
            HonoreeId = honoree.Id,
            ExternalUserObjectId = adminObjectId,
            ExternalUserEmail = adminEmail,
            ExternalUserName = adminName,
            ClaimStatus = "AdminDirectEdit",
            CreatedUtc = DateTime.UtcNow,
            FlagGrid = honoree.FlagGrid,
            Honoree = honoree
        };

        claim.HonoreeChangeRequests.Add(BuildDraftFromHonoree(claim, honoree, adminEmail));

        db.FlagClaims.Add(claim);
        await db.SaveChangesAsync(ct);

        var imageUrl = await db.HonoreeSearchResults
            .AsNoTracking()
            .Where(h => h.Id == honoree.Id)
            .Select(h => h.ImageUrl)
            .FirstOrDefaultAsync(ct);

        return Ok(ToDto(claim, imageUrl));
    }

    [HttpPut("{claimId:int}/honoree-draft")]
    public async Task<ActionResult<HonoreeChangeRequestDto>> SaveHonoreeDraft(
        int claimId,
        [FromForm] SaveHonoreeChangeRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var userObjectId = User.GetExternalObjectId();
        var isAdmin = User.IsInRole("PFOH.Admin");

        var claim = await db.FlagClaims
            .Include(c => c.HonoreeChangeRequests)
            .FirstOrDefaultAsync(
                c => c.Id == claimId &&
                    (c.ExternalUserObjectId == userObjectId ||
                     (isAdmin && c.ClaimStatus == "AdminDirectEdit")),
                ct);

        if (claim is null)
        {
            return NotFound("Claim was not found.");
        }

        if (claim.ClaimStatus is "Cancelled")
        {
            return Conflict("This claim has been cancelled and cannot be edited.");
        }

        var draft = claim.HonoreeChangeRequests
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefault(r => r.RequestStatus == "Draft");

        if (draft is null)
        {
            draft = new HonoreeChangeRequest
            {
                FlagClaimId = claim.Id,
                FlagGridId = claim.FlagGridId,
                HonoreeId = claim.HonoreeId,
                CreatedUtc = DateTime.UtcNow,
                RequestStatus = "Draft"
            };

            db.HonoreeChangeRequests.Add(draft);
        }

        ApplyRequest(draft, request);

        if (draft.Id == 0)
        {
            await db.SaveChangesAsync(ct);
        }

        var pendingPhotoFileName = await fileStorage.UploadPendingImageAsync(request.Photo, draft.Id, ct);
        if (!string.IsNullOrWhiteSpace(pendingPhotoFileName))
        {
            draft.PhotoFileName = pendingPhotoFileName;
        }

        // Returning to Draft/Claimed lets an owner submit another change after a prior approval/rejection.
        // AdminDirectEdit is intentionally not a claim/ownership status.
        if (claim.ClaimStatus != "AdminDirectEdit")
        {
            claim.ClaimStatus = "Claimed";
        }

        await db.SaveChangesAsync(ct);

        return Ok(ToDto(draft));
    }

    [HttpPost("{claimId:int}/submit")]
    public async Task<ActionResult<FlagClaimDto>> SubmitClaim(int claimId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();

        var claim = await db.FlagClaims
            .Include(c => c.FlagGrid)
            .Include(c => c.Honoree)
            .Include(c => c.HonoreeChangeRequests)
            .FirstOrDefaultAsync(c => c.Id == claimId && c.ExternalUserObjectId == userObjectId, ct);

        if (claim is null)
        {
            return NotFound("Claim was not found.");
        }

        var latestDraft = claim.HonoreeChangeRequests
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefault(r => r.RequestStatus == "Draft");

        if (latestDraft is null)
        {
            return BadRequest("Make at least one change and save a draft before submitting this claim.");
        }

        latestDraft.RequestStatus = "Submitted";
        latestDraft.SubmittedUtc = DateTime.UtcNow;

        claim.ClaimStatus = "Submitted";
        claim.SubmittedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        await pendingReviews.SupersedeOlderPendingAsync(latestDraft, ct);

        return Ok(ToDto(claim, null));
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("{claimId:int}/admin-apply-reprint")]
    public async Task<ActionResult<FlagClaimDto>> AdminApplyAndQueueReprint(int claimId, CancellationToken ct)
    {
        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var claim = await db.FlagClaims
            .Include(c => c.FlagGrid)
            .Include(c => c.Honoree)
            .Include(c => c.HonoreeChangeRequests)
            .FirstOrDefaultAsync(c => c.Id == claimId && c.ClaimStatus == "AdminDirectEdit", ct);

        if (claim is null)
        {
            return NotFound("Admin edit session was not found.");
        }

        var latestDraft = claim.HonoreeChangeRequests
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefault(r => r.RequestStatus == "Draft");

        if (latestDraft is null)
        {
            return BadRequest("Save a draft before applying the admin edit.");
        }

        var honoree = await ApplyApprovedChanges(latestDraft, adminName, ct);

        latestDraft.HonoreeId = honoree.Id;
        latestDraft.RequestStatus = "Approved";
        latestDraft.SubmittedUtc ??= DateTime.UtcNow;
        latestDraft.ReviewedUtc = DateTime.UtcNow;
        latestDraft.ReviewedBy = adminName;
        latestDraft.ReviewNotes = "Direct administrator edit.";
        latestDraft.RequiresCardReprint = true;

        claim.HonoreeId = honoree.Id;
        claim.ClaimStatus = "AdminDirectEditCompleted";
        claim.SubmittedUtc = DateTime.UtcNow;
        claim.ApprovedUtc = DateTime.UtcNow;
        claim.ApprovedBy = adminName;
        claim.AdminNotes = "Direct administrator edit queued for reprint.";

        await db.SaveChangesAsync(ct);
        await reprintQueue.DeactivateOtherActiveReprintItemsForHonoreeAsync(honoree.Id, latestDraft.Id, adminName, ct);
        await transaction.CommitAsync(ct);

        var imageUrl = await db.HonoreeSearchResults
            .AsNoTracking()
            .Where(h => h.Id == honoree.Id)
            .Select(h => h.ImageUrl)
            .FirstOrDefaultAsync(ct);

        return Ok(ToDto(claim, imageUrl));
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

    private static HonoreeChangeRequest BuildDraftFromHonoree(
        FlagClaim claim,
        Honoree honoree,
        string submitterEmail)
    {
        return new HonoreeChangeRequest
        {
            FlagGridId = claim.FlagGridId,
            HonoreeId = honoree.Id,

            FirstName = honoree.FirstName,
            MiddleName = honoree.MiddleName,
            LastName = honoree.LastName,
            Suffix = honoree.Suffix,
            Nickname = honoree.Nickname,
            Rank = honoree.Rank,
            ServiceBranchId = honoree.ServiceBranchId,
            ServiceBranchCategoryId = honoree.ServiceBranchCategoryId,
            StartYear = honoree.StartYear,
            EndYear = honoree.EndYear,
            DatesUserEntry = honoree.DatesUserEntry,
            ConflictsServed = honoree.ConflictsServed,
            Awards = honoree.Awards,
            Description = honoree.Description,
            KIA = honoree.KIA,
            PhotoFileName = honoree.PhotoFileName,
            SubmitterPhoneNumber = null,
            SubmitterEmailAddress = submitterEmail,
            RequestStatus = "Draft",
            CreatedUtc = DateTime.UtcNow
        };
    }

    private static void ApplyRequest(HonoreeChangeRequest draft, SaveHonoreeChangeRequest request)
    {
        draft.FirstName = request.FirstName.Trim();
        draft.MiddleName = request.MiddleName?.Trim();
        draft.LastName = request.LastName.Trim();
        draft.Suffix = request.Suffix?.Trim();
        draft.Nickname = request.Nickname?.Trim();
        draft.Rank = request.Rank?.Trim();
        draft.ServiceBranchId = request.ServiceBranchId;
        draft.ServiceBranchCategoryId = request.ServiceBranchCategoryId;
        draft.StartYear = request.StartYear;
        draft.EndYear = request.EndYear;
        draft.DatesUserEntry = request.DatesUserEntry?.Trim();
        draft.ConflictsServed = request.ConflictsServed?.Trim();
        draft.Awards = request.Awards?.Trim();
        draft.Description = request.Description?.Trim();
        draft.KIA = request.Kia;
        draft.SubmitterPhoneNumber = request.SubmitterPhoneNumber?.Trim();
        draft.SubmitterEmailAddress = request.SubmitterEmailAddress?.Trim();
    }

    private static FlagClaimDto ToDto(FlagClaim claim, string? honoreeImageUrl, int otherClaimantCount = 0, string? claimNotice = null)
    {
        var latest = claim.HonoreeChangeRequests
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefault();

        var honoreeName = BuildHonoreeName(latest, claim.Honoree);

        return new FlagClaimDto(
            claim.Id,
            claim.FlagGridId,
            claim.FlagGrid?.FlagGridName ?? string.Empty,
            claim.HonoreeId,
            honoreeName,
            honoreeImageUrl,
            claim.ClaimStatus,
            claim.ExternalUserEmail,
            claim.ExternalUserName,
            claim.CreatedUtc,
            claim.SubmittedUtc,
            latest is null ? null : ToDto(latest),
            otherClaimantCount > 0,
            otherClaimantCount,
            claimNotice);
    }


    private static string? BuildOtherClaimantsNotice(int otherClaimantCount)
    {
        return otherClaimantCount <= 0
            ? null
            : otherClaimantCount == 1
                ? "Someone else has also suggested a change for this record. You may still submit a suggested change. Contact the Plano Flags of Honor administrator if you have questions."
                : $"Other people have also suggested a change for this record ({otherClaimantCount}). You may still submit a suggested change. Contact the Plano Flags of Honor administrator if you have questions.";
    }

    private static string BuildHonoreeName(HonoreeChangeRequest? latest, Honoree? honoree)
    {
        if (latest is not null)
        {
            var fromChangeRequest = AddNickname(
                string.Join(
                    " ",
                    new[] { latest.FirstName, latest.MiddleName, latest.LastName, latest.Suffix }
                        .Where(v => !string.IsNullOrWhiteSpace(v))),
                latest.Nickname);

            if (!string.IsNullOrWhiteSpace(fromChangeRequest))
            {
                return fromChangeRequest;
            }
        }

        if (honoree is not null)
        {
            var fromHonoree = AddNickname(
                string.Join(
                    " ",
                    new[] { honoree.FirstName, honoree.MiddleName, honoree.LastName, honoree.Suffix }
                        .Where(v => !string.IsNullOrWhiteSpace(v))),
                honoree.Nickname);

            if (!string.IsNullOrWhiteSpace(fromHonoree))
            {
                return fromHonoree;
            }
        }

        return string.Empty;
    }

    private static string AddNickname(string name, string? nickname)
    {
        var cleanName = name.Trim();
        var cleanNickname = nickname?.Trim();

        if (string.IsNullOrWhiteSpace(cleanNickname))
        {
            return cleanName;
        }

        return cleanName.EndsWith($"({cleanNickname})", StringComparison.OrdinalIgnoreCase)
            ? cleanName
            : $"{cleanName} ({cleanNickname})";
    }

    private static bool IsPendingHonoreePhoto(string? photoFileName)
    {
        if (string.IsNullOrWhiteSpace(photoFileName))
        {
            return false;
        }

        var name = photoFileName.Trim().Replace('\\', '/');
        return name.StartsWith("pending/", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("..", StringComparison.Ordinal);
    }

    private static HonoreeChangeRequestDto ToDto(HonoreeChangeRequest request) => new(
        request.Id,
        request.FlagClaimId,
        request.FlagGridId,
        request.HonoreeId,
        request.FirstName,
        request.MiddleName,
        request.LastName,
        request.Suffix,
        request.Nickname,
        request.Rank,
        request.ServiceBranchId,
        request.ServiceBranchCategoryId,
        request.StartYear,
        request.EndYear,
        request.DatesUserEntry,
        request.ConflictsServed,
        request.Awards,
        request.Description,
        request.KIA,
        request.PhotoFileName,
        request.SubmitterPhoneNumber,
        request.SubmitterEmailAddress,
        request.RequestStatus,
        request.CreatedUtc,
        request.SubmittedUtc);
}
