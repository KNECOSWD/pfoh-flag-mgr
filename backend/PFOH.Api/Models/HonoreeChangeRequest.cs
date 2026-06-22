namespace PFOH.Api.Models;

public class HonoreeChangeRequest
{
    public int Id { get; set; }

    public int FlagClaimId { get; set; }
    public int FlagGridId { get; set; }
    public int? HonoreeId { get; set; }

    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = string.Empty;
    public string? Suffix { get; set; }
    public string? Nickname { get; set; }

    public string? Rank { get; set; }
    public int? ServiceBranchId { get; set; }
    public int? ServiceBranchCategoryId { get; set; }
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
    public string? DatesUserEntry { get; set; }
    public string? ConflictsServed { get; set; }
    public string? Awards { get; set; }
    public string? Description { get; set; }
    public bool KIA { get; set; }

    public string? PhotoFileName { get; set; }

    public string? SubmitterPhoneNumber { get; set; }
    public string? SubmitterEmailAddress { get; set; }

    // Draft, Submitted, Approved, Rejected.
    public string RequestStatus { get; set; } = "Draft";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedUtc { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public string? ReviewedBy { get; set; }
    public string? ReviewNotes { get; set; }

    public bool RequiresCardReprint { get; set; }
    public DateTime? CardPrintedUtc { get; set; }
    public Guid? CardPrintBatchId { get; set; }

    public FlagClaim? FlagClaim { get; set; }
    public FlagGrid? FlagGrid { get; set; }
    public Honoree? Honoree { get; set; }
    public ServiceBranch? ServiceBranch { get; set; }
    public ServiceBranchCategory? ServiceBranchCategory { get; set; }
}
