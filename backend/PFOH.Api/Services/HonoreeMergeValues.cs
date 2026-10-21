using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PFOH.Api.Data;

namespace PFOH.Api.Services;

/// <summary>
/// Typed values for a duplicate merge. Each flag means the administrator typed
/// that field. Text entries are already sanitized. Lookups are existing ids.
/// </summary>
public sealed class MergeCustom
{
    public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);

    public bool HasBranch { get; set; }

    public int? BranchId { get; set; }

    public int? BranchCategoryId { get; set; }

    public bool HasYears { get; set; }

    public int? StartYear { get; set; }

    public int? EndYear { get; set; }

    public string DatesUserEntry { get; set; } = string.Empty;

    public bool HasKia { get; set; }

    public bool Kia { get; set; }

    public bool HasGrid { get; set; }

    public int? FlagGridId { get; set; }

    public bool HasSponsor { get; set; }

    public int? SponsorId { get; set; }

    public bool NoPhoto { get; set; }

    public bool CombineAwards { get; set; }
}

/// <summary>
/// Whitelist, sanitizing, and the same length and format checks as the honoree
/// edit form. Photo is one record's stored file or no photo. Awards are one
/// record's list, both lists combined, or text edited with the same rules as
/// the honoree edit path. <see cref="Sanitize"/> is the text cleanup the
/// review preview copies, so the preview matches the saved value.
/// </summary>
public static class HonoreeMergeValues
{

    private static readonly HashSet<string> OptionalText = new(StringComparer.Ordinal)
    {
        "middleName",
        "suffix",
        "nickname"
    };

    private static readonly Regex YearRange = new(
        @"^(?<start>\d{1,4})\s*(?:-|–|—|to)\s*(?<end>\d{1,4}|present)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex YearOnly = new(
        @"^(?<start>\d{1,4})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? ValidateShape(
        IReadOnlyDictionary<string, int> fields,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyCollection<int> duplicateIds,
        bool combineAwards)
    {
        foreach (var key in fields.Keys.Concat(values.Keys).Distinct(StringComparer.Ordinal))
        {
            if (!DuplicateMergeFields.Keys.Contains(key))
            {
                return $"Unknown field '{key}'.";
            }
        }

        if (values.TryGetValue("photo", out var photoValue) && !string.IsNullOrEmpty(photoValue))
        {
            return "Choose which record's photo to keep, or choose no photo. A typed photo value is not accepted.";
        }

        if (!fields.TryGetValue("flagGrid", out var gridSource) || !duplicateIds.Contains(gridSource))
        {
            return "Choose the flag grid from one of the included records. That record is the survivor, even when its grid is blank.";
        }

        foreach (var key in DuplicateMergeFields.Keys)
        {
            if (key == "flagGrid")
            {
                continue;
            }

            var chosen = key switch
            {
                "photo" => fields.ContainsKey(key) || values.ContainsKey(key),
                "awards" => fields.ContainsKey(key) || values.ContainsKey(key) || combineAwards,
                _ => fields.ContainsKey(key) || values.ContainsKey(key)
            };
            if (!chosen)
            {
                return "Choose a value for every field before merging.";
            }
        }

        if (fields.Values.Any(id => !duplicateIds.Contains(id)))
        {
            return "Each kept value has to come from one of the confirmed records.";
        }

        return null;
    }

    /// <summary>
    /// The surviving record is the one whose flag-grid row was selected.
    /// A blank grid still counts. An explicit survivor id of 0 is not a choice.
    /// </summary>
    public static string? SurvivorError(int explicitSurvivorId, int gridSourceId)
    {
        if (explicitSurvivorId > 0 && explicitSurvivorId != gridSourceId)
        {
            return "The surviving record has to be the record whose flag grid you selected.";
        }

        return null;
    }

    public static string CombineAwardLists(IEnumerable<string?> lists)
    {
        var sources = lists.Select(value => value ?? string.Empty).ToList();
        var delimiter = AwardDelimiter(sources);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<string>();
        foreach (var source in sources)
        {
            foreach (var item in SplitAwardItems(source))
            {
                if (!seen.Add(item))
                {
                    continue;
                }

                items.Add(item);
            }
        }

        return string.Join(delimiter, items);
    }

    public static List<string> SplitAwardItems(string? value)
    {
        var items = new List<string>();
        if (string.IsNullOrEmpty(value))
        {
            return items;
        }

        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var line in text.Split('\n'))
        {
            foreach (var piece in line.Split([';', ','], StringSplitOptions.None))
            {
                var item = Sanitize(piece, multiline: false);
                if (item.Length > 0)
                {
                    items.Add(item);
                }
            }
        }

        return items;
    }

    private static string AwardDelimiter(IReadOnlyList<string> sources)
    {
        var hasNewline = false;
        var hasSemicolon = false;
        var hasComma = false;
        foreach (var source in sources)
        {
            var text = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (text.Contains('\n'))
            {
                hasNewline = true;
            }

            if (source.Contains(';'))
            {
                hasSemicolon = true;
            }

            if (source.Contains(','))
            {
                hasComma = true;
            }
        }

        if (hasNewline)
        {
            return "\n";
        }

        if (hasSemicolon && !hasComma)
        {
            return "; ";
        }

        if (hasComma && !hasSemicolon)
        {
            return ", ";
        }

        return "\n";
    }

    public static async Task<(MergeCustom? Custom, string? Error)> ResolveAsync(
        PfohDbContext db,
        IReadOnlyDictionary<string, int> fields,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyCollection<int> duplicateIds,
        CancellationToken ct)
    {
        var custom = new MergeCustom();
        foreach (var (key, raw) in values)
        {
            var error = await ApplyOneAsync(db, custom, key, raw, duplicateIds, ct);
            if (error is not null)
            {
                return (null, error);
            }
        }

        return (custom, null);
    }

    public static string Sanitize(string? value, bool multiline)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(value.Length);
        foreach (var character in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (character is '\n' or '\t')
            {
                buffer.Append(multiline && character == '\n' ? '\n' : ' ');
                continue;
            }

            if (char.IsControl(character))
            {
                continue;
            }

            buffer.Append(character);
        }

        var text = buffer.ToString().Trim();
        if (!multiline)
        {
            while (text.Contains("  ", StringComparison.Ordinal))
            {
                text = text.Replace("  ", " ", StringComparison.Ordinal);
            }
        }

        return text;
    }

    private static async Task<string?> ApplyOneAsync(
        PfohDbContext db,
        MergeCustom custom,
        string key,
        string? raw,
        IReadOnlyCollection<int> duplicateIds,
        CancellationToken ct)
    {
        switch (key)
        {
            case "firstName":
                return Text(custom, key, raw, multiline: false, required: true, max: 200, "First name");
            case "middleName":
                return Text(custom, key, raw, multiline: false, required: false, max: 200, "Middle name");
            case "lastName":
                return Text(custom, key, raw, multiline: false, required: true, max: 200, "Last name");
            case "suffix":
                return Text(custom, key, raw, multiline: false, required: false, max: 100, "Suffix");
            case "nickname":
                return Text(custom, key, raw, multiline: false, required: false, max: 200, "Nickname");
            case "rank":
                return Text(custom, key, raw, multiline: false, required: false, max: 200, "Rank");
            case "conflictsServed":
                return Text(custom, key, raw, multiline: true, required: false, max: null, "Conflicts served");
            case "description":
                return Text(custom, key, raw, multiline: true, required: true, max: null, "Honoree description");
            case "awards":
                return Text(custom, key, raw, multiline: true, required: false, max: null, "Awards");
            case "photo":
                if (!string.IsNullOrEmpty(Sanitize(raw, multiline: false)))
                {
                    return "Choose which record's photo to keep, or choose no photo. A typed photo value is not accepted.";
                }

                custom.NoPhoto = true;
                return null;
            case "phone":
                return Text(custom, key, raw, multiline: false, required: false, max: 50, "Phone");
            case "email":
                return Email(custom, raw);
            case "serviceBranch":
                return await BranchAsync(db, custom, raw, ct);
            case "serviceYears":
                return Years(custom, raw);
            case "kia":
                return Kia(custom, raw);
            case "flagGrid":
                return await GridAsync(db, custom, raw, duplicateIds, ct);
            case "sponsor":
                return await SponsorAsync(db, custom, raw, ct);
            default:
                return $"Unknown field '{key}'.";
        }
    }

    private static string? Text(
        MergeCustom custom,
        string key,
        string? raw,
        bool multiline,
        bool required,
        int? max,
        string label)
    {
        var text = Sanitize(raw, multiline);
        if (required && text.Length == 0)
        {
            return $"{label} is required.";
        }

        if (max is int limit && text.Length > limit)
        {
            return $"{label} must be {limit} characters or fewer.";
        }

        if (OptionalText.Contains(key) && text.Length == 0)
        {
            custom.Text[key] = string.Empty;
            return null;
        }

        custom.Text[key] = text;
        return null;
    }

    private static string? Email(MergeCustom custom, string? raw)
    {
        var text = Sanitize(raw, multiline: false);
        if (text.Length == 0)
        {
            custom.Text["email"] = string.Empty;
            return null;
        }

        if (text.Length > 255)
        {
            return "Email must be 255 characters or fewer.";
        }

        if (!new EmailAddressAttribute().IsValid(text))
        {
            return "Email is not a valid email address.";
        }

        custom.Text["email"] = text;
        return null;
    }

    private static string? Years(MergeCustom custom, string? raw)
    {
        var text = Sanitize(raw, multiline: false);
        if (text.Length > 500)
        {
            return "Service years must be 500 characters or fewer.";
        }

        custom.HasYears = true;
        custom.DatesUserEntry = text;
        custom.StartYear = null;
        custom.EndYear = null;
        if (text.Length == 0)
        {
            return null;
        }

        var range = YearRange.Match(text);
        if (range.Success && TryYear(range.Groups["start"].Value, out var start))
        {
            custom.StartYear = start;
            var endText = range.Groups["end"].Value;
            if (!endText.Equals("present", StringComparison.OrdinalIgnoreCase) &&
                TryYear(endText, out var end))
            {
                custom.EndYear = end;
            }

            return null;
        }

        var only = YearOnly.Match(text);
        if (only.Success && TryYear(only.Groups["start"].Value, out var single))
        {
            custom.StartYear = single;
        }

        return null;
    }

    private static bool TryYear(string text, out int year)
    {
        year = 0;
        return int.TryParse(text, out year) && year is >= 1 and <= 9999;
    }

    private static string? Kia(MergeCustom custom, string? raw)
    {
        var text = Sanitize(raw, multiline: false);
        var kia = text.ToLowerInvariant() switch
        {
            "yes" or "true" or "y" => true,
            "no" or "false" or "n" => false,
            _ => (bool?)null
        };
        if (kia is null)
        {
            return "Killed in action must be Yes or No.";
        }

        custom.HasKia = true;
        custom.Kia = kia.Value;
        return null;
    }

    private static async Task<string?> BranchAsync(PfohDbContext db, MergeCustom custom, string? raw, CancellationToken ct)
    {
        var text = Sanitize(raw, multiline: false);
        if (text.Length == 0)
        {
            return "Service branch is required.";
        }

        if (text.Length > 200)
        {
            return "Service branch must be 200 characters or fewer.";
        }

        var lowered = text.ToLowerInvariant();
        var matches = await db.ServiceBranches
            .AsNoTracking()
            .Where(branch => branch.ServiceBranchName.ToLower() == lowered)
            .Select(branch => new { branch.Id, branch.ServiceBranchCategoryId })
            .ToListAsync(ct);
        if (matches.Count != 1)
        {
            return matches.Count == 0
                ? "Type a service branch that already exists, or choose one of the records."
                : "More than one service branch has that name. Choose the branch from one of the records.";
        }

        custom.HasBranch = true;
        custom.BranchId = matches[0].Id;
        custom.BranchCategoryId = matches[0].ServiceBranchCategoryId;
        return null;
    }

    private static async Task<string?> GridAsync(
        PfohDbContext db,
        MergeCustom custom,
        string? raw,
        IReadOnlyCollection<int> duplicateIds,
        CancellationToken ct)
    {
        var text = Sanitize(raw, multiline: false);
        custom.HasGrid = true;
        if (text.Length == 0 || text.Equals("No flag grid", StringComparison.OrdinalIgnoreCase))
        {
            custom.FlagGridId = null;
            return null;
        }

        if (text.Length > 100)
        {
            return "Flag grid must be 100 characters or fewer.";
        }

        var lowered = text.ToLowerInvariant();
        var matches = await db.FlagGrids
            .AsNoTracking()
            .Where(grid => grid.FlagGridName.ToLower() == lowered)
            .Select(grid => new { grid.Id, grid.HonoreeId, grid.Reserved, grid.DeletedDate })
            .ToListAsync(ct);
        // Deleted grids are not assignable, matching flag-position assignment.
        var live = matches.Where(grid => grid.DeletedDate == null).ToList();
        if (live.Count == 0)
        {
            return matches.Count > 0
                ? "That flag grid was deleted and cannot be assigned."
                : "Type a flag grid location that already exists, or choose one of the records.";
        }

        if (live.Count != 1)
        {
            return "More than one flag grid has that location. Choose the flag grid from one of the records.";
        }

        var chosen = live[0];
        if (chosen.Reserved)
        {
            return "This flag position is reserved and cannot be assigned.";
        }

        var holders = await db.Honorees
            .AsNoTracking()
            .Where(honoree => honoree.FlagGridId == chosen.Id)
            .Select(honoree => honoree.Id)
            .ToListAsync(ct);
        var pointedOutside = chosen.HonoreeId is int pointed && !duplicateIds.Contains(pointed);
        if (pointedOutside || holders.Any(id => !duplicateIds.Contains(id)))
        {
            return "That flag grid is already assigned to a record that is not part of this merge.";
        }

        custom.FlagGridId = chosen.Id;
        return null;
    }

    private static async Task<string?> SponsorAsync(PfohDbContext db, MergeCustom custom, string? raw, CancellationToken ct)
    {
        var text = Sanitize(raw, multiline: false);
        custom.HasSponsor = true;
        if (text.Length == 0)
        {
            custom.SponsorId = null;
            return null;
        }

        if (text.Length > 400)
        {
            return "Sponsor must be 400 characters or fewer.";
        }

        var sponsors = await db.Sponsors
            .AsNoTracking()
            .Where(sponsor => sponsor.IsActive && sponsor.DeletedDate == null)
            .Select(sponsor => new { sponsor.Id, sponsor.FirstName, sponsor.MiddleName, sponsor.LastName, sponsor.Suffix })
            .ToListAsync(ct);
        var matches = sponsors
            .Where(sponsor => string.Equals(PersonName(sponsor.FirstName, sponsor.MiddleName, sponsor.LastName, sponsor.Suffix), text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
        {
            return matches.Count == 0
                ? "Type a sponsor name that already exists, or choose one of the records."
                : "More than one sponsor has that name. Choose the sponsor from one of the records.";
        }

        custom.SponsorId = matches[0].Id;
        return null;
    }

    private static string PersonName(string? first, string? middle, string? last, string? suffix) =>
        string.Join(
            " ",
            new[] { first, middle, last, suffix }.Where(part => !string.IsNullOrWhiteSpace(part)));
}
