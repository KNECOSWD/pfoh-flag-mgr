using PFOH.Api.Dtos;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

public static class ReviewSubmissionDiff
{
    public const string NewSubmission = "New";
    public const string Change = "Change";

    public static string Kind(int? honoreeId) => honoreeId is > 0 ? Change : NewSubmission;

    public static ReviewSnapshot FromHonoree(Honoree honoree) => new(
        honoree.Id,
        honoree.FirstName,
        honoree.MiddleName,
        honoree.LastName,
        honoree.Suffix,
        honoree.Nickname,
        honoree.Rank,
        honoree.ServiceBranch?.ServiceBranchName,
        honoree.StartYear,
        honoree.EndYear,
        honoree.ConflictsServed,
        honoree.Awards,
        honoree.Description,
        honoree.KIA,
        honoree.PhotoFileName);

    public static IReadOnlyList<ReviewFieldChangeDto> Compare(ReviewSnapshot proposed, ReviewSnapshot? current)
    {
        if (current is null || Kind(proposed.HonoreeId) != Change)
        {
            return Array.Empty<ReviewFieldChangeDto>();
        }

        var changes = new List<ReviewFieldChangeDto>();
        Add(changes, "First name", current.FirstName, proposed.FirstName);
        Add(changes, "Middle name", current.MiddleName, proposed.MiddleName);
        Add(changes, "Last name", current.LastName, proposed.LastName);
        Add(changes, "Suffix", current.Suffix, proposed.Suffix);
        Add(changes, "Nickname", current.Nickname, proposed.Nickname);
        Add(changes, "Rank", current.Rank, proposed.Rank);
        Add(changes, "Service branch", current.ServiceBranchName, proposed.ServiceBranchName);
        Add(changes, "Start year", Year(current.StartYear), Year(proposed.StartYear));
        Add(changes, "End year", Year(current.EndYear), Year(proposed.EndYear));
        Add(changes, "Conflicts served", current.ConflictsServed, proposed.ConflictsServed);
        Add(changes, "Awards", current.Awards, proposed.Awards);
        Add(changes, "Description", current.Description, proposed.Description);
        Add(
            changes,
            "Killed in action / Gold Star recognition",
            current.Kia ? "Yes" : "No",
            proposed.Kia ? "Yes" : "No");
        AddPhoto(changes, current.PhotoFileName, proposed.PhotoFileName);
        return changes;
    }

    private static void Add(List<ReviewFieldChangeDto> changes, string label, string? previous, string? proposed)
    {
        var oldValue = Normalize(previous);
        var newValue = Normalize(proposed);
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add(new ReviewFieldChangeDto(label, Display(oldValue), Display(newValue)));
    }

    private static void AddPhoto(List<ReviewFieldChangeDto> changes, string? current, string? proposed)
    {
        var currentName = Normalize(current);
        var proposedName = Normalize(proposed);
        if (currentName is null && proposedName is null)
        {
            return;
        }

        if (currentName is not null &&
            proposedName is not null &&
            string.Equals(currentName, proposedName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        changes.Add(new ReviewFieldChangeDto(
            "Photo",
            currentName is null ? "(blank)" : "Current photo",
            proposedName is null ? "(blank)" : "New photo"));
    }

    private static string? Year(int? year) => year?.ToString();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Display(string? value) => value ?? "(blank)";
}

public sealed record ReviewSnapshot(
    int? HonoreeId,
    string? FirstName,
    string? MiddleName,
    string? LastName,
    string? Suffix,
    string? Nickname,
    string? Rank,
    string? ServiceBranchName,
    int? StartYear,
    int? EndYear,
    string? ConflictsServed,
    string? Awards,
    string? Description,
    bool Kia,
    string? PhotoFileName);
