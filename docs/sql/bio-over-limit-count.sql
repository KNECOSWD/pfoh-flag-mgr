-- Read-only count of honoree bios longer than the application limit.
-- Do not run this against production from the application, and do not turn it into a migration.
-- Description is nvarchar. LEN ignores trailing spaces. DATALENGTH / 2 is the
-- UTF-16 code-unit count, which matches .NET string.Length and [MaxLength],
-- including trailing spaces. The limit is HonoreeBioLimits.MaxLength (200).

DECLARE @MaxBio int = 200;

SELECT
    (
        SELECT COUNT(*)
        FROM dbo.Honorees
        WHERE DATALENGTH(Description) / 2 > @MaxBio
    ) AS HonoreesOverLimit,
    (
        SELECT COUNT(*)
        FROM dbo.HonoreeChangeRequests
        WHERE RequestStatus = N'Submitted'
          AND DATALENGTH(Description) / 2 > @MaxBio
    ) AS PendingRequestsOverLimit;
