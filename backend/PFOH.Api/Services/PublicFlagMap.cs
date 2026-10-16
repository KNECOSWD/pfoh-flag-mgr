using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;
using PFOH.Api.Dtos;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public class PublicFlagMap(PfohDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PublicFlagMapSnapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        var grids = await LiveGridsAsync(ct);
        var honorees = await EligibleHonoreesAsync(ct);
        var occupiedByGrid = OccupiedByGrid(honorees);
        var storedLogos = await db.ServiceBranches
            .AsNoTracking()
            .Where(branch => branch.DeletedDate == null)
            .Select(branch => branch.LogoFileName)
            .ToListAsync(ct);

        var sections = new List<(PublicFlagSectionDto Summary, IReadOnlyList<PublicFlagSeatDto> Seats)>();
        foreach (var group in grids.GroupBy(grid => SectionLabel(grid.FlagGridName), StringComparer.OrdinalIgnoreCase))
        {
            var seats = group
                .Select(grid =>
                {
                    occupiedByGrid.TryGetValue(grid.Id, out var honoree);
                    return ToSeat(grid, honoree);
                })
                .OrderBy(seat => seat.ColumnNumber ?? int.MaxValue)
                .ThenBy(seat => seat.FlagGridName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var summary = new PublicFlagSectionDto(
                seats[0].Section,
                seats.Count,
                seats.Count(seat => seat.Status == "Occupied"));
            sections.Add((summary, seats));
        }

        sections.Sort((left, right) => SectionComparer.Instance.Compare(left.Summary.Section, right.Summary.Section));
        var summaries = sections.Select(section => section.Summary).ToList();
        var sectionPayloads = new Dictionary<string, (string Json, string Etag)>(StringComparer.OrdinalIgnoreCase);
        var fieldSections = new List<object>(sections.Count);
        foreach (var (summary, seats) in sections)
        {
            var payload = new { section = summary.Section, seats };
            var (json, etag) = JsonAndEtag(payload);
            sectionPayloads[summary.Section] = (json, etag);
            fieldSections.Add(new
            {
                section = summary.Section,
                flagCount = summary.FlagCount,
                occupiedCount = summary.OccupiedCount,
                seats
            });
        }

        var search = honorees
            .Where(honoree => honoree.FlagGrid is not null)
            .OrderBy(honoree => honoree.LastName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(honoree => honoree.FirstName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(honoree => honoree.FlagGrid?.FlagGridName, StringComparer.OrdinalIgnoreCase)
            .Select(ToSearch)
            .ToList();

        var (sectionsJson, sectionsEtag) = JsonAndEtag(summaries);
        var (searchJson, searchEtag) = JsonAndEtag(search);
        var (fieldJson, fieldEtag) = JsonAndEtag(new { sections = fieldSections });
        return new PublicFlagMapSnapshot
        {
            SectionsJson = sectionsJson,
            SectionsEtag = sectionsEtag,
            SearchJson = searchJson,
            SearchEtag = searchEtag,
            FieldJson = fieldJson,
            FieldEtag = fieldEtag,
            Sections = sectionPayloads,
            StoredLogoFileNames = storedLogos
        };
    }

    public static bool IsPublishedLogo(string fileName, IEnumerable<string?> storedNames)
    {
        if (!HonoreeFileStorage.IsPublicLogoFileName(fileName))
        {
            return false;
        }

        if (HonoreeFileStorage.FallbackLogoFileNames.Contains(fileName))
        {
            return true;
        }

        return storedNames.Any(name =>
            string.Equals(name?.Trim(), fileName, StringComparison.OrdinalIgnoreCase) &&
            HonoreeFileStorage.IsPublicLogoFileName(name));
    }

    private static (string Json, string Etag) JsonAndEtag(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return (json, $"\"{hash}\"");
    }

    public async Task<IReadOnlyList<PublicFlagSectionDto>> SectionsAsync(CancellationToken ct)
    {
        var grids = await LiveGridsAsync(ct);
        var honorees = await EligibleHonoreesAsync(ct);
        var occupiedByGrid = OccupiedByGrid(honorees);

        return grids
            .GroupBy(grid => SectionLabel(grid.FlagGridName))
            .Select(group => new PublicFlagSectionDto(
                group.Key,
                group.Count(),
                group.Count(grid => occupiedByGrid.ContainsKey(grid.Id))))
            .OrderBy(section => section.Section, SectionComparer.Instance)
            .ToList();
    }

    public async Task<IReadOnlyList<PublicFlagSeatDto>?> SeatsAsync(string section, CancellationToken ct)
    {
        var wanted = section.Trim();
        var grids = (await LiveGridsAsync(ct))
            .Where(grid => string.Equals(SectionLabel(grid.FlagGridName), wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (grids.Count == 0)
        {
            return null;
        }

        var honorees = await EligibleHonoreesAsync(ct);
        var occupiedByGrid = OccupiedByGrid(honorees);

        return grids
            .Select(grid =>
            {
                occupiedByGrid.TryGetValue(grid.Id, out var honoree);
                return ToSeat(grid, honoree);
            })
            .OrderBy(seat => seat.ColumnNumber ?? int.MaxValue)
            .ThenBy(seat => seat.FlagGridName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<PublicFlagSearchDto>> SearchIndexAsync(CancellationToken ct)
    {
        var honorees = await EligibleHonoreesAsync(ct);
        return honorees
            .Where(honoree => honoree.FlagGrid is not null)
            .OrderBy(honoree => honoree.LastName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(honoree => honoree.FirstName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(honoree => honoree.FlagGrid?.FlagGridName, StringComparer.OrdinalIgnoreCase)
            .Select(ToSearch)
            .ToList();
    }

    public async Task<SubmitterLookupResult> BySubmitterAsync(string? first, string? last, CancellationToken ct)
    {
        if (IsPrivateNameToken(first) || IsPrivateNameToken(last))
        {
            return new SubmitterLookupResult("none", 0, []);
        }

        var firstQuery = NormalizeName(first);
        var lastQuery = NormalizeName(last);
        if (firstQuery.Length < 2 || lastQuery.Length < 3)
        {
            return new SubmitterLookupResult("rejected", 0, []);
        }

        var honorees = await db.Honorees
            .AsNoTracking()
            .Include(honoree => honoree.FlagGrid)
            .Include(honoree => honoree.ServiceBranch)
            .ThenInclude(branch => branch!.ServiceBranchCategory)
            .Where(honoree =>
                honoree.IsActive &&
                honoree.DeletedDate == null &&
                honoree.FlagGridId != null &&
                honoree.FlagGrid != null &&
                honoree.FlagGrid.DeletedDate == null)
            .ToListAsync(ct);

        var honoreeIds = honorees.Select(honoree => honoree.Id).ToList();
        var sponsorIds = honorees
            .Where(honoree => honoree.SponsorId.HasValue)
            .Select(honoree => honoree.SponsorId!.Value)
            .Distinct()
            .ToList();
        var sponsorNames = new Dictionary<int, (string FirstName, string LastName)>();
        if (sponsorIds.Count > 0)
        {
            var sponsorRows = await db.Sponsors
                .AsNoTracking()
                .Where(sponsor => sponsorIds.Contains(sponsor.Id))
                .Select(sponsor => new { sponsor.Id, sponsor.FirstName, sponsor.LastName })
                .ToListAsync(ct);
            foreach (var row in sponsorRows)
            {
                sponsorNames[row.Id] = (row.FirstName, row.LastName);
            }
        }

        var claimNames = honoreeIds.Count == 0
            ? []
            : await db.FlagClaims
                .AsNoTracking()
                .Where(claim =>
                    claim.HonoreeId != null &&
                    honoreeIds.Contains(claim.HonoreeId.Value) &&
                    (claim.ClaimStatus == "Claimed" || claim.ClaimStatus == "Submitted" || claim.ClaimStatus == "Approved"))
                .Select(claim => new { claim.HonoreeId, claim.ExternalUserName })
                .ToListAsync(ct);

        var claimersByHonoree = claimNames
            .Where(claim => claim.HonoreeId.HasValue)
            .GroupBy(claim => claim.HonoreeId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(claim => SplitDisplayName(claim.ExternalUserName)).ToList());

        var matches = honorees
            .Where(honoree =>
            {
                if (honoree.SponsorId is int sponsorId &&
                    sponsorNames.TryGetValue(sponsorId, out var sponsorName) &&
                    NameMatches(sponsorName.FirstName, sponsorName.LastName, firstQuery, lastQuery))
                {
                    return true;
                }

                return claimersByHonoree.TryGetValue(honoree.Id, out var claimers) &&
                    claimers.Any(name => NameMatches(name.First, name.Last, firstQuery, lastQuery));
            })
            .Select(ToSearch)
            .OrderBy(hit => hit.FlagGridName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            return new SubmitterLookupResult("none", 0, []);
        }

        if (matches.Count > 10)
        {
            return new SubmitterLookupResult("refine", matches.Count, []);
        }

        return new SubmitterLookupResult("ok", matches.Count, matches);
    }

    public const int SubmitterSuggestionLimit = 8;

    public async Task<SubmitterSuggestionResult> SuggestSubmittersAsync(string? query, CancellationToken ct)
    {
        if (SuggestionQueryOutcome(query) is string outcome)
        {
            return new SubmitterSuggestionResult(outcome, []);
        }

        var tokens = await LoadSubmitterTokensAsync(ct);
        return SuggestFromTokens(tokens, query);
    }

    public async Task<IReadOnlyList<SubmitterIdentity>> LoadSubmitterTokensAsync(CancellationToken ct)
    {
        var honoreeRows = await db.Honorees
            .AsNoTracking()
            .Where(honoree =>
                honoree.IsActive &&
                honoree.DeletedDate == null &&
                honoree.FlagGridId != null &&
                honoree.FlagGrid != null &&
                honoree.FlagGrid.DeletedDate == null)
            .Select(honoree => new { honoree.Id, honoree.SponsorId })
            .ToListAsync(ct);

        var sponsorIds = honoreeRows
            .Where(row => row.SponsorId.HasValue)
            .Select(row => row.SponsorId!.Value)
            .Distinct()
            .ToList();
        var pairs = new List<(string? First, string? Last)>();
        if (sponsorIds.Count > 0)
        {
            var sponsorRows = await db.Sponsors
                .AsNoTracking()
                .Where(sponsor => sponsorIds.Contains(sponsor.Id))
                .Select(sponsor => new { sponsor.FirstName, sponsor.LastName })
                .ToListAsync(ct);
            foreach (var row in sponsorRows)
            {
                pairs.Add((row.FirstName, row.LastName));
            }
        }

        var honoreeIds = honoreeRows.Select(row => row.Id).ToList();
        if (honoreeIds.Count > 0)
        {
            var claimNames = await db.FlagClaims
                .AsNoTracking()
                .Where(claim =>
                    claim.HonoreeId != null &&
                    honoreeIds.Contains(claim.HonoreeId.Value) &&
                    (claim.ClaimStatus == "Claimed" || claim.ClaimStatus == "Submitted" || claim.ClaimStatus == "Approved"))
                .Select(claim => claim.ExternalUserName)
                .ToListAsync(ct);
            foreach (var displayName in claimNames)
            {
                var split = SplitDisplayName(displayName);
                pairs.Add((split.First, split.Last));
            }
        }

        return CollectSubmitterIdentities(pairs);
    }

    internal static string? SuggestionQueryOutcome(string? query)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length < 3)
        {
            return "rejected";
        }

        if (IsPrivateNameToken(trimmed))
        {
            return "none";
        }

        return null;
    }

    public static SubmitterSuggestionResult SuggestFromTokens(IReadOnlyList<SubmitterIdentity> tokens, string? query)
    {
        if (SuggestionQueryOutcome(query) is string outcome)
        {
            return new SubmitterSuggestionResult(outcome, []);
        }

        var names = SubmitterTokenIndex.Match(tokens, query, SubmitterSuggestionLimit);
        return new SubmitterSuggestionResult(names.Count == 0 ? "none" : "ok", names);
    }

    internal static List<SubmitterIdentity> CollectSubmitterIdentities(IEnumerable<(string? First, string? Last)> pairs)
    {
        var map = new Dictionary<string, SubmitterIdentity>(StringComparer.Ordinal);
        foreach (var (first, last) in pairs)
        {
            if (IsPrivateNameToken(first) || IsPrivateNameToken(last))
            {
                continue;
            }

            var normalizedFirst = NormalizeName(first);
            var normalizedLast = NormalizeName(last);
            if (normalizedFirst.Length == 0 || normalizedLast.Length == 0)
            {
                continue;
            }

            var key = normalizedFirst + "\n" + normalizedLast;
            if (map.ContainsKey(key))
            {
                continue;
            }

            var display = $"{(first ?? string.Empty).Trim()} {(last ?? string.Empty).Trim()}".Trim();
            map[key] = new SubmitterIdentity(normalizedFirst, normalizedLast, display);
        }

        return map.Values.ToList();
    }

    public async Task<IReadOnlyList<PublicFlagSearchDto>> FlagsForExactSubmitterAsync(
        string normalizedFirst,
        string normalizedLast,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(normalizedFirst) || string.IsNullOrWhiteSpace(normalizedLast))
        {
            return [];
        }

        var named = await LoadNamedFlagsAsync(ct);
        return named
            .Where(row => row.NormalizedFirst == normalizedFirst && row.NormalizedLast == normalizedLast)
            .GroupBy(row => row.Honoree.Id)
            .Select(group => group.First().Honoree)
            .Select(ToSearch)
            .OrderBy(hit => hit.FlagGridName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool SuggestionMatches(string normalizedFirst, string normalizedLast, string? query)
    {
        var raw = (query ?? string.Empty).Trim();
        if (raw.Length < 3 || IsPrivateNameToken(raw) || IsPrivateNameToken(normalizedFirst) || IsPrivateNameToken(normalizedLast))
        {
            return false;
        }

        var needle = NormalizeName(raw);
        if (needle.Length < 3 || normalizedFirst.Length == 0 || normalizedLast.Length == 0)
        {
            return false;
        }

        var full = normalizedFirst + " " + normalizedLast;
        if (full.StartsWith(needle, StringComparison.Ordinal))
        {
            return true;
        }

        if (needle.Contains(' '))
        {
            return false;
        }

        return normalizedFirst.StartsWith(needle, StringComparison.Ordinal)
            || normalizedLast.StartsWith(needle, StringComparison.Ordinal);
    }

    private async Task<List<NamedFlag>> LoadNamedFlagsAsync(CancellationToken ct)
    {
        var honorees = await db.Honorees
            .AsNoTracking()
            .Include(honoree => honoree.FlagGrid)
            .Include(honoree => honoree.ServiceBranch)
            .ThenInclude(branch => branch!.ServiceBranchCategory)
            .Where(honoree =>
                honoree.IsActive &&
                honoree.DeletedDate == null &&
                honoree.FlagGridId != null &&
                honoree.FlagGrid != null &&
                honoree.FlagGrid.DeletedDate == null)
            .ToListAsync(ct);

        var sponsorIds = honorees
            .Where(honoree => honoree.SponsorId.HasValue)
            .Select(honoree => honoree.SponsorId!.Value)
            .Distinct()
            .ToList();
        var sponsorNames = new Dictionary<int, (string FirstName, string LastName)>();
        if (sponsorIds.Count > 0)
        {
            var sponsorRows = await db.Sponsors
                .AsNoTracking()
                .Where(sponsor => sponsorIds.Contains(sponsor.Id))
                .Select(sponsor => new { sponsor.Id, sponsor.FirstName, sponsor.LastName })
                .ToListAsync(ct);
            foreach (var row in sponsorRows)
            {
                sponsorNames[row.Id] = (row.FirstName, row.LastName);
            }
        }

        var honoreeIds = honorees.Select(honoree => honoree.Id).ToList();
        var claimNames = honoreeIds.Count == 0
            ? []
            : await db.FlagClaims
                .AsNoTracking()
                .Where(claim =>
                    claim.HonoreeId != null &&
                    honoreeIds.Contains(claim.HonoreeId.Value) &&
                    (claim.ClaimStatus == "Claimed" || claim.ClaimStatus == "Submitted" || claim.ClaimStatus == "Approved"))
                .Select(claim => new { claim.HonoreeId, claim.ExternalUserName })
                .ToListAsync(ct);
        var claimersByHonoree = claimNames
            .Where(claim => claim.HonoreeId.HasValue)
            .GroupBy(claim => claim.HonoreeId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(claim => SplitDisplayName(claim.ExternalUserName)).ToList());

        var named = new List<NamedFlag>();
        foreach (var honoree in honorees)
        {
            if (honoree.SponsorId is int sponsorId && sponsorNames.TryGetValue(sponsorId, out var sponsorName))
            {
                AddNamedFlag(named, honoree, sponsorName.FirstName, sponsorName.LastName);
            }

            if (claimersByHonoree.TryGetValue(honoree.Id, out var claimers))
            {
                foreach (var claimer in claimers)
                {
                    AddNamedFlag(named, honoree, claimer.First, claimer.Last);
                }
            }
        }

        return named;
    }

    private static void AddNamedFlag(List<NamedFlag> named, Honoree honoree, string? first, string? last)
    {
        if (IsPrivateNameToken(first) || IsPrivateNameToken(last))
        {
            return;
        }

        var normalizedFirst = NormalizeName(first);
        var normalizedLast = NormalizeName(last);
        if (normalizedFirst.Length == 0 || normalizedLast.Length == 0)
        {
            return;
        }

        var displayFirst = (first ?? string.Empty).Trim();
        var displayLast = (last ?? string.Empty).Trim();
        named.Add(new NamedFlag(honoree, normalizedFirst, normalizedLast, displayFirst + " " + displayLast));
    }

    internal static bool IsPrivateNameToken(string? value)
    {
        var token = (value ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            return false;
        }

        if (token.Contains('@') || token.Contains('_') || token.Any(char.IsDigit))
        {
            return true;
        }

        var dot = token.IndexOf('.');
        return dot >= 0 && dot < token.Length - 1;
    }

    internal static bool NameMatches(string? storedFirst, string? storedLast, string firstQuery, string lastQuery)
    {
        if (IsPrivateNameToken(storedFirst) || IsPrivateNameToken(storedLast) ||
            IsPrivateNameToken(firstQuery) || IsPrivateNameToken(lastQuery))
        {
            return false;
        }

        var first = NormalizeName(storedFirst);
        var last = NormalizeName(storedLast);
        if (first.Length == 0 || last.Length == 0 || lastQuery.Length == 0 || firstQuery.Length == 0)
        {
            return false;
        }

        if (!string.Equals(last, lastQuery, StringComparison.Ordinal))
        {
            return false;
        }

        return first.StartsWith(firstQuery, StringComparison.Ordinal);
    }

    internal static string NormalizeName(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static (string First, string Last) SplitDisplayName(string? displayName)
    {
        var parts = (displayName ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !IsPrivateNameToken(part))
            .ToArray();
        if (parts.Length == 0)
        {
            return ("", "");
        }

        if (parts.Length == 1)
        {
            return (parts[0], "");
        }

        return (parts[0], parts[^1]);
    }

    public async Task<bool> IsPublishedLogoAsync(string fileName, CancellationToken ct)
    {
        if (!HonoreeFileStorage.IsPublicLogoFileName(fileName))
        {
            return false;
        }

        if (HonoreeFileStorage.FallbackLogoFileNames.Contains(fileName))
        {
            return true;
        }

        var storedNames = await db.ServiceBranches
            .AsNoTracking()
            .Where(branch => branch.DeletedDate == null)
            .Select(branch => branch.LogoFileName)
            .ToListAsync(ct);

        return storedNames.Any(name =>
            string.Equals(name?.Trim(), fileName, StringComparison.OrdinalIgnoreCase) &&
            HonoreeFileStorage.IsPublicLogoFileName(name));
    }

    private async Task<List<FlagGrid>> LiveGridsAsync(CancellationToken ct)
    {
        return await db.FlagGrids
            .AsNoTracking()
            .Where(grid => grid.DeletedDate == null)
            .ToListAsync(ct);
    }

    private async Task<List<Honoree>> EligibleHonoreesAsync(CancellationToken ct)
    {
        return await db.Honorees
            .AsNoTracking()
            .Include(honoree => honoree.FlagGrid)
            .Include(honoree => honoree.ServiceBranch)
            .ThenInclude(branch => branch!.ServiceBranchCategory)
            .Where(honoree =>
                honoree.IsActive &&
                honoree.DeletedDate == null &&
                honoree.FlagGridId != null &&
                honoree.FlagGrid != null &&
                honoree.FlagGrid.DeletedDate == null)
            .ToListAsync(ct);
    }

    private static Dictionary<int, Honoree> OccupiedByGrid(IEnumerable<Honoree> honorees)
    {
        return honorees
            .Where(honoree => honoree.FlagGridId.HasValue)
            .GroupBy(honoree => honoree.FlagGridId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(honoree => honoree.LastName)
                    .ThenBy(honoree => honoree.FirstName)
                    .First());
    }

    private static PublicFlagSeatDto ToSeat(FlagGrid grid, Honoree? honoree)
    {
        var occupied = honoree is not null;
        return new PublicFlagSeatDto(
            grid.Id,
            grid.FlagGridName,
            SectionLabel(grid.FlagGridName),
            ColumnNumber(grid.FlagGridName),
            occupied ? "Occupied" : grid.Reserved ? "Reserved" : "Open",
            occupied ? honoree!.Id : null,
            occupied ? DisplayName(honoree!) : null,
            occupied ? BlankToNull(honoree!.FirstName) : null,
            occupied ? BlankToNull(honoree!.MiddleName) : null,
            occupied ? BlankToNull(honoree!.LastName) : null,
            occupied ? BlankToNull(honoree!.Suffix) : null,
            occupied ? BlankToNull(honoree!.Nickname) : null,
            occupied ? BlankToNull(honoree!.Rank) : null,
            occupied ? BlankToNull(honoree!.ServiceBranch?.ServiceBranchName) : null,
            occupied ? BlankToNull(honoree!.ServiceBranch?.ServiceBranchCategory?.ServiceBranchCategoryName) : null,
            occupied ? LogoUrl(honoree!.ServiceBranch) : null,
            occupied ? YearOrNull(honoree!.StartYear) : null,
            occupied ? YearOrNull(honoree!.EndYear) : null,
            occupied ? BlankToNull(honoree!.DatesUserEntry) : null,
            occupied ? honoree!.KIA : null,
            occupied ? BlankToNull(honoree!.Description) : null,
            occupied ? BlankToNull(honoree!.ConflictsServed) : null,
            occupied ? BlankToNull(honoree!.Awards) : null,
            occupied ? PhotoUrl(honoree!) : null);
    }

    private static PublicFlagSearchDto ToSearch(Honoree honoree)
    {
        return new PublicFlagSearchDto(
            honoree.Id,
            honoree.FlagGridId!.Value,
            honoree.FlagGrid?.FlagGridName ?? string.Empty,
            SectionLabel(honoree.FlagGrid?.FlagGridName),
            ColumnNumber(honoree.FlagGrid?.FlagGridName),
            DisplayName(honoree),
            BlankToNull(honoree.Rank),
            BlankToNull(honoree.ServiceBranch?.ServiceBranchName),
            BlankToNull(honoree.ServiceBranch?.ServiceBranchCategory?.ServiceBranchCategoryName),
            LogoUrl(honoree.ServiceBranch));
    }

    public static string SectionLabel(string? flagGridName)
    {
        var value = (flagGridName ?? string.Empty).Trim();
        var prefix = new string(value.TakeWhile(character => !char.IsDigit(character)).ToArray())
            .Trim('-', '_', ' ')
            .ToUpperInvariant();

        return string.IsNullOrWhiteSpace(prefix) ? "Other" : prefix;
    }

    public static int? ColumnNumber(string? flagGridName)
    {
        var value = (flagGridName ?? string.Empty).Trim();
        var digits = new string(value.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? parsed : null;
    }

    public static string? LogoFileName(ServiceBranch? branch)
    {
        var stored = branch?.LogoFileName?.Trim();
        if (HonoreeFileStorage.IsPublicLogoFileName(stored))
        {
            return stored;
        }

        foreach (var candidate in HonoreeFileStorage.ServiceLogoCandidates(branch?.ServiceBranchName))
        {
            if (HonoreeFileStorage.IsPublicLogoFileName(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string? LogoUrl(ServiceBranch? branch)
    {
        var fileName = LogoFileName(branch);
        return fileName is null ? null : "/api/public/flag-map/logos/" + Uri.EscapeDataString(fileName);
    }

    private static string? PhotoUrl(Honoree honoree)
    {
        return string.IsNullOrWhiteSpace(honoree.PhotoFileName) ? null : $"/api/honorees/{honoree.Id}/photo";
    }

    private static string DisplayName(Honoree honoree)
    {
        var name = string.Join(
            " ",
            new[] { honoree.FirstName, honoree.MiddleName, honoree.LastName, honoree.Suffix }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var nickname = BlankToNull(honoree.Nickname);
        if (nickname is null)
        {
            return name.Trim();
        }

        return name.EndsWith($"({nickname})", StringComparison.OrdinalIgnoreCase)
            ? name.Trim()
            : $"{name.Trim()} ({nickname})";
    }

    private static string? BlankToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? YearOrNull(int? year)
    {
        return year is null or 0 ? null : year;
    }

    public sealed record SubmitterLookupResult(string Outcome, int Count, IReadOnlyList<PublicFlagSearchDto> Flags);

    public sealed record SubmitterIdentity(string NormalizedFirst, string NormalizedLast, string Display);

    public sealed record SubmitterSuggestionResult(string Outcome, IReadOnlyList<SubmitterIdentity> Names);

    private sealed record NamedFlag(Honoree Honoree, string NormalizedFirst, string NormalizedLast, string Display);

    private sealed class SectionComparer : IComparer<string>
    {
        public static readonly SectionComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            var leftValue = left ?? string.Empty;
            var rightValue = right ?? string.Empty;
            var leftOther = leftValue.Equals("Other", StringComparison.OrdinalIgnoreCase);
            var rightOther = rightValue.Equals("Other", StringComparison.OrdinalIgnoreCase);
            if (leftOther && rightOther) return 0;
            if (leftOther) return 1;
            if (rightOther) return -1;
            if (leftValue.Length != rightValue.Length) return leftValue.Length - rightValue.Length;
            return string.Compare(leftValue, rightValue, StringComparison.OrdinalIgnoreCase);
        }
    }
}
