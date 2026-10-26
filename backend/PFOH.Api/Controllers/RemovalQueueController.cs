using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Extensions;
using PFOH.Api.Services;

namespace PFOH.Api.Controllers;

public sealed class MarkRemovalDoneRequest
{
    public List<int> Ids { get; set; } = new();
}

public sealed record RemovalQueueItemDto(
    int Id,
    int? HonoreeId,
    string HonoreeName,
    int FlagGridId,
    string FlagGridName,
    int? NewFlagGridId,
    string? NewFlagGridName,
    string Source,
    string? SourceDetail,
    int? MergeAuditId,
    DateTime CreatedUtc,
    string CreatedBy,
    DateTime? DoneUtc,
    string? DoneBy,
    DateTime? CanceledUtc,
    string? CanceledBy,
    string? CancelReason,
    bool LoggedAlreadyDone);

[ApiController]
[Route("api/admin/removal-queue")]
[Authorize(Policy = "AdminOnly")]
public class RemovalQueueController(PfohDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RemovalQueueItemDto>>> List(
        [FromQuery] string status = "open",
        CancellationToken ct = default)
    {
        var chosen = await ChooseAsync(status, ct);
        if (chosen.Error is not null)
        {
            return chosen.Error;
        }

        return Ok(chosen.Items.Select(ToDto).ToList());
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string status = "open", CancellationToken ct = default)
    {
        var chosen = await ChooseAsync(status, ct);
        if (chosen.Error is not null)
        {
            return chosen.Error;
        }

        var bytes = RemovalQueueExcelExporter.Build(chosen.Items);
        var fileName = $"pfoh-removal-queue-{DateTime.UtcNow:yyyyMMdd}.xlsx";
        return File(bytes, RemovalQueueExcelExporter.ContentType, fileName);
    }

    [HttpGet("count")]
    public async Task<ActionResult<object>> Count(CancellationToken ct)
    {
        await FlagRemovalQueueSchema.EnsureAsync(db, ct);
        var count = await db.FlagRemovalQueueItems.CountAsync(item =>
            item.DoneUtc == null && item.CanceledUtc == null, ct);
        return Ok(new { count });
    }

    [HttpPost("done")]
    public async Task<IActionResult> MarkDone([FromBody] MarkRemovalDoneRequest request, CancellationToken ct)
    {
        if (request.Ids is null || request.Ids.Count == 0)
        {
            return BadRequest("Select at least one item to mark done.");
        }

        await FlagRemovalQueueSchema.EnsureAsync(db, ct);
        var admin = AdminName();
        var now = DateTime.UtcNow;
        var ids = request.Ids.Distinct().ToList();
        var items = await db.FlagRemovalQueueItems
            .Where(item => ids.Contains(item.Id))
            .ToListAsync(ct);
        var marked = 0;
        foreach (var item in items)
        {
            if (item.DoneUtc != null || item.CanceledUtc != null)
            {
                continue;
            }

            item.DoneUtc = now;
            item.DoneBy = admin;
            marked++;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { count = marked });
    }

    [HttpPost("{id:int}/done")]
    public Task<IActionResult> MarkOne(int id, CancellationToken ct) =>
        MarkDone(new MarkRemovalDoneRequest { Ids = [id] }, ct);

    private async Task<(List<Models.FlagRemovalQueueItem> Items, ActionResult? Error)> ChooseAsync(
        string? status,
        CancellationToken ct)
    {
        await FlagRemovalQueueSchema.EnsureAsync(db, ct);
        var normalized = (status ?? "open").Trim();
        var rows = await db.FlagRemovalQueueItems.AsNoTracking().ToListAsync(ct);
        if (normalized.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            var open = rows
                .Where(item => item.DoneUtc == null && item.CanceledUtc == null)
                .OrderBy(item => item, Comparer<Models.FlagRemovalQueueItem>.Create((left, right) =>
                    FlagRemovalQueue.CompareSpots(left.FlagGridName, right.FlagGridName)))
                .ThenBy(item => item.Id)
                .ToList();
            return (open, null);
        }

        if (normalized.Equals("done", StringComparison.OrdinalIgnoreCase))
        {
            var done = rows
                .Where(item => item.DoneUtc != null || item.CanceledUtc != null)
                .OrderByDescending(Activity)
                .ThenByDescending(item => item.Id)
                .ToList();
            return (done, null);
        }

        return (new List<Models.FlagRemovalQueueItem>(), BadRequest("Status must be open or done."));
    }

    private string AdminName()
    {
        var name = User.GetEmail();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = User.GetDisplayName() ?? "PFOH.Admin";
        }

        return name.Length <= 256 ? name : name[..256];
    }

    private static DateTime Activity(Models.FlagRemovalQueueItem item) =>
        item.DoneUtc ?? item.CanceledUtc ?? item.CreatedUtc;

    private static RemovalQueueItemDto ToDto(Models.FlagRemovalQueueItem item) => new(
        item.Id,
        item.HonoreeId,
        item.HonoreeName,
        item.FlagGridId,
        item.FlagGridName,
        item.NewFlagGridId,
        item.NewFlagGridName,
        item.Source,
        item.SourceDetail,
        item.MergeAuditId,
        item.CreatedUtc,
        item.CreatedBy,
        item.DoneUtc,
        item.DoneBy,
        item.CanceledUtc,
        item.CanceledBy,
        item.CancelReason,
        item.DoneUtc != null &&
        item.DoneUtc == item.CreatedUtc &&
        item.Source == FlagRemovalQueue.Other &&
        (item.SourceDetail?.Contains("Withdrawal", StringComparison.OrdinalIgnoreCase) ?? false));
}
