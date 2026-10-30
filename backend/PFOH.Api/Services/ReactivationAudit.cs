using Microsoft.Extensions.Logging;

namespace PFOH.Api.Services;

/// <summary>
/// Audit lines for reactivation. They carry the honoree id and the action only.
/// Email, the note, and the token stay out of the log.
/// </summary>
public static class ReactivationAudit
{
    public static void Generated(ILogger logger, int honoreeId) =>
        logger.LogInformation("Reactivation link {Action}. HonoreeId={HonoreeId}", "generated", honoreeId);

    public static void Revoked(ILogger logger, int honoreeId) =>
        logger.LogInformation("Reactivation link {Action}. HonoreeId={HonoreeId}", "revoked", honoreeId);

    public static void Used(ILogger logger, int honoreeId) =>
        logger.LogInformation("Reactivation link {Action}. HonoreeId={HonoreeId}", "used", honoreeId);

    public static void Approved(ILogger logger, int honoreeId, int changeRequestId, bool flagAssigned) =>
        logger.LogInformation(
            "Reactivation request {Action}. HonoreeId={HonoreeId} ChangeRequestId={ChangeRequestId} FlagAssigned={FlagAssigned}",
            "approved",
            honoreeId,
            changeRequestId,
            flagAssigned);
}
