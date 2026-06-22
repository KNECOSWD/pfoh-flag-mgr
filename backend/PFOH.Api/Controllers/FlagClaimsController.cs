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
public class FlagClaimsController(PfohDbContext db, IConfiguration configuration, IWebHostEnvironment environment) : ControllerBase
{
    private readonly HonoreeFileStorage fileStorage = new(configuration);

    // A flag claim represents semi-ownership by a user.
    // These statuses keep the flag associated with the claimant.
    private static readonly string[] OwnershipClaimStatuses = ["Claimed", "Submitted", "Approved", "Rejected"];

    // These statuses mean there is a submitted or in-progress change cycle.
    private static readonly string[] OpenChangeStatuses = ["Submitted"];

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
                c.ClaimStatus != "AdminDirectEditCompleted")
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

    [HttpPost("{flagGridId:int}/claim")]
    public async Task<ActionResult<FlagClaimDto>> ClaimFlagGrid(int flagGridId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();
        var email = User.GetEmail();
        var name = User.GetDisplayName();

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

        var existingOwnershipClaim = await db.FlagClaims
            .Include(c => c.FlagGrid)
            .Include(c => c.Honoree)
            .Include(c => c.HonoreeChangeRequests)
            .FirstOrDefaultAsync(c => c.FlagGridId == flagGridId && OwnershipClaimStatuses.Contains(c.ClaimStatus), ct);

        if (existingOwnershipClaim is not null)
        {
            if (existingOwnershipClaim.ExternalUserObjectId == userObjectId)
            {
                return Ok(ToDto(existingOwnershipClaim, null));
            }

            return Conflict("This flag grid is already managed by another claimant.");
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

        return Ok(ToDto(claim, null));
    }

    [HttpPost("honoree/{honoreeId:int}/claim")]
    public async Task<ActionResult<FlagClaimDto>> ClaimExistingHonoree(int honoreeId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();
        var email = User.GetEmail();
        var name = User.GetDisplayName();

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

        var existingOwnershipClaim = await db.FlagClaims
            .Include(c => c.FlagGrid)
            .Include(c => c.Honoree)
            .Include(c => c.HonoreeChangeRequests)
            .FirstOrDefaultAsync(
                c => c.FlagGridId == honoree.FlagGridId.Value &&
                     OwnershipClaimStatuses.Contains(c.ClaimStatus),
                ct);

        if (existingOwnershipClaim is not null)
        {
            if (existingOwnershipClaim.ExternalUserObjectId == userObjectId)
            {
                return Ok(ToDto(existingOwnershipClaim, null));
            }

            return Conflict("This honoree's flag is already managed by another claimant.");
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

        return Ok(ToDto(claim, imageUrl));
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
        var name = User.GetDisplayName();

        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Your signed-in account must have an email address before you can nominate a honoree.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var submitter = await GetOrCreateSubmitterSponsorAsync(email, name, ct);
        if (submitter is null)
        {
            return Conflict("No sponsor category exists for creating the submitter record.");
        }

        var flagGrid = await GetNextAvailableFlagGridAsync(ct);
        if (flagGrid is null)
        {
            return Conflict("No available flag grid was found for this nomination.");
        }

        var claim = new FlagClaim
        {
            FlagGridId = flagGrid.Id,
            HonoreeId = null,
            ExternalUserObjectId = userObjectId,
            ExternalUserEmail = email,
            ExternalUserName = name,
            ClaimStatus = "Submitted",
            CreatedUtc = DateTime.UtcNow,
            SubmittedUtc = DateTime.UtcNow,
            FlagGrid = flagGrid
        };

        var change = new HonoreeChangeRequest
        {
            FlagGridId = flagGrid.Id,
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

        await transaction.CommitAsync(ct);

        return Ok(ToDto(claim, null));
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("admin/honoree/{honoreeId:int}/edit")]
    public async Task<ActionResult<FlagClaimDto>> StartAdminDirectHonoreeEdit(int honoreeId, CancellationToken ct)
    {
        var adminObjectId = User.GetExternalObjectId();
        var adminEmail = User.GetEmail();
        var adminName = User.GetDisplayName();

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
        var pdf = HonoreeReportPdfGenerator.Create(environment, honoree, searchResult, photoBytes);
        await fileStorage.UploadPdfAsync(honoree.Id, pdf, ct);
    }

    private async Task<FlagGrid?> GetNextAvailableFlagGridAsync(CancellationToken ct)
    {
        return await db.FlagGrids
            .Where(flagGrid =>
                !flagGrid.Reserved &&
                flagGrid.DeletedDate == null &&
                flagGrid.HonoreeId == null &&
                !db.Honorees.Any(h =>
                    h.IsActive &&
                    h.DeletedDate == null &&
                    h.FlagGridId == flagGrid.Id) &&
                !db.FlagClaims.Any(claim =>
                    claim.FlagGridId == flagGrid.Id &&
                    claim.ClaimStatus != "Cancelled" &&
                    claim.ClaimStatus != "AdminDirectEditCompleted"))
            .OrderBy(flagGrid => flagGrid.FlagGridName)
            .FirstOrDefaultAsync(ct);
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

    private static FlagClaimDto ToDto(FlagClaim claim, string? honoreeImageUrl)
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
            latest is null ? null : ToDto(latest));
    }

    private static string BuildHonoreeName(HonoreeChangeRequest? latest, Honoree? honoree)
    {
        if (latest is not null)
        {
            var fromChangeRequest = string.Join(
                " ",
                new[] { latest.FirstName, latest.MiddleName, latest.LastName, latest.Suffix }
                    .Where(v => !string.IsNullOrWhiteSpace(v)));

            if (!string.IsNullOrWhiteSpace(fromChangeRequest))
            {
                return fromChangeRequest;
            }
        }

        if (honoree is not null)
        {
            var fromHonoree = string.Join(
                " ",
                new[] { honoree.FirstName, honoree.MiddleName, honoree.LastName, honoree.Suffix }
                    .Where(v => !string.IsNullOrWhiteSpace(v)));

            if (!string.IsNullOrWhiteSpace(fromHonoree))
            {
                return fromHonoree;
            }
        }

        return string.Empty;
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
