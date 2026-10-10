using System.Security.Claims;
using System.Text.RegularExpressions;
using PFOH.Api.Extensions;
using PFOH.Api.Services;
using Xunit;

namespace PFOH.Api.Tests;

public class E164PhoneTests
{
    [Theory]
    [InlineData("+19725550100", "+19725550100")]
    [InlineData("+1 (972) 555-0100", "+19725550100")]
    [InlineData("+1-972-555-0100", "+19725550100")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    [InlineData("+1 2145551234", "+12145551234")]
    public void Accepts_an_e164_number(string input, string expected)
    {
        Assert.True(E164Phone.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9725550100")]
    [InlineData("+012345678")]
    [InlineData("+1")]
    [InlineData("++19725550100")]
    [InlineData("not-a-phone")]
    public void Rejects_a_value_that_is_not_e164(string? input)
    {
        Assert.False(E164Phone.TryNormalize(input, out _));
    }
}

public class EntraProfileClaimTests
{
    [Fact]
    public void Phone_and_organization_claims_are_optional()
    {
        var user = Principal();
        Assert.Null(user.GetPhoneNumber());
        Assert.Null(user.GetOrganization());
    }

    [Fact]
    public void Reads_phone_and_the_organization_extension_claim()
    {
        var user = Principal(
            new Claim("phone_number", " +19725550100 "),
            new Claim("extension_0123456789abcdef0123456789abcdef_Organization", " Honor Guard "));

        Assert.Equal("+19725550100", user.GetPhoneNumber());
        Assert.Equal("Honor Guard", user.GetOrganization());
    }

    [Fact]
    public void Reads_a_custom_phone_extension_claim()
    {
        var user = Principal(new Claim("extension_0123456789abcdef0123456789abcdef_Phone", "+442079460958"));
        Assert.Equal("+442079460958", user.GetPhoneNumber());
    }

    [Fact]
    public void Configured_extensions_app_id_selects_only_that_apps_claims()
    {
        const string appId = "11111111-2222-3333-4444-555555555555";
        var user = Principal(
            new Claim("extension_99999999999999999999999999999999_Phone", "+19995550100"),
            new Claim("extension_11111111222233334444555555555555_Phone", "+1 2145551234"),
            new Claim("extension_99999999999999999999999999999999_Organization", "Other"),
            new Claim("extension_11111111222233334444555555555555_Organization", "Plano"));

        Assert.Equal("+1 2145551234", user.GetPhoneNumber(appId));
        Assert.Equal("Plano", user.GetOrganization(appId));
        Assert.Null(user.GetOrganization("not-a-guid"));
        Assert.Null(Principal().GetPhoneNumber(appId));
    }

    [Fact]
    public void Extension_phone_wins_over_a_standard_phone_claim()
    {
        var user = Principal(
            new Claim("phone_number", "+19995550100"),
            new Claim("extension_0123456789abcdef0123456789abcdef_Phone", "+12145551234"));

        Assert.Equal("+12145551234", user.GetPhoneNumber());
    }

    private static ClaimsPrincipal Principal(params Claim[] extra)
    {
        var claims = new List<Claim> { new("oid", "user-1") };
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}

public class UserProfileSchemaTests
{
    [Fact]
    public void Profile_sql_adds_phone_and_organization_without_city_or_state()
    {
        Assert.Contains("[Phone] nvarchar(16)", UserProfileSchema.CreateSql, StringComparison.Ordinal);
        Assert.Contains("[Organization] nvarchar(200)", UserProfileSchema.CreateSql, StringComparison.Ordinal);
        Assert.Contains("COL_LENGTH(N'[dbo].[UserProfiles]', N'Phone')", UserProfileSchema.AddPhoneAndOrganizationSql, StringComparison.Ordinal);
        Assert.Contains("COL_LENGTH(N'[dbo].[UserProfiles]', N'Organization')", UserProfileSchema.AddPhoneAndOrganizationSql, StringComparison.Ordinal);
        Assert.DoesNotContain("City", UserProfileSchema.CreateSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("State", UserProfileSchema.CreateSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AddUserProfilePhoneAndOrganization", typeof(PFOH.Api.Data.Migrations.AddUserProfilePhoneAndOrganization).Name, StringComparison.Ordinal);
    }
}

public class HonoreeCopyTests
{
    [Fact]
    public void Frontend_user_facing_copy_does_not_say_veteran()
    {
        var root = FindRepoRoot();
        var files = new List<string> { Path.Combine(root, "frontend", "index.html") };
        var src = Path.Combine(root, "frontend", "src");
        files.AddRange(Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)));

        var publicDir = Path.Combine(root, "frontend", "public");
        if (Directory.Exists(publicDir))
        {
            files.AddRange(Directory.EnumerateFiles(publicDir, "*.*", SearchOption.AllDirectories)
                .Where(path =>
                    path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)));
        }

        var failures = new List<string>();
        var aliasCount = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var aliasHits = Regex.Matches(line, "submit-veteran", RegexOptions.CultureInvariant).Count;
                if (aliasHits > 0)
                {
                    if (!string.Equals(relative, "frontend/src/App.tsx", StringComparison.Ordinal))
                    {
                        failures.Add($"{relative}:{index + 1}: the legacy alias is only allowed in App.tsx");
                    }

                    aliasCount += aliasHits;
                    line = line.Replace("submit-veteran", "", StringComparison.Ordinal);
                }

                if (line.Contains("veteran", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{relative}:{index + 1}: {lines[index].Trim()}");
                }
            }
        }

        if (aliasCount != 1)
        {
            failures.Add($"Expected the legacy alias submit-veteran once in frontend/src/App.tsx, found {aliasCount}.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "src", "App.tsx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root from the test output directory.");
    }
}
