using Xunit;

namespace PFOH.Snapshot.Tests;

public class SnapshotSecurityArtifactTests
{
    [Fact]
    public void Bicep_keeps_shared_key_off_and_roles_on_the_container()
    {
        var bicep = File.ReadAllText(RepoFile("infra", "snapshot.bicep"));
        Assert.Contains("allowSharedKeyAccess: false", bicep, StringComparison.Ordinal);
        Assert.Contains("allowBlobPublicAccess: false", bicep, StringComparison.Ordinal);
        Assert.Contains("supportsHttpsTrafficOnly: true", bicep, StringComparison.Ordinal);
        Assert.Contains("minimumTlsVersion: 'TLS1_2'", bicep, StringComparison.Ordinal);
        Assert.Contains("publicAccess: 'None'", bicep, StringComparison.Ordinal);
        Assert.Contains("snapshotRetentionDays", bicep, StringComparison.Ordinal);
        Assert.Contains("daysAfterModificationGreaterThan: snapshotRetentionDays", bicep, StringComparison.Ordinal);
        Assert.Contains("scope: reportsContainer", bicep, StringComparison.Ordinal);
        Assert.Contains("defaultToOAuthAuthentication: true", bicep, StringComparison.Ordinal);
        Assert.Contains("allowCrossTenantReplication: false", bicep, StringComparison.Ordinal);
        Assert.Contains("FUNCTIONS_EXTENSION_VERSION", bicep, StringComparison.Ordinal);
        Assert.Contains("FUNCTIONS_WORKER_RUNTIME", bicep, StringComparison.Ordinal);
        Assert.Contains("value: 'dotnet-isolated'", bicep, StringComparison.Ordinal);
        Assert.Contains("basicPublishingCredentialsPolicies", bicep, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(bicep, "name: 'scm'") + CountOf(bicep, "name: 'ftp'"));
        Assert.Contains("allow: false", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("listKeys", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sas", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountKey", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scope: resourceGroup()", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void Function_is_timer_only()
    {
        var source = File.ReadAllText(RepoFile("backend", "PFOH.Snapshot.Function", "SnapshotNightly.cs"));
        Assert.Contains("TimerTrigger(\"0 45 1 * * *\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpTrigger", source, StringComparison.Ordinal);
        var program = File.ReadAllText(RepoFile("backend", "PFOH.Snapshot.Function", "Program.cs"));
        Assert.DoesNotContain("HttpTrigger", program, StringComparison.Ordinal);
        Assert.Contains("api://AzureADTokenExchange", File.ReadAllText(RepoFile("backend", "PFOH.Snapshot", "GraphDirectorySource.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_workflow_uses_oidc_and_has_no_secret_material()
    {
        var workflow = File.ReadAllText(RepoFile(".github", "workflows", "snapshot-deploy.yml"));
        Assert.Contains("id-token: write", workflow, StringComparison.Ordinal);
        Assert.Contains("azure/login@v2", workflow, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch", workflow, StringComparison.Ordinal);
        Assert.Contains("github.ref == 'refs/heads/main'", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: pfoh-snapshot", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role assignment create", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runbook_documents_the_certificate_reader_and_not_a_sas()
    {
        var runbook = File.ReadAllText(RepoFile("docs", "snapshot-job.md"));
        Assert.Contains("GRANT SELECT ON snapshot.", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("db_datareader", runbook, StringComparison.Ordinal);
        Assert.Contains("install -d -m 0700", runbook, StringComparison.Ordinal);
        Assert.Contains("chmod 600", runbook, StringComparison.Ordinal);
        Assert.Contains("Cloud Shell", runbook, StringComparison.Ordinal);
        Assert.Contains("pfoh-snapshot", runbook, StringComparison.Ordinal);
        Assert.Contains("api://AzureADTokenExchange", runbook, StringComparison.Ordinal);
        Assert.Contains("AuditLog.Read.All", runbook, StringComparison.Ordinal);
        Assert.Contains("User.Read.All", runbook, StringComparison.Ordinal);
        Assert.Contains("77f89427-b4a7-43ed-92fc-3eb5bd63d0f1", runbook, StringComparison.Ordinal);
        Assert.Contains("Storage Blob Data Reader", runbook, StringComparison.Ordinal);
        Assert.Contains("30 days", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("sp=", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", runbook, StringComparison.Ordinal);
        Assert.Contains("/admin/functions/SnapshotNightly", runbook, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}
