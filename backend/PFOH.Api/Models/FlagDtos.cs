using System.ComponentModel.DataAnnotations;

namespace PFOH.Api.Models;

public record FlagDto(
    int Id,
    string HonoreeName,
    string? ServiceBranch,
    string? RankOrTitle,
    string? FlagNumber,
    string? GridLocation,
    string? TributeText,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public class UpsertFlagRequest
{
    [Required, MaxLength(200)]
    public string HonoreeName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? ServiceBranch { get; set; }

    [MaxLength(100)]
    public string? RankOrTitle { get; set; }

    [MaxLength(50)]
    public string? FlagNumber { get; set; }

    [MaxLength(50)]
    public string? GridLocation { get; set; }

    [MaxLength(2000)]
    public string? TributeText { get; set; }

    [MaxLength(30)]
    public string Status { get; set; } = "Draft";
}

public static class FlagMapper
{
    public static FlagDto ToDto(this FlagRecord f) => new(
        f.Id,
        f.HonoreeName,
        f.ServiceBranch,
        f.RankOrTitle,
        f.FlagNumber,
        f.GridLocation,
        f.TributeText,
        f.Status,
        f.CreatedUtc,
        f.UpdatedUtc);
}
