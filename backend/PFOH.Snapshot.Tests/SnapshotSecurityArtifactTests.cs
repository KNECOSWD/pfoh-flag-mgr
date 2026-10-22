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
        var webappShow = script.IndexOf("webapp show", StringComparison.Ordinal);
        var sqlShow = script.IndexOf("sql server show", StringComparison.Ordinal);
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
        Assert.Contains("(^|[^[:alnum:]])(dev|test|sandbox)([^[:alnum:]]|$)", script, StringComparison.Ordinal);
        Assert.Contains("ResourceNotFound", script, StringComparison.Ordinal);
        Assert.Contains("SQL_PASSWORD", script, StringComparison.Ordinal);
        Assert.Contains("PFOH_ALLOW_AZURE_SERVICES", script, StringComparison.Ordinal);
        Assert.Contains("P1v3", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeThisToAStrongPassword", script, StringComparison.Ordinal);
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
            WriteAzStub(stub);
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
                ["AZ_GROUP_EXISTS"] = "yes",
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
                ["AZ_ACTIVE_ID"] = "11111111-1111-1111-1111-111111111111",
                ["SQL_PASSWORD"] = SyntheticSqlPassword
            });
            Assert.Equal(0, created.ExitCode);
            var calls = File.ReadAllText(log);
            var groupExists = calls.IndexOf("group exists", StringComparison.Ordinal);
            var group = calls.IndexOf("group create", StringComparison.Ordinal);
            var planShow = calls.IndexOf("appservice plan show", StringComparison.Ordinal);
            var planCreate = calls.IndexOf("appservice plan create", StringComparison.Ordinal);
            var show = calls.IndexOf("webapp show", StringComparison.Ordinal);
            var sqlShow = calls.IndexOf("sql server show", StringComparison.Ordinal);
            var sqlCreate = calls.IndexOf("sql server create", StringComparison.Ordinal);
            var connection = calls.IndexOf("connection-string set", StringComparison.Ordinal);
            Assert.True(groupExists >= 0 && group > groupExists && planShow > group && planCreate > planShow);
            Assert.True(show > planCreate && sqlShow > show && sqlCreate > sqlShow && connection > sqlCreate);
            Assert.DoesNotContain("firewall-rule create", calls, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticSqlPassword, calls, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticSqlPassword, created.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Setup_script_fails_closed_for_sql_plan_password_and_devops()
    {
        var source = File.ReadAllText(RepoFile("infra", "azure-cli-setup.sh"));
        var directory = Path.Combine(Path.GetTempPath(), "pfoh-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var stub = Path.Combine(directory, "az");
            var log = Path.Combine(directory, "az.log");
            WriteAzStub(stub);
            using (var chmod = Process.Start("chmod", "+x " + stub))
            {
                Assert.NotNull(chmod);
                chmod.WaitForExit();
                Assert.Equal(0, chmod.ExitCode);
            }

            var sqlExists = RunSetup(source, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>
            {
                ["PFOH_ALLOW_PROD_SETUP"] = "yes",
                ["PFOH_CONFIRM_SUBSCRIPTION"] = "11111111-1111-1111-1111-111111111111",
                ["AZ_ACTIVE_ID"] = "11111111-1111-1111-1111-111111111111",
                ["AZ_GROUP_EXISTS"] = "yes",
                ["AZ_SQL_EXISTS"] = "yes"
            });
            Assert.Equal(1, sqlExists.ExitCode);
            Assert.Contains("SQL server", sqlExists.Error, StringComparison.Ordinal);
            Assert.Contains("does not reset the SQL admin password", sqlExists.Error, StringComparison.Ordinal);
            var sqlLog = File.ReadAllText(log);
            Assert.Contains("sql server show", sqlLog, StringComparison.Ordinal);
            Assert.DoesNotContain("sql server create", sqlLog, StringComparison.Ordinal);
            Assert.DoesNotContain("admin-password", sqlLog, StringComparison.Ordinal);

            File.Delete(log);
            var otherError = RunSetup(source, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>
            {
                ["PFOH_ALLOW_PROD_SETUP"] = "yes",
                ["PFOH_CONFIRM_SUBSCRIPTION"] = "11111111-1111-1111-1111-111111111111",
                ["AZ_ACTIVE_ID"] = "11111111-1111-1111-1111-111111111111",
                ["AZ_GROUP_EXISTS"] = "yes",
                ["AZ_WEBAPP_ERROR"] = "AuthorizationFailed"
            });
            Assert.Equal(1, otherError.ExitCode);
            Assert.Contains("AuthorizationFailed", otherError.Error, StringComparison.Ordinal);
            Assert.Contains("Only ResourceNotFound", otherError.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("group create", File.ReadAllText(log), StringComparison.Ordinal);
            Assert.DoesNotContain("sql server create", File.ReadAllText(log), StringComparison.Ordinal);

            File.Delete(log);
            var wrongSku = RunSetup(source, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>
            {
                ["PFOH_ALLOW_PROD_SETUP"] = "yes",
                ["PFOH_CONFIRM_SUBSCRIPTION"] = "11111111-1111-1111-1111-111111111111",
                ["AZ_ACTIVE_ID"] = "11111111-1111-1111-1111-111111111111",
                ["AZ_GROUP_EXISTS"] = "yes",
                ["AZ_PLAN_EXISTS"] = "yes",
                ["AZ_PLAN_SKU"] = "B1",
                ["SQL_PASSWORD"] = SyntheticSqlPassword
            });
            Assert.Equal(1, wrongSku.ExitCode);
            Assert.Contains("SKU B1", wrongSku.Error, StringComparison.Ordinal);
            Assert.Contains("P1v3", wrongSku.Error, StringComparison.Ordinal);
            var skuLog = File.ReadAllText(log);
            Assert.Contains("appservice plan show", skuLog, StringComparison.Ordinal);
            Assert.DoesNotContain("appservice plan create", skuLog, StringComparison.Ordinal);
            Assert.DoesNotContain("webapp create", skuLog, StringComparison.Ordinal);
            Assert.DoesNotContain("sql server create", skuLog, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticSqlPassword, skuLog, StringComparison.Ordinal);

            File.Delete(log);
            var missingPassword = source
                .Replace("PLANO_FLAGS_OF_HONOR", "pfoh-dev", StringComparison.Ordinal)
                .Replace("asp-pfoh-flags-prod", "asp-pfoh-dev", StringComparison.Ordinal)
                .Replace("pfoh-flag-manager-app", "pfoh-flag-manager-dev", StringComparison.Ordinal)
                .Replace("SQL_SERVER=\"pfoh\"", "SQL_SERVER=\"pfoh-test\"", StringComparison.Ordinal)
                .Replace("SQL_DB=\"pfoh\"", "SQL_DB=\"pfoh-sandbox\"", StringComparison.Ordinal);
            var noPassword = RunSetup(missingPassword, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>
            {
                ["AZ_ACTIVE_ID"] = "11111111-1111-1111-1111-111111111111"
            });
            Assert.Equal(1, noPassword.ExitCode);
            Assert.Contains("SQL_PASSWORD", noPassword.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(log) && File.ReadAllText(log).Contains("group create", StringComparison.Ordinal));

            File.Delete(log);
            var firewall = RunSetup(missingPassword, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>
            {
                ["AZ_ACTIVE_ID"] = "11111111-1111-1111-1111-111111111111",
                ["SQL_PASSWORD"] = SyntheticSqlPassword,
                ["PFOH_ALLOW_AZURE_SERVICES"] = "yes"
            });
            Assert.Equal(0, firewall.ExitCode);
            var firewallLog = File.ReadAllText(log);
            Assert.Contains("firewall-rule create", firewallLog, StringComparison.Ordinal);
            Assert.Contains("0.0.0.0", firewallLog, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticSqlPassword, firewallLog, StringComparison.Ordinal);

            var devops = source
                .Replace("PLANO_FLAGS_OF_HONOR", "pfoh-dev", StringComparison.Ordinal)
                .Replace("asp-pfoh-flags-prod", "asp-devops", StringComparison.Ordinal)
                .Replace("pfoh-flag-manager-app", "pfoh-flag-manager-dev", StringComparison.Ordinal)
                .Replace("SQL_SERVER=\"pfoh\"", "SQL_SERVER=\"pfoh-test\"", StringComparison.Ordinal)
                .Replace("SQL_DB=\"pfoh\"", "SQL_DB=\"pfoh-sandbox\"", StringComparison.Ordinal);
            if (File.Exists(log))
            {
                File.Delete(log);
            }

            var devopsResult = RunSetup(devops, directory, stub, log, "11111111-1111-1111-1111-111111111111", new Dictionary<string, string>());
            Assert.Equal(1, devopsResult.ExitCode);
            Assert.Contains("asp-devops", devopsResult.Error, StringComparison.Ordinal);
            Assert.Contains("PFOH_ALLOW_PROD_SETUP=yes", devopsResult.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(log));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string SyntheticSqlPassword = "synthetic-setup-password";

    private static void WriteAzStub(string stub)
    {
        File.WriteAllText(stub, """
            #!/bin/bash
            out=()
            skip=0
            for arg in "$@"; do
              if [[ "$skip" == "1" ]]; then
                out+=("***")
                skip=0
                continue
              fi
              out+=("$arg")
              case "$arg" in
                --admin-password|--settings) skip=1 ;;
              esac
            done
            printf '%s\n' "${out[*]}" >> "$AZ_LOG"
            if [[ "$1" == "account" && "$2" == "show" ]]; then
              printf '%s\n' "${AZ_ACTIVE_ID}"
              exit 0
            fi
            if [[ "$1" == "group" && "$2" == "exists" ]]; then
              if [[ -n "${AZ_GROUP_ERROR:-}" ]]; then
                echo "ERROR: (${AZ_GROUP_ERROR}) synthetic failure" >&2
                exit 1
              fi
              if [[ "${AZ_GROUP_EXISTS:-}" == "yes" ]]; then
                printf 'true\n'
              else
                printf 'false\n'
              fi
              exit 0
            fi
            if [[ "$1" == "webapp" && "$2" == "show" ]]; then
              if [[ "${AZ_WEBAPP_EXISTS:-}" == "yes" ]]; then
                exit 0
              fi
              if [[ -n "${AZ_WEBAPP_ERROR:-}" ]]; then
                echo "ERROR: (${AZ_WEBAPP_ERROR}) synthetic failure" >&2
                exit 1
              fi
              echo "ERROR: (ResourceNotFound) The Resource was not found." >&2
              exit 3
            fi
            if [[ "$1" == "sql" && "$2" == "server" && "$3" == "show" ]]; then
              if [[ "${AZ_SQL_EXISTS:-}" == "yes" ]]; then
                exit 0
              fi
              if [[ -n "${AZ_SQL_ERROR:-}" ]]; then
                echo "ERROR: (${AZ_SQL_ERROR}) synthetic failure" >&2
                exit 1
              fi
              echo "ERROR: (ResourceNotFound) The Resource was not found." >&2
              exit 3
            fi
            if [[ "$1" == "appservice" && "$2" == "plan" && "$3" == "show" ]]; then
              if [[ "${AZ_PLAN_EXISTS:-}" == "yes" ]]; then
                printf '%s\n' "${AZ_PLAN_SKU:-P1v3}"
                exit 0
              fi
              if [[ -n "${AZ_PLAN_ERROR:-}" ]]; then
                echo "ERROR: (${AZ_PLAN_ERROR}) synthetic failure" >&2
                exit 1
              fi
              echo "ERROR: (ResourceNotFound) The Resource was not found." >&2
              exit 3
            fi
            exit 0
            """);
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
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        start.Environment["PATH"] = directory + Path.PathSeparator + "/usr/bin" + Path.PathSeparator + "/bin";
        start.Environment["AZ_LOG"] = log;
        start.Environment["AZ_ACTIVE_ID"] = subscriptionId;
        start.Environment.Remove("SQL_PASSWORD");
        start.Environment.Remove("PFOH_ALLOW_PROD_SETUP");
        start.Environment.Remove("PFOH_CONFIRM_SUBSCRIPTION");
        start.Environment.Remove("PFOH_ALLOW_AZURE_SERVICES");
        start.Environment.Remove("AZ_GROUP_EXISTS");
        start.Environment.Remove("AZ_GROUP_ERROR");
        start.Environment.Remove("AZ_WEBAPP_EXISTS");
        start.Environment.Remove("AZ_WEBAPP_ERROR");
        start.Environment.Remove("AZ_SQL_EXISTS");
        start.Environment.Remove("AZ_SQL_ERROR");
        start.Environment.Remove("AZ_PLAN_EXISTS");
        start.Environment.Remove("AZ_PLAN_SKU");
        start.Environment.Remove("AZ_PLAN_ERROR");
        foreach (var pair in environment)
        {
            start.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(start);
        Assert.NotNull(process);
        process.StandardInput.Close();
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
