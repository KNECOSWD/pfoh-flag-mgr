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

6. In the app database, as an Azure SQL admin, create a contained user for the job identity. The identity name is `id-pfoh-snapshot`. There is no SQL password, no write role, and no `db_owner`.

   ```sql
   CREATE USER [id-pfoh-snapshot] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_datareader ADD MEMBER [id-pfoh-snapshot];
   ```

7. Create the reader used by the off-Azure box. It is an app registration in the Azure tenant that holds the storage account, not the External ID tenant. Its credential is a certificate with a 1-year lifetime. Do not create a client secret. Do not create a shared access signature. Shared key access is off, so a stored-access-policy signature is not available.

   On the box, create the key and a public certificate. Leave the private key on that box. Do not copy the private key into the repository, the workflow, a log, or a ticket.

   ```bash
   openssl req -x509 -newkey rsa:2048 -sha256 -days 365 -nodes \
     -keyout pfoh-snapshot-reader.key \
     -out pfoh-snapshot-reader.crt \
     -subj "/CN=pfoh-snapshot-reader"
   openssl x509 -in pfoh-snapshot-reader.crt -outform der -out pfoh-snapshot-reader.cer
   az ad app create --display-name pfoh-snapshot-reader --query appId -o tsv
   az ad sp create --id <reader-app-id>
   az ad app credential reset --id <reader-app-id> --cert @pfoh-snapshot-reader.cer --append
   ```

   `az ad app credential list --id <reader-app-id>` shows `endDateTime`. Put a reminder 30 days before that date.

   Rotation: create a new 1-year certificate, upload the new public certificate, switch the box to the new private key, confirm a read, then delete the old credential. The old private key is removed from the box after the switch. It is never committed.

   Assign Storage Blob Data Reader on the report container only. `reportsContainerId` is the deployment output. Do not assign it on the storage account, the resource group, or the subscription.

   ```bash
   az role assignment create \
     --assignee-object-id <reader-service-principal-object-id> \
     --assignee-principal-type ServicePrincipal \
     --role "Storage Blob Data Reader" \
     --scope <reportsContainerId>
   ```

   If the principal id was not known at the first deploy, deploy the Bicep again with `readerPrincipalId`.

   The box signs in with the certificate and reads the container. That is the access path this job uses.

8. Let the existing GitHub OIDC deploy app publish the function. Give it Website Contributor on the function app resource only.

   ```bash
   FUNC_ID=$(az functionapp show --resource-group rg-pfoh-flags-prod --name func-pfoh-snapshot --query id -o tsv)
   az role assignment create \
     --assignee <existing-github-oidc-app-id> \
     --role "Website Contributor" \
     --scope "$FUNC_ID"
   ```

   Then run the **Deploy snapshot job** workflow by hand. It uses `azure/login` with the federated credential already used for the website. It does not store a client secret or a certificate, and it does not assign roles.

9. Manual test, after the function is deployed. The host key stays in Azure. Do not write it into the repo or a log. The `az` result below is the key, so do not paste the command output into a ticket.

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
