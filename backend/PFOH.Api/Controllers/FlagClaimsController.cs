using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;
using PFOH.Api.Models;

namespace PFOH.Api.Controllers;

[ApiController]
[Route("api/flag-claims")]
[Authorize]
public class FlagClaimsController(PfohDbContext db) : ControllerBase
{
    private static readonly string[] ActiveClaimStatuses = ["Claimed", "Submitted", "Approved"];

    [HttpGet("my")]
    public async Task<ActionResult<IReadOnlyList<FlagClaimDto>>> GetMyClaims(CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();

        var claims = await db.FlagClaims
            .AsNoTracking()
            .Include(c => c.FlagGrid)
            .Include(c => c.HonoreeChangeRequests)
            .Where(c => c.ExternalUserObjectId == userObjectId)
            .OrderByDescending(c => c.CreatedUtc)
            .ToListAsync(ct);

        return Ok(claims.Select(ToDto).ToList());
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
            .AnyAsync(h => h.IsActive && h.FlagGridId == flagGridId, ct);

        if (activeHonoreeExists)
        {
            return Conflict("This flag grid is already assigned to an active honoree.");
        }

        var activeClaimExists = await db.FlagClaims
            .AnyAsync(c => c.FlagGridId == flagGridId && ActiveClaimStatuses.Contains(c.ClaimStatus), ct);

        if (activeClaimExists)
        {
            return Conflict("This flag grid has already been claimed.");
        }

        var claim = new FlagClaim
        {
            FlagGridId = flagGridId,
            HonoreeId = flagGrid.HonoreeId,
            ExternalUserObjectId = userObjectId,
            ExternalUserEmail = email,
            ExternalUserName = name,
            ClaimStatus = "Claimed",
            CreatedUtc = DateTime.UtcNow
        };

        db.FlagClaims.Add(claim);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        claim.FlagGrid = flagGrid;
        return Ok(ToDto(claim));
    }

    [HttpPut("{claimId:int}/honoree-draft")]
    public async Task<ActionResult<HonoreeChangeRequestDto>> SaveHonoreeDraft(
        int claimId,
        [FromBody] SaveHonoreeChangeRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var userObjectId = User.GetExternalObjectId();

        var claim = await db.FlagClaims
            .Include(c => c.HonoreeChangeRequests)
            .FirstOrDefaultAsync(c => c.Id == claimId && c.ExternalUserObjectId == userObjectId, ct);

        if (claim is null)
        {
            return NotFound("Claim was not found.");
        }

        if (claim.ClaimStatus is "Approved" or "Rejected" or "Cancelled")
        {
            return Conflict("This claim cannot be edited in its current status.");
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
        await db.SaveChangesAsync(ct);

        return Ok(ToDto(draft));
    }

    [HttpPost("{claimId:int}/submit")]
    public async Task<ActionResult<FlagClaimDto>> SubmitClaim(int claimId, CancellationToken ct)
    {
        var userObjectId = User.GetExternalObjectId();

        var claim = await db.FlagClaims
            .Include(c => c.FlagGrid)
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
            return BadRequest("Create a honoree draft before submitting this claim.");
        }

        latestDraft.RequestStatus = "Submitted";
        latestDraft.SubmittedUtc = DateTime.UtcNow;

        claim.ClaimStatus = "Submitted";
        claim.SubmittedUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        return Ok(ToDto(claim));
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

    private static FlagClaimDto ToDto(FlagClaim claim)
    {
        var latest = claim.HonoreeChangeRequests
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefault();

        return new FlagClaimDto(
            claim.Id,
            claim.FlagGridId,
            claim.FlagGrid?.FlagGridName ?? string.Empty,
            claim.HonoreeId,
            claim.ClaimStatus,
            claim.ExternalUserEmail,
            claim.ExternalUserName,
            claim.CreatedUtc,
            claim.SubmittedUtc,
            latest is null ? null : ToDto(latest));
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
        request.SubmitterPhoneNumber,
        request.SubmitterEmailAddress,
        request.RequestStatus,
        request.CreatedUtc,
        request.SubmittedUtc);
}
