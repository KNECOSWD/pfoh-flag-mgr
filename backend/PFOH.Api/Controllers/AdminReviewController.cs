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
public class AdminReviewController(
    PfohDbContext db,
    IWebHostEnvironment environment,
    UserProfileService profiles,
    HonoreeFileStorage fileStorage,
    ILogger<AdminReviewController> logger,
    PendingReviewMaintenance pendingReviews,
    FlagGridAssignment flagGrids,
    ReprintQueueMaintenance reprintQueue) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<ActionResult<IReadOnlyList<AdminReviewItemDto>>> GetPending(CancellationToken ct)
    {
        // Project nullable columns so a NULL photo, name, or detail on one honoree
        // cannot abort the SQL reader for the other submitted reviews.
        var submittedRequests = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Where(r => r.RequestStatus == "Submitted" || r.RequestStatus == PendingReviewMaintenance.Withdrawal)
            .OrderBy(r => r.SubmittedUtc ?? r.CreatedUtc)
            .Select(r => new PendingReviewSource
            {
                Id = r.Id,
                FlagClaimId = r.FlagClaimId,
                FlagGridId = r.FlagGridId,
                FlagGridName = r.FlagGrid == null ? null : r.FlagGrid.FlagGridName ?? "",
                HonoreeId = r.HonoreeId,
                FirstName = r.FirstName ?? "",
                MiddleName = r.MiddleName,
                LastName = r.LastName ?? "",
                Suffix = r.Suffix,
                Nickname = r.Nickname,
                Rank = r.Rank,
                ServiceBranchName = r.ServiceBranch == null ? null : r.ServiceBranch.ServiceBranchName ?? "",
                ClaimantEmail = r.FlagClaim == null ? null : r.FlagClaim.ExternalUserEmail ?? "",
                ClaimantName = r.FlagClaim == null ? null : r.FlagClaim.ExternalUserName,
                RequestStatus = r.RequestStatus ?? "",
                CreatedUtc = r.CreatedUtc,
                SubmittedUtc = r.SubmittedUtc,
                RequiresCardReprint = r.RequiresCardReprint,
                CardPrintedUtc = r.CardPrintedUtc,
                ReviewNotes = r.ReviewNotes,
                PhotoFileName = r.PhotoFileName,
                Description = r.Description,
                Awards = r.Awards,
                ConflictsServed = r.ConflictsServed,
                StartYear = r.StartYear,
                EndYear = r.EndYear,
                Kia = r.KIA
            })
            .ToListAsync(ct);

        var withdrawalRequests = submittedRequests
            .Where(request => request.RequestStatus == PendingReviewMaintenance.Withdrawal)
            .ToList();
        var newestRequests = PendingReviewMaintenance.KeepNewest(
            submittedRequests.Where(request => request.RequestStatus != PendingReviewMaintenance.Withdrawal).ToList(),
            request => new PendingReviewMaintenance.CardRef(
                request.Id,
                request.FlagGridId,
                request.HonoreeId,
                request.SubmittedUtc,
                request.CreatedUtc ?? DateTime.UtcNow))
            .Concat(withdrawalRequests)
            .ToList();

        var honoreeIds = newestRequests
            .Where(request => request.HonoreeId is > 0)
            .Select(request => request.HonoreeId!.Value)
            .Distinct()
            .ToList();
        var currentHonorees = honoreeIds.Count == 0
            ? new List<Honoree>()
            : await db.Honorees
                .AsNoTracking()
                .Include(honoree => honoree.ServiceBranch)
                .Where(honoree => honoreeIds.Contains(honoree.Id))
                .ToListAsync(ct);
        var honoreeById = currentHonorees.ToDictionary(honoree => honoree.Id);

        var submitted = new List<AdminReviewItemDto>(newestRequests.Count);
        foreach (var request in newestRequests)
        {
            try
            {
                var dto = ToReviewDto(request);
                ReviewSnapshot? current = null;
                if (request.HonoreeId is int honoreeId && honoreeById.TryGetValue(honoreeId, out var honoree))
                {
                    current = ReviewSubmissionDiff.FromHonoree(honoree);
                }

                var isWithdrawal = request.RequestStatus == PendingReviewMaintenance.Withdrawal;
                dto = dto with
                {
                    FieldChanges = isWithdrawal
                        ? Array.Empty<ReviewFieldChangeDto>()
                        : ReviewSubmissionDiff.Compare(PendingSnapshot(request), current)
                };
                submitted.Add(await WithPhotoProbeAsync(dto, ct));
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Pending honoree review {ChangeRequestId} could not be loaded. Returning a degraded item so the rest of the list can load.",
                    request.Id);
                submitted.Add(DegradedReviewDto(request));
            }
        }

        try
        {
            return Ok(await AttachClaimantAccountsAsync(submitted, ct));
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Claimant phone and organization could not be attached to pending honoree reviews. Returning the review list without them.");
            return Ok(submitted);
        }
    }

    [HttpGet("{id:int}/card-preview")]
    public async Task<IActionResult> CardPreview(int id, CancellationToken ct)
    {
        var change = await db.HonoreeChangeRequests
            .AsNoTracking()
            .Include(request => request.FlagGrid)
            .Include(request => request.ServiceBranch)
            .FirstOrDefaultAsync(request => request.Id == id, ct);

        if (change is null)
        {
            return NotFound("Change request was not found.");
        }

        if (!string.Equals(change.RequestStatus, "Submitted", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { message = "Only a submitted honoree can be previewed." });
        }

        var honoree = HonoreeCardPdf.FromChangeRequest(change);

        try
        {
            var pdf = await HonoreeCardPdf.RenderAsync(
                environment,
                fileStorage,
                honoree,
                null,
                change.PhotoFileName,
                null,
                ct);
            return File(pdf, "application/pdf", $"honoree-card-{id}.pdf");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Card preview for change request {ChangeRequestId} could not be created.", id);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = "The honoree card preview could not be created.",
                    detail = ex.Message
                });
        }
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


    [HttpPost("reprint-queue/remove")]
    public async Task<ActionResult<object>> RemoveFromReprintQueue(
        [FromBody] RemoveReprintQueueItemsRequest? request,
        CancellationToken ct)
    {
        if (request?.ChangeRequestIds is null)
        {
            return BadRequest(new { message = "Select at least one reprint queue item to remove." });
        }

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

        if (adminName.Length > 255)
        {
            adminName = adminName[..255];
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
        var internalClaims = new List<FlagClaim>();

        foreach (var item in queueItems)
        {
            item.RequiresCardReprint = false;
            item.ReviewedBy = adminName;
            item.ReviewedUtc = now;
            item.ReviewNotes = string.IsNullOrWhiteSpace(item.ReviewNotes)
                ? "Removed from the reprint queue by administrator."
                : $"{item.ReviewNotes}\nRemoved from the reprint queue by administrator.";

            // The 500 is this claim write. Admin-queued cards share a flag grid with
            // the approved ownership claim. Setting ClaimStatus to AdminReprintRemoved
            // and appending the removal sentence onto the 76-character creation note
            // is the only update other admin saves do not make. SQL Server rejects it
            // (the new status is outside the original claim-status set, and the notes
            // append overflows the existing AdminNotes column). There is no catch, so
            // that DbUpdateException becomes HTTP 500 and the queue row stays. InMemory
            // enforces neither constraint. Cancelled is an original status, is outside
            // the active-grid unique index, and is omitted from claimant lists. The
            // creation note is left as stored.
            if (item.FlagClaim?.ClaimStatus == "AdminReprintQueued")
            {
                item.FlagClaim.ClaimStatus = "Cancelled";
                internalClaims.Add(item.FlagClaim);
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (internalClaims.Count > 0)
        {
            foreach (var claim in internalClaims)
            {
                db.Entry(claim).State = EntityState.Unchanged;
            }

            await db.SaveChangesAsync(ct);
        }

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

    [HttpGet("flag-map-export")]
    public async Task<IActionResult> ExportFlagMap(CancellationToken ct)
    {
        // A missing columns query keeps the full offer set. columns= (present but empty)
        // is an explicit empty selection and is rejected.
        var columns = Request.Query.ContainsKey("columns")
            ? Request.Query["columns"].ToString()
            : null;

        if (!FlagMapExcelExporter.TryParseColumns(columns, out var selected, out var error))
        {
            return BadRequest(new { message = error });
        }

        var sources = await BuildFlagMapExportSourcesAsync(ct);
        var rows = FlagMapExcelExporter.SelectRows(sources);
        var bytes = FlagMapExcelExporter.Build(rows, selected);
        var fileName = $"pfoh-flag-map-{DateTime.UtcNow:yyyyMMdd}.xlsx";
        return File(bytes, FlagMapExcelExporter.ContentType, fileName);
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

            await reprintQueue.DeactivateOtherActiveReprintItemsForHonoreeAsync(honoree.Id, keepChangeRequestId: null, adminName, ct);

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

            if (change.RequestStatus == "Approved")
            {
                // Treat repeated approval calls as idempotent success. This avoids false
                // failures when the admin double-clicks, a request is retried, or the UI is
                // working from a slightly stale pending-review list.
                if (request.RequiresCardReprint && change.HonoreeId.HasValue && !change.CardPrintedUtc.HasValue)
                {
                    change.RequiresCardReprint = true;
                    change.ReviewedBy = adminName;
                    change.ReviewedUtc = DateTime.UtcNow;

                    await db.SaveChangesAsync(ct);
                    await reprintQueue.DeactivateOtherActiveReprintItemsForHonoreeAsync(change.HonoreeId.Value, change.Id, adminName, ct);

                    try
                    {
                        await RegenerateHonoreePdfAsync(change.HonoreeId.Value, ct);
                    }
                    catch
                    {
                        // Do not convert an otherwise approved item into a failure just because
                        // PDF regeneration/storage had a temporary problem.
                    }
                }

                await transaction.CommitAsync(ct);
                return Ok(ToReviewDto(change));
            }

            if (change.RequestStatus != "Submitted")
            {
                return Conflict(new
                {
                    message = "Only submitted change requests can be approved.",
                    currentStatus = change.RequestStatus
                });
            }

            var isNewSubmission = change.HonoreeId is not > 0;
            bool? flagSpotAssigned = null;
            string? flagSpotNotice = null;
            string? assignedGridName = null;
            if (isNewSubmission)
            {
                await PendingSubmissionGridSchema.EnsureAsync(db, ct);
                await pendingReviews.ReleasePendingSubmissionSpotsAsync(ct);
            }

            var honoree = await ApplyApprovedChanges(change, adminName, ct);

            if (isNewSubmission)
            {
                var grid = await flagGrids.NextAvailableAsync(ct);
                if (grid is null)
                {
                    flagSpotAssigned = false;
                    flagSpotNotice = FlagGridAssignment.NoSpotNotice;
                    honoree.FlagGridId = null;
                    change.FlagGridId = null;
                    if (change.FlagClaim is { HonoreeId: null })
                    {
                        change.FlagClaim.FlagGridId = null;
                    }
                }
                else
                {
                    honoree.FlagGridId = grid.Id;
                    change.FlagGridId = grid.Id;
                    if (change.FlagClaim is not null)
                    {
                        change.FlagClaim.FlagGridId = grid.Id;
                    }

                    grid.HonoreeId = honoree.Id;
                    grid.ModifiedBy = adminName;
                    grid.ModifiedDate = DateTime.UtcNow;
                    flagSpotAssigned = true;
                    assignedGridName = grid.FlagGridName;
                }
            }

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
                await reprintQueue.DeactivateOtherActiveReprintItemsForHonoreeAsync(honoree.Id, change.Id, adminName, ct);

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
                    await db.SaveChangesAsync(ct);
                }
            }

            await transaction.CommitAsync(ct);

            var approved = ToReviewDto(change);
            if (isNewSubmission)
            {
                approved = approved with
                {
                    FlagGridId = change.FlagGridId,
                    FlagGridName = assignedGridName ?? string.Empty,
                    FlagSpotAssigned = flagSpotAssigned,
                    FlagSpotNotice = flagSpotNotice,
                    SubmissionKind = ReviewSubmissionDiff.NewSubmission
                };
            }

            return Ok(approved);
        }
        catch (HonoreePhotoMoveException ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    message = ex.Message,
                    detail = ex.Message
                });
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

    [HttpPost("{id:int}/removed-from-field")]
    public async Task<IActionResult> RemovedFromField(int id, CancellationToken ct)
    {
        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        var change = await db.HonoreeChangeRequests
            .Include(request => request.FlagClaim)
            .Include(request => request.Honoree)
            .ThenInclude(honoree => honoree!.FlagGrid)
            .FirstOrDefaultAsync(request => request.Id == id, ct);

        if (change is null)
        {
            return NotFound("Change request was not found.");
        }

        if (change.RequestStatus != PendingReviewMaintenance.Withdrawal)
        {
            return Conflict(new
            {
                message = "Only a withdrawal can be confirmed as removed from the field.",
                currentStatus = change.RequestStatus
            });
        }

        var now = DateTime.UtcNow;
        if (change.Honoree is { } honoree)
        {
            if (honoree.FlagGrid is { } linkedGrid && linkedGrid.HonoreeId == honoree.Id)
            {
                linkedGrid.HonoreeId = null;
                linkedGrid.ModifiedBy = adminName;
                linkedGrid.ModifiedDate = now;
            }
            else if (honoree.FlagGridId is int gridId)
            {
                var grid = await db.FlagGrids.FirstOrDefaultAsync(item => item.Id == gridId, ct);
                if (grid is not null && grid.HonoreeId == honoree.Id)
                {
                    grid.HonoreeId = null;
                    grid.ModifiedBy = adminName;
                    grid.ModifiedDate = now;
                }
            }

            honoree.FlagGridId = null;
            honoree.IsActive = false;
            honoree.DeletedDate ??= now;
            honoree.DeletedBy = string.IsNullOrWhiteSpace(honoree.DeletedBy) ? adminName : honoree.DeletedBy;
            honoree.ModifiedBy = adminName;
            honoree.ModifiedDate = now;
        }

        if (change.FlagClaim is not null)
        {
            change.FlagClaim.FlagGridId = null;
        }

        change.RequestStatus = PendingReviewMaintenance.Removed;
        change.ReviewedUtc = now;
        change.ReviewedBy = adminName;
        change.ReviewNotes = AppendReviewNote(
            change.ReviewNotes,
            "Removed from the field. The flag spot was released and the honoree record was deactivated.");

        await db.SaveChangesAsync(ct);
        return Ok(new { message = "Honoree removed from the field." });
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
                string? finalPhotoFileName = null;
                Exception? moveError = null;

                try
                {
                    finalPhotoFileName = await fileStorage.PromoteImageAsync(change.PhotoFileName, honoree.Id, ct);
                }
                catch (Exception ex)
                {
                    moveError = ex;
                }

                if (moveError is not null || string.IsNullOrWhiteSpace(finalPhotoFileName))
                {
                    var detail = moveError?.Message
                        ?? $"The pending honoree photo '{change.PhotoFileName}' was not found in honoreeimages.";

                    throw new HonoreePhotoMoveException(
                        $"The pending honoree photo could not be moved from honoreeimages. The honoree change was not approved. {detail}");
                }

                honoree.PhotoFileName = finalPhotoFileName;
                change.PhotoFileName = finalPhotoFileName;
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
        await reprintQueue.DeactivateOtherActiveReprintItemsForHonoreeAsync(honoree.Id, keepChangeRequestId: null, adminName, ct);

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

    private async Task<IReadOnlyList<AdminReviewItemDto>> AttachClaimantAccountsAsync(
        IReadOnlyList<AdminReviewItemDto> items,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var claimIds = items.Select(item => item.ClaimId).Distinct().ToList();
        var owners = await db.FlagClaims
            .AsNoTracking()
            .Where(claim => claimIds.Contains(claim.Id))
            .Select(claim => new { claim.Id, claim.ExternalUserObjectId })
            .ToListAsync(ct);

        var contacts = await profiles.GetAccountContactsAsync(owners.Select(owner => owner.ExternalUserObjectId), ct);
        var ownerByClaim = owners.ToDictionary(owner => owner.Id, owner => owner.ExternalUserObjectId);

        return items.Select(item =>
        {
            if (!ownerByClaim.TryGetValue(item.ClaimId, out var ownerId) ||
                !contacts.TryGetValue(ownerId, out var contact))
            {
                return item;
            }

            return item with
            {
                ClaimantPhone = contact.Phone,
                ClaimantOrganization = contact.Organization
            };
        }).ToList();
    }

    private async Task<AdminReviewItemDto> WithPhotoProbeAsync(AdminReviewItemDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.PhotoFileName))
        {
            return dto;
        }

        var probe = await fileStorage.ProbeHonoreeImageAsync(dto.PhotoFileName, ct);
        if (probe == HonoreeImageProbeResult.Missing)
        {
            logger.LogWarning(
                "Pending honoree photo {PhotoFileName} for change request {ChangeRequestId} is missing from honoreeimages. The honoree stays in the review list without that photo.",
                dto.PhotoFileName,
                dto.ChangeRequestId);
            return dto with
            {
                PhotoFileName = null,
                ReviewNotes = AppendReviewNote(dto.ReviewNotes, "The pending honoree photo was not found in honoreeimages.")
            };
        }

        if (probe == HonoreeImageProbeResult.Failed)
        {
            logger.LogWarning(
                "Pending honoree photo {PhotoFileName} for change request {ChangeRequestId} could not be checked in honoreeimages. The honoree stays in the review list.",
                dto.PhotoFileName,
                dto.ChangeRequestId);
        }

        return dto;
    }

    private static AdminReviewItemDto ToReviewDto(HonoreeChangeRequest r) =>
        ToReviewDto(new PendingReviewSource
        {
            Id = r.Id,
            FlagClaimId = r.FlagClaimId,
            FlagGridId = r.FlagGridId,
            FlagGridName = r.FlagGrid?.FlagGridName,
            HonoreeId = r.HonoreeId,
            FirstName = r.FirstName,
            MiddleName = r.MiddleName,
            LastName = r.LastName,
            Suffix = r.Suffix,
            Nickname = r.Nickname,
            Rank = r.Rank,
            ServiceBranchName = r.ServiceBranch?.ServiceBranchName,
            ClaimantEmail = r.FlagClaim?.ExternalUserEmail,
            ClaimantName = r.FlagClaim?.ExternalUserName,
            RequestStatus = r.RequestStatus,
            CreatedUtc = r.CreatedUtc,
            SubmittedUtc = r.SubmittedUtc,
            RequiresCardReprint = r.RequiresCardReprint,
            CardPrintedUtc = r.CardPrintedUtc,
            ReviewNotes = r.ReviewNotes,
            PhotoFileName = r.PhotoFileName,
            Description = r.Description,
            Awards = r.Awards,
            ConflictsServed = r.ConflictsServed
        });

    private static AdminReviewItemDto ToReviewDto(PendingReviewSource r)
    {
        var honoreeName = BuildHonoreeName(r.FirstName, r.MiddleName, r.LastName, r.Suffix, r.Nickname);
        return new AdminReviewItemDto(
            r.Id,
            r.FlagClaimId,
            r.FlagGridId,
            r.FlagGridName ?? string.Empty,
            r.HonoreeId,
            string.IsNullOrWhiteSpace(honoreeName) ? "Honoree" : honoreeName,
            BlankToNull(r.Rank),
            BlankToNull(r.ServiceBranchName),
            r.ClaimantEmail ?? string.Empty,
            BlankToNull(r.ClaimantName),
            string.IsNullOrWhiteSpace(r.RequestStatus) ? "Submitted" : r.RequestStatus,
            r.CreatedUtc ?? DateTime.UtcNow,
            r.SubmittedUtc,
            r.RequiresCardReprint,
            r.CardPrintedUtc,
            r.ReviewNotes,
            BlankToNull(r.PhotoFileName),
            r.Description,
            r.Awards,
            r.ConflictsServed,
            SubmissionKind: ReviewSubmissionDiff.Kind(r.RequestStatus, r.HonoreeId));
    }

    private static AdminReviewItemDto DegradedReviewDto(PendingReviewSource r)
    {
        var honoreeName = BuildHonoreeName(r.FirstName, r.MiddleName, r.LastName, r.Suffix, r.Nickname);
        return new AdminReviewItemDto(
            r.Id,
            r.FlagClaimId,
            r.FlagGridId,
            r.FlagGridName ?? string.Empty,
            r.HonoreeId,
            string.IsNullOrWhiteSpace(honoreeName) ? "Honoree" : honoreeName,
            null,
            null,
            r.ClaimantEmail ?? string.Empty,
            BlankToNull(r.ClaimantName),
            string.IsNullOrWhiteSpace(r.RequestStatus) ? "Submitted" : r.RequestStatus,
            r.CreatedUtc ?? DateTime.UtcNow,
            r.SubmittedUtc,
            r.RequiresCardReprint,
            r.CardPrintedUtc,
            "This honoree submission could not be fully loaded. The other pending reviews are still listed.",
            null,
            null,
            null,
            null,
            SubmissionKind: ReviewSubmissionDiff.Kind(r.RequestStatus, r.HonoreeId));
    }

    private static ReviewSnapshot PendingSnapshot(PendingReviewSource request) => new(
        request.HonoreeId,
        request.FirstName,
        request.MiddleName,
        request.LastName,
        request.Suffix,
        request.Nickname,
        request.Rank,
        request.ServiceBranchName,
        request.StartYear,
        request.EndYear,
        request.ConflictsServed,
        request.Awards,
        request.Description,
        request.Kia,
        request.PhotoFileName);

    private static string? AppendReviewNote(string? notes, string addition)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return addition;
        }

        return notes.Contains(addition, StringComparison.Ordinal)
            ? notes
            : $"{notes.Trim()}\n{addition}";
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class PendingReviewSource
    {
        public int Id { get; set; }
        public int FlagClaimId { get; set; }
        public int? FlagGridId { get; set; }
        public string? FlagGridName { get; set; }
        public int? HonoreeId { get; set; }
        public string? FirstName { get; set; }
        public string? MiddleName { get; set; }
        public string? LastName { get; set; }
        public string? Suffix { get; set; }
        public string? Nickname { get; set; }
        public string? Rank { get; set; }
        public string? ServiceBranchName { get; set; }
        public string? ClaimantEmail { get; set; }
        public string? ClaimantName { get; set; }
        public string? RequestStatus { get; set; }
        public DateTime? CreatedUtc { get; set; }
        public DateTime? SubmittedUtc { get; set; }
        public bool RequiresCardReprint { get; set; }
        public DateTime? CardPrintedUtc { get; set; }
        public string? ReviewNotes { get; set; }
        public string? PhotoFileName { get; set; }
        public string? Description { get; set; }
        public string? Awards { get; set; }
        public string? ConflictsServed { get; set; }
        public int? StartYear { get; set; }
        public int? EndYear { get; set; }
        public bool Kia { get; set; }
    }

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

    private async Task<IReadOnlyList<FlagMapExportSource>> BuildFlagMapExportSourcesAsync(CancellationToken ct)
    {
        var flagGrids = await db.FlagGrids
            .AsNoTracking()
            .Where(g => g.DeletedDate == null)
            .ToListAsync(ct);

        var gridIds = flagGrids.Select(g => g.Id).ToList();

        var honorees = gridIds.Count == 0
            ? new List<Honoree>()
            : await db.Honorees
                .AsNoTracking()
                .Include(h => h.ServiceBranch)
                .Include(h => h.Sponsor)
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
                var isReserved = flagGrid.Reserved;
                var isOpen = honoree is null && !flagGrid.HonoreeId.HasValue && !flagGrid.Reserved;
                return new FlagMapExportSource(
                    flagGrid.FlagGridName,
                    BuildFlagPositionRowLabel(flagGrid.FlagGridName),
                    BuildFlagPositionColumnNumber(flagGrid.FlagGridName),
                    honoree is null ? null : BuildHonoreeName(honoree),
                    isOpen,
                    isReserved,
                    honoree?.Rank,
                    honoree?.ServiceBranch?.ServiceBranchName,
                    honoree is null ? null : BuildSponsorName(honoree.Sponsor),
                    honoree?.KIA ?? false);
            })
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

    internal static string BuildHonoreeName(HonoreeChangeRequest r) =>
        BuildHonoreeName(r.FirstName, r.MiddleName, r.LastName, r.Suffix, r.Nickname);

    private static string BuildHonoreeName(
        string? firstName,
        string? middleName,
        string? lastName,
        string? suffix,
        string? nickname)
    {
        return AddNickname(
            string.Join(
                " ",
                new[] { firstName, middleName, lastName, suffix }
                    .Where(v => !string.IsNullOrWhiteSpace(v))),
            nickname);
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

    private sealed class HonoreePhotoMoveException(string message) : Exception(message);
}
