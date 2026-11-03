namespace PFOH.Api.Services;

public interface ISubmissionMailer
{
    Task SendConfirmationAsync(SubmissionConfirmation message, CancellationToken ct);
}

public sealed record SubmissionConfirmation(
    string ToEmail,
    string Subject,
    string Body,
    string Reference);
