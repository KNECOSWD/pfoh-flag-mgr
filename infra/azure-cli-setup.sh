#!/usr/bin/env bash
set -euo pipefail

# Update these values before running. SUBSCRIPTION_ID must be set to a real
# subscription. The zero id below is refused.
SUBSCRIPTION_ID="00000000-0000-0000-0000-000000000000"
LOCATION="eastus"
RG="PLANO_FLAGS_OF_HONOR"
PLAN="asp-pfoh-flags-prod"
APP="pfoh-flag-manager-app"
SQL_SERVER="pfoh"
SQL_DB="pfoh"
SQL_ADMIN="pfohsqladmin"
SQL_PASSWORD="ChangeThisToAStrongPassword_12345!"

# A name is non-production only when it contains dev, test, or sandbox.
# The match is case-insensitive. "latest" contains "test" and counts as non-prod.
is_nonprod_name() {
  local lower="${1,,}"
  [[ "$lower" == *dev* || "$lower" == *test* || "$lower" == *sandbox* ]]
}

prod_hit=""
for pair in \
  "resource group:$RG" \
  "plan:$PLAN" \
  "app:$APP" \
  "SQL server:$SQL_SERVER" \
  "SQL database:$SQL_DB"
do
  label="${pair%%:*}"
  value="${pair#*:}"
  if ! is_nonprod_name "$value"; then
    prod_hit="${prod_hit:+$prod_hit, }$label $value"
  fi
done

if [[ -z "${SUBSCRIPTION_ID}" || "${SUBSCRIPTION_ID}" == "00000000-0000-0000-0000-000000000000" ]]; then
  echo "Refusing to run: set SUBSCRIPTION_ID in infra/azure-cli-setup.sh to the subscription this script should use." >&2
  exit 1
fi

# Production names need both flags. Either one missing or mismatched refuses.
# PFOH_CONFIRM_SUBSCRIPTION must be the subscription id this script is about to use.
if [[ -n "$prod_hit" ]]; then
  if [[ "${PFOH_ALLOW_PROD_SETUP:-}" != "yes" || "${PFOH_CONFIRM_SUBSCRIPTION:-}" != "$SUBSCRIPTION_ID" ]]; then
    echo "Refusing to run: infra/azure-cli-setup.sh is aimed at production ($prod_hit). Set PFOH_ALLOW_PROD_SETUP=yes and PFOH_CONFIRM_SUBSCRIPTION to the exact subscription id ($SUBSCRIPTION_ID). Names that do not contain dev, test, or sandbox are production." >&2
    exit 1
  fi
fi

echo "Using subscription $SUBSCRIPTION_ID"
az account set --subscription "$SUBSCRIPTION_ID"
active="$(az account show --query id -o tsv)"
echo "Active subscription $active"
if [[ "$active" != "$SUBSCRIPTION_ID" ]]; then
  echo "Refusing to run: active subscription $active does not match SUBSCRIPTION_ID $SUBSCRIPTION_ID." >&2
  exit 1
fi
if [[ -n "$prod_hit" && "${PFOH_CONFIRM_SUBSCRIPTION:-}" != "$active" ]]; then
  echo "Refusing to run: PFOH_CONFIRM_SUBSCRIPTION does not match the active subscription $active." >&2
  exit 1
fi

# Existing resources are refused even when both production flags are set.
# A create would reset the SQL admin password and overwrite the connection string.
if az webapp show --resource-group "$RG" --name "$APP" >/dev/null 2>&1; then
  echo "Refusing to run: web app $APP already exists. This script does not reset an existing app or overwrite its connection string." >&2
  exit 1
fi
if az sql server show --resource-group "$RG" --name "$SQL_SERVER" >/dev/null 2>&1; then
  echo "Refusing to run: SQL server $SQL_SERVER already exists. This script does not reset the SQL admin password or overwrite the connection string." >&2
  exit 1
fi

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
