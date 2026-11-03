using Microsoft.Extensions.Logging;

namespace PFOH.Api.Services;

/// <summary>
/// Records a submission confirmation. This app has no outbound mail provider,
/// so the message is written to the log and is not handed to a network client.
/// Tests replace <see cref="ISubmissionMailer"/> with a recording fake.
/// </summary>
public sealed class LoggingSubmissionMailer(ILogger<LoggingSubmissionMailer> logger) : ISubmissionMailer
{
    public Task SendConfirmationAsync(SubmissionConfirmation message, CancellationToken ct)
    {
        logger.LogInformation(
            "Submission confirmation recorded for reference {Reference}. No outbound mail provider is configured, so nothing was sent.",
            message.Reference);
        return Task.CompletedTask;
    }
}
