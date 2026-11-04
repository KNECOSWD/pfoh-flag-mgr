using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PFOH.Api.Data;
using PFOH.Api.Models;

namespace PFOH.Api.Services;

/// <summary>
/// Header authentication for a local screenshot run only.
/// Registered when the environment is Development and PFOH_LOCAL_SEED is 1.
/// </summary>
public sealed class LocalSeedAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalSeed";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var environment = Context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        if (!environment.IsDevelopment()
            || !string.Equals(Context.RequestServices.GetRequiredService<IConfiguration>()["PFOH_LOCAL_SEED"], "1", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!Request.Headers.TryGetValue("X-Test-Oid", out var oidValues) || string.IsNullOrWhiteSpace(oidValues.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var oid = oidValues.ToString();
        var claims = new List<Claim>
        {
            new("oid", oid),
            new(ClaimTypes.NameIdentifier, oid)
        };
        if (Request.Headers.TryGetValue("X-Test-Email", out var email) && !string.IsNullOrWhiteSpace(email.ToString()))
        {
            claims.Add(new Claim("preferred_username", email.ToString()));
            claims.Add(new Claim(ClaimTypes.Email, email.ToString()));
        }

        if (Request.Headers.TryGetValue("X-Test-Name", out var name) && !string.IsNullOrWhiteSpace(name.ToString()))
        {
            claims.Add(new Claim("name", name.ToString()));
        }

        if (Request.Headers.TryGetValue("X-Test-Roles", out var roles))
        {
            foreach (var role in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class LocalSeedData
{
    public static async Task SeedAsync(PfohDbContext db, CancellationToken ct = default)
    {
        if (await db.Honorees.AnyAsync(ct) || await db.HonoreeChangeRequests.AnyAsync(ct))
        {
            return;
        }

        var now = DateTime.UtcNow;
        db.SponsorCategories.Add(new SponsorCategory
        {
            Id = 1,
            SponsorCategoryName = "Family",
            Description = "Family",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.ServiceBranchCategories.Add(new ServiceBranchCategory
        {
            Id = 1,
            ServiceBranchCategoryName = "Armed forces",
            Description = "Armed forces",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        });
        db.ServiceBranches.Add(Branch(1, "Army", now));
        db.ServiceBranches.Add(Branch(2, "Navy", now));
        var grid = new FlagGrid
        {
            FlagGridName = "AA-10",
            Notes = "Field spot",
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.FlagGrids.Add(grid);
        var ada = new Honoree
        {
            FirstName = "Ada",
            LastName = "Mann",
            Rank = "Captain",
            ServiceBranchId = 1,
            ServiceBranchCategoryId = 1,
            Description = "A private note that the match preview must not show.",
            Salutation = string.Empty,
            NameUserEntry = string.Empty,
            PhotoFileName = string.Empty,
            PhoneNumber = "2145550199",
            EmailAddress = "secret-approved@example.test",
            Awards = string.Empty,
            ConflictsServed = string.Empty,
            DatesUserEntry = string.Empty,
            IsActive = true,
            FlagGrid = grid,
            CreatedBy = "seed",
            ModifiedBy = "seed",
            CreatedDate = now,
            ModifiedDate = now
        };
        db.Honorees.Add(ada);
        db.FlagClaims.Add(Pending("Ben", "Brooks", "Petty Officer", 2, "other-family@example.test", "Submitted", now, "other-owner"));
        db.FlagClaims.Add(Pending("Ada", "Mann", "Captain", 1, "pending-ada@example.test", "Submitted", now, "ada-pending-owner"));
        db.FlagClaims.Add(Pending("Quinn", "Hale", "Sergeant", 1, "queue@example.test", "Submitted", now, "queue-owner"));
        db.FlagClaims.Add(Pending("Drew", "Older", "Corporal", 1, "family@example.test", "Superseded", now.AddMinutes(-30), "family-owner"));
        await db.SaveChangesAsync(ct);
        grid.HonoreeId = ada.Id;
        await db.SaveChangesAsync(ct);
    }

    private static ServiceBranch Branch(int id, string name, DateTime now) => new()
    {
        Id = id,
        ServiceBranchCategoryId = 1,
        ServiceBranchName = name,
        Description = name,
        LogoFileName = string.Empty,
        CreatedBy = "seed",
        ModifiedBy = "seed",
        CreatedDate = now,
        ModifiedDate = now
    };

    private static FlagClaim Pending(
        string first,
        string last,
        string rank,
        int branchId,
        string email,
        string status,
        DateTime submitted,
        string owner)
    {
        var claim = new FlagClaim
        {
            ExternalUserObjectId = owner,
            ExternalUserEmail = email,
            ExternalUserName = "Example Family",
            ClaimStatus = "Submitted",
            CreatedUtc = submitted,
            SubmittedUtc = submitted
        };
        claim.HonoreeChangeRequests.Add(new HonoreeChangeRequest
        {
            FirstName = first,
            LastName = last,
            Rank = rank,
            ServiceBranchId = branchId,
            ServiceBranchCategoryId = 1,
            Description = "A pending tribute for the review queue.",
            RequestStatus = status,
            SubmitterEmailAddress = email,
            SubmitterPhoneNumber = "2145550144",
            CreatedUtc = submitted,
            SubmittedUtc = submitted
        });
        return claim;
    }
}
