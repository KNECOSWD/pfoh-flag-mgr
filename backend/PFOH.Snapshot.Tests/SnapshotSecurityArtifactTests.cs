using System.Diagnostics;
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
        Assert.Contains("Snapshot__MonthlyFromUtcDate", bicep, StringComparison.Ordinal);
        Assert.Contains("'2026-12-01'", bicep, StringComparison.Ordinal);
        Assert.Contains("value: 'dotnet-isolated'", bicep, StringComparison.Ordinal);
        Assert.Contains("basicPublishingCredentialsPolicies", bicep, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(bicep, "name: 'scm'") + CountOf(bicep, "name: 'ftp'"));
        Assert.Contains("allow: false", bicep, StringComparison.Ordinal);
        Assert.Contains("enableRbacAuthorization: true", bicep, StringComparison.Ordinal);
        Assert.Contains("enableSoftDelete: true", bicep, StringComparison.Ordinal);
        Assert.Contains("enablePurgeProtection: true", bicep, StringComparison.Ordinal);
        Assert.Contains("name: 'standard'", bicep, StringComparison.Ordinal);
        Assert.Contains("publicNetworkAccess: 'Enabled'", bicep, StringComparison.Ordinal);
        Assert.Contains("4633458b-17de-408a-b874-0445c86b69e6", bicep, StringComparison.Ordinal);
        Assert.Contains("scope: vault", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("a4417e6f-fecd-4de8-b567-7b0420556985", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("listKeys", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sas", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountKey", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scope: resourceGroup()", bicep, StringComparison.Ordinal);
        Assert.Contains("param location string", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("resourceGroup().location", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void Function_is_timer_only()
    {
        var source = File.ReadAllText(RepoFile("backend", "PFOH.Snapshot.Function", "SnapshotNightly.cs"));
        Assert.Contains("TimerTrigger(\"0 45 1 * * *\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpTrigger", source, StringComparison.Ordinal);
        var program = File.ReadAllText(RepoFile("backend", "PFOH.Snapshot.Function", "Program.cs"));
        Assert.DoesNotContain("HttpTrigger", program, StringComparison.Ordinal);
        var graph = File.ReadAllText(RepoFile("backend", "PFOH.Snapshot", "GraphDirectorySource.cs"));
        Assert.Contains("https://graph.microsoft.com/.default", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("AzureADTokenExchange", graph, StringComparison.Ordinal);
        Assert.Contains("ClientCertificateCredential", File.ReadAllText(RepoFile("backend", "PFOH.Snapshot", "GraphCertificateCredential.cs")), StringComparison.Ordinal);
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
        Assert.Contains("PLANO_FLAGS_OF_HONOR", runbook, StringComparison.Ordinal);
        Assert.Contains("location=canadacentral", runbook, StringComparison.Ordinal);
        Assert.Contains("sqlServerName=pfoh", runbook, StringComparison.Ordinal);
        Assert.Contains("sqlDatabaseName=pfoh", runbook, StringComparison.Ordinal);
        Assert.Contains("P1v3", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("rg-pfoh-flags-prod", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("sqldb-pfoh-flags-prod", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("B1", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("AzureADTokenExchange", runbook, StringComparison.Ordinal);
        Assert.Contains("az keyvault certificate create", runbook, StringComparison.Ordinal);
        Assert.Contains("validityInMonths", runbook, StringComparison.Ordinal);
        Assert.Contains("daysBeforeExpiry", runbook, StringComparison.Ordinal);
        Assert.Contains("EmailContacts", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoRenew", runbook, StringComparison.Ordinal);
        Assert.Contains("repo:KNECOSWD/pfoh-flag-mgr:environment:pfoh-snapshot", runbook, StringComparison.Ordinal);
        Assert.Contains("Matthew", runbook, StringComparison.Ordinal);
        Assert.Contains("outside this repository", runbook, StringComparison.Ordinal);
        Assert.Contains("az ad app credential reset", runbook, StringComparison.Ordinal);
        Assert.Contains("az ad app credential list", runbook, StringComparison.Ordinal);
        Assert.Contains("Upload-first is not possible", runbook, StringComparison.Ordinal);
        Assert.Contains("8:45 PM COT", runbook, StringComparison.Ordinal);
        Assert.Contains("token-test procedure", runbook, StringComparison.Ordinal);
        Assert.Contains("publicNetworkAccess", runbook, StringComparison.Ordinal);
        Assert.Contains("AuditLog.Read.All", runbook, StringComparison.Ordinal);
        Assert.Contains("User.Read.All", runbook, StringComparison.Ordinal);
        Assert.Contains("77f89427-b4a7-43ed-92fc-3eb5bd63d0f1", runbook, StringComparison.Ordinal);
        Assert.Contains("Storage Blob Data Reader", runbook, StringComparison.Ordinal);
        Assert.Contains("30 days", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("sp=", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", runbook, StringComparison.Ordinal);
        Assert.Contains("/admin/functions/SnapshotNightly", runbook, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_refuses_production_names_without_both_flags()
    {
        var script = File.ReadAllText(RepoFile("infra", "azure-cli-setup.sh"));
        var allow = script.IndexOf("PFOH_ALLOW_PROD_SETUP", StringComparison.Ordinal);
        var confirm = script.IndexOf("PFOH_CONFIRM_SUBSCRIPTION", StringComparison.Ordinal);
        var create = script.IndexOf("az group create", StringComparison.Ordinal);
        var webappShow = script.IndexOf("az webapp show", StringComparison.Ordinal);
        var sqlShow = script.IndexOf("az sql server show", StringComparison.Ordinal);
        var webappCreate = script.IndexOf("az webapp create", StringComparison.Ordinal);
        var sqlCreate = script.IndexOf("az sql server create", StringComparison.Ordinal);
        var firewall = script.IndexOf("0.0.0.0", StringComparison.Ordinal);
        var connection = script.IndexOf("az webapp config connection-string set", StringComparison.Ordinal);
        Assert.True(allow >= 0 && confirm >= 0 && allow < create && confirm < create);
        Assert.True(webappShow > confirm && sqlShow > confirm);
        Assert.True(webappCreate > webappShow && sqlCreate > sqlShow);
        Assert.True(firewall > sqlShow && connection > sqlShow);
        Assert.Contains("Refusing to run", script, StringComparison.Ordinal);
        Assert.Contains("PLANO_FLAGS_OF_HONOR", script, StringComparison.Ordinal);
        Assert.Contains("asp-pfoh-flags-prod", script, StringComparison.Ordinal);
        Assert.Contains("pfoh-flag-manager-app", script, StringComparison.Ordinal);
        Assert.Contains("*dev*", script, StringComparison.Ordinal);
        Assert.Contains("*test*", script, StringComparison.Ordinal);
        Assert.Contains("*sandbox*", script, StringComparison.Ordinal);
        Assert.Contains("az account show", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_script_refuses_prod_unless_both_flags_match_and_resources_are_new()
    {
        var source = File.ReadAllText(RepoFile("infra", "azure-cli-setup.sh"));
        var directory = Path.Combine(Path.GetTempPath(), "pfoh-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = Path.Combine(directory, "azure-cli-setup.sh");
            var stub = Path.Combine(directory, "az");
            var log = Path.Combine(directory, "az.log");
            File.WriteAllText(stub, """
                #!/bin/bash
                printf '%s\n' "$*" >> "$AZ_LOG"
                if [[ "$1" == "account" && "$2" == "show" ]]; then
                  printf '%s\n' "${AZ_ACTIVE_ID}"
                  exit 0
                fi
                if [[ "$1" == "webapp" && "$2" == "show" && "${AZ_WEBAPP_EXISTS:-}" == "yes" ]]; then
                  exit 0
                fi
                if [[ "$1" == "sql" && "$2" == "server" && "$3" == "show" && "${AZ_SQL_EXISTS:-}" == "yes" ]]; then
                  exit 0
                fi
                if [[ "$1" == "webapp" && "$2" == "show" ]]; then exit 3; fi
                if [[ "$1" == "sql" && "$2" == "server" && "$3" == "show" ]]; then exit 3; fi
                exit 0
                """);
            using (var chmod = Process.Start("chmod", "+x " + stub))
            {
                Assert.NotNull(chmod);
                chmod.WaitForExit();
                Assert.Equal(0, chmod.ExitCode);
            }

            var placeholder = RunSetup(source, directory, stub, log, "00000000-0000-0000-0000-000000000000", new Dictionary<string, string>());
            Assert.Equal(1, placeholder.ExitCode);
            Assert.Contains("set SUBSCRIPTION_ID", placeholder.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(log));

            var refused = RunSetup(source, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>());
            Assert.Equal(1, refused.ExitCode);
            Assert.Contains("PFOH_ALLOW_PROD_SETUP=yes", refused.Error, StringComparison.Ordinal);
            Assert.Contains("PFOH_CONFIRM_SUBSCRIPTION", refused.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(log));

            var allowOnly = RunSetup(source, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>
            {
                ["PFOH_ALLOW_PROD_SETUP"] = "yes"
            });
            Assert.Equal(1, allowOnly.ExitCode);
            Assert.Contains("PFOH_CONFIRM_SUBSCRIPTION", allowOnly.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(log));

            var wrongConfirm = RunSetup(source, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>
            {
                ["PFOH_ALLOW_PROD_SETUP"] = "yes",
                ["PFOH_CONFIRM_SUBSCRIPTION"] = "22222222-2222-2222-2222-222222222222"
            });
            Assert.Equal(1, wrongConfirm.ExitCode);
            Assert.False(File.Exists(log));

            var wrongActive = RunSetup(source, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>
            {
                ["PFOH_ALLOW_PROD_SETUP"] = "yes",
                ["PFOH_CONFIRM_SUBSCRIPTION"] = "11111111-1111-1111-1111-111111111111",
                ["AZ_ACTIVE_ID"] = "33333333-3333-3333-3333-333333333333"
            });
            Assert.Equal(1, wrongActive.ExitCode);
            Assert.Contains("does not match SUBSCRIPTION_ID", wrongActive.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("group create", File.Exists(log) ? File.ReadAllText(log) : string.Empty, StringComparison.Ordinal);

            File.Delete(log);
            var exists = RunSetup(source, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>
            {
                ["PFOH_ALLOW_PROD_SETUP"] = "yes",
                ["PFOH_CONFIRM_SUBSCRIPTION"] = "11111111-1111-1111-1111-111111111111",
                ["AZ_ACTIVE_ID"] = "11111111-1111-1111-1111-111111111111",
                ["AZ_WEBAPP_EXISTS"] = "yes"
            });
            Assert.Equal(1, exists.ExitCode);
            Assert.Contains("already exists", exists.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("group create", File.ReadAllText(log), StringComparison.Ordinal);
            Assert.DoesNotContain("connection-string set", File.ReadAllText(log), StringComparison.Ordinal);

            File.Delete(log);
            var nonprod = source
                .Replace("PLANO_FLAGS_OF_HONOR", "pfoh-dev", StringComparison.Ordinal)
                .Replace("asp-pfoh-flags-prod", "asp-pfoh-dev", StringComparison.Ordinal)
                .Replace("pfoh-flag-manager-app", "pfoh-flag-manager-dev", StringComparison.Ordinal)
                .Replace("SQL_SERVER=\"pfoh\"", "SQL_SERVER=\"pfoh-test\"", StringComparison.Ordinal)
                .Replace("SQL_DB=\"pfoh\"", "SQL_DB=\"pfoh-sandbox\"", StringComparison.Ordinal);
            var created = RunSetup(nonprod, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>
            {
                ["AZ_ACTIVE_ID"] = "11111111-1111-1111-1111-111111111111"
            });
            Assert.Equal(0, created.ExitCode);
            var calls = File.ReadAllText(log);
            var show = calls.IndexOf("webapp show", StringComparison.Ordinal);
            var sqlShow = calls.IndexOf("sql server show", StringComparison.Ordinal);
            var group = calls.IndexOf("group create", StringComparison.Ordinal);
            var firewall = calls.IndexOf("firewall-rule create", StringComparison.Ordinal);
            var connection = calls.IndexOf("connection-string set", StringComparison.Ordinal);
            Assert.True(show >= 0 && sqlShow > show && group > sqlShow && firewall > group && connection > firewall);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (int ExitCode, string Error) RunSetup(
        string source,
        string directory,
        string stub,
        string log,
        string subscriptionId,
        IReadOnlyDictionary<string, string> environment)
    {
        var script = Path.Combine(directory, "azure-cli-setup.sh");
        var rewritten = source.Replace(
            "SUBSCRIPTION_ID=\"00000000-0000-0000-0000-000000000000\"",
            "SUBSCRIPTION_ID=\"" + subscriptionId + "\"",
            StringComparison.Ordinal);
        File.WriteAllText(script, rewritten);
        var start = new ProcessStartInfo("bash", script)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        start.Environment["PATH"] = directory + Path.PathSeparator + "/usr/bin" + Path.PathSeparator + "/bin";
        start.Environment["AZ_LOG"] = log;
        start.Environment["AZ_ACTIVE_ID"] = subscriptionId;
        foreach (var pair in environment)
        {
            start.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(start);
        Assert.NotNull(process);
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error);
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
