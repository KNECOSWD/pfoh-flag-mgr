# Nightly metrics snapshot

This job writes two JSON files a night. It does not sign a person in, and it does not call OneDrive. Nothing in this repository creates the Azure resources. An admin runs the steps below.

The files are UTF-8 without a BOM:

- `pfoh-reports/YYYY-MM-DD/db-dump.json`
- `pfoh-reports/YYYY-MM-DD/directory-users.json`

`YYYY-MM-DD` is the UTC date of the run. 8:45 PM Colombia time is 01:45 UTC the next calendar day, so the folder date is that UTC date.

`db-dump.json` has `pulledAt`, `columns`, and `tables`. Every user table is present with its full `count`. `rows` are present only for the tables and columns in `SnapshotFieldSpec`. Honoree bios, awards, conflicts, and photo names are not written. The file has `HasPhoto`, `HasDescription`, `HasAwards`, and `HasConflictsServed` instead. `directory-users.json` has `pulledAt`, `users`, and `signInActivity`.

## Why a Function App

A WebJob on the website would run as that site. A user-assigned identity attached to the website is also available to the website, so it is not dedicated to this job.

The function is a separate app on the existing Basic B1 plan (`asp-pfoh-flags-prod` in `infra/azure-cli-setup.sh`). The website is unchanged and does not receive this identity. The plan is already paid and already has Always On, so the nightly run adds no plan charge. A Consumption plan would also stay inside the free grant (1 million executions and 400,000 GB-s a month, from the Azure Functions pricing page) but it is a second plan and a cold start. Sharing the existing plan is the lower incremental cost and still isolates the identity.

Storage is the incremental cost. Two Standard LRS accounts (Hot) are defined: one for the function host, one for the report container. Azure Blob Storage Hot LRS list price is about $0.018 per GB-month in East US, and write transactions are about $0.05 per 10,000. The JSON is small and the job writes two blobs a night. Expect well under $0.20 a month for both accounts. Confirm both figures on the Azure pricing calculator before the first deploy. No second App Service plan is included in that estimate.

## Schedule

The timer is `0 45 1 * * *`. Azure Functions evaluates that NCRONTAB in UTC. Colombia (`America/Bogota`) is UTC-5 all year, so 01:45 UTC is 8:45 PM Colombia time. `WEBSITE_TIME_ZONE` is not set. The schedule does not depend on it.

There is no HTTP trigger. A manual run uses the platform admin endpoint and the host key. Do not add a public URL for it.

## What the admin runs, in order

Set the resource group and names you already use. Do not paste keys, certificates, or connection strings into the repository, the workflow, or a log.

1. Create the Graph app registration in the External ID tenant `pfohflags.onmicrosoft.com` (`77f89427-b4a7-43ed-92fc-3eb5bd63d0f1`). Name it `pfoh-snapshot-graph`. Leave it without a client secret and without a certificate. Its only credential is the federated identity created in step 4.

2. Note the app's client id. You need it as `graphClientId`. You cannot finish step 4 until step 3 has produced the job identity's principal id, so deploy the identity first with `graphClientId` still empty, then repeat the deploy after step 4.

3. Deploy `infra/snapshot.bicep` from the resource group. This is the step that creates resources, and it is not run by CI.

   ```bash
   az deployment group create \
     --resource-group rg-pfoh-flags-prod \
     --template-file infra/snapshot.bicep \
     --parameters appServicePlanName=asp-pfoh-flags-prod sqlServerName=<sql-server-name> sqlDatabaseName=sqldb-pfoh-flags-prod graphClientId=<graph-app-client-id> readerPrincipalId=<reader-principal-id-or-omit>
   ```

   The file creates:

   - User-assigned identity `id-pfoh-snapshot`. The website does not use it.
   - Report storage: public blob access off, HTTPS only, TLS 1.2 minimum, shared key access off. Container `pfoh-reports` with public access `None`.
   - A lifecycle rule that deletes blobs under `pfoh-reports/` `snapshotRetentionDays` after they are written. The default is 30 days. Change the parameter and deploy again to change it. The function does not delete blobs itself.
   - A separate host storage account, also with shared key access off. The function runtime needs blob, queue, and table data roles on that account. Those roles are on the host account resource, not on the resource group or the subscription, and not on the report account.
   - Storage Blob Data Contributor for `id-pfoh-snapshot` on the `pfoh-reports` container only.
   - Function app `func-pfoh-snapshot` on the existing plan, user-assigned identity only, HTTPS only, TLS 1.2.

   Read `jobIdentityPrincipalId` and `reportsContainerId` from the deployment outputs.

4. Add the federated credential on `pfoh-snapshot-graph`. The subject is the job identity's principal id from step 3, not its client id. The audience is `api://AzureADTokenExchange`. The issuer is the Microsoft Entra tenant that holds the identity (the Azure subscription tenant), not the External ID tenant.

   ```json
   {
     "name": "pfoh-snapshot-job",
     "issuer": "https://login.microsoftonline.com/<azure-tenant-id>/v2.0",
     "subject": "<jobIdentityPrincipalId>",
     "audiences": ["api://AzureADTokenExchange"]
   }
   ```

   ```bash
   az ad app federated-credential create --id <graph-app-client-id> --parameters federated.json
   ```

   Deploy the Bicep again with `graphClientId` set to that app's client id so the function can request tokens.

5. Grant application permissions and admin consent in tenant `77f89427-b4a7-43ed-92fc-3eb5bd63d0f1`. Do it in that tenant's portal or Graph, not from this repository.

   - `User.Read.All`, so the job can read the user fields in `SnapshotFieldSpec.DirectoryUserSelect`.
   - `AuditLog.Read.All`, only because Graph omits `signInActivity` without it. The job's second select is `id,signInActivity`. It keeps `lastSignInDateTime` and `lastSuccessfulSignInDateTime`. It does not read audit log events.

6. In the app database, as an Azure SQL admin, create a contained user for the job identity and read-only views. The identity name is `id-pfoh-snapshot`. There is no SQL password, no write role, and no `db_owner`. Do not add the user to a data-reader role. `GRANT SELECT` is only on the `snapshot` views. Those views project the columns in `SnapshotFieldSpec` (`backend/PFOH.Snapshot/snapshot-field-spec.json`). Honoree bios, awards, conflicts, and photo names stay on `dbo.Honorees`. The view turns them into the four presence flags. The count view is owned by `dbo`, so the job can count every user table without a grant on the base tables. Run this again when a new user table appears.

   ```sql
   CREATE USER [id-pfoh-snapshot] FROM EXTERNAL PROVIDER;
   GO
   CREATE SCHEMA snapshot AUTHORIZATION dbo;
   GO
   CREATE OR ALTER VIEW snapshot.FlagClaims AS
   SELECT Id, FlagGridId, HonoreeId, ExternalUserObjectId, ExternalUserEmail, ExternalUserName,
          ClaimStatus, CreatedUtc, SubmittedUtc, ApprovedUtc, RejectedUtc, ApprovedBy, RejectedBy
   FROM dbo.FlagClaims;
   GO
   CREATE OR ALTER VIEW snapshot.HonoreeClaims AS
   SELECT Id, HonoreeId, Email, ClaimStatus FROM dbo.HonoreeClaims;
   GO
   CREATE OR ALTER VIEW snapshot.HonoreeChangeRequests AS
   SELECT Id, FlagClaimId, FlagGridId, HonoreeId, RequestStatus, CreatedUtc, SubmittedUtc, ReviewedUtc, ReviewedBy
   FROM dbo.HonoreeChangeRequests;
   GO
   CREATE OR ALTER VIEW snapshot.Honorees AS
   SELECT Id, FlagGridId, Rank, FirstName, MiddleName, LastName, Suffix, IsActive, CreatedBy, CreatedDate, DeletedDate, ServiceBranchId,
     CAST(CASE WHEN PhotoFileName IS NOT NULL AND TRIM(PhotoFileName) <> N'' AND LOWER(TRIM(PhotoFileName)) NOT LIKE N'silhouette%' THEN 1 ELSE 0 END AS bit) AS HasPhoto,
     CAST(CASE WHEN Description IS NOT NULL AND TRIM(Description) <> N'' AND LOWER(TRIM(Description)) NOT IN (N'no information provided', N'n/a', N'none', N'unknown') THEN 1 ELSE 0 END AS bit) AS HasDescription,
     CAST(CASE WHEN Awards IS NOT NULL AND TRIM(Awards) <> N'' AND LOWER(TRIM(Awards)) NOT IN (N'no information provided', N'n/a', N'none', N'unknown') THEN 1 ELSE 0 END AS bit) AS HasAwards,
     CAST(CASE WHEN ConflictsServed IS NOT NULL AND TRIM(ConflictsServed) <> N'' AND LOWER(TRIM(ConflictsServed)) NOT IN (N'no information provided', N'n/a', N'none', N'unknown') THEN 1 ELSE 0 END AS bit) AS HasConflictsServed
   FROM dbo.Honorees;
   GO
   CREATE OR ALTER VIEW snapshot.FlagGrids AS
   SELECT Id, HonoreeId, FlagGridName, Reserved, CreatedBy, CreatedDate, DeletedDate FROM dbo.FlagGrids;
   GO
   CREATE OR ALTER VIEW snapshot.ServiceBranches AS
   SELECT Id, ServiceBranchName, DeletedDate FROM dbo.ServiceBranches;
   GO
   CREATE OR ALTER VIEW snapshot.UserProfiles AS
   SELECT Id, OwnerObjectId, Email, CreatedUtc, Phone, Organization FROM dbo.UserProfiles;
   GO
   CREATE OR ALTER VIEW snapshot.CardPrintEvents AS
   SELECT Id, CardPrintBatchId, PrintedUtc, CardCount FROM dbo.CardPrintEvents;
   GO
   DECLARE @select nvarchar(max) = N'';
   SELECT @select = @select + CASE WHEN @select = N'' THEN N'' ELSE N' UNION ALL ' END
     + N'SELECT N''' + REPLACE(s.name, '''', '''''') + N''' AS SchemaName, N'''
     + REPLACE(t.name, '''', '''''') + N''' AS TableName, COUNT_BIG(*) AS [RowCount] FROM '
     + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
   FROM sys.tables AS t
   INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
   WHERE t.is_ms_shipped = 0 AND s.name <> N'snapshot';
   EXEC(N'CREATE OR ALTER VIEW snapshot.UserTableRowCounts AS ' + @select);
   GO
   GRANT SELECT ON snapshot.FlagClaims TO [id-pfoh-snapshot];
   GRANT SELECT ON snapshot.HonoreeClaims TO [id-pfoh-snapshot];
   GRANT SELECT ON snapshot.HonoreeChangeRequests TO [id-pfoh-snapshot];
   GRANT SELECT ON snapshot.Honorees TO [id-pfoh-snapshot];
   GRANT SELECT ON snapshot.FlagGrids TO [id-pfoh-snapshot];
   GRANT SELECT ON snapshot.ServiceBranches TO [id-pfoh-snapshot];
   GRANT SELECT ON snapshot.UserProfiles TO [id-pfoh-snapshot];
   GRANT SELECT ON snapshot.CardPrintEvents TO [id-pfoh-snapshot];
   GRANT SELECT ON snapshot.UserTableRowCounts TO [id-pfoh-snapshot];
   ```

7. Create the reader used by the off-Azure box. It is an app registration in the Azure tenant that holds the storage account, not the External ID tenant. Its credential is a certificate with a 1-year lifetime. Do not create a client secret. Do not create a shared access signature. Shared key access is off, so a stored-access-policy signature is not available.

   On the box, create the key and a public certificate in a directory outside the git checkout and outside `/workspace`. Leave the private key on that box. Do not copy the private key into the repository, the workflow, a log, or a ticket.

   ```bash
   READER_DIR="${HOME}/pfoh-snapshot-reader"
   install -d -m 0700 "${READER_DIR}"
   openssl req -x509 -newkey rsa:2048 -sha256 -days 365 -nodes \
     -keyout "${READER_DIR}/pfoh-snapshot-reader.pem" \
     -out "${READER_DIR}/pfoh-snapshot-reader.crt" \
     -subj "/CN=pfoh-snapshot-reader"
   chmod 600 "${READER_DIR}/pfoh-snapshot-reader.pem" "${READER_DIR}/pfoh-snapshot-reader.crt"
   openssl x509 -in "${READER_DIR}/pfoh-snapshot-reader.crt" -outform der -out "${READER_DIR}/pfoh-snapshot-reader.cer"
   az ad app create --display-name pfoh-snapshot-reader --query appId -o tsv
   az ad sp create --id <reader-app-id>
   az ad app credential reset --id <reader-app-id> --cert @"${READER_DIR}/pfoh-snapshot-reader.cer" --append
   ```

   `az ad app credential list --id <reader-app-id>` shows `endDateTime`. Put a reminder 30 days before that date.

   Rotation: create a new 1-year certificate in that same directory, upload the new public certificate, switch the box to the new private key, confirm a read, then delete the old credential. `chmod 600` the new key. The old private key is removed from the box after the switch. It is never committed.

   The reader role is the Bicep assignment `reportsReader` on the report container. Do not assign it with the CLI. Deploy `infra/snapshot.bicep` again with `readerPrincipalId` set to the reader service principal object id. That is the only Storage Blob Data Reader assignment.

   Verify the reader before anyone uses it. The app must have no API permissions. The service principal must have exactly one role assignment, and that scope must be the report container.

   ```bash
   az ad app permission list --id <reader-app-id> --query "length(@)"
   az role assignment list \
     --assignee-object-id <reader-service-principal-object-id> \
     --assignee-principal-type ServicePrincipal \
     --query "[].{role:roleDefinitionName,scope:scope}" -o table
   ```

   The permission count is 0. The table has one row: Storage Blob Data Reader, and the scope ends with `/blobServices/default/containers/pfoh-reports`.

   The box signs in with the certificate and reads the container. That is the access path this job uses.

8. Let the existing GitHub OIDC deploy app publish the function. Give it Website Contributor on the function app resource only.

   ```bash
   FUNC_ID=$(az functionapp show --resource-group rg-pfoh-flags-prod --name func-pfoh-snapshot --query id -o tsv)
   az role assignment create \
     --assignee <existing-github-oidc-app-id> \
     --role "Website Contributor" \
     --scope "$FUNC_ID"
   ```

   Create a protected GitHub environment named `pfoh-snapshot`. Require a reviewer, and limit deployment branches to `main`. The **Deploy snapshot job** workflow runs only when its ref is `refs/heads/main`, and it uses that environment. Run it by hand from `main`. It uses `azure/login` with the federated credential already used for the website. It does not store a client secret or a certificate, and it does not assign roles.

9. Manual test, after the function is deployed. Run this only in Azure Cloud Shell. Do not run it on the box. The box must never see the host key. Do not write the key into the repo or a log. The `az` result below is the key, so do not paste the command output into a ticket.

   ```bash
   MASTER_KEY=$(az functionapp keys list --resource-group rg-pfoh-flags-prod --name func-pfoh-snapshot --query masterKey -o tsv)
   curl -sS -X POST "https://func-pfoh-snapshot.azurewebsites.net/admin/functions/SnapshotNightly" \
     -H "Content-Type: application/json" \
     -H "x-functions-key: ${MASTER_KEY}" \
     -d '{}'
   unset MASTER_KEY
   ```

   The function log should contain table, row, and byte counts. It should not contain names, emails, bios, or row contents. The container should contain that UTC date's two JSON files.

## Logs

The job logs how many tables, counted rows, emitted rows, directory users, sign-in rows, and bytes it wrote. It does not log row contents or token values. A Graph failure is logged as the HTTP status only.
