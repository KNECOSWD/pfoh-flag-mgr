namespace PFOH.Snapshot;

public static class DirectorySnapshotBuilder
{
    public static DirectorySnapshotDocument Build(DateTimeOffset pulledAt, IReadOnlyList<DirectoryUserRecord> users)
    {
        var documents = new List<DirectoryUserDocument>(users.Count);
        var activity = new List<SignInActivityDocument>(users.Count);
        foreach (var user in users)
        {
            documents.Add(new DirectoryUserDocument
            {
                Id = user.Id,
                DisplayName = user.DisplayName,
                Mail = user.Mail,
                OtherMails = user.OtherMails,
                Identities = user.Identities
                    .Select(identity => new IdentityDocument
                    {
                        SignInType = identity.SignInType,
                        IssuerAssignedId = identity.IssuerAssignedId
                    })
                    .ToArray(),
                UserPrincipalName = user.UserPrincipalName,
                CreatedDateTime = user.CreatedDateTime,
                AccountEnabled = user.AccountEnabled,
                Phone = user.Phone,
                Organization = user.Organization
            });
            activity.Add(new SignInActivityDocument
            {
                Id = user.Id,
                SignInActivity = user.SignInActivity is null
                    ? null
                    : new SignInActivityValueDocument
                    {
                        LastSignInDateTime = user.SignInActivity.LastSignInDateTime,
                        LastSuccessfulSignInDateTime = user.SignInActivity.LastSuccessfulSignInDateTime
                    }
            });
        }

        return new DirectorySnapshotDocument
        {
            PulledAt = DbDumpBuilder.Format(pulledAt),
            Users = documents,
            SignInActivity = activity
        };
    }
}
