using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

/// <summary>
/// Adds a removal-queue row to the caller's change tracker. The caller saves
/// it in the same SaveChanges or transaction that changes the spot.
/// There is no database trigger.
/// Any future bulk or import path that changes a grid assignment must call
/// <see cref="Record"/>.
/// </summary>
public static class FlagRemovalQueue
{
    public const string Manual = "Manual";
    public const string Move = "Move";
    public const string Merge = "Merge";
    public const string Other = "Other";

    public const string MergeUndoneReason = "Canceled: merge undone";
    public const string SameSpotReason = "Canceled: put back at same spot";
    public const string WithdrawalDetail = "Withdrawal confirmed removed from field";

    public const string RemovalApprovedDetail = "Removal request approved";

    public static FlagRemovalQueueItem Record(
        PfohDbContext db,
        Honoree? honoree,
        FlagGrid oldGrid,
        string source,
        string admin,
        DateTime now,
        FlagGrid? newGrid = null,
        int? mergeAuditId = null,
        HonoreeMergeAudit? mergeAudit = null,
        string? sourceDetail = null,
        bool alreadyDone = false)
    {
        var item = new FlagRemovalQueueItem
        {
            HonoreeId = honoree is { Id: > 0 } ? honoree.Id : null,
            HonoreeName = Clip(honoree is null ? "Unknown honoree" : DisplayName(honoree), 200),
            FlagGridId = oldGrid.Id,
            FlagGridName = Clip(oldGrid.FlagGridName, 50),
            NewFlagGridId = newGrid?.Id,
            NewFlagGridName = newGrid is null ? null : Clip(newGrid.FlagGridName, 50),
            Source = Clip(source, 20),
            SourceDetail = string.IsNullOrWhiteSpace(sourceDetail) ? null : Clip(sourceDetail.Trim(), 200),
            CreatedUtc = now,
            CreatedBy = Clip(string.IsNullOrWhiteSpace(admin) ? "PFOH.Admin" : admin.Trim(), 256)
        };

        if (alreadyDone)
        {
            item.DoneUtc = now;
            item.DoneBy = item.CreatedBy;
        }

        if (mergeAudit is not null)
        {
            item.MergeAudit = mergeAudit;
        }
        else if (mergeAuditId is int auditId)
        {
            item.MergeAuditId = auditId;
        }

        db.FlagRemovalQueueItems.Add(item);
        return item;
    }

    /// <summary>
    /// Clear then Assign is how a card moves. An open Manual row with no new
    /// spot becomes a Move, unless Assign puts the honoree back on that same spot.
    /// Assigning a spot when no open Manual row exists queues nothing.
    /// </summary>
    public static async Task NoteAssignmentAsync(
        PfohDbContext db,
        Honoree honoree,
        FlagGrid newGrid,
        string admin,
        DateTime now,
        CancellationToken ct)
    {
        if (honoree.Id <= 0)
        {
            return;
        }

        var open = await db.FlagRemovalQueueItems
            .Where(item =>
                item.HonoreeId == honoree.Id &&
                item.Source == Manual &&
                item.DoneUtc == null &&
                item.CanceledUtc == null &&
                item.NewFlagGridId == null)
            .ToListAsync(ct);

        var who = Clip(string.IsNullOrWhiteSpace(admin) ? "PFOH.Admin" : admin.Trim(), 256);
        foreach (var item in open)
        {
            if (item.FlagGridId == newGrid.Id)
            {
                item.CanceledUtc = now;
                item.CanceledBy = who;
                item.CancelReason = SameSpotReason;
                continue;
            }

            item.Source = Move;
            item.NewFlagGridId = newGrid.Id;
            item.NewFlagGridName = Clip(newGrid.FlagGridName, 50);
        }
    }

    /// <summary>
    /// An approved change that points an existing honoree at a different grid
    /// frees the old grid's honoree link and queues the old spot.
    /// </summary>
    public static async Task ReleaseReplacedGridAsync(
        PfohDbContext db,
        Honoree honoree,
        int? previousGridId,
        int? newGridId,
        string admin,
        DateTime now,
        CancellationToken ct)
    {
        if (previousGridId is not int oldId || oldId == newGridId || honoree.Id <= 0)
        {
            return;
        }

        var oldGrid = await db.FlagGrids.FirstOrDefaultAsync(grid => grid.Id == oldId, ct);
        if (oldGrid is null)
        {
            return;
        }

        if (oldGrid.HonoreeId == honoree.Id)
        {
            oldGrid.HonoreeId = null;
            oldGrid.ModifiedBy = string.IsNullOrWhiteSpace(admin) ? "PFOH.Admin" : admin.Trim();
            oldGrid.ModifiedDate = now;
        }

        FlagGrid? newGrid = null;
        if (newGridId is int nextId)
        {
            newGrid = await db.FlagGrids.FirstOrDefaultAsync(grid => grid.Id == nextId, ct);
        }

        var source = newGrid is null ? Other : Move;
        var detail = newGrid is null
            ? $"Approved change took this honoree off {oldGrid.FlagGridName}."
            : $"Approved change moved this honoree from {oldGrid.FlagGridName} to {newGrid.FlagGridName}.";
        Record(db, honoree, oldGrid, source, admin, now, newGrid, sourceDetail: detail);
    }

    public static async Task OnMergeUndoneAsync(
        PfohDbContext db,
        HonoreeMergeAudit audit,
        string admin,
        DateTime now,
        CancellationToken ct)
    {
        if (audit.Id <= 0)
        {
            return;
        }

        var items = await db.FlagRemovalQueueItems
            .Where(item => item.MergeAuditId == audit.Id)
            .ToListAsync(ct);
        var who = Clip(string.IsNullOrWhiteSpace(admin) ? "PFOH.Admin" : admin.Trim(), 256);
        var putBack = new List<FlagRemovalQueueItem>();
        foreach (var item in items)
        {
            if (item.CanceledUtc != null)
            {
                continue;
            }

            if (item.DoneUtc == null)
            {
                item.CanceledUtc = now;
                item.CanceledBy = who;
                item.CancelReason = MergeUndoneReason;
                continue;
            }

            putBack.Add(item);
        }

        foreach (var item in putBack)
        {
            db.FlagRemovalQueueItems.Add(new FlagRemovalQueueItem
            {
                HonoreeId = item.HonoreeId,
                HonoreeName = item.HonoreeName,
                FlagGridId = item.FlagGridId,
                FlagGridName = item.FlagGridName,
                Source = Other,
                SourceDetail = Clip($"Put back: card for {item.FlagGridName} was pulled; put it back or reprint", 200),
                MergeAuditId = audit.Id,
                CreatedUtc = now,
                CreatedBy = who
            });
        }
    }

    public static async Task<bool> HasOpenAsync(
        PfohDbContext db,
        int? honoreeId,
        int flagGridId,
        CancellationToken ct)
    {
        if (honoreeId is not int id)
        {
            return false;
        }

        return await db.FlagRemovalQueueItems.AnyAsync(item =>
            item.HonoreeId == id &&
            item.FlagGridId == flagGridId &&
            item.DoneUtc == null &&
            item.CanceledUtc == null, ct);
    }

    public static string DisplayName(Honoree honoree)
    {
        var name = string.Join(
            " ",
            new[] { honoree.FirstName, honoree.MiddleName, honoree.LastName, honoree.Suffix }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim()));
        var nickname = honoree.Nickname?.Trim();
        if (!string.IsNullOrWhiteSpace(nickname) &&
            !name.EndsWith($"({nickname})", StringComparison.OrdinalIgnoreCase))
        {
            name = string.IsNullOrWhiteSpace(name) ? nickname : $"{name} ({nickname})";
        }

        return string.IsNullOrWhiteSpace(name) ? "Unknown honoree" : name;
    }

    public static int CompareSpots(string? left, string? right)
    {
        var leftRow = RowLabel(left);
        var rightRow = RowLabel(right);
        if (leftRow == "OTHER" && rightRow != "OTHER")
        {
            return 1;
        }

        if (rightRow == "OTHER" && leftRow != "OTHER")
        {
            return -1;
        }

        if (leftRow.Length != rightRow.Length)
        {
            return leftRow.Length - rightRow.Length;
        }

        var row = string.Compare(leftRow, rightRow, StringComparison.OrdinalIgnoreCase);
        if (row != 0)
        {
            return row;
        }

        var leftColumn = ColumnNumber(left);
        var rightColumn = ColumnNumber(right);
        if (leftColumn != rightColumn)
        {
            return leftColumn - rightColumn;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static string RowLabel(string? name)
    {
        var value = (name ?? string.Empty).Trim().ToUpperInvariant();
        var prefix = new string(value.TakeWhile(character => !char.IsDigit(character)).ToArray())
            .Trim('-', '_', ' ');
        return string.IsNullOrWhiteSpace(prefix) ? "OTHER" : prefix;
    }

    public static int ColumnNumber(string? name)
    {
        var value = name ?? string.Empty;
        var digits = new string(value.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : int.MaxValue;
    }

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
