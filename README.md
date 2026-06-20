# Plano Flags of Honor - Azure Flag Manager

A starter Azure web app for Plano Flags of Honor-style public users. It lets external users register/sign in through Microsoft Entra External ID, satisfy MFA, and manage only their own flag records.

## What this repo contains

- `frontend/` - Vite + React + MSAL React
- `backend/PFOH.Api/` - ASP.NET Core 8 Web API + static React hosting
- `backend/PFOH.Api/Data/` - Entity Framework Core SQL Server context
- `.github/workflows/azure-app-service.yml` - GitHub Actions deployment to Azure App Service using OIDC
- `infra/azure-cli-setup.sh` - starter Azure resource creation script

## Architecture

```text
Public user browser
    |
    | 1. Register/sign in with Microsoft-hosted UI
    v
Microsoft Entra External ID tenant
    |
    | 2. Issues ID/access tokens after MFA policy passes
    v
React SPA on Azure App Service
    |
    | 3. Calls /api/flags with Bearer token
    v
ASP.NET Core API on Azure App Service
    |
    | 4. Stores flags by authenticated owner object ID
    v
Azure SQL Database
```

## Data model

The starter model intentionally keeps ownership simple and safe:

- `OwnerObjectId`: the signed-in Entra user object ID from the token.
- `HonoreeName`
- `ServiceBranch`
- `RankOrTitle`
- `FlagNumber`
- `GridLocation`
- `TributeText`
- `Status`: `Draft`, `Submitted`, or `Approved`.

Every API query filters by `OwnerObjectId`, so a user can only read, update, or delete their own flag records.

## Local development

### 1. Requirements

- .NET 8 SDK
- Node.js 22 LTS or current LTS
- Azure SQL, SQL Server LocalDB, or SQL Server Developer Edition
- A Microsoft Entra External ID tenant

### 2. Create app registrations in the External ID tenant

Create **two app registrations** in your Microsoft Entra External ID tenant.

#### API app registration

1. Name: `pfoh-flags-api`
2. Supported account type: accounts in this organizational directory only.
3. Expose an API:
   - Application ID URI: `api://<API_CLIENT_ID>`
   - Scope name: `access_as_user`
   - Who can consent: Admins and users, or Admins only if you want tighter control.
4. Record:
   - Directory tenant ID
   - API application client ID
   - Tenant subdomain, such as `pfoh` from `https://pfoh.ciamlogin.com/...`

#### SPA app registration

1. Name: `pfoh-flags-spa`
2. Platform: Single-page application
3. Redirect URIs:
   - `http://localhost:5173`
   - `https://<your-app-name>.azurewebsites.net`
4. API permissions:
   - Add permission to your API app registration.
   - Select delegated scope `access_as_user`.
   - Grant admin consent if your tenant requires it.

### 3. Create a sign-up/sign-in user flow

In the External ID tenant:

1. Go to **Entra ID > External Identities > User flows**.
2. Create a user flow such as `SignUpSignIn`.
3. Choose sign-in methods such as email with password.
4. Select user attributes you want to collect, such as display name, given name, surname, city, state, or custom attributes.
5. Add the SPA app registration to the user flow.

### 4. Enable MFA

In the External ID tenant:

1. Go to **Protection > Conditional Access**.
2. Create a policy such as `Require MFA - PFOH Flag Manager`.
3. Users: include all users, but exclude break-glass/admin emergency accounts.
4. Target resources/cloud apps: select the SPA/API app, or all cloud apps if appropriate.
5. Grant: require multifactor authentication.
6. Enable the policy.

Also review **Entra ID > Authentication methods** so your desired MFA second factors are available.

### 5. Configure frontend

Copy the example environment file:

```bash
cd frontend
cp .env.example .env
```

Edit `.env`:

```text
VITE_TENANT_SUBDOMAIN=pfoh
VITE_TENANT_ID=<external-tenant-id>
VITE_SPA_CLIENT_ID=<spa-client-id>
VITE_API_CLIENT_ID=<api-client-id>
VITE_API_BASE_URL=https://localhost:7050
```

For local split frontend/API development, keep `VITE_API_BASE_URL` pointing to your API. For production same-origin hosting, leave it blank.

### 6. Configure backend

Use user secrets for local development:

```bash
cd backend/PFOH.Api
dotnet user-secrets init
dotnet user-secrets set "AzureAd:Instance" "https://<tenant-subdomain>.ciamlogin.com/"
dotnet user-secrets set "AzureAd:TenantId" "<external-tenant-id>"
dotnet user-secrets set "AzureAd:ClientId" "<api-client-id>"
dotnet user-secrets set "AzureAd:Audience" "api://<api-client-id>"
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=(localdb)\\mssqllocaldb;Database=PfohFlagManager;Trusted_Connection=True;MultipleActiveResultSets=true"
```

### 7. Run locally

Terminal 1:

```bash
cd backend/PFOH.Api
dotnet run
```

Terminal 2:

```bash
cd frontend
npm install
npm run dev
```

Open `http://localhost:5173`.

## Deploy to Azure with GitHub Actions

### 1. Create the Azure resources

Edit `infra/azure-cli-setup.sh`, then run it:

```bash
cd infra
./azure-cli-setup.sh
```

It creates:

- Resource group
- Linux App Service Plan
- Azure App Service for .NET 8
- Azure SQL Database
- App Service SQL connection string

For production, replace the simple SQL username/password with managed identity + Azure SQL access or Key Vault references.

### 2. Configure App Service environment variables

In Azure Portal, open the App Service, then go to **Settings > Environment variables** and add:

| Name | Value |
|---|---|
| `AzureAd__Instance` | `https://<tenant-subdomain>.ciamlogin.com/` |
| `AzureAd__TenantId` | `<external-tenant-id>` |
| `AzureAd__ClientId` | `<api-client-id>` |
| `AzureAd__Audience` | `api://<api-client-id>` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |

The SQL connection string can be stored under **Connection strings** as `DefaultConnection` with type `SQLAzure`.

### 3. Configure GitHub repository secrets

In GitHub, go to **Settings > Secrets and variables > Actions** and add:

| Secret | Purpose |
|---|---|
| `ENTRA_TENANT_SUBDOMAIN` | External ID tenant subdomain, such as `pfoh` |
| `ENTRA_TENANT_ID` | External ID tenant ID |
| `ENTRA_SPA_CLIENT_ID` | SPA app registration client ID |
| `ENTRA_API_CLIENT_ID` | API app registration client ID |
| `AZURE_CLIENT_ID` | GitHub deployment app/client ID |
| `AZURE_TENANT_ID` | Azure tenant ID for deployment identity |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |

### 4. Create GitHub OIDC deployment identity

Replace the placeholders and run from Azure CLI. This avoids storing an Azure client secret in GitHub.

```bash
SUBSCRIPTION_ID="<subscription-id>"
RG="rg-pfoh-flags-prod"
APP_NAME="pfoh-flag-manager-app"
GITHUB_ORG="<github-user-or-org>"
GITHUB_REPO="<repo-name>"
BRANCH="main"

APP_ID=$(az ad app create --display-name "gha-pfoh-flags-deploy" --query appId -o tsv)
az ad sp create --id "$APP_ID"

SCOPE=$(az webapp show --resource-group "$RG" --name "$APP_NAME" --query id -o tsv)
az role assignment create --assignee "$APP_ID" --role "Website Contributor" --scope "$SCOPE"

cat > federated-credential.json <<JSON
{
  "name": "github-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:${GITHUB_ORG}/${GITHUB_REPO}:ref:refs/heads/${BRANCH}",
  "description": "GitHub Actions main branch deployment",
  "audiences": ["api://AzureADTokenExchange"]
}
JSON

az ad app federated-credential create --id "$APP_ID" --parameters federated-credential.json

echo "AZURE_CLIENT_ID=$APP_ID"
echo "AZURE_TENANT_ID=$(az account show --query tenantId -o tsv)"
echo "AZURE_SUBSCRIPTION_ID=$SUBSCRIPTION_ID"
```

Add those three values to GitHub Actions secrets.

### 5. Update workflow app name

Open `.github/workflows/azure-app-service.yml` and update:

```yaml
env:
  AZURE_WEBAPP_NAME: pfoh-flag-manager-app
```

Use your actual App Service name.

### 6. Push to GitHub

```bash
git init
git add .
git commit -m "Initial PFOH flag manager"
git branch -M main
git remote add origin https://github.com/<github-user-or-org>/<repo-name>.git
git push -u origin main
```

The workflow will build React, copy it into the ASP.NET Core `wwwroot`, publish the .NET API, and deploy one combined app to Azure App Service.

### 7. Final Entra redirect URI update

After the first deployment, confirm the SPA app registration has this redirect URI:

```text
https://<your-app-name>.azurewebsites.net
```

Then test:

1. Browse to the App Service URL.
2. Select **Register / sign in**.
3. Create a new account.
4. Complete MFA.
5. Add a flag.
6. Sign out, sign in as a different user, and confirm the previous user's flags are not visible.

## Production hardening checklist

- Replace `EnsureCreated()` with EF Core migrations in a controlled deployment step.
- Add admin/moderator roles for approving submitted flags.
- Add a separate public read-only honoree lookup endpoint if the public site should display approved records.
- Move SQL authentication to managed identity or Key Vault.
- Add App Service deployment slots for staging/production swap.
- Add Application Insights.
- Add rate limiting and audit logs.
- Add file upload support to Azure Blob Storage if users will attach honoree photos or documents.
- Add data export/backups before the event.

## Important implementation notes

- User registration is handled by Microsoft Entra External ID user flows, not by storing passwords in this app.
- MFA is enforced by Conditional Access, not by custom application code.
- The API treats the token's user object ID as the ownership boundary.
- The React app uses MSAL to acquire an access token and sends it as a Bearer token to the API.
