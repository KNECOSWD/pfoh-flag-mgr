using PFOH.Api.Models;

namespace PFOH.Api.Services;

/// <summary>
/// Clears a flag spot the same way AdminReviewController.ClearFlagPosition does:
/// the honoree's FlagGridId is set null, and FlagGrid.HonoreeId is set null.
/// The honoree stays active. Callers that also deactivate a record set IsActive
/// themselves. This method does not set DeletedDate.
/// </summary>
public static class FlagSpotRelease
{
    public static void ClearAssignment(Honoree? honoree, FlagGrid flagGrid, string adminName, DateTime now)
    {
        if (honoree is not null)
        {
            honoree.FlagGridId = null;
            honoree.ModifiedBy = adminName;
            honoree.ModifiedDate = now;
        }

        flagGrid.HonoreeId = null;
        flagGrid.ModifiedBy = adminName;
        flagGrid.ModifiedDate = now;
    }
}
