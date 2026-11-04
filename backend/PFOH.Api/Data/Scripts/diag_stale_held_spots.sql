-- Read-only diagnostic. SELECT only. Do not run this as a change script.
-- Counts and lists flag spots that 5.2.0 released at startup
-- (CleanupDuplicatePendingAsync, then ReleasePendingSubmissionSpotsAsync).
-- 5.3.0 does not release them at startup.
-- A held spot is a new submission (no honoree yet) in Submitted, Superseded,
-- Rejected, or Draft, with a flag grid on the change request or on its claim.

SELECT COUNT(*) AS HeldChangeRequestCount
FROM dbo.HonoreeChangeRequests AS c
LEFT JOIN dbo.FlagClaims AS claim ON claim.Id = c.FlagClaimId
WHERE (c.HonoreeId IS NULL OR c.HonoreeId <= 0)
  AND c.RequestStatus IN (N'Submitted', N'Superseded', N'Rejected', N'Draft')
  AND (
        c.FlagGridId IS NOT NULL
        OR (claim.HonoreeId IS NULL AND claim.FlagGridId IS NOT NULL)
      );

SELECT COUNT(DISTINCT held.FlagGridId) AS HeldGridCount
FROM (
    SELECT c.FlagGridId
    FROM dbo.HonoreeChangeRequests AS c
    WHERE (c.HonoreeId IS NULL OR c.HonoreeId <= 0)
      AND c.RequestStatus IN (N'Submitted', N'Superseded', N'Rejected', N'Draft')
      AND c.FlagGridId IS NOT NULL
    UNION
    SELECT claim.FlagGridId
    FROM dbo.HonoreeChangeRequests AS c
    INNER JOIN dbo.FlagClaims AS claim ON claim.Id = c.FlagClaimId
    WHERE (c.HonoreeId IS NULL OR c.HonoreeId <= 0)
      AND c.RequestStatus IN (N'Submitted', N'Superseded', N'Rejected', N'Draft')
      AND claim.HonoreeId IS NULL
      AND claim.FlagGridId IS NOT NULL
) AS held;

SELECT
    c.Id AS ChangeRequestId,
    c.RequestStatus,
    g.Id AS FlagGridId,
    g.FlagGridName AS GridLocation
FROM dbo.HonoreeChangeRequests AS c
INNER JOIN dbo.FlagGrids AS g ON g.Id = c.FlagGridId
WHERE (c.HonoreeId IS NULL OR c.HonoreeId <= 0)
  AND c.RequestStatus IN (N'Submitted', N'Superseded', N'Rejected', N'Draft')
  AND c.FlagGridId IS NOT NULL
UNION ALL
SELECT
    c.Id AS ChangeRequestId,
    c.RequestStatus,
    g.Id AS FlagGridId,
    g.FlagGridName AS GridLocation
FROM dbo.HonoreeChangeRequests AS c
INNER JOIN dbo.FlagClaims AS claim ON claim.Id = c.FlagClaimId
INNER JOIN dbo.FlagGrids AS g ON g.Id = claim.FlagGridId
WHERE (c.HonoreeId IS NULL OR c.HonoreeId <= 0)
  AND c.RequestStatus IN (N'Submitted', N'Superseded', N'Rejected', N'Draft')
  AND claim.HonoreeId IS NULL
  AND claim.FlagGridId IS NOT NULL
  AND (c.FlagGridId IS NULL OR c.FlagGridId <> claim.FlagGridId)
ORDER BY GridLocation, ChangeRequestId;
