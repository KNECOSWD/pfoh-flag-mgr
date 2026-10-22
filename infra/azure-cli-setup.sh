#!/usr/bin/env bash
set -euo pipefail

# Update these values before running. SUBSCRIPTION_ID must be set to a real
# subscription. The zero id below is refused.
# SQL_PASSWORD has no default. Export it, or run this script in a terminal
# so it can prompt. The value is never printed.
SUBSCRIPTION_ID="00000000-0000-0000-0000-000000000000"
LOCATION="eastus"
RG="PLANO_FLAGS_OF_HONOR"
PLAN="asp-pfoh-flags-prod"
APP="pfoh-flag-manager-app"
SQL_SERVER="pfoh"
SQL_DB="pfoh"
SQL_ADMIN="pfohsqladmin"
SQL_PASSWORD="${SQL_PASSWORD:-}"

# A name is non-production only when dev, test, or sandbox is a whole token.
# The match is case-insensitive and splits on any character that is not a
# letter or digit. "pfoh-dev" is non-production. "devops" and "latest" are not.
is_nonprod_name() {
  local lower="${1,,}"
  [[ "$lower" =~ (^|[^[:alnum:]])(dev|test|sandbox)([^[:alnum:]]|$) ]]
}

# 0 when the resource exists, 1 when Azure reports ResourceNotFound.
# Any other error aborts. ResourceGroupNotFound is not treated as missing.
az_resource_exists() {
  local err
  err="$(mktemp)"
  if az "$@" >/dev/null 2>"$err"; then
    rm -f "$err"
    return 0
  fi
  if grep -Eq '\(ResourceNotFound\)|"code"[[:space:]]*:[[:space:]]*"ResourceNotFound"' "$err"; then
    rm -f "$err"
    return 1
  fi
  local code
  code="$(sed -n 's/^ERROR: (\([^)]*\)).*/\1/p' "$err" | head -n 1)"
  rm -f "$err"
  echo "Refusing to run: Azure CLI failed while checking an existing resource. Only ResourceNotFound is treated as missing. Any other error stops this script so it cannot recreate a resource or reset the SQL admin password." >&2
  if [[ -n "$code" ]]; then
    echo "Azure CLI error code: $code" >&2
  else
    echo "Azure CLI returned an error without a ResourceNotFound code." >&2
  fi
  exit 1
}

require_sql_password() {
  if [[ -z "${SQL_PASSWORD:-}" && -t 0 ]]; then
    read -r -s -p "SQL admin password: " SQL_PASSWORD
    echo
  fi
  if [[ -z "${SQL_PASSWORD:-}" ]]; then
    echo "Refusing to run: set SQL_PASSWORD in the environment, or run this script in a terminal so it can prompt. This script does not use a default SQL admin password." >&2
    exit 1
  fi
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
# A name is production unless dev, test, or sandbox appears as a whole token.
if [[ -n "$prod_hit" ]]; then
  if [[ "${PFOH_ALLOW_PROD_SETUP:-}" != "yes" || "${PFOH_CONFIRM_SUBSCRIPTION:-}" != "$SUBSCRIPTION_ID" ]]; then
    echo "Refusing to run: infra/azure-cli-setup.sh is aimed at production ($prod_hit). Set PFOH_ALLOW_PROD_SETUP=yes and PFOH_CONFIRM_SUBSCRIPTION to the exact subscription id ($SUBSCRIPTION_ID). Names that do not contain a whole dev, test, or sandbox token are production." >&2
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

# az group exists prints true or false and exits 0. A failed check aborts.
# It does not treat an auth or network error as "the group is missing".
group_err="$(mktemp)"
if ! group_state="$(az group exists --name "$RG" -o tsv 2>"$group_err")"; then
  rm -f "$group_err"
  echo "Refusing to run: could not check whether resource group $RG exists. This script stops instead of assuming the group is missing." >&2
  exit 1
fi
rm -f "$group_err"
if [[ "$group_state" != "true" && "$group_state" != "false" ]]; then
  echo "Refusing to run: could not tell whether resource group $RG exists (got ${group_state:-empty})." >&2
  exit 1
fi

# Existing web app or SQL server: refuse before any create. A later create would
# reset the SQL admin password or overwrite the connection string.
if [[ "$group_state" == "true" ]]; then
  if az_resource_exists webapp show --resource-group "$RG" --name "$APP"; then
    echo "Refusing to run: web app $APP already exists. This script does not reset an existing app or overwrite its connection string." >&2
    exit 1
  fi
  if az_resource_exists sql server show --resource-group "$RG" --name "$SQL_SERVER"; then
    echo "Refusing to run: SQL server $SQL_SERVER already exists. This script does not reset the SQL admin password or overwrite the connection string." >&2
    exit 1
  fi
fi

require_sql_password

az group create --name "$RG" --location "$LOCATION"

# Leave an existing plan alone when it is already P1v3. Any other SKU aborts.
# A missing plan is created at P1v3. Any error other than ResourceNotFound aborts.
if az_resource_exists appservice plan show --resource-group "$RG" --name "$PLAN"; then
  plan_sku="$(az appservice plan show --resource-group "$RG" --name "$PLAN" --query sku.name -o tsv)"
  if [[ "${plan_sku,,}" != "p1v3" ]]; then
    echo "Refusing to run: App Service plan $PLAN already exists with SKU ${plan_sku:-unknown}, not P1v3. This script does not change the SKU of an existing plan." >&2
    exit 1
  fi
  echo "App Service plan $PLAN already exists with SKU P1v3. Leaving it in place."
else
  az appservice plan create \
    --resource-group "$RG" \
    --name "$PLAN" \
    --location canadacentral \
    --is-linux \
    --sku P1v3
fi

if az_resource_exists webapp show --resource-group "$RG" --name "$APP"; then
  echo "Refusing to run: web app $APP already exists. This script does not reset an existing app or overwrite its connection string." >&2
  exit 1
fi

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

# Check again immediately before create. Never reset the admin password on a server that exists.
if az_resource_exists sql server show --resource-group "$RG" --name "$SQL_SERVER"; then
  echo "Refusing to run: SQL server $SQL_SERVER already exists. This script does not reset the SQL admin password or overwrite the connection string." >&2
  exit 1
fi

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

# The Azure SQL "Allow Azure services" switch is the AllowAllWindowsAzureIps firewall
# rule (0.0.0.0 through 0.0.0.0). It is off unless PFOH_ALLOW_AZURE_SERVICES=yes.
# For production, prefer a private endpoint instead of this rule.
if [[ "${PFOH_ALLOW_AZURE_SERVICES:-}" == "yes" ]]; then
  az sql server firewall-rule create \
    --resource-group "$RG" \
    --server "$SQL_SERVER" \
    --name AllowAllWindowsAzureIps \
    --start-ip-address 0.0.0.0 \
    --end-ip-address 0.0.0.0
else
  echo "SQL firewall: no Allow Azure services rule was added. Set PFOH_ALLOW_AZURE_SERVICES=yes to add it, or use a private endpoint."
fi

CONN="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Initial Catalog=${SQL_DB};Persist Security Info=False;User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

az webapp config connection-string set \
  --resource-group "$RG" \
  --name "$APP" \
  --connection-string-type SQLAzure \
  --settings DefaultConnection="$CONN"

echo "Created app: https://${APP}.azurewebsites.net"
echo "Next: set AzureAd app settings and configure GitHub OIDC secrets."
