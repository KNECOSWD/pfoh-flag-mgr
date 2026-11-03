using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;
using PFOH.Api.Models;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/flag-claims")]
[Authorize]
[EnableRateLimiting("AuthenticatedGet")]
public class FlagClaimsController(
    PfohDbContext db,
    IWebHostEnvironment environment,
    UserProfileService profiles,
    HonoreeFileStorage fileStorage,
    PendingReviewMaintenance pendingReviews,
    ReprintQueueMaintenance reprintQueue,
    HonoreePdfService pdfs,
    SubmissionGuardService submissions) : ControllerBase
{
    private const string IdempotencyHeader = "Idempotency-Key";

    // A flag claim represents semi-ownership by a user.
    // These statuses keep the flag associated with the claimant.
    private static readonly string[] OwnershipClaimStatuses = ["Claimed", "Submitted", "Approved", "Rejected"];

    // These statuses mean there is a submitted or in-progress change cycle.
    private static readonly string[] OpenChangeStatuses = ["Submitted"];

    private bool CallerIsAdmin() => User.IsInRole("PFOH.Admin") || User.IsInRole("Admin");

    // One request per review thumbnail. Not part of the 60/min cap.
    [DisableRateLimiting]
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

        var isAdmin = CallerIsAdmin();
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

        var isAdmin = CallerIsAdmin();
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
                c.ClaimStatus != "Unlinked" &&
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
        await HonoreeClaimStore.SyncAsync(db, ct);
        var rows = await db.HonoreeClaims
            .AsNoTracking()
            .Where(claim => claim.HonoreeId == honoreeId && !HonoreeClaimStore.HiddenStatuses.Contains(claim.ClaimStatus))
            .OrderByDescending(claim => claim.ClaimedUtc)
            .ToListAsync(ct);

        var sourceIds = rows
            .Where(claim => claim.SourceFlagClaimId.HasValue)
            .Select(claim => claim.SourceFlagClaimId!.Value)
            .ToList();
        var sources = sourceIds.Count == 0
            ? new Dictionary<int, FlagClaim>()
            : await db.FlagClaims
                .AsNoTracking()
                .Include(claim => claim.HonoreeChangeRequests)
                .Where(claim => sourceIds.Contains(claim.Id))
                .ToDictionaryAsync(claim => claim.Id, ct);

        var contacts = await profiles.GetAccountContactsAsync(rows.Select(claim => claim.UserId), ct);

        return Ok(rows.Select(claim =>
        {
            contacts.TryGetValue(claim.UserId, out var contact);
            sources.TryGetValue(claim.SourceFlagClaimId ?? 0, out var source);
            return new
            {
                claimId = claim.SourceFlagClaimId ?? 0,
                claimantName = claim.DisplayName,
                claimantEmail = claim.Email,
                claimantPhone = contact?.Phone,
                claimantOrganization = contact?.Organization,
                claimStatus = claim.ClaimStatus,
                createdUtc = claim.ClaimedUtc,
                submittedUtc = source?.SubmittedUtc,
                latestRequestStatus = source?.HonoreeChangeRequests
                    .OrderByDescending(request => request.CreatedUtc)
                    .Select(request => request.RequestStatus)
                    .FirstOrDefault()
            };
        }));
    }

    [HttpPost("{claimId:int}/unclaim")]
    public async Task<ActionResult> Unclaim(int claimId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();
        var isAdmin = CallerIsAdmin();

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

        if (approvedOnField && honoree is not null)
        {
            claim.AdminNotes = AppendNote(claim.AdminNotes, $"Removal requested on {now:u}.");
            await WithdrawOpenEditsAsync(claim, approvedOnField: true, ct);

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
                    ReviewNotes = "Removal requested by the submitter. The flag stays on the field until an administrator approves the request."
                });
            }

            await HonoreeClaimStore.SyncAsync(db, ct);
            return Ok(new { message = "Removal requested.", removalRequested = true });
        }

        claim.ClaimStatus = "Unclaimed";
        claim.AdminNotes = AppendNote(claim.AdminNotes, $"Withdrawn on {now:u}.");
        claim.FlagGridId = null;
        await WithdrawOpenEditsAsync(claim, approvedOnField: false, ct);
        await HonoreeClaimStore.SyncAsync(db, ct);
        return Ok(new { message = "Flag unclaimed.", removalRequested = false });
    }

    [HttpPost("{claimId:int}/unlink")]
    public async Task<ActionResult> Unlink(int claimId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();
        var isAdmin = CallerIsAdmin();

        var claim = await db.FlagClaims
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

        if (claim.ClaimStatus is "AdminDirectEdit" or "AdminReprintQueued")
        {
            return BadRequest(new { message = "This record cannot be unlinked from My submissions." });
        }

        claim.ClaimStatus = "Unlinked";
        claim.AdminNotes = AppendNote(
            claim.AdminNotes,
            $"Unlinked from the submitter account on {DateTime.UtcNow:u}. The honoree was not cancelled.");

        await HonoreeClaimStore.SyncAsync(db, ct);
        return Ok(new { message = "Unlinked from your account." });
    }

    private async Task WithdrawOpenEditsAsync(FlagClaim claim, bool approvedOnField, CancellationToken ct)
    {
        foreach (var request in claim.HonoreeChangeRequests.Where(r => r.RequestStatus is "Submitted" or "Draft"))
        {
            request.RequestStatus = PendingReviewMaintenance.Withdrawn;
            request.ReviewNotes = AppendNote(request.ReviewNotes, "Withdrawn by the submitter.");

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
    }

    private static string AppendNote(string? existing, string note) =>
        string.IsNullOrWhiteSpace(existing) ? note : $"{existing.Trim()}\n{note}";


    [HttpPost("{flagGridId:int}/claim")]
    public async Task<ActionResult<FlagClaimDto>> ClaimFlagGrid(int flagGridId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();
        var email = User.GetEmail();
        var name = await profiles.ResolveDisplayNameAsync(User, ct);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteInTransactionAsync<ActionResult<FlagClaimDto>>(async _ =>
        {
        // A failed save leaves Added entities tracked. Clear before rebuilding
        // them so a retry cannot insert the same claim twice.
        db.ChangeTracker.Clear();

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
        await HonoreeClaimStore.SyncAsync(db, ct);

        return Ok(ToDto(
            claim,
            null,
            otherClaimantCount,
            BuildOtherClaimantsNotice(otherClaimantCount)));
        },
        async _ =>
        {
            db.ChangeTracker.Clear();
            return await db.FlagClaims.AnyAsync(
                c => c.FlagGridId == flagGridId
                    && c.ExternalUserObjectId == userObjectId
                    && OwnershipClaimStatuses.Contains(c.ClaimStatus),
                ct);
        },
        ct);
    }

    [HttpPost("honoree/{honoreeId:int}/claim")]
    public async Task<ActionResult<FlagClaimDto>> ClaimExistingHonoree(int honoreeId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();
        var email = User.GetEmail();
        var name = await profiles.ResolveDisplayNameAsync(User, ct);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteInTransactionAsync<ActionResult<FlagClaimDto>>(async _ =>
        {
        db.ChangeTracker.Clear();

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
        await HonoreeClaimStore.SyncAsync(db, ct);
        await pdfs.TryRebuildAsync(honoree.Id, ct);

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
        },
        async _ =>
        {
            db.ChangeTracker.Clear();
            return await db.FlagClaims.AnyAsync(
                c => c.HonoreeId == honoreeId
                    && c.ExternalUserObjectId == userObjectId
                    && OwnershipClaimStatuses.Contains(c.ClaimStatus),
                ct);
        },
        ct);
    }

    [HttpPost("duplicate-check")]
    public async Task<ActionResult<DuplicateCheckResponse>> DuplicateCheck(
        [FromBody] DuplicateCheckRequest? request,
        CancellationToken ct)
    {
        request ??= new DuplicateCheckRequest();
        var matches = await submissions.FindAsync(
            new SubmissionProbe(
                request.FirstName,
                request.MiddleName,
                request.LastName,
                request.Suffix,
                request.Nickname,
                request.Rank,
                request.ServiceBranchId,
                User.GetEmail()),
            request.ExcludeHonoreeId,
            request.ExcludeChangeRequestId,
            CallerIsAdmin(),
            User.GetExternalObjectId(),
            ct);
        return Ok(new DuplicateCheckResponse(submissions.Enabled, matches));
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

        var clientRequestId = ReadIdempotencyKey() ?? Guid.NewGuid();
        var replay = await submissions.FindByIdempotencyKeyAsync(clientRequestId, ct);
        if (replay is not null)
        {
            return await ReplayForCallerAsync(replay, userObjectId, ct);
        }

        var duplicateAction = submissions.Enabled
            ? request.DuplicateAction?.Trim().ToLowerInvariant()
            : null;
        if (submissions.Enabled && duplicateAction is not ("submit-new" or "update"))
        {
            var recent = await submissions.FindRecentIdenticalAsync(
                userObjectId,
                request.FirstName,
                request.LastName,
                request.ServiceBranchId,
                ct);
            if (recent is not null)
            {
                return Ok(await WithReceiptAsync(recent, ct));
            }
        }

        if (duplicateAction == "update")
        {
            return await UpdateMatchedAsync(request, email, name, userObjectId, clientRequestId, ct);
        }

        if (submissions.Enabled && duplicateAction != "submit-new")
        {
            var matches = await submissions.FindAsync(
                ProbeFrom(request, email),
                null,
                null,
                CallerIsAdmin(),
                userObjectId,
                ct);
            if (matches.Count > 0)
            {
                var raced = await ReplayIfKeyExistsAsync(clientRequestId, userObjectId, ct);
                if (raced is not null)
                {
                    return raced;
                }

                return Conflict(new { code = "similar-entry", matches });
            }
        }

        var strategy = db.Database.CreateExecutionStrategy();
        (int ChangeId, ActionResult<FlagClaimDto>? Error) created;
        try
        {
        created = await strategy.ExecuteInTransactionAsync<(int ChangeId, ActionResult<FlagClaimDto>? Error)>(async _ =>
        {
        db.ChangeTracker.Clear();
        var already = await db.FlagClaims
            .AsNoTracking()
            .AnyAsync(row => row.ClientRequestId == clientRequestId, ct);
        if (already)
        {
            return (-1, null);
        }

        var submitter = await GetOrCreateSubmitterSponsorAsync(email, name, ct);
        if (submitter is null)
        {
            return (0, new ActionResult<FlagClaimDto>(Conflict("No sponsor category exists for creating the submitter record.")));
        }

        var claim = new FlagClaim
        {
            ClientRequestId = clientRequestId,
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
        await pendingReviews.SupersedeOlderPendingAsync(change, ct);

        return (change.Id, null);
        },
        async _ =>
        {
            db.ChangeTracker.Clear();
            return await db.FlagClaims.AnyAsync(
                row => row.ClientRequestId == clientRequestId && row.HonoreeChangeRequests.Any(),
                ct);
        },
        ct);
        }
        catch (DbUpdateException ex) when (UserProfileService.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var raced = await ReplayIfKeyExistsAsync(clientRequestId, userObjectId, ct);
            if (raced is not null)
            {
                return raced;
            }

            throw;
        }

        if (created.ChangeId < 0)
        {
            var raced = await ReplayIfKeyExistsAsync(clientRequestId, userObjectId, ct);
            if (raced is not null)
            {
                return raced;
            }

            return Conflict(new { code = "idempotency-key-in-use" });
        }

        if (created.Error is not null)
        {
            return created.Error;
        }

        // The blob name is pending/{changeRequestId}. The row is committed first so
        // the id exists, then the file is uploaded once, outside the retry. Only the
        // filename write is retried. A final failure deletes the blob.
        var pendingPhotoFileName = await fileStorage.UploadPendingImageAsync(request.Photo, created.ChangeId, ct);
        if (!string.IsNullOrWhiteSpace(pendingPhotoFileName))
        {
            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    var stored = await db.HonoreeChangeRequests
                        .FirstAsync(row => row.Id == created.ChangeId, ct);
                    stored.PhotoFileName = pendingPhotoFileName;
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                });
            }
            catch
            {
                await fileStorage.DeletePendingImageAsync(pendingPhotoFileName, CancellationToken.None);
                throw;
            }
        }

        var saved = await db.FlagClaims
            .Include(claim => claim.FlagGrid)
            .Include(claim => claim.Honoree)
            .Include(claim => claim.HonoreeChangeRequests)
            .FirstAsync(claim => claim.HonoreeChangeRequests.Any(row => row.Id == created.ChangeId), ct);
        var savedChange = saved.HonoreeChangeRequests.First(row => row.Id == created.ChangeId);
        if (duplicateAction == "submit-new")
        {
            await submissions.SupersedeSameDayAsync(savedChange, email, userObjectId, ct);
        }

        return Ok(await FinishSubmissionAsync(saved, savedChange, ct));
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
        await HonoreeClaimStore.SyncAsync(db, ct);

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
        var isAdmin = CallerIsAdmin();

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
        var actor = AdminActor.From(User);
        var adminName = actor.Email;
        int? changedHonoreeId = null;

        if (submissions.Enabled && Request.Headers.ContainsKey("X-Duplicate-Check"))
        {
            var draft = await db.HonoreeChangeRequests
                .AsNoTracking()
                .Include(row => row.FlagClaim)
                .Where(row => row.FlagClaimId == claimId && row.RequestStatus == "Draft")
                .OrderByDescending(row => row.CreatedUtc)
                .FirstOrDefaultAsync(ct);
            var action = Request.Headers["X-Duplicate-Action"].ToString();
            if (draft is not null
                && !string.Equals(action, "proceed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(action, "submit-new", StringComparison.OrdinalIgnoreCase))
            {
                var matches = await submissions.FindAsync(
                    new SubmissionProbe(
                        draft.FirstName,
                        draft.MiddleName,
                        draft.LastName,
                        draft.Suffix,
                        draft.Nickname,
                        draft.Rank,
                        draft.ServiceBranchId,
                        draft.SubmitterEmailAddress),
                    draft.HonoreeId,
                    draft.Id,
                    true,
                    actor.ObjectId,
                    ct);
                if (matches.Count > 0)
                {
                    return Conflict(new { code = "similar-entry", matches });
                }
            }
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteInTransactionAsync<ActionResult<FlagClaimDto>>(async _ =>
        {
        db.ChangeTracker.Clear();

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
        await HonoreeClaimStore.SyncAsync(db, ct);
        await reprintQueue.DeactivateOtherActiveReprintItemsForHonoreeAsync(
            honoree.Id, latestDraft.Id, adminName, ct, actor.ObjectId);
        await ReprintQueueEventSchema.EnsureAsync(db, ct);
        ReprintQueueAudit.Add(
            db,
            latestDraft.Id,
            honoree.Id,
            ReprintQueueActions.Add,
            actor.Email,
            actor.ObjectId,
            DateTime.UtcNow,
            "Direct administrator edit queued for reprint.",
            "apply-reprint",
            1);
        await db.SaveChangesAsync(ct);
        changedHonoreeId = honoree.Id;

        var imageUrl = await db.HonoreeSearchResults
            .AsNoTracking()
            .Where(h => h.Id == honoree.Id)
            .Select(h => h.ImageUrl)
            .FirstOrDefaultAsync(ct);

        return Ok(ToDto(claim, imageUrl));
        },
        async _ =>
        {
            db.ChangeTracker.Clear();
            var status = await db.FlagClaims
                .Where(row => row.Id == claimId)
                .Select(row => row.ClaimStatus)
                .FirstOrDefaultAsync(ct);
            return status == "AdminDirectEditCompleted";
        },
        ct);

        if (changedHonoreeId is int rebuiltId)
        {
            await pdfs.TryRebuildAsync(rebuiltId, ct);
        }

        return result;
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

        var previousGridId = honoree is { Id: > 0 } ? honoree.FlagGridId : null;

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

        await FlagRemovalQueue.ReleaseReplacedGridAsync(
            db,
            honoree,
            previousGridId,
            change.FlagGridId,
            adminName,
            honoree.ModifiedDate,
            ct);
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

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("admin/honoree/{honoreeId:int}/apply-fields")]
    public async Task<ActionResult<FlagClaimDto>> AdminApplyFields(
        int honoreeId,
        [FromForm] NominateHonoreeRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var key = ReadIdempotencyKey();
        if (key is Guid existingKey)
        {
            var replay = await submissions.FindByIdempotencyKeyAsync(existingKey, ct);
            if (replay is not null)
            {
                return Ok(await WithReceiptAsync(replay, ct));
            }
        }

        var honoree = await db.Honorees
            .FirstOrDefaultAsync(row => row.Id == honoreeId && row.IsActive && row.DeletedDate == null, ct);
        if (honoree is null)
        {
            return NotFound("Honoree was not found.");
        }

        var admin = AdminActor.From(User);
        honoree.FirstName = request.FirstName.Trim();
        honoree.MiddleName = request.MiddleName?.Trim();
        honoree.LastName = request.LastName.Trim();
        honoree.Suffix = request.Suffix?.Trim();
        honoree.Nickname = request.Nickname?.Trim();
        honoree.Rank = request.Rank?.Trim() ?? string.Empty;
        honoree.ServiceBranchId = request.ServiceBranchId;
        honoree.ServiceBranchCategoryId = request.ServiceBranchCategoryId;
        honoree.StartYear = request.StartYear;
        honoree.EndYear = request.EndYear;
        honoree.DatesUserEntry = request.DatesUserEntry?.Trim() ?? string.Empty;
        honoree.ConflictsServed = request.ConflictsServed?.Trim() ?? string.Empty;
        honoree.Awards = request.Awards?.Trim() ?? string.Empty;
        honoree.Description = request.Description?.Trim() ?? string.Empty;
        honoree.KIA = request.Kia;
        honoree.ModifiedBy = admin.Email;
        honoree.ModifiedDate = DateTime.UtcNow;

        var claim = new FlagClaim
        {
            ClientRequestId = key ?? Guid.NewGuid(),
            FlagGridId = honoree.FlagGridId,
            HonoreeId = honoree.Id,
            ExternalUserObjectId = admin.ObjectId ?? User.GetExternalObjectId(),
            ExternalUserEmail = admin.Email,
            ExternalUserName = await profiles.ResolveDisplayNameAsync(User, ct),
            ClaimStatus = "AdminDirectEditCompleted",
            CreatedUtc = DateTime.UtcNow,
            SubmittedUtc = DateTime.UtcNow,
            ApprovedUtc = DateTime.UtcNow,
            ApprovedBy = admin.Email
        };
        var change = new HonoreeChangeRequest
        {
            FlagGridId = honoree.FlagGridId,
            HonoreeId = honoree.Id,
            RequestStatus = "Approved",
            CreatedUtc = DateTime.UtcNow,
            SubmittedUtc = DateTime.UtcNow,
            ReviewedUtc = DateTime.UtcNow,
            ReviewedBy = admin.Email,
            RequiresCardReprint = true,
            SubmitterEmailAddress = request.SubmitterEmailAddress,
            SubmitterPhoneNumber = request.SubmitterPhoneNumber
        };
        ApplyRequest(change, request);
        change.FlagGridId = honoree.FlagGridId;
        change.HonoreeId = honoree.Id;
        change.RequestStatus = "Approved";
        claim.HonoreeChangeRequests.Add(change);
        db.FlagClaims.Add(claim);
        await db.SaveChangesAsync(ct);
        var honoreeIdSaved = honoree.Id;
        await AttachPendingPhotoAsync(change.Id, request.Photo, ct);
        var stored = await db.HonoreeChangeRequests.FirstAsync(row => row.Id == change.Id, ct);
        if (!string.IsNullOrWhiteSpace(stored.PhotoFileName))
        {
            var promoted = await fileStorage.PromoteImageAsync(stored.PhotoFileName, honoreeIdSaved, ct);
            if (!string.IsNullOrWhiteSpace(promoted))
            {
                var current = await db.Honorees.FirstAsync(row => row.Id == honoreeIdSaved, ct);
                current.PhotoFileName = promoted;
                stored.PhotoFileName = promoted;
                await db.SaveChangesAsync(ct);
            }
        }

        await pdfs.TryRebuildAsync(honoree.Id, ct);
        var saved = await db.FlagClaims
            .Include(row => row.FlagGrid)
            .Include(row => row.Honoree)
            .Include(row => row.HonoreeChangeRequests)
            .FirstAsync(row => row.Id == claim.Id, ct);
        var savedChange = saved.HonoreeChangeRequests.OrderByDescending(row => row.Id).First();
        return Ok(await FinishSubmissionAsync(saved, savedChange, ct));
    }

    [HttpPost("pending/{changeRequestId:int}/apply")]
    public async Task<ActionResult<FlagClaimDto>> ApplyPending(
        int changeRequestId,
        [FromForm] NominateHonoreeRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (!submissions.Enabled)
        {
            return NotFound("That pending submission was not found.");
        }

        var key = ReadIdempotencyKey();
        if (key is Guid existingKey)
        {
            var replay = await submissions.FindByIdempotencyKeyAsync(existingKey, ct);
            if (replay is not null)
            {
                return await ReplayForCallerAsync(replay, User.GetExternalObjectId(), ct);
            }
        }

        var change = await db.HonoreeChangeRequests
            .Include(row => row.FlagClaim)
            .FirstOrDefaultAsync(row => row.Id == changeRequestId && row.RequestStatus == "Submitted", ct);
        if (change is null || change.HonoreeId is > 0)
        {
            return NotFound("That pending submission was not found.");
        }

        var callerId = User.GetExternalObjectId();
        var owns = !string.IsNullOrWhiteSpace(callerId)
            && string.Equals(change.FlagClaim?.ExternalUserObjectId, callerId, StringComparison.Ordinal);
        if (!CallerIsAdmin() && !owns)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var keepEmail = change.SubmitterEmailAddress;
        var keepPhone = change.SubmitterPhoneNumber;
        ApplyRequest(change, request);
        if (!owns)
        {
            change.SubmitterEmailAddress = keepEmail;
            change.SubmitterPhoneNumber = keepPhone;
        }

        if (change.FlagClaim is not null && key is Guid claimKey && change.FlagClaim.ClientRequestId is null)
        {
            change.FlagClaim.ClientRequestId = claimKey;
        }

        await db.SaveChangesAsync(ct);
        await AttachPendingPhotoAsync(change.Id, request.Photo, ct);
        var claim = await db.FlagClaims
            .Include(row => row.FlagGrid)
            .Include(row => row.Honoree)
            .Include(row => row.HonoreeChangeRequests)
            .FirstAsync(row => row.Id == change.FlagClaimId, ct);
        var savedChange = claim.HonoreeChangeRequests.First(row => row.Id == change.Id);
        return Ok(await FinishSubmissionAsync(claim, savedChange, ct));
    }

    private async Task<ActionResult<FlagClaimDto>> UpdateMatchedAsync(
        NominateHonoreeRequest request,
        string email,
        string? name,
        string userObjectId,
        Guid clientRequestId,
        CancellationToken ct)
    {
        if (submissions.TryReadMatchToken(request.MatchToken, out var tokenKind, out var tokenId))
        {
            request.MatchKind = tokenKind;
            if (tokenKind == "Approved")
            {
                request.MatchHonoreeId = tokenId;
            }

            if (tokenKind == "Pending")
            {
                request.MatchChangeRequestId = tokenId;
            }
        }

        var kind = request.MatchKind?.Trim();
        if (string.Equals(kind, "Approved", StringComparison.OrdinalIgnoreCase) && request.MatchHonoreeId is int honoreeId)
        {
            if (CallerIsAdmin())
            {
                return await AdminApplyFields(honoreeId, request, ct);
            }

            return await CreateCorrectionAsync(request, email, name, userObjectId, clientRequestId, honoreeId, null, ct);
        }

        if (string.Equals(kind, "Pending", StringComparison.OrdinalIgnoreCase) && request.MatchChangeRequestId is int changeId)
        {
            var pending = await db.HonoreeChangeRequests
                .Include(row => row.FlagClaim)
                .FirstOrDefaultAsync(row => row.Id == changeId && row.RequestStatus == "Submitted", ct);
            if (pending is null)
            {
                return NotFound("That pending submission was not found.");
            }

            var owns = !string.IsNullOrWhiteSpace(userObjectId)
                && string.Equals(pending.FlagClaim?.ExternalUserObjectId, userObjectId, StringComparison.Ordinal);
            if (pending.HonoreeId is > 0 && CallerIsAdmin())
            {
                return await AdminApplyFields(pending.HonoreeId.Value, request, ct);
            }

            if ((CallerIsAdmin() || owns) && pending.HonoreeId is not > 0)
            {
                return await ApplyPending(changeId, request, ct);
            }

            return await CreateCorrectionAsync(request, email, name, userObjectId, clientRequestId, null, changeId, ct);
        }

        return BadRequest("Choose the entry to update.");
    }

    private async Task<ActionResult<FlagClaimDto>> CreateCorrectionAsync(
        NominateHonoreeRequest request,
        string email,
        string? name,
        string userObjectId,
        Guid clientRequestId,
        int? honoreeId,
        int? correctsChangeRequestId,
        CancellationToken ct)
    {
        int? flagGridId = null;
        if (honoreeId is int approvedId)
        {
            var honoree = await db.Honorees
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Id == approvedId && row.IsActive && row.DeletedDate == null, ct);
            if (honoree is null)
            {
                return NotFound("Honoree was not found.");
            }

            flagGridId = honoree.FlagGridId;
        }

        var claim = new FlagClaim
        {
            ClientRequestId = clientRequestId,
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
            FlagGridId = flagGridId,
            HonoreeId = honoreeId,
            CorrectsChangeRequestId = correctsChangeRequestId,
            RequestStatus = "Submitted",
            CreatedUtc = DateTime.UtcNow,
            SubmittedUtc = DateTime.UtcNow,
            SubmitterPhoneNumber = request.SubmitterPhoneNumber,
            SubmitterEmailAddress = request.SubmitterEmailAddress ?? email
        };
        ApplyRequest(change, request);
        change.HonoreeId = honoreeId;
        change.FlagGridId = flagGridId;
        change.CorrectsChangeRequestId = correctsChangeRequestId;
        if (string.IsNullOrWhiteSpace(change.SubmitterEmailAddress))
        {
            change.SubmitterEmailAddress = email;
        }

        claim.HonoreeChangeRequests.Add(change);
        db.FlagClaims.Add(claim);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UserProfileService.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            var raced = await ReplayIfKeyExistsAsync(clientRequestId, userObjectId, ct);
            if (raced is not null)
            {
                return raced;
            }

            throw;
        }

        await AttachPendingPhotoAsync(change.Id, request.Photo, ct);
        var saved = await db.FlagClaims
            .Include(row => row.FlagGrid)
            .Include(row => row.Honoree)
            .Include(row => row.HonoreeChangeRequests)
            .FirstAsync(row => row.Id == claim.Id, ct);
        var savedChange = saved.HonoreeChangeRequests.OrderByDescending(row => row.Id).First();
        return Ok(await FinishSubmissionAsync(saved, savedChange, ct));
    }

    private async Task<FlagClaimDto> FinishSubmissionAsync(
        FlagClaim claim,
        HonoreeChangeRequest change,
        CancellationToken ct)
    {
        var receipt = await submissions.EnsureReceiptAsync(change.Id, ct);
        return ToDto(claim, null) with { ReceiptToken = receipt.PublicToken };
    }

    private async Task<FlagClaimDto> WithReceiptAsync(FlagClaim claim, CancellationToken ct)
    {
        var change = claim.HonoreeChangeRequests.OrderByDescending(row => row.Id).FirstOrDefault();
        if (change is null)
        {
            return ToDto(claim, null);
        }

        var receipt = await submissions.EnsureReceiptAsync(change.Id, ct);
        return ToDto(claim, null) with { ReceiptToken = receipt.PublicToken };
    }

    private Guid? ReadIdempotencyKey()
    {
        if (!Request.Headers.TryGetValue(IdempotencyHeader, out var values))
        {
            return null;
        }

        return Guid.TryParse(values.ToString(), out var key) ? key : null;
    }

    private async Task<ActionResult<FlagClaimDto>?> ReplayIfKeyExistsAsync(
        Guid key,
        string userObjectId,
        CancellationToken ct)
    {
        var raced = await submissions.FindByIdempotencyKeyAsync(key, ct);
        if (raced is null)
        {
            return null;
        }

        return await ReplayForCallerAsync(raced, userObjectId, ct);
    }

    private async Task<ActionResult<FlagClaimDto>> ReplayForCallerAsync(
        FlagClaim replay,
        string userObjectId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userObjectId)
            || !string.Equals(replay.ExternalUserObjectId, userObjectId, StringComparison.Ordinal))
        {
            return Conflict(new { code = "idempotency-key-in-use" });
        }

        return Ok(await WithReceiptAsync(replay, ct));
    }

    private static SubmissionProbe ProbeFrom(NominateHonoreeRequest request, string email) =>
        new(
            request.FirstName,
            request.MiddleName,
            request.LastName,
            request.Suffix,
            request.Nickname,
            request.Rank,
            request.ServiceBranchId,
            email);

    private async Task AttachPendingPhotoAsync(int changeId, IFormFile? photo, CancellationToken ct)
    {
        if (photo is null || photo.Length == 0)
        {
            return;
        }

        var pendingPhotoFileName = await fileStorage.UploadPendingImageAsync(photo, changeId, ct);
        if (string.IsNullOrWhiteSpace(pendingPhotoFileName))
        {
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var stored = await db.HonoreeChangeRequests.FirstAsync(row => row.Id == changeId, ct);
                stored.PhotoFileName = pendingPhotoFileName;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            });
        }
        catch
        {
            await fileStorage.DeletePendingImageAsync(pendingPhotoFileName, CancellationToken.None);
            throw;
        }
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
