using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public static class ReprintQueueActions
{
    public const string Add = "Add";
    public const string Remove = "Remove";
    public const string DedupRemove = "DedupRemove";
    public const string Printed = "Printed";
}

public static class ReprintQueueAudit
{
    public const int ReasonMaxLength = 1000;

    public static void Add(
        PfohDbContext db,
        int changeRequestId,
        int? honoreeId,
        string action,
        string actorEmail,
        string? actorObjectId,
        DateTime utc,
        string? reason,
        string source,
        int batchSize)
    {
        var who = string.IsNullOrWhiteSpace(actorEmail) ? "PFOH.Admin" : actorEmail.Trim();
        if (who.Length > 256)
        {
            who = who[..256];
        }

        var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (why is { Length: > ReasonMaxLength })
        {
            why = why[..ReasonMaxLength];
        }

        var oid = string.IsNullOrWhiteSpace(actorObjectId) ? null : actorObjectId.Trim();
        if (oid is { Length: > 128 })
        {
            oid = oid[..128];
        }

        db.ReprintQueueEvents.Add(new ReprintQueueEvent
        {
            ChangeRequestId = changeRequestId,
            HonoreeId = honoreeId,
            Action = action,
            ActorEmail = who,
            ActorObjectId = oid,
            Utc = utc,
            Reason = why,
            Source = source.Length <= 20 ? source : source[..20],
            BatchSize = batchSize < 1 ? 1 : batchSize
        });
    }
}
