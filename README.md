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

### 3. Sign-up user flow (live)

Tenant `pfohflags.onmicrosoft.com` (`77f89427-b4a7-43ed-92fc-3eb5bd63d0f1`) already has this configuration. Do not change it from the app.

User flow **SignUpSignIn** is attached to SPA app **pfoh-flags-spa** (`06e5ea83-b3c7-4d53-9513-9ddbcfc2d434`).

Already live:

- Custom user attributes, type String: **Phone** and **Organization**.
- The user flow collects only **Email**, **Display Name** (required), **Phone** (required, label `Mobile phone (e.g. +1 2145551234)`), and **Organization** (optional).
- Identity provider: **Email with password**. Email one-time passcode is offered and is not selected.
- SMS is not enabled. No Conditional Access changes were made.

Entra does not check the phone as E.164. The app does, including the spaced example `+1 2145551234`.

The custom attributes are stored on **b2c-extensions-app**. They are not in the token until a claims-mapping policy emits them. This app does not read app-registration `optionalClaims`. Set `Entra:ExtensionsAppId` (and `VITE_ENTRA_EXTENSIONS_APP_ID`) to the b2c-extensions-app application id. The API then reads `extension_{that id without dashes}_Phone` and `extension_{that id without dashes}_Organization` from the access token. The SPA reads the same names from the ID token when the profile value is still blank.

Remaining admin step, on the API enterprise application and on pfoh-flags-spa: **Single sign-on > Attributes & Claims > Add new claim**, Source **Directory schema extension**, application **b2c-extensions-app**. Name the claims exactly `extension_{id-without-dashes}_Phone` and `extension_{id-without-dashes}_Organization`. Set `acceptMappedClaims` to true on both app manifests. SMS stays off until the per-message charge is approved.

### 4. Enable MFA

SMS multifactor authentication is not enabled in the live tenant, and no Conditional Access policy was added for it. Leave it that way until the SMS charge is approved. Email with password remains the first factor.

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
VITE_ENTRA_EXTENSIONS_APP_ID=<b2c-extensions-app-client-id>
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
dotnet user-secrets set "Entra:ExtensionsAppId" "<b2c-extensions-app-client-id>"
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

### Flag Map Excel export

Sign in as a `PFOH.Admin` user, open **Flag Map**, and click **Export Excel** next to **Refresh Flag Map**. A dialog lists the fields below, all checked. Uncheck a field to leave that column out of the workbook, then click **Export Excel** again. At least one field is required.

The API (`GET /api/admin/review/flag-map-export`, same admin-only policy as the map) returns `pfoh-flag-map-YYYY-MM-DD.xlsx`. Optional query `columns` is a comma-separated list of `firstName`, `lastName`, `fullName`, `flagGrid`, `status`, `rank`, `serviceBranch`, `sponsorName`, and `kia`. Omit `columns` to include every field. Any other name is rejected. The workbook has one sheet, **Flag Map**, and one Excel table, **FlagMap**, with AutoFilter on so Excel can filter and sort. Columns stay in this order:

| Column | Source |
| --- | --- |
| First Name | Given names before the surname on the existing display name. A middle name stored on the honoree stays with those given names. There is no separate Middle column. |
| Last Name | Surname, plus a trailing Jr, Sr, II, III, or IV. A trailing `(Nickname)` stays on the last name, as in the current name split. |
| Full Name | Existing display name: given names, surname, optional suffix, optional trailing nickname. |
| Flag Grid | `FlagGrid.FlagGridName`, such as `A-01`. |
| Status | Flag-map status: `Reserved` when the grid is reserved, otherwise `Open` when it has no honoree, otherwise `Occupied`. |
| Rank | `Honoree.Rank`. |
| Service Branch | `Honoree.ServiceBranch.ServiceBranchName`. |
| Sponsor Name | Honoree sponsor join: sponsor first, middle, last, and suffix. |
| KIA | `Honoree.KIA`, written `Yes` or `No`. |

Rows are seats that have an honoree, sorted by section then grid number (`A-01`, `A-02`, `A-10`, `B-01`). Open grids and reserved grids with no honoree are left out. The Review page honoree export is unchanged.

### Your profile

Signed-in users open **Profile** from the header (and from the mobile menu). The form asks for **Name** (stored as the display name), **Email**, **Phone**, and optional **Organization**. Email comes from the Entra token and stays read-only. Name, email, and phone are required. Phone must be E.164, such as `+1 2145551234` (stored as `+12145551234`). Entra does not validate that format. The profile is stored in `dbo.UserProfiles`, keyed by the token object id (`OwnerObjectId`). The saved name replaces a placeholder token name such as `unknown` in the header and is copied onto that user's flag claims.

There are no city or state fields on the account profile. `dbo.Sponsors` still has City and State because those columns hold sponsor mailing addresses, and dropping them would delete existing data.

Display name is trimmed, required, and limited to 100 characters. Organization is optional and limited to 200 characters. Users can read and update only their own profile. `GET /api/profile` and `PUT /api/profile` require a signed-in user. `PUT /api/profile/{ownerObjectId}` returns 403 when the id is not the caller.

If the access token includes `extension_{extensionsAppIdWithoutDashes}_Phone` or `extension_{extensionsAppIdWithoutDashes}_Organization`, the API copies a missing value onto the profile. `extensionsAppId` is `Entra:ExtensionsAppId`, the b2c-extensions-app application id, and is not hard-coded. A missing claim is left blank. A saved phone or organization is not overwritten by a different claim. Those claims are not optionalClaims in the app manifest. They appear only after the claims-mapping step in the sign-up section above. The app still requires an E.164 phone number on save.

The API creates `dbo.UserProfiles` on first use if the table is missing, and adds Phone and Organization when an older profile table is already present. It does not change the existing honoree or sponsor tables. If the database login cannot create tables, run this once against the app database:

```sql
IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[UserProfiles] (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_UserProfiles] PRIMARY KEY,
        [OwnerObjectId] nvarchar(128) NOT NULL,
        [DisplayName] nvarchar(100) NOT NULL,
        [Email] nvarchar(255) NOT NULL CONSTRAINT [DF_UserProfiles_Email] DEFAULT (N''),
        [Phone] nvarchar(16) NOT NULL CONSTRAINT [DF_UserProfiles_Phone] DEFAULT (N''),
        [Organization] nvarchar(200) NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [UpdatedUtc] datetime2 NOT NULL,
        CONSTRAINT [UQ_UserProfiles_OwnerObjectId] UNIQUE ([OwnerObjectId])
    );
END

IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL AND COL_LENGTH(N'[dbo].[UserProfiles]', N'Phone') IS NULL
BEGIN
    ALTER TABLE [dbo].[UserProfiles]
        ADD [Phone] nvarchar(16) NOT NULL CONSTRAINT [DF_UserProfiles_Phone] DEFAULT (N'');
END

IF OBJECT_ID(N'[dbo].[UserProfiles]', N'U') IS NOT NULL AND COL_LENGTH(N'[dbo].[UserProfiles]', N'Organization') IS NULL
BEGIN
    ALTER TABLE [dbo].[UserProfiles]
        ADD [Organization] nvarchar(200) NULL;
END
```

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
| `Entra__ExtensionsAppId` | b2c-extensions-app application (client) id |
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
| `ENTRA_EXTENSIONS_APP_ID` | b2c-extensions-app application id, baked into the SPA as `VITE_ENTRA_EXTENSIONS_APP_ID` |
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
