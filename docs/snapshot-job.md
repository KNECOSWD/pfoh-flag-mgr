# Nightly metrics snapshot

This job writes two JSON files a night. It does not sign a person in, and it does not call OneDrive. Nothing in this repository creates the Azure resources. An admin runs the steps below.

The files are UTF-8 without a BOM:

- `pfoh-reports/YYYY-MM-DD/db-dump.json`
- `pfoh-reports/YYYY-MM-DD/directory-users.json`

`YYYY-MM-DD` is the UTC date of the run. 8:45 PM Colombia time is 01:45 UTC the next calendar day, so the folder date is that UTC date.

`db-dump.json` has `pulledAt`, `columns`, and `tables`. Every user table is present with its full `count`. `rows` are present only for the tables and columns in `SnapshotFieldSpec`. Honoree bios, awards, conflicts, and photo names are not written. The file has `HasPhoto`, `HasDescription`, `HasAwards`, and `HasConflictsServed` instead. `directory-users.json` has `pulledAt`, `users`, and `signInActivity`.

## Why a Function App

A WebJob on the website would run as that site. A user-assigned identity attached to the website is also available to the website, so it is not dedicated to this job.

The function is a separate app on the existing P1v3 plan (`asp-pfoh-flags-prod`). That plan is in canadacentral. The resource group `PLANO_FLAGS_OF_HONOR` is in eastus, so the deploy passes `location=canadacentral` and does not use the resource group location. The website is unchanged and does not receive this identity. The plan is already paid and already has Always On, so the nightly run adds no plan charge. A Consumption plan would also stay inside the free grant (1 million executions and 400,000 GB-s a month, from the Azure Functions pricing page) but it is a second plan and a cold start. Sharing the existing plan is the lower incremental cost and still isolates the identity.

Storage is the incremental cost. Two Standard LRS accounts (Hot) are defined: one for the function host, one for the report container. Azure Blob Storage Hot LRS list price is about $0.018 per GB-month in East US, and write transactions are about $0.05 per 10,000. The JSON is small and the job writes two blobs a night. Expect well under $0.20 a month for both accounts. Key Vault Standard has no base monthly fee. Secret operations are about $0.03 per 10,000 transactions on the Key Vault pricing page, so one certificate read a night is well under $0.01. A self-signed certificate has no certificate-authority charge. Confirm the figures on the Azure pricing calculator before the first deploy. No second App Service plan is included in that estimate.

## Schedule

The timer stays `0 45 1 * * *`. Azure Functions evaluates that NCRONTAB in UTC, every day at 01:45 UTC. `WEBSITE_TIME_ZONE` is not set. The schedule does not depend on it. There is no redeploy on the cutover date.

`Snapshot__MonthlyFromUtcDate` defaults to `2026-12-01`. The function reads it through options and checks it when the host starts. An empty or invalid value stops startup with a clear message. On each fire the run's UTC date is D.

- If D is before `Snapshot__MonthlyFromUtcDate`, the job runs.
- If D is on or after that date, the job runs only when D is the 1st. Any other day logs `snapshot skipped: monthly mode` and writes no blobs.

The last nightly run is 2026-12-01 01:45 UTC, which is Nov 30, 8:45 PM Colombia time (COT, UTC-5). After that, the job runs on the 1st of each month at 01:45 UTC. That instant is the evening of the last day of the prior month in Colombia (8:45 PM COT) and in US Central (7:45 PM CST from November through March, 8:45 PM CDT in summer). Changing `Snapshot__MonthlyFromUtcDate` changes the cutover. No code deploy is required.

There is no HTTP trigger. A manual run uses the platform admin endpoint and the host key. Do not add a public URL for it. A manual run on a skipped day still follows this rule.

## What the admin runs, in order

Set the resource group and names you already use. Do not paste keys, certificates, or connection strings into the repository, the workflow, or a log.

1. Create the Graph app registration in the External ID tenant `pfohflags.onmicrosoft.com` (`77f89427-b4a7-43ed-92fc-3eb5bd63d0f1`). Name it `pfoh-snapshot-graph`. The app is single-tenant. Do not create a client secret. Do not add a federated credential. Its credential is the Key Vault certificate from step 4.

2. Note the app's client id. You need it as `graphClientId` on the step 3 deploy.

3. Deploy `infra/snapshot.bicep` from the resource group. This is the step that creates resources, and it is not run by CI.

   ```bash
   az deployment group create \
     --resource-group PLANO_FLAGS_OF_HONOR \
     --template-file infra/snapshot.bicep \
     --parameters location=canadacentral appServicePlanName=asp-pfoh-flags-prod sqlServerName=pfoh sqlDatabaseName=pfoh graphClientId=<graph-app-client-id> readerPrincipalId=<reader-principal-id-or-omit>
   ```

   The file creates:

   - User-assigned identity `id-pfoh-snapshot`. The website does not use it.
   - Report storage: public blob access off, HTTPS only, TLS 1.2 minimum, shared key access off. Container `pfoh-reports` with public access `None`.
   - A lifecycle rule that deletes blobs under `pfoh-reports/` `snapshotRetentionDays` after they are written. The default is 30 days. Change the parameter and deploy again to change it. The function does not delete blobs itself.
   - A separate host storage account, also with shared key access off. The function runtime needs blob, queue, and table data roles on that account. Those roles are on the host account resource, not on the resource group or the subscription, and not on the report account.
   - Storage Blob Data Contributor for `id-pfoh-snapshot` on the `pfoh-reports` container only.
   - Key Vault, Standard SKU, RBAC authorization, soft delete, and purge protection. `publicNetworkAccess` is `Enabled`. That is the standard default for a function that is not in a virtual network, so a private endpoint is not used. The only data-plane role in this file is Key Vault Secrets User for `id-pfoh-snapshot` on the vault. The function reads the certificate private key through the secret endpoint. This file does not grant an admin a standing certificate role.
   - Function app `func-pfoh-snapshot` on the existing plan, user-assigned identity only, HTTPS only, TLS 1.2.

   Read `jobIdentityPrincipalId`, `reportsContainerId`, `keyVaultName`, and `keyVaultUri` from the deployment outputs.

4. Create the Graph certificate in that Key Vault. The private key stays in the vault. Do not download it, and do not copy it to the box, the repository, or a log.

   Assign Key Vault Certificates Officer to the admin for this step only. `<vault-id>` is the vault resource id. Remove the assignment at the end of the step. It is not a standing grant.

   ```bash
   az role assignment create \
     --assignee-object-id <admin-object-id> \
     --assignee-principal-type User \
     --role "Key Vault Certificates Officer" \
     --scope <vault-id>
   az keyvault certificate contact add \
     --vault-name <keyVaultName> \
     --email <admin-email> \
     --name <admin-name>
   ```

   Write this policy to a file outside the repository, then create the certificate. Validity is 12 months. The vault does not renew it. Thirty days before expiry it emails the contacts. Rotation is deliberate: the function reads the latest certificate version, so a new version is used on the next run as soon as it exists.

   ```json
   {
     "issuerParameters": { "name": "Self" },
     "keyProperties": { "exportable": true, "keySize": 2048, "keyType": "RSA", "reuseKey": false },
     "secretProperties": { "contentType": "application/x-pkcs12" },
     "x509CertificateProperties": { "subject": "CN=pfoh-snapshot-graph", "validityInMonths": 12 },
     "lifetimeActions": [
       { "trigger": { "daysBeforeExpiry": 30 }, "action": { "actionType": "EmailContacts" } }
     ]
   }
   ```

   ```bash
   az keyvault certificate create \
     --vault-name <keyVaultName> \
     --name pfoh-snapshot-graph \
     --policy @graph-cert-policy.json
   az keyvault certificate download \
     --vault-name <keyVaultName> \
     --name pfoh-snapshot-graph \
     --encoding DER \
     --file pfoh-snapshot-graph.cer
   az ad app credential reset \
     --id <graph-app-client-id> \
     --cert @pfoh-snapshot-graph.cer \
     --append
   rm -f pfoh-snapshot-graph.cer graph-cert-policy.json
   az role assignment delete \
     --assignee-object-id <admin-object-id> \
     --role "Key Vault Certificates Officer" \
     --scope <vault-id>
   ```

   The download is the public certificate. The private key never leaves Key Vault. Delete the public file after the upload. Do not commit it.

   Rotation is a new certificate version, not an automatic renewal. Assign Key Vault Certificates Officer again, create the new version, download that public certificate, and upload it with `az ad app credential reset --id <graph-app-client-id> --cert @pfoh-snapshot-graph.cer --append`. Verify a snapshot run. The function reads the latest version, which is why this order matters. After that run succeeds, remove the previous certificate credential from the app, delete the public file, and remove the Certificates Officer assignment.

   A live token test in the pfohflags tenant passed on 2026-10-22: certificate sign-in, users read, and signInActivity with lastSignInDateTime. Keep those last-sign-in fields. The procedure is held separately and is not in this repository.

5. Grant application permissions and admin consent in tenant `77f89427-b4a7-43ed-92fc-3eb5bd63d0f1`. Do it in that tenant's portal or Graph, not from this repository.

   - `User.Read.All`, so the job can read the user fields in `SnapshotFieldSpec.DirectoryUserSelect`.
   - `AuditLog.Read.All`, only because Graph omits `signInActivity` without it. The job's second select is `id,signInActivity`. It keeps `lastSignInDateTime` and `lastSuccessfulSignInDateTime`. It does not read audit log events.

   `appRoleAssignments` can read 0 for a few minutes after admin consent while the grant propagates. Wait and re-check. Confirm the count is 2 before deploying the code.

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

8. Publish the function only after the Graph app registration exists, the Key Vault certificate exists, and `Snapshot__GraphClientId`, `Snapshot__GraphTenantId`, and `Snapshot__GraphCertificateName` are non-empty. The host checks those three when it starts. If one is empty it logs that line and the timer skips the run, so the shared plan does not restart. Do not run the workflow before the settings are filled.

   Let the existing GitHub OIDC deploy app publish the function. Give it Website Contributor on the function app resource only.

   ```bash
   FUNC_ID=$(az functionapp show --resource-group PLANO_FLAGS_OF_HONOR --name func-pfoh-snapshot --query id -o tsv)
   az role assignment create \
     --assignee <existing-github-oidc-app-id> \
     --role "Website Contributor" \
     --scope "$FUNC_ID"
   ```

   Two admin steps need Matthew's consent. Do not create them from this repository or from CI.

   Create a protected GitHub environment named `pfoh-snapshot`. Require Matthew as a reviewer, and limit deployment branches to `main`.

   On the existing GitHub OIDC app, create an environment-scoped federated credential. The subject is `repo:KNECOSWD/pfoh-flag-mgr:environment:pfoh-snapshot`. The issuer is `https://token.actions.githubusercontent.com`, with the same audience as the website deploy credential. This is not a client secret and not the Graph certificate.

   The **Deploy snapshot job** workflow runs only when its ref is `refs/heads/main`, and it uses that environment. Run it by hand from `main` after both steps exist. It uses `azure/login` with that environment credential. It does not store a client secret or a certificate, and it does not assign roles.

9. Manual test, after the function is deployed. Run this only in Azure Cloud Shell. Do not run it on the box. The box must never see the host key. Do not write the key into the repo or a log. The `az` result below is the key, so do not paste the command output into a ticket.

   ```bash
   MASTER_KEY=$(az functionapp keys list --resource-group PLANO_FLAGS_OF_HONOR --name func-pfoh-snapshot --query masterKey -o tsv)
   curl -sS -X POST "https://func-pfoh-snapshot.azurewebsites.net/admin/functions/SnapshotNightly" \
     -H "Content-Type: application/json" \
     -H "x-functions-key: ${MASTER_KEY}" \
     -d '{}'
   unset MASTER_KEY
   ```

   The function log should contain table, row, and byte counts. It should not contain names, emails, bios, or row contents. The container should contain that UTC date's two JSON files.

## Missed run

A missed timer fire is not replayed later. The next scheduled fire writes the snapshot for that fire's UTC date, or skips. It does not write the date that was missed.

To run the job once before the next timer fire, use the Cloud Shell command in step 9. That POST calls the existing timer function through the platform admin endpoint. It uses the UTC date of the call. Before `Snapshot__MonthlyFromUtcDate`, that writes today's two blobs. On or after the cutover it writes them only when today is the 1st. Any other day logs `snapshot skipped: monthly mode` and writes nothing. There is no endpoint that backfills another date, and this repository does not add one.

The daily check that the blobs are present runs outside this repository.

## Logs

The job logs how many tables, counted rows, emitted rows, directory users, sign-in rows, and bytes it wrote. It does not log row contents or token values. A Graph failure is logged as the HTTP status only.
