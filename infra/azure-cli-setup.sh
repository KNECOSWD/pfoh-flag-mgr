#!/usr/bin/env bash
set -euo pipefail

# Update these values before running.
SUBSCRIPTION_ID="00000000-0000-0000-0000-000000000000"
LOCATION="eastus"
RG="PLANO_FLAGS_OF_HONOR"
PLAN="asp-pfoh-flags-prod"
APP="pfoh-flag-manager-app"
SQL_SERVER="pfoh"
SQL_DB="pfoh"
SQL_ADMIN="pfohsqladmin"
SQL_PASSWORD="ChangeThisToAStrongPassword_12345!"

# The defaults above are the live production names. Refuse them unless the
# operator sets the override. An empty value does not count.
if [[ "${PFOH_ALLOW_PROD_SETUP:-}" != "yes" ]]; then
  prod_hit=""
  [[ "$RG" == "PLANO_FLAGS_OF_HONOR" ]] && prod_hit="resource group PLANO_FLAGS_OF_HONOR"
  [[ "$PLAN" == "asp-pfoh-flags-prod" ]] && prod_hit="${prod_hit:+$prod_hit, }plan asp-pfoh-flags-prod"
  [[ "$APP" == "pfoh-flag-manager-app" ]] && prod_hit="${prod_hit:+$prod_hit, }app pfoh-flag-manager-app"
  [[ "$SQL_SERVER" == "pfoh" ]] && prod_hit="${prod_hit:+$prod_hit, }SQL server pfoh"
  [[ "$SQL_DB" == "pfoh" ]] && prod_hit="${prod_hit:+$prod_hit, }SQL database pfoh"
  if [[ -n "$prod_hit" ]]; then
    echo "Refusing to run: infra/azure-cli-setup.sh is aimed at production ($prod_hit). Set PFOH_ALLOW_PROD_SETUP=yes to override." >&2
    exit 1
  fi
fi

az account set --subscription "$SUBSCRIPTION_ID"
az group create --name "$RG" --location "$LOCATION"

az appservice plan create \
  --resource-group "$RG" \
  --name "$PLAN" \
  --location canadacentral \
  --is-linux \
  --sku P1v3

az webapp create \
  --resource-group "$RG" \
  --plan "$PLAN" \
  --name "$APP" \
  --runtime "DOTNETCORE:8.0"

az webapp config set \
  --resource-group "$RG" \
  --name "$APP" \
  --always-on true \
  --ftps-state Disabled \
  --http20-enabled true

az sql server create \
  --resource-group "$RG" \
  --location "$LOCATION" \
  --name "$SQL_SERVER" \
  --admin-user "$SQL_ADMIN" \
  --admin-password "$SQL_PASSWORD"

az sql db create \
  --resource-group "$RG" \
  --server "$SQL_SERVER" \
  --name "$SQL_DB" \
  --service-objective Basic

# Allows Azure services to reach SQL. For tighter production security, use private endpoint/VNet integration.
az sql server firewall-rule create \
  --resource-group "$RG" \
  --server "$SQL_SERVER" \
  --name AllowAzureServices \
  --start-ip-address 0.0.0.0 \
  --end-ip-address 0.0.0.0

CONN="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Initial Catalog=${SQL_DB};Persist Security Info=False;User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

az webapp config connection-string set \
  --resource-group "$RG" \
  --name "$APP" \
  --connection-string-type SQLAzure \
  --settings DefaultConnection="$CONN"

echo "Created app: https://${APP}.azurewebsites.net"
echo "Next: set AzureAd app settings and configure GitHub OIDC secrets."
