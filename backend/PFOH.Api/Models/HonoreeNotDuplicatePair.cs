namespace PFOH.Api.Models;

/// <summary>
/// An administrator decision that two honorees are not duplicates of each other.
/// The pair is stored once, with the lower id first.
/// </summary>
public class HonoreeNotDuplicatePair
{
    public int Id { get; set; }

    public int LowHonoreeId { get; set; }

    public int HighHonoreeId { get; set; }

    public string MarkedBy { get; set; } = string.Empty;

    public DateTime MarkedUtc { get; set; }

    public Honoree? LowHonoree { get; set; }

    public Honoree? HighHonoree { get; set; }
}
