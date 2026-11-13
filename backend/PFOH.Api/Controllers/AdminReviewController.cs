using System.Net;
using System.Text;
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
    string? ServiceBranchName,
    string? PdfWarning = null);

public record AssignFlagPositionRequest(int HonoreeId);

public class RemoveReprintQueueItemsRequest
{
    public IReadOnlyList<int>? ChangeRequestIds { get; set; }

    public string? Reason { get; set; }

    public string? Source { get; set; }
}

public record RemoveFromFlagGridRequest(string? Reason, bool AcknowledgeDuplicates = false);

public record AdminUnassignedHonoreeDto(
    int Id,
    string FullName,
    string? Nickname,
    string? Rank,
    string? ServiceBranchName);

[ApiController]
[Route("api/admin/review")]
[Authorize(Policy = "AdminOnly")]
[EnableRateLimiting("AuthenticatedGet")]
public class AdminReviewController(
    PfohDbContext db,
    IWebHostEnvironment environment,
    UserProfileService profiles,
    HonoreeFileStorage fileStorage,
    ILogger<AdminReviewController> logger,
    PendingReviewMaintenance pendingReviews,
    FlagGridAssignment flagGrids,
    ReprintQueueMaintenance reprintQueue,
    HonoreePdfService pdfs,
    FlagGridRemovalService removals,
    SubmissionGuardService submissions) : ControllerBase
{
    [HttpGet("superseded")]
    public async Task<ActionResult<SupersededSubmissionPage>> GetSuperseded(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default) =>
        Ok(await submissions.ListSupersededAsync(page, pageSize, ct));

    [HttpPost("{changeRequestId:int}/restore")]
    public async Task<IActionResult> RestoreSuperseded(int changeRequestId, CancellationToken ct)
    {
        var actor = AdminActor.From(User);
        var restored = await submissions.RestoreAsync(changeRequestId, actor.Email, actor.ObjectId, ct);
        if (!restored)
        {
            return NotFound("That superseded submission was not found.");
        }

        return Ok(new { message = "Restored to the approval queue." });
    }

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
                ClaimantObjectId = r.FlagClaim == null ? null : r.FlagClaim.ExternalUserObjectId,
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
                Kia = r.KIA,
                CorrectsChangeRequestId = r.CorrectsChangeRequestId
            })
            .ToListAsync(ct);

        var withdrawalRequests = submittedRequests
            .Where(request => request.RequestStatus == PendingReviewMaintenance.Withdrawal)
            .ToList();
        var openRequests = submittedRequests
            .Where(request => request.RequestStatus != PendingReviewMaintenance.Withdrawal)
            .ToList();
        var restoredIds = await RestoredChangeRequestIdsAsync(openRequests.Select(request => request.Id).ToList(), ct);
        var newestRequests = PendingReviewMaintenance.KeepNewest(
            openRequests,
            request => new PendingReviewMaintenance.CardRef(
                request.Id,
                request.FlagGridId,
                request.HonoreeId,
                request.SubmittedUtc,
                request.CreatedUtc ?? DateTime.UtcNow,
                request.ClaimantObjectId,
                request.FlagClaimId))
            .Concat(openRequests.Where(request => restoredIds.Contains(request.Id)))
            .Concat(withdrawalRequests)
            .DistinctBy(request => request.Id)
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
                Honoree? liveHonoree = null;
                if (request.HonoreeId is int honoreeId && honoreeById.TryGetValue(honoreeId, out var honoree))
                {
                    liveHonoree = honoree;
                    current = ReviewSubmissionDiff.FromHonoree(honoree);
                }

                var isWithdrawal = request.RequestStatus == PendingReviewMaintenance.Withdrawal;
                dto = dto with
                {
                    FieldChanges = isWithdrawal
                        ? Array.Empty<ReviewFieldChangeDto>()
                        : ReviewSubmissionDiff.Compare(PendingSnapshot(request), current)
                };
                dto = await WithPhotoProbeAsync(dto, ct);
                dto = await WithLivePhotoAsync(dto, liveHonoree, ct);
                submitted.Add(dto);
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

        Response.Headers[DuplicateReviewGroups.OpenGroupCountHeader] =
            (await DuplicateReviewGroups.CountOpenAsync(db, ct)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            submitted = await AttachReactivationAsync(submitted, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Reactivation labels could not be attached to pending honoree reviews. Returning the review list without them.");
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

    [HttpGet("honorees/{honoreeId:int}/photo")]
    public async Task<IActionResult> CurrentHonoreePhoto(int honoreeId, CancellationToken ct)
    {
        var honoree = await db.Honorees
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == honoreeId && row.DeletedDate == null, ct);
        if (honoree is null || !IsStoredLivePhoto(honoree.PhotoFileName))
        {
            return NotFound("Honoree photo was not found.");
        }

        var probe = await fileStorage.ProbeHonoreeImageAsync(honoree.PhotoFileName, ct);
        if (probe is HonoreeImageProbeResult.Missing or HonoreeImageProbeResult.NoPhoto)
        {
            return NotFound("Honoree photo was not found.");
        }

        byte[]? bytes = null;
        try
        {
            bytes = await fileStorage.DownloadImageAsync(honoree.PhotoFileName, null, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live honoree photo {PhotoFileName} for honoree {HonoreeId} could not be read.", honoree.PhotoFileName, honoreeId);
        }

        if (bytes is null || bytes.Length == 0)
        {
            return NotFound("Honoree photo was not found.");
        }

        var contentType = Path.GetExtension(honoree.PhotoFileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "image/jpeg"
        };
        var display = DisplayImage.ForHonoreePhoto(bytes, contentType);
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return File(display.Bytes, display.ContentType);
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
        var photoFileName = change.PhotoFileName;
        if (string.IsNullOrWhiteSpace(photoFileName) && change.HonoreeId is int liveHonoreeId)
        {
            photoFileName = await db.Honorees
                .AsNoTracking()
                .Where(row => row.Id == liveHonoreeId)
                .Select(row => row.PhotoFileName)
                .FirstOrDefaultAsync(ct);
        }

        try
        {
            var pdf = await HonoreeCardPdf.RenderAsync(
                environment,
                fileStorage,
                honoree,
                null,
                string.IsNullOrWhiteSpace(photoFileName) ? null : photoFileName,
                null,
                ct,
                logger);
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

        var reason = ReasonText.Normalize(request.Reason);
        if (reason.Length == 0)
        {
            return BadRequest(new { message = "A reason is required." });
        }

        if (reason.Length > ReprintQueueAudit.ReasonMaxLength)
        {
            return BadRequest(new { message = $"The reason must be {ReprintQueueAudit.ReasonMaxLength} characters or fewer." });
        }

        var actor = AdminActor.From(User);
        var adminName = actor.Email;
        var source = string.Equals(request.Source, "bulk", StringComparison.OrdinalIgnoreCase) || ids.Count > 1
            ? "bulk"
            : "row";

        await ReprintQueueEventSchema.EnsureAsync(db, ct);

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
            var note = $"Removed from the reprint queue by administrator. Actor: {adminName}. At: {now:u}. Reason: {reason}";
            item.ReviewNotes = string.IsNullOrWhiteSpace(item.ReviewNotes)
                ? note
                : $"{item.ReviewNotes}\n{note}";
            ReprintQueueAudit.Add(
                db,
                item.Id,
                item.HonoreeId,
                ReprintQueueActions.Remove,
                adminName,
                actor.ObjectId,
                now,
                reason,
                source,
                queueItems.Count);
        }

        // The queue membership is saved first. The Cancelled claim-status write
        // is the one SQL Server rejects. Saving it second means that rejection
        // cannot roll the removal back or become HTTP 500.
        await db.SaveChangesAsync(ct);

        var internalClaims = queueItems
            .Where(item => item.FlagClaim?.ClaimStatus == "AdminReprintQueued")
            .Select(item => item.FlagClaim!)
            .ToList();

        foreach (var claim in internalClaims)
        {
            claim.ClaimStatus = "Cancelled";
        }

        if (internalClaims.Count > 0)
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                foreach (var claim in internalClaims)
                {
                    var status = db.Entry(claim).Property(item => item.ClaimStatus);
                    status.CurrentValue = status.OriginalValue;
                    status.IsModified = false;
                }

                logger.LogWarning(
                    "Reprint queue remove saved change requests {ChangeRequestIds} but SQL rejected Cancelled for claims {ClaimIds}: {DbError}",
                    string.Join(',', queueItems.Select(item => item.Id)),
                    string.Join(',', internalClaims.Select(claim => claim.Id)),
                    DatabaseError(ex));
            }
        }

        return Ok(new { count = queueItems.Count });
    }

    private static string DatabaseError(DbUpdateException ex)
    {
        Exception current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        var message = string.IsNullOrWhiteSpace(current.Message) ? ex.Message : current.Message;
        return message.Length > 500 ? message[..500] : message;
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

        await HonoreeClaimStore.SyncAsync(db, ct);
        var activeClaims = honoreeIds.Count == 0
            ? new List<HonoreeClaim>()
            : await db.HonoreeClaims
                .AsNoTracking()
                .Where(c =>
                    honoreeIds.Contains(c.HonoreeId) &&
                    !HonoreeClaimStore.HiddenStatuses.Contains(c.ClaimStatus))
                .OrderBy(c => c.DisplayName ?? c.Email)
                .ToListAsync(ct);

        var claimsByHonoreeId = activeClaims
            .GroupBy(c => c.HonoreeId)
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
            claims ??= new List<HonoreeClaim>();

            var claimantSummary = string.Join(
                "; ",
                claims.Select(c =>
                    $"{Clean(c.DisplayName) ?? Clean(c.Email)} <{Clean(c.Email)}> ({Clean(c.ClaimStatus)})"));

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

        var assignedUtc = DateTime.UtcNow;
        honoree.FlagGridId = flagGrid.Id;
        honoree.ModifiedBy = adminName;
        honoree.ModifiedDate = assignedUtc;

        flagGrid.HonoreeId = honoree.Id;
        flagGrid.ModifiedBy = adminName;
        flagGrid.ModifiedDate = assignedUtc;

        // Clear then Assign of a different spot turns the open Manual row into a Move.
        // The same spot cancels that row. A first assignment queues nothing.
        await FlagRemovalQueue.NoteAssignmentAsync(db, honoree, flagGrid, adminName, assignedUtc, ct);

        await db.SaveChangesAsync(ct);

        var assignWarning = await pdfs.TryRebuildAsync(honoree.Id, ct);

        var assignActor = AdminActor.From(User);
        await AddHonoreeToReprintQueueAsync(
            honoree,
            flagGrid,
            assignActor.Email,
            "Automatically added to the reprint queue after flag grid assignment.",
            ct,
            assignActor.ObjectId);

        var position = await BuildFlagPositionAsync(flagGrid.Id, ct);
        if (position is null)
        {
            return NotFound(new { message = "Flag position was not found after assignment." });
        }

        return Ok(assignWarning is null ? position : position with { PdfWarning = assignWarning });
    }

    [HttpGet("flag-positions/{flagGridId:int}/removal-preview")]
    public async Task<ActionResult<FlagGridRemovalPreview>> PreviewFlagPositionRemoval(int flagGridId, CancellationToken ct)
    {
        var result = await removals.PreviewAsync(flagGridId, null, ct);
        return RemovalResult(result);
    }

    [HttpGet("honorees/{honoreeId:int}/removal-preview")]
    public async Task<ActionResult<FlagGridRemovalPreview>> PreviewHonoreeRemoval(int honoreeId, CancellationToken ct)
    {
        var result = await removals.PreviewAsync(null, honoreeId, ct);
        return RemovalResult(result);
    }

    [HttpPost("flag-positions/{flagGridId:int}/clear")]
    public async Task<ActionResult<FlagGridRemovalResponse>> ClearFlagPosition(
        int flagGridId,
        [FromBody] RemoveFromFlagGridRequest? request,
        CancellationToken ct)
    {
        var result = await removals.RemoveAsync(
            flagGridId,
            null,
            request?.Reason,
            request?.AcknowledgeDuplicates ?? false,
            AdminActor.From(User),
            ct);
        return RemovalResult(result);
    }

    [HttpPost("honorees/{honoreeId:int}/remove-from-grid")]
    public async Task<ActionResult<FlagGridRemovalResponse>> RemoveHonoreeFromGrid(
        int honoreeId,
        [FromBody] RemoveFromFlagGridRequest? request,
        CancellationToken ct)
    {
        var result = await removals.RemoveAsync(
            null,
            honoreeId,
            request?.Reason,
            request?.AcknowledgeDuplicates ?? false,
            AdminActor.From(User),
            ct);
        return RemovalResult(result);
    }

    private ActionResult RemovalResult(FlagGridRemovalResult result)
    {
        if (result.Body is not null)
        {
            return Ok(result.Body);
        }

        if (result.Preview is not null && result.Status == StatusCodes.Status200OK)
        {
            return Ok(result.Preview);
        }

        if (result.Status == StatusCodes.Status409Conflict)
        {
            return Conflict(new
            {
                code = FlagGridRemovalService.DuplicateCode,
                message = result.Error,
                duplicates = result.Preview?.Duplicates ?? []
            });
        }

        return StatusCode(result.Status == 0 ? StatusCodes.Status400BadRequest : result.Status, new { message = result.Error });
    }

    [HttpPost("honoree/{honoreeId:int}/queue-reprint")]
    public async Task<ActionResult<AdminPrintQueueItemDto>> QueueHonoreeReprint(
        int honoreeId,
        CancellationToken ct)
    {
        try
        {
            var actor = AdminActor.From(User);
            var adminName = actor.Email;

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

            await reprintQueue.DeactivateOtherActiveReprintItemsForHonoreeAsync(
                honoree.Id, keepChangeRequestId: null, adminName, ct, actor.ObjectId);

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

            var queueWarning = await pdfs.TryRebuildAsync(honoree.Id, ct);

            if (existingQueueItem is not null)
            {
                var existing = await ToPrintQueueDto(existingQueueItem, ct);
                return Ok(queueWarning is null ? existing : existing with { PdfWarning = queueWarning });
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
            await RecordReprintAdd(queueRequest, actor, "Added to the reprint queue by administrator.", "queue", ct);

            var queued = await ToPrintQueueDto(queueRequest, ct);
            return Ok(queueWarning is null ? queued : queued with { PdfWarning = queueWarning });
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

            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteInTransactionAsync<ActionResult<AdminReviewItemDto>>(async _ =>
            {
            db.ChangeTracker.Clear();

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
                    var repeatActor = AdminActor.From(User);
                    await reprintQueue.DeactivateOtherActiveReprintItemsForHonoreeAsync(
                        change.HonoreeId.Value, change.Id, adminName, ct, repeatActor.ObjectId);
                    await pdfs.TryRebuildAsync(change.HonoreeId.Value, ct);
                }

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

            if (change.CorrectsChangeRequestId is int correctsId && change.HonoreeId is not > 0)
            {
                var target = await db.HonoreeChangeRequests
                    .FirstOrDefaultAsync(row => row.Id == correctsId, ct);
                if (target is { RequestStatus: "Submitted", HonoreeId: null or <= 0 })
                {
                    target.FirstName = change.FirstName;
                    target.MiddleName = change.MiddleName;
                    target.LastName = change.LastName;
                    target.Suffix = change.Suffix;
                    target.Nickname = change.Nickname;
                    target.Rank = change.Rank;
                    target.ServiceBranchId = change.ServiceBranchId;
                    target.ServiceBranchCategoryId = change.ServiceBranchCategoryId;
                    target.StartYear = change.StartYear;
                    target.EndYear = change.EndYear;
                    target.DatesUserEntry = change.DatesUserEntry;
                    target.ConflictsServed = change.ConflictsServed;
                    target.Awards = change.Awards;
                    target.Description = change.Description;
                    target.KIA = change.KIA;
                    change.RequestStatus = "Approved";
                    change.ReviewedUtc = DateTime.UtcNow;
                    change.ReviewedBy = adminName;
                    change.ReviewNotes = string.IsNullOrWhiteSpace(request.ReviewNotes)
                        ? "Applied as a correction to the pending submission."
                        : request.ReviewNotes;
                    change.RequiresCardReprint = false;
                    await db.SaveChangesAsync(ct);
                    return Ok(ToReviewDto(change));
                }

                if (target is { HonoreeId: > 0 })
                {
                    change.HonoreeId = target.HonoreeId;
                    change.FlagGridId ??= target.FlagGridId;
                }
                else
                {
                    return Conflict(new
                    {
                        message = "The pending submission is no longer waiting for review. Reject this correction, or ask the submitter to send a new one."
                    });
                }
            }

            var isReactivation = await db.HonoreeReactivationRequests
                .AnyAsync(row => row.ChangeRequestId == change.Id, ct);
            var isNewSubmission = !isReactivation && change.HonoreeId is not > 0;
            var assignFlag = isNewSubmission || isReactivation;
            bool? flagSpotAssigned = null;
            string? flagSpotNotice = null;
            string? assignedGridName = null;
            if (isNewSubmission)
            {
                await PendingSubmissionGridSchema.EnsureAsync(db, ct);
                await pendingReviews.ReleasePendingSubmissionSpotsAsync(ct);
            }

            if (isReactivation)
            {
                if (change.HonoreeId is not int reactivationHonoreeId ||
                    !await ReactivationStillEligibleAsync(reactivationHonoreeId, ct))
                {
                    return Conflict(new
                    {
                        message = "This honoree is no longer inactive and unflagged, so the reactivation cannot be approved."
                    });
                }
            }

            var honoree = await ApplyApprovedChanges(change, adminName, ct);
            if (isReactivation)
            {
                honoree.DeletedDate = null;
                honoree.DeletedBy = null;
            }

            if (assignFlag)
            {
                var grid = await flagGrids.NextAvailableAsync(ct);
                if (grid is null)
                {
                    flagSpotAssigned = false;
                    flagSpotNotice = FlagGridAssignment.NoSpotNotice;
                    honoree.FlagGridId = null;
                    change.FlagGridId = null;
                    if (change.FlagClaim is not null && (change.FlagClaim.HonoreeId is null || isReactivation))
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
            await HonoreeClaimStore.SyncAsync(db, ct);

            var approveActor = AdminActor.From(User);
            if (request.RequiresCardReprint)
            {
                await reprintQueue.DeactivateOtherActiveReprintItemsForHonoreeAsync(
                    honoree.Id, change.Id, adminName, ct, approveActor.ObjectId);
                await RecordReprintAdd(change, approveActor, "Approved and added to the reprint queue.", "approve", ct);
            }

            var approveWarning = await pdfs.TryRebuildAsync(honoree.Id, ct);
            if (approveWarning is not null && request.RequiresCardReprint)
            {
                // Approval should not silently fail or roll back just because the PDF could not be regenerated.
                // The item remains in the reprint queue so the next open can rebuild the PDF.
                change.ReviewNotes = string.IsNullOrWhiteSpace(change.ReviewNotes)
                    ? "Approved, but PDF regeneration failed. Regenerate the PDF manually before printing."
                    : $"{change.ReviewNotes}\n\nApproved, but PDF regeneration failed. Regenerate the PDF manually before printing.";
                await db.SaveChangesAsync(ct);
            }

            var approved = ToReviewDto(change);
            if (assignFlag)
            {
                approved = approved with
                {
                    FlagGridId = change.FlagGridId,
                    FlagGridName = assignedGridName ?? string.Empty,
                    FlagSpotAssigned = flagSpotAssigned,
                    FlagSpotNotice = flagSpotNotice,
                    SubmissionKind = isReactivation
                        ? ReviewSubmissionDiff.Reactivation
                        : ReviewSubmissionDiff.NewSubmission
                };
            }

            if (isReactivation)
            {
                ReactivationAudit.Approved(logger, honoree.Id, change.Id, flagSpotAssigned == true);
            }

            return Ok(approved);
            },
            async _ =>
            {
                db.ChangeTracker.Clear();
                var status = await db.HonoreeChangeRequests
                    .Where(row => row.Id == changeRequestId)
                    .Select(row => row.RequestStatus)
                    .FirstOrDefaultAsync(ct);
                return status == "Approved";
            },
            ct);
        }
        catch (RetryLimitExceededException)
        {
            throw;
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
            FlagGrid? removedGrid = null;
            if (honoree.FlagGrid is { } linkedGrid && linkedGrid.HonoreeId == honoree.Id)
            {
                removedGrid = linkedGrid;
                linkedGrid.HonoreeId = null;
                linkedGrid.ModifiedBy = adminName;
                linkedGrid.ModifiedDate = now;
            }
            else if (honoree.FlagGridId is int gridId)
            {
                var grid = await db.FlagGrids.FirstOrDefaultAsync(item => item.Id == gridId, ct);
                if (grid is not null && grid.HonoreeId == honoree.Id)
                {
                    removedGrid = grid;
                    grid.HonoreeId = null;
                    grid.ModifiedBy = adminName;
                    grid.ModifiedDate = now;
                }
            }

            if (removedGrid is not null && !await FlagRemovalQueue.HasOpenAsync(db, honoree.Id, removedGrid.Id, ct))
            {
                FlagRemovalQueue.Record(
                    db,
                    honoree,
                    removedGrid,
                    FlagRemovalQueue.Other,
                    adminName,
                    now,
                    sourceDetail: FlagRemovalQueue.RemovalApprovedDetail);
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
            change.FlagClaim.ClaimStatus = "Unclaimed";
        }

        change.RequestStatus = PendingReviewMaintenance.Removed;
        change.ReviewedUtc = now;
        change.ReviewedBy = adminName;
        change.ReviewNotes = AppendReviewNote(
            change.ReviewNotes,
            "Removal request approved. The card is in the removal queue.");

        await db.SaveChangesAsync(ct);
        string? pdfWarning = null;
        if (change.HonoreeId is int removedHonoreeId)
        {
            pdfWarning = await pdfs.TryRebuildAsync(removedHonoreeId, ct, includeInactive: true);
        }

        return Ok(new
        {
            message = "Removal approved. The card is in the removal queue.",
            pdfWarning = pdfWarning is null
                ? null
                : "The removal was saved, but the card PDF could not be rebuilt."
        });
    }

    [HttpPost("{id:int}/decline-removal")]
    public async Task<IActionResult> DeclineRemoval(int id, CancellationToken ct)
    {
        var adminName = User.GetEmail();
        if (string.IsNullOrWhiteSpace(adminName))
        {
            adminName = User.GetDisplayName() ?? "PFOH.Admin";
        }

        var change = await db.HonoreeChangeRequests
            .Include(request => request.FlagClaim)
            .Include(request => request.Honoree)
            .FirstOrDefaultAsync(request => request.Id == id, ct);

        if (change is null)
        {
            return NotFound("Change request was not found.");
        }

        if (change.RequestStatus != PendingReviewMaintenance.Withdrawal)
        {
            return Conflict(new
            {
                message = "Only a removal request can be declined.",
                currentStatus = change.RequestStatus
            });
        }

        var now = DateTime.UtcNow;
        if (change.Honoree is { } honoree)
        {
            honoree.IsActive = true;
            honoree.DeletedDate = null;
            honoree.DeletedBy = null;
        }

        if (change.FlagClaim is { ClaimStatus: "Unclaimed" })
        {
            change.FlagClaim.ClaimStatus = "Claimed";
        }

        change.RequestStatus = "Rejected";
        change.ReviewedUtc = now;
        change.ReviewedBy = adminName;
        change.ReviewNotes = AppendReviewNote(
            change.ReviewNotes,
            "Removal request declined. The honoree stays on the field.");

        await db.SaveChangesAsync(ct);
        if (change.HonoreeId is int declinedHonoreeId)
        {
            await pdfs.TryRebuildAsync(declinedHonoreeId, ct);
        }

        return Ok(new { message = "Removal declined. The honoree stays on the field." });
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

        await FlagRemovalQueue.ReleaseReplacedGridAsync(
            db,
            honoree,
            previousGridId,
            change.FlagGridId,
            adminName,
            honoree.ModifiedDate,
            ct);
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

    private async Task RecordReprintAdd(
        HonoreeChangeRequest request,
        AdminActor actor,
        string reason,
        string source,
        CancellationToken ct)
    {
        await ReprintQueueEventSchema.EnsureAsync(db, ct);
        ReprintQueueAudit.Add(
            db,
            request.Id,
            request.HonoreeId,
            ReprintQueueActions.Add,
            actor.Email,
            actor.ObjectId,
            DateTime.UtcNow,
            reason,
            source,
            1);
        await db.SaveChangesAsync(ct);
    }

    private async Task<AdminPrintQueueItemDto> AddHonoreeToReprintQueueAsync(
        Honoree honoree,
        FlagGrid flagGrid,
        string adminName,
        string reviewNotes,
        CancellationToken ct,
        string? actorObjectId = null)
    {
        await reprintQueue.DeactivateOtherActiveReprintItemsForHonoreeAsync(
            honoree.Id, keepChangeRequestId: null, adminName, ct, actorObjectId);

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
        await RecordReprintAdd(
            queueRequest,
            new AdminActor(adminName, actorObjectId),
            reviewNotes,
            "queue",
            ct);

        return await ToPrintQueueDto(queueRequest, ct);
    }

    private async Task<AdminPrintQueueItemDto> ToPrintQueueDto(HonoreeChangeRequest r, CancellationToken ct)
    {
        string? pdfUrl = null;

        if (r.HonoreeId.HasValue)
        {
                pdfUrl = HonoreePdfService.CacheBust(await db.HonoreeSearchResults
                .AsNoTracking()
                .Where(h => h.Id == r.HonoreeId.Value)
                .Select(h => h.PDFUrl)
                .FirstOrDefaultAsync(ct));
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

    private async Task<bool> ReactivationStillEligibleAsync(int honoreeId, CancellationToken ct)
    {
        var honoree = await db.Honorees.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == honoreeId, ct);
        if (honoree is null || honoree.IsActive || honoree.FlagGridId is not null)
        {
            return false;
        }

        return !await db.FlagGrids.AsNoTracking()
            .AnyAsync(grid => grid.HonoreeId == honoreeId && grid.DeletedDate == null, ct);
    }

    private async Task<List<AdminReviewItemDto>> AttachReactivationAsync(
        List<AdminReviewItemDto> items,
        CancellationToken ct)
    {
        var ids = items.Select(item => item.ChangeRequestId).ToList();
        if (ids.Count == 0)
        {
            return items;
        }

        var rows = await db.HonoreeReactivationRequests
            .AsNoTracking()
            .Where(row => ids.Contains(row.ChangeRequestId))
            .Select(row => new { row.ChangeRequestId, row.RequesterNote, row.LinkUsedUtc })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return items;
        }

        var byChange = rows.ToDictionary(row => row.ChangeRequestId);
        return items.Select(item =>
        {
            if (!byChange.TryGetValue(item.ChangeRequestId, out var row))
            {
                return item;
            }

            return item with
            {
                SubmissionKind = ReviewSubmissionDiff.Reactivation,
                RequesterNote = row.RequesterNote,
                LinkUsedUtc = row.LinkUsedUtc,
                FieldChanges = Array.Empty<ReviewFieldChangeDto>()
            };
        }).ToList();
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

    private async Task<AdminReviewItemDto> WithLivePhotoAsync(
        AdminReviewItemDto dto,
        Honoree? honoree,
        CancellationToken ct)
    {
        if (honoree is null || !IsStoredLivePhoto(honoree.PhotoFileName))
        {
            return dto with { CurrentPhotoAvailable = false, CurrentPhotoUrl = null };
        }

        var probe = await fileStorage.ProbeHonoreeImageAsync(honoree.PhotoFileName, ct);
        if (probe is HonoreeImageProbeResult.Missing or HonoreeImageProbeResult.NoPhoto)
        {
            return dto with { CurrentPhotoAvailable = false, CurrentPhotoUrl = null };
        }

        return dto with
        {
            CurrentPhotoAvailable = true,
            CurrentPhotoUrl = $"/api/admin/review/honorees/{honoree.Id}/photo"
        };
    }

    private static bool IsStoredLivePhoto(string? photoFileName)
    {
        var name = photoFileName?.Trim();
        return !string.IsNullOrWhiteSpace(name)
            && !string.Equals(name, "silhouette.jpeg", StringComparison.OrdinalIgnoreCase);
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
            ConflictsServed = r.ConflictsServed,
            CorrectsChangeRequestId = r.CorrectsChangeRequestId
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
            SubmissionKind: ReviewSubmissionDiff.Kind(
                r.RequestStatus,
                r.HonoreeId,
                r.CorrectsChangeRequestId is > 0 && r.HonoreeId is not > 0));
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
            SubmissionKind: ReviewSubmissionDiff.Kind(
                r.RequestStatus,
                r.HonoreeId,
                r.CorrectsChangeRequestId is > 0 && r.HonoreeId is not > 0));
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

    private async Task<HashSet<int>> RestoredChangeRequestIdsAsync(
        IReadOnlyCollection<int> changeRequestIds,
        CancellationToken ct)
    {
        if (changeRequestIds.Count == 0)
        {
            return [];
        }

        var events = await db.SubmissionAuditEvents
            .AsNoTracking()
            .Where(evt => changeRequestIds.Contains(evt.ChangeRequestId))
            .Select(evt => new { evt.Id, evt.ChangeRequestId, evt.Action })
            .ToListAsync(ct);
        return events
            .GroupBy(evt => evt.ChangeRequestId)
            .Where(group => string.Equals(
                group.OrderByDescending(evt => evt.Id).First().Action,
                SubmissionAuditEvent.Restored,
                StringComparison.Ordinal))
            .Select(group => group.Key)
            .ToHashSet();
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
        public string? ClaimantObjectId { get; set; }
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
        public int? CorrectsChangeRequestId { get; set; }
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
