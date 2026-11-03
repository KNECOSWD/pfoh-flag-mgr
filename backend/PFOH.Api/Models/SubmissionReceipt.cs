namespace PFOH.Api.Models;

/// <summary>
/// Unguessable handle for the submitter's own pending-review page.
/// The integer change request id is never used as the public reference.
/// </summary>
public class SubmissionReceipt
{
    public int Id { get; set; }

    public int ChangeRequestId { get; set; }

    public Guid PublicToken { get; set; }

    /// <summary>
    /// Short code shown to the submitter, such as PFOH-7K3Q9X.
    /// It does not open the page. Only <see cref="PublicToken"/> does.
    /// </summary>
    public string PublicCode { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public HonoreeChangeRequest? ChangeRequest { get; set; }
}
