namespace PFOH.Api.Models;

/// <summary>
/// Ties a used reactivation link to the honoree change request in the admin review queue.
/// </summary>
public class HonoreeReactivationRequest
{
    public int Id { get; set; }

    public int HonoreeId { get; set; }

    public int ChangeRequestId { get; set; }

    public int TokenId { get; set; }

    public string? RequesterNote { get; set; }

    public DateTime LinkUsedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public Honoree? Honoree { get; set; }

    public HonoreeChangeRequest? ChangeRequest { get; set; }

    public HonoreeReactivationToken? Token { get; set; }
}
