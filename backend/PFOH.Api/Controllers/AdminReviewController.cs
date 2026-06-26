using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Extensions;
using PFOH.Api.Models;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

public record AdminFlagPositionDto(
    int FlagGridId,
    string FlagGridName,
    string RowLabel,
    int? ColumnNumber,
    bool IsOpen,
    bool IsReserved,
    int? HonoreeId,
    string? HonoreeName,
    string? Rank,
    string? ServiceBranchName);

public record AssignFlagPositionRequest(int HonoreeId);

public record RemoveReprintQueueItemsRequest(IReadOnlyList<int> ChangeRequestIds);

public record AdminUnassignedHonoreeDto(
    int Id,
    string FullName,
    string? Nickname,
    string? Rank,
    string? ServiceBranchName);

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
        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        await DeactivateDuplicateActiveReprintQueueItemsAsync(adminName, ct);

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


    [HttpPost("reprint-queue/remove")]
    public async Task<ActionResult<object>> RemoveFromReprintQueue(
        [FromBody] RemoveReprintQueueItemsRequest request,
        CancellationToken ct)
    {
        var ids = request.ChangeRequestIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return BadRequest(new { message = "Select at least one reprint queue item to remove." });
        }

        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        var queueItems = await db.HonoreeChangeRequests
            .Include(r => r.FlagClaim)
            .Where(r =>
                ids.Contains(r.Id) &&
                r.RequestStatus == "Approved" &&
                r.RequiresCardReprint &&
                r.CardPrintedUtc == null)
            .ToListAsync(ct);

        if (queueItems.Count == 0)
        {
            return NotFound(new { message = "No active reprint queue items were found for the selected records." });
        }

        var now = DateTime.UtcNow;

        foreach (var item in queueItems)
        {
            item.RequiresCardReprint = false;
            item.ReviewedBy = adminName;
            item.ReviewedUtc = now;
            item.ReviewNotes = string.IsNullOrWhiteSpace(item.ReviewNotes)
                ? "Removed from the reprint queue by administrator."
                : $"{item.ReviewNotes}\nRemoved from the reprint queue by administrator.";

            if (item.FlagClaim?.ClaimStatus == "AdminReprintQueued")
            {
                item.FlagClaim.ClaimStatus = "AdminReprintRemoved";
                item.FlagClaim.AdminNotes = string.IsNullOrWhiteSpace(item.FlagClaim.AdminNotes)
                    ? "Removed from the reprint queue by administrator."
                    : $"{item.FlagClaim.AdminNotes}\nRemoved from the reprint queue by administrator.";
            }
        }

        await db.SaveChangesAsync(ct);

        return Ok(new { count = queueItems.Count });
    }


    [HttpGet("honorees-export")]
    public async Task<IActionResult> ExportHonorees(CancellationToken ct)
    {
        var honorees = await db.Honorees
            .AsNoTracking()
            .Include(h => h.FlagGrid)
            .Include(h => h.ServiceBranch)
            .Include(h => h.Sponsor)
            .Where(h => h.IsActive && h.DeletedDate == null)
            .OrderBy(h => h.LastName)
            .ThenBy(h => h.FirstName)
            .ToListAsync(ct);

        var honoreeIds = honorees.Select(h => h.Id).ToList();

        var activeClaims = honoreeIds.Count == 0
            ? new List<FlagClaim>()
            : await db.FlagClaims
                .AsNoTracking()
                .Where(c =>
                    c.HonoreeId.HasValue &&
                    honoreeIds.Contains(c.HonoreeId.Value) &&
                    c.ClaimStatus != "Unclaimed" &&
                    c.ClaimStatus != "Cancelled" &&
                    c.ClaimStatus != "AdminDirectEditCompleted")
                .OrderBy(c => c.ExternalUserName ?? c.ExternalUserEmail)
                .ToListAsync(ct);

        var claimsByHonoreeId = activeClaims
            .Where(c => c.HonoreeId.HasValue)
            .GroupBy(c => c.HonoreeId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new StringBuilder();
        rows.AppendLine("<html><head><meta charset=\"utf-8\" /></head><body>");
        rows.AppendLine("<table border=\"1\">");
        rows.AppendLine("<thead><tr>");
        foreach (var header in new[]
        {
            "Honoree ID",
            "Name",
            "Nickname",
            "Flag Grid",
            "Rank",
            "Service Branch",
            "Service Years",
            "KIA",
            "Submitter",
            "Submitter Email",
            "Submitter Phone",
            "Claimed",
            "Claimant Count",
            "Claimants",
            "Description"
        })
        {
            rows.Append("<th>").Append(WebUtility.HtmlEncode(header)).AppendLine("</th>");
        }

        rows.AppendLine("</tr></thead><tbody>");

        foreach (var honoree in honorees)
        {
            claimsByHonoreeId.TryGetValue(honoree.Id, out var claims);
            claims ??= new List<FlagClaim>();

            var claimantSummary = string.Join(
                "; ",
                claims.Select(c =>
                    $"{Clean(c.ExternalUserName) ?? Clean(c.ExternalUserEmail)} <{Clean(c.ExternalUserEmail)}> ({Clean(c.ClaimStatus)})"));

            var values = new[]
            {
                honoree.Id.ToString(),
                BuildHonoreeName(honoree),
                Clean(honoree.Nickname),
                Clean(honoree.FlagGrid?.FlagGridName),
                Clean(honoree.Rank),
                Clean(honoree.ServiceBranch?.ServiceBranchName),
                BuildServiceYears(honoree),
                honoree.KIA ? "Yes" : "No",
                BuildSponsorName(honoree.Sponsor),
                Clean(honoree.Sponsor?.EmailAddress),
                Clean(honoree.Sponsor?.PhoneNumber),
                claims.Count > 0 ? "Yes" : "No",
                claims.Count.ToString(),
                claimantSummary,
                Clean(honoree.Description)
            };

            rows.AppendLine("<tr>");
            foreach (var value in values)
            {
                rows.Append("<td>").Append(WebUtility.HtmlEncode(value ?? string.Empty)).AppendLine("</td>");
            }

            rows.AppendLine("</tr>");
        }

        rows.AppendLine("</tbody></table></body></html>");

        var bytes = Encoding.UTF8.GetBytes(rows.ToString());
        var fileName = $"pfoh-honorees-{DateTime.UtcNow:yyyyMMdd}.xls";
        return File(bytes, "application/vnd.ms-excel; charset=utf-8", fileName);
    }

    [HttpGet("unassigned-honorees")]
    public async Task<ActionResult<IReadOnlyList<AdminUnassignedHonoreeDto>>> GetUnassignedHonorees(CancellationToken ct)
    {
        var honorees = await db.Honorees
            .AsNoTracking()
            .Include(h => h.ServiceBranch)
            .Where(h =>
                h.IsActive &&
                h.DeletedDate == null &&
                !h.FlagGridId.HasValue)
            .OrderBy(h => h.LastName)
            .ThenBy(h => h.FirstName)
            .Select(h => new AdminUnassignedHonoreeDto(
                h.Id,
                BuildHonoreeName(h),
                h.Nickname,
                h.Rank,
                h.ServiceBranch == null ? null : h.ServiceBranch.ServiceBranchName))
            .ToListAsync(ct);

        return Ok(honorees);
    }

    [HttpGet("flag-positions")]
    public async Task<ActionResult<IReadOnlyList<AdminFlagPositionDto>>> GetFlagPositions(CancellationToken ct)
    {
        var positions = await BuildFlagPositionsAsync(ct);
        return Ok(positions);
    }

    [HttpPost("flag-positions/{flagGridId:int}/assign")]
    public async Task<ActionResult<AdminFlagPositionDto>> AssignFlagPosition(
        int flagGridId,
        [FromBody] AssignFlagPositionRequest request,
        CancellationToken ct)
    {
        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        var flagGrid = await db.FlagGrids
            .FirstOrDefaultAsync(g => g.Id == flagGridId && g.DeletedDate == null, ct);

        if (flagGrid is null)
        {
            return NotFound(new { message = "Flag position was not found." });
        }

        if (flagGrid.Reserved)
        {
            return Conflict(new { message = "This flag position is reserved and cannot be assigned." });
        }

        var occupiedHonoree = await db.Honorees
            .AsNoTracking()
            .FirstOrDefaultAsync(h =>
                h.FlagGridId == flagGridId &&
                h.IsActive &&
                h.DeletedDate == null,
                ct);

        if (flagGrid.HonoreeId.HasValue || occupiedHonoree is not null)
        {
            return Conflict(new { message = "This flag position is already occupied. Remove the current honoree before assigning another honoree." });
        }

        var honoree = await db.Honorees
            .Include(h => h.ServiceBranch)
            .FirstOrDefaultAsync(h =>
                h.Id == request.HonoreeId &&
                h.IsActive &&
                h.DeletedDate == null,
                ct);

        if (honoree is null)
        {
            return NotFound(new { message = "Honoree was not found." });
        }

        if (honoree.FlagGridId.HasValue)
        {
            return Conflict(new { message = "This honoree is already assigned to a flag position. Remove the honoree from the current position first." });
        }

        honoree.FlagGridId = flagGrid.Id;
        honoree.ModifiedBy = adminName;
        honoree.ModifiedDate = DateTime.UtcNow;

        flagGrid.HonoreeId = honoree.Id;
        flagGrid.ModifiedBy = adminName;
        flagGrid.ModifiedDate = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        try
        {
            await RegenerateHonoreePdfAsync(honoree.Id, ct);
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = "The flag grid was assigned, but the card PDF could not be regenerated. The honoree was not added to the reprint queue.",
                    detail = ex.InnerException?.Message ?? ex.Message
                });
        }

        await AddHonoreeToReprintQueueAsync(
            honoree,
            flagGrid,
            adminName,
            "Automatically added to the reprint queue after flag grid assignment.",
            ct);

        var position = await BuildFlagPositionAsync(flagGrid.Id, ct);
        return position is null
            ? NotFound(new { message = "Flag position was not found after assignment." })
            : Ok(position);
    }

    [HttpPost("flag-positions/{flagGridId:int}/clear")]
    public async Task<ActionResult<AdminFlagPositionDto>> ClearFlagPosition(
        int flagGridId,
        CancellationToken ct)
    {
        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        var flagGrid = await db.FlagGrids
            .FirstOrDefaultAsync(g => g.Id == flagGridId && g.DeletedDate == null, ct);

        if (flagGrid is null)
        {
            return NotFound(new { message = "Flag position was not found." });
        }

        var honoree = await db.Honorees
            .FirstOrDefaultAsync(h =>
                h.FlagGridId == flagGridId &&
                h.IsActive &&
                h.DeletedDate == null,
                ct);

        if (honoree is null && flagGrid.HonoreeId.HasValue)
        {
            honoree = await db.Honorees
                .FirstOrDefaultAsync(h =>
                    h.Id == flagGrid.HonoreeId.Value &&
                    h.IsActive &&
                    h.DeletedDate == null,
                    ct);
        }

        if (honoree is not null)
        {
            honoree.FlagGridId = null;
            honoree.ModifiedBy = adminName;
            honoree.ModifiedDate = DateTime.UtcNow;
        }

        flagGrid.HonoreeId = null;
        flagGrid.ModifiedBy = adminName;
        flagGrid.ModifiedDate = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        if (honoree is not null)
        {
            await RegenerateHonoreePdfAsync(honoree.Id, ct);
        }

        var position = await BuildFlagPositionAsync(flagGrid.Id, ct);
        return position is null
            ? NotFound(new { message = "Flag position was not found after removal." })
            : Ok(position);
    }

    [HttpPost("honoree/{honoreeId:int}/queue-reprint")]
    public async Task<ActionResult<AdminPrintQueueItemDto>> QueueHonoreeReprint(
        int honoreeId,
        CancellationToken ct)
    {
        try
        {
            var adminName = User.GetEmail();
            if (string.IsNullOrWhiteSpace(adminName))
            {
                adminName = User.GetDisplayName() ?? "PFOH.Admin";
            }

            var honoree = await db.Honorees
                .Include(h => h.FlagGrid)
                .Include(h => h.ServiceBranch)
                .FirstOrDefaultAsync(h => h.Id == honoreeId && h.IsActive && h.DeletedDate == null, ct);

            if (honoree is null)
            {
                return NotFound(new { message = "Honoree was not found." });
            }

            if (!honoree.FlagGridId.HasValue)
            {
                return BadRequest(new { message = "This honoree is not assigned to a flag grid yet." });
            }

            await DeactivateOtherActiveReprintItemsForHonoreeAsync(honoree.Id, keepChangeRequestId: null, adminName, ct);

            var existingQueueItem = await db.HonoreeChangeRequests
                .AsNoTracking()
                .Include(r => r.FlagGrid)
                .Include(r => r.ServiceBranch)
                .Where(r =>
                    r.HonoreeId == honoree.Id &&
                    r.RequestStatus == "Approved" &&
                    r.RequiresCardReprint &&
                    r.CardPrintedUtc == null)
                .OrderByDescending(r => r.ReviewedUtc ?? r.SubmittedUtc ?? r.CreatedUtc)
                .FirstOrDefaultAsync(ct);

            if (existingQueueItem is not null)
            {
                return Ok(await ToPrintQueueDto(existingQueueItem, ct));
            }

            try
            {
                await RegenerateHonoreePdfAsync(honoree.Id, ct);
            }
            catch (Exception ex)
            {
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new
                    {
                        message = "The card PDF could not be regenerated. The honoree was not added to the reprint queue.",
                        detail = ex.InnerException?.Message ?? ex.Message
                    });
            }

            // HonoreeChangeRequest.FlagClaimId is required. Create an internal/admin claim
            // so the reprint queue item has a valid foreign key without assigning the flag
            // to a public user's My claimed flags list.
            var queueClaim = new FlagClaim
            {
                FlagGridId = honoree.FlagGridId.Value,
                HonoreeId = honoree.Id,
                ExternalUserObjectId = $"admin-reprint:{Guid.NewGuid():N}",
                ExternalUserEmail = adminName,
                ExternalUserName = "PFOH Admin reprint queue",
                ClaimStatus = "AdminReprintQueued",
                CreatedUtc = DateTime.UtcNow,
                ApprovedUtc = DateTime.UtcNow,
                ApprovedBy = adminName,
                AdminNotes = "Internal claim created to add an existing honoree to the card reprint queue.",
                FlagGrid = honoree.FlagGrid,
                Honoree = honoree
            };

            var queueRequest = new HonoreeChangeRequest
            {
                FlagClaim = queueClaim,
                FlagGridId = honoree.FlagGridId.Value,
                HonoreeId = honoree.Id,
                FirstName = honoree.FirstName ?? string.Empty,
                MiddleName = honoree.MiddleName,
                LastName = honoree.LastName ?? string.Empty,
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
                SubmitterPhoneNumber = honoree.PhoneNumber,
                SubmitterEmailAddress = honoree.EmailAddress,
                RequestStatus = "Approved",
                CreatedUtc = DateTime.UtcNow,
                SubmittedUtc = DateTime.UtcNow,
                ReviewedUtc = DateTime.UtcNow,
                ReviewedBy = adminName,
                ReviewNotes = "Added to the reprint queue by administrator.",
                RequiresCardReprint = true,
                FlagGrid = honoree.FlagGrid,
                ServiceBranch = honoree.ServiceBranch
            };

            db.FlagClaims.Add(queueClaim);
            db.HonoreeChangeRequests.Add(queueRequest);
            await db.SaveChangesAsync(ct);

            return Ok(await ToPrintQueueDto(queueRequest, ct));
        }
        catch (DbUpdateException ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = "Unable to add this honoree to the reprint queue because the database save failed.",
                    detail = ex.InnerException?.Message ?? ex.Message
                });
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = "Unable to add this honoree to the reprint queue.",
                    detail = ex.InnerException?.Message ?? ex.Message
                });
        }
    }


    [HttpPost("{changeRequestId:int}/approve")]
    public async Task<ActionResult<AdminReviewItemDto>> Approve(
        int changeRequestId,
        [FromBody] ApproveChangeRequest request,
        CancellationToken ct)
    {

        try
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

            if (request.RequiresCardReprint)
            {
                await DeactivateOtherActiveReprintItemsForHonoreeAsync(honoree.Id, change.Id, adminName, ct);

                try
                {
                    await RegenerateHonoreePdfAsync(honoree.Id, ct);
                }
                catch
                {
                    // Approval should not silently fail or roll back just because the PDF could not be regenerated.
                    // The item remains in the reprint queue so the PDF can be regenerated manually after storage/config is corrected.
                    change.ReviewNotes = string.IsNullOrWhiteSpace(change.ReviewNotes)
                        ? "Approved, but PDF regeneration failed. Regenerate the PDF manually before printing."
                        : $"{change.ReviewNotes}\n\nApproved, but PDF regeneration failed. Regenerate the PDF manually before printing.";
                }
            }

            await transaction.CommitAsync(ct);

            return Ok(ToReviewDto(change));
        }
        catch (DbUpdateException ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = "Approval failed while saving to the database.",
                    detail = ex.InnerException?.Message ?? ex.Message
                });
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = "Approval failed before it could complete.",
                    detail = ex.InnerException?.Message ?? ex.Message
                });
        }
    }

    [HttpPost("{changeRequestId:int}/reject")]
    public async Task<ActionResult<AdminReviewItemDto>> Reject(
        int changeRequestId,
        [FromBody] RejectChangeRequest request,
        CancellationToken ct)
    {

        try
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
        catch (DbUpdateException ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = "Reject failed while saving to the database.",
                    detail = ex.InnerException?.Message ?? ex.Message
                });
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = "Reject failed before it could complete.",
                    detail = ex.InnerException?.Message ?? ex.Message
                });
        }
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

        // The honoree stores SponsorId as a foreign key. A newly added Sponsor has Id = 0
        // until it is saved, which causes FK_Honorees_Sponsors_SponsorId to fail when
        // honoree.SponsorId is assigned during approval.
        await db.SaveChangesAsync(ct);

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

        if (!string.IsNullOrWhiteSpace(change.PhotoFileName))
        {
            if (change.PhotoFileName.StartsWith("pending/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var finalPhotoFileName = await fileStorage.PromoteImageAsync(change.PhotoFileName, honoree.Id, ct);
                    if (!string.IsNullOrWhiteSpace(finalPhotoFileName))
                    {
                        honoree.PhotoFileName = finalPhotoFileName;
                        change.PhotoFileName = finalPhotoFileName;
                    }
                }
                catch
                {
                    // Do not block admin approval because a pending photo could not be promoted.
                    // The honoree record remains approved and the photo can be corrected/re-uploaded later.
                }
            }
            else
            {
                // Existing/non-pending photo names are already final. Avoid hitting Blob Storage during approval.
                honoree.PhotoFileName = change.PhotoFileName;
            }
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


    private async Task<AdminPrintQueueItemDto> AddHonoreeToReprintQueueAsync(
        Honoree honoree,
        FlagGrid flagGrid,
        string adminName,
        string reviewNotes,
        CancellationToken ct)
    {
        await DeactivateOtherActiveReprintItemsForHonoreeAsync(honoree.Id, keepChangeRequestId: null, adminName, ct);

        var existingQueueItem = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Include(r => r.FlagGrid)
            .Include(r => r.ServiceBranch)
            .Where(r =>
                r.HonoreeId == honoree.Id &&
                r.RequestStatus == "Approved" &&
                r.RequiresCardReprint &&
                r.CardPrintedUtc == null)
            .OrderByDescending(r => r.ReviewedUtc ?? r.SubmittedUtc ?? r.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        if (existingQueueItem is not null)
        {
            return await ToPrintQueueDto(existingQueueItem, ct);
        }

        var now = DateTime.UtcNow;
        var queueClaim = new FlagClaim
        {
            FlagGridId = flagGrid.Id,
            HonoreeId = honoree.Id,
            ExternalUserObjectId = $"admin-reprint:{Guid.NewGuid():N}",
            ExternalUserEmail = adminName,
            ExternalUserName = "PFOH Admin reprint queue",
            ClaimStatus = "AdminReprintQueued",
            CreatedUtc = now,
            ApprovedUtc = now,
            ApprovedBy = adminName,
            AdminNotes = reviewNotes,
            FlagGrid = flagGrid,
            Honoree = honoree
        };

        var queueRequest = new HonoreeChangeRequest
        {
            FlagClaim = queueClaim,
            FlagGridId = flagGrid.Id,
            HonoreeId = honoree.Id,
            FirstName = honoree.FirstName ?? string.Empty,
            MiddleName = honoree.MiddleName,
            LastName = honoree.LastName ?? string.Empty,
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
            SubmitterPhoneNumber = honoree.PhoneNumber,
            SubmitterEmailAddress = honoree.EmailAddress,
            RequestStatus = "Approved",
            CreatedUtc = now,
            SubmittedUtc = now,
            ReviewedUtc = now,
            ReviewedBy = adminName,
            ReviewNotes = reviewNotes,
            RequiresCardReprint = true,
            FlagGrid = flagGrid,
            ServiceBranch = honoree.ServiceBranch
        };

        db.FlagClaims.Add(queueClaim);
        db.HonoreeChangeRequests.Add(queueRequest);
        await db.SaveChangesAsync(ct);

        return await ToPrintQueueDto(queueRequest, ct);
    }

    private async Task DeactivateDuplicateActiveReprintQueueItemsAsync(string adminName, CancellationToken ct)
    {
        var duplicateHonoreeIds = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Where(r =>
                r.HonoreeId.HasValue &&
                r.RequestStatus == "Approved" &&
                r.RequiresCardReprint &&
                r.CardPrintedUtc == null)
            .GroupBy(r => r.HonoreeId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(ct);

        foreach (var honoreeId in duplicateHonoreeIds)
        {
            await DeactivateOtherActiveReprintItemsForHonoreeAsync(honoreeId, keepChangeRequestId: null, adminName, ct);
        }
    }

    private async Task DeactivateOtherActiveReprintItemsForHonoreeAsync(
        int honoreeId,
        int? keepChangeRequestId,
        string adminName,
        CancellationToken ct)
    {
        var activeItems = await db.HonoreeChangeRequests
            .Include(r => r.FlagClaim)
            .Where(r =>
                r.HonoreeId == honoreeId &&
                r.RequestStatus == "Approved" &&
                r.RequiresCardReprint &&
                r.CardPrintedUtc == null)
            .OrderByDescending(r => r.ReviewedUtc ?? r.SubmittedUtc ?? r.CreatedUtc)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct);

        if (activeItems.Count <= 1)
        {
            return;
        }

        var keepItem = keepChangeRequestId.HasValue
            ? activeItems.FirstOrDefault(r => r.Id == keepChangeRequestId.Value)
            : activeItems.FirstOrDefault();

        if (keepItem is null)
        {
            keepItem = activeItems.First();
        }

        var now = DateTime.UtcNow;
        var duplicates = activeItems
            .Where(r => r.Id != keepItem.Id)
            .ToList();

        foreach (var duplicate in duplicates)
        {
            duplicate.RequiresCardReprint = false;
            duplicate.ReviewedBy = adminName;
            duplicate.ReviewedUtc = now;
            duplicate.ReviewNotes = string.IsNullOrWhiteSpace(duplicate.ReviewNotes)
                ? $"Removed duplicate reprint queue item. Active queue item #{keepItem.Id} remains for this honoree."
                : $"{duplicate.ReviewNotes}\nRemoved duplicate reprint queue item. Active queue item #{keepItem.Id} remains for this honoree.";

            if (duplicate.FlagClaim?.ClaimStatus == "AdminReprintQueued")
            {
                duplicate.FlagClaim.ClaimStatus = "AdminReprintDuplicateRemoved";
                duplicate.FlagClaim.AdminNotes = string.IsNullOrWhiteSpace(duplicate.FlagClaim.AdminNotes)
                    ? $"Removed duplicate reprint queue item. Active queue item #{keepItem.Id} remains for this honoree."
                    : $"{duplicate.FlagClaim.AdminNotes}\nRemoved duplicate reprint queue item. Active queue item #{keepItem.Id} remains for this honoree.";
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<AdminPrintQueueItemDto> ToPrintQueueDto(HonoreeChangeRequest r, CancellationToken ct)
    {
        string? pdfUrl = null;

        if (r.HonoreeId.HasValue)
        {
            pdfUrl = await db.HonoreeSearchResults
                .AsNoTracking()
                .Where(h => h.Id == r.HonoreeId.Value)
                .Select(h => h.PDFUrl)
                .FirstOrDefaultAsync(ct);
        }

        return new AdminPrintQueueItemDto(
            r.Id,
            r.FlagClaimId,
            r.HonoreeId,
            BuildHonoreeName(r),
            r.FlagGrid?.FlagGridName ?? string.Empty,
            r.ServiceBranch?.ServiceBranchName,
            pdfUrl,
            r.ReviewedUtc,
            r.CardPrintedUtc);
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

    private async Task<IReadOnlyList<AdminFlagPositionDto>> BuildFlagPositionsAsync(CancellationToken ct)
    {
        var flagGrids = await db.FlagGrids
            .AsNoTracking()
            .Where(g => g.DeletedDate == null)
            .OrderBy(g => g.FlagGridName)
            .ToListAsync(ct);

        var gridIds = flagGrids.Select(g => g.Id).ToList();

        var honorees = gridIds.Count == 0
            ? new List<Honoree>()
            : await db.Honorees
                .AsNoTracking()
                .Include(h => h.ServiceBranch)
                .Where(h =>
                    h.FlagGridId.HasValue &&
                    gridIds.Contains(h.FlagGridId.Value) &&
                    h.IsActive &&
                    h.DeletedDate == null)
                .ToListAsync(ct);

        var honoreeByGridId = honorees
            .Where(h => h.FlagGridId.HasValue)
            .GroupBy(h => h.FlagGridId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(h => h.LastName)
                    .ThenBy(h => h.FirstName)
                    .First());

        return flagGrids
            .Select(flagGrid =>
            {
                honoreeByGridId.TryGetValue(flagGrid.Id, out var honoree);
                return ToFlagPositionDto(flagGrid, honoree);
            })
            .OrderBy(position => position.RowLabel)
            .ThenBy(position => position.ColumnNumber ?? int.MaxValue)
            .ThenBy(position => position.FlagGridName)
            .ToList();
    }

    private async Task<AdminFlagPositionDto?> BuildFlagPositionAsync(int flagGridId, CancellationToken ct)
    {
        var flagGrid = await db.FlagGrids
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == flagGridId && g.DeletedDate == null, ct);

        if (flagGrid is null)
        {
            return null;
        }

        var honoree = await db.Honorees
            .AsNoTracking()
            .Include(h => h.ServiceBranch)
            .Where(h =>
                h.FlagGridId == flagGrid.Id &&
                h.IsActive &&
                h.DeletedDate == null)
            .OrderBy(h => h.LastName)
            .ThenBy(h => h.FirstName)
            .FirstOrDefaultAsync(ct);

        return ToFlagPositionDto(flagGrid, honoree);
    }

    private static AdminFlagPositionDto ToFlagPositionDto(FlagGrid flagGrid, Honoree? honoree)
    {
        return new AdminFlagPositionDto(
            flagGrid.Id,
            flagGrid.FlagGridName,
            BuildFlagPositionRowLabel(flagGrid.FlagGridName),
            BuildFlagPositionColumnNumber(flagGrid.FlagGridName),
            honoree is null && !flagGrid.HonoreeId.HasValue && !flagGrid.Reserved,
            flagGrid.Reserved,
            honoree?.Id ?? flagGrid.HonoreeId,
            honoree is null ? null : BuildHonoreeName(honoree),
            honoree?.Rank,
            honoree?.ServiceBranch?.ServiceBranchName);
    }

    private static string BuildFlagPositionRowLabel(string? flagGridName)
    {
        var value = Clean(flagGridName) ?? string.Empty;
        var prefix = new string(value.TakeWhile(character => !char.IsDigit(character)).ToArray())
            .Trim('-', '_', ' ')
            .ToUpperInvariant();

        return string.IsNullOrWhiteSpace(prefix) ? "Other" : prefix;
    }

    private static int? BuildFlagPositionColumnNumber(string? flagGridName)
    {
        var value = Clean(flagGridName) ?? string.Empty;
        var digits = new string(
            value
                .SkipWhile(character => !char.IsDigit(character))
                .TakeWhile(char.IsDigit)
                .ToArray());

        return int.TryParse(digits, out var parsed) ? parsed : null;
    }

    private static string BuildHonoreeName(HonoreeChangeRequest r)
    {
        return AddNickname(
            string.Join(
                " ",
                new[] { r.FirstName, r.MiddleName, r.LastName, r.Suffix }
                    .Where(v => !string.IsNullOrWhiteSpace(v))),
            r.Nickname);
    }

    private static string BuildHonoreeName(Honoree h)
    {
        return AddNickname(
            string.Join(
                " ",
                new[] { h.FirstName, h.MiddleName, h.LastName, h.Suffix }
                    .Where(v => !string.IsNullOrWhiteSpace(v))),
            h.Nickname);
    }

    private static string AddNickname(string name, string? nickname)
    {
        var cleanName = Clean(name) ?? string.Empty;
        var cleanNickname = Clean(nickname);

        if (string.IsNullOrWhiteSpace(cleanNickname))
        {
            return cleanName;
        }

        return cleanName.EndsWith($"({cleanNickname})", StringComparison.OrdinalIgnoreCase)
            ? cleanName
            : $"{cleanName} ({cleanNickname})";
    }

    private static string BuildSponsorName(Sponsor? sponsor)
    {
        if (sponsor is null)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            new[] { sponsor.FirstName, sponsor.MiddleName, sponsor.LastName, sponsor.Suffix }
                .Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    private static string BuildServiceYears(Honoree honoree)
    {
        if (!string.IsNullOrWhiteSpace(honoree.DatesUserEntry))
        {
            return honoree.DatesUserEntry;
        }

        if (honoree.StartYear.HasValue && honoree.EndYear.HasValue)
        {
            return $"{honoree.StartYear} - {honoree.EndYear}";
        }

        if (honoree.StartYear.HasValue)
        {
            return $"{honoree.StartYear} - Present";
        }

        return honoree.EndYear.HasValue ? $"- {honoree.EndYear}" : string.Empty;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Replace("\r", "").Replace("\n", "").Trim();
}
