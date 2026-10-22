targetScope = 'resourceGroup'

@description('Region for the snapshot function, storage, and Key Vault. Required. The resource group PLANO_FLAGS_OF_HONOR is eastus and the App Service plan is canadacentral, so pass canadacentral. This parameter has no default.')
param location string

@description('Existing App Service plan. The function shares it and does not create a plan.')
param appServicePlanName string

@description('Existing Azure SQL server name, without the protocol or suffix.')
param sqlServerName string

@description('Existing Azure SQL database name. This file does not create the server or database.')
param sqlDatabaseName string

@description('Days to keep report blobs. Default is 30.')
@minValue(1)
@maxValue(3650)
param snapshotRetentionDays int = 30

@description('External ID tenant id for directory reads. This is not a secret.')
param graphTenantId string = '77f89427-b4a7-43ed-92fc-3eb5bd63d0f1'

@description('Client id of the Graph app registration in that tenant. Empty until the app exists.')
param graphClientId string = ''

@description('Object id of the certificate reader service principal. Empty skips that role.')
param readerPrincipalId string = ''

var blobContributorRole = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var blobReaderRole = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
var blobOwnerRole = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var queueContributorRole = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var tableContributorRole = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var keyVaultSecretsUserRole = '4633458b-17de-408a-b874-0445c86b69e6'

resource plan 'Microsoft.Web/serverfarms@2023-12-01' existing = {
  name: appServicePlanName
}

resource jobIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-pfoh-snapshot'
  location: location
}

resource reports 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stpfohrpt${take(uniqueString(resourceGroup().id), 10)}'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowCrossTenantReplication: false
    accessTier: 'Hot'
  }
}

resource reportsBlob 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: reports
  name: 'default'
}

resource reportsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: reportsBlob
  name: 'pfoh-reports'
  properties: {
    publicAccess: 'None'
  }
}

resource reportsLifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: reports
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          enabled: true
          name: 'delete-old-reports'
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                'pfoh-reports/'
              ]
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: snapshotRetentionDays
                }
              }
            }
          }
        }
      ]
    }
  }
}

resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stpfohhost${take(uniqueString(resourceGroup().id), 9)}'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowCrossTenantReplication: false
  }
}

resource hostBlobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, jobIdentity.id, blobOwnerRole)
  scope: hostStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobOwnerRole)
    principalId: jobIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource hostQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, jobIdentity.id, queueContributorRole)
  scope: hostStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', queueContributorRole)
    principalId: jobIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource hostTable 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, jobIdentity.id, tableContributorRole)
  scope: hostStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', tableContributorRole)
    principalId: jobIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource reportsContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(reportsContainer.id, jobIdentity.id, blobContributorRole)
  scope: reportsContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributorRole)
    principalId: jobIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-02-01' = {
  name: 'kv-pfoh-${take(uniqueString(resourceGroup().id), 12)}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

resource vaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, jobIdentity.id, keyVaultSecretsUserRole)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRole)
    principalId: jobIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource reportsReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(readerPrincipalId)) {
  name: guid(reportsContainer.id, readerPrincipalId, blobReaderRole)
  scope: reportsContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobReaderRole)
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-pfoh-snapshot'
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${jobIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    reserved: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      alwaysOn: true
      appSettings: [
        {
          name: 'AzureWebJobsStorage__accountName'
          value: hostStorage.name
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'AzureWebJobsStorage__clientId'
          value: jobIdentity.properties.clientId
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: jobIdentity.properties.clientId
        }
        {
          name: 'Snapshot__StorageAccountName'
          value: reports.name
        }
        {
          name: 'Snapshot__StorageContainer'
          value: 'pfoh-reports'
        }
        {
          name: 'Snapshot__ManagedIdentityClientId'
          value: jobIdentity.properties.clientId
        }
        {
          name: 'Snapshot__GraphTenantId'
          value: graphTenantId
        }
        {
          name: 'Snapshot__GraphClientId'
          value: graphClientId
        }
        {
          name: 'Snapshot__GraphCertificateName'
          value: 'pfoh-snapshot-graph'
        }
        {
          name: 'Snapshot__KeyVaultUri'
          value: vault.properties.vaultUri
        }
        {
          name: 'Snapshot__SqlConnectionString'
          value: 'Server=tcp:${sqlServerName}.database.windows.net,1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;User ID=${jobIdentity.properties.clientId};Encrypt=True;'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
      ]
    }
  }
}

resource functionScmPublishing 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: functionApp
  name: 'scm'
  properties: {
    allow: false
  }
}

resource functionFtpPublishing 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: functionApp
  name: 'ftp'
  properties: {
    allow: false
  }
}

output functionName string = functionApp.name
output reportsAccountName string = reports.name
output reportsContainerId string = reportsContainer.id
output jobIdentityClientId string = jobIdentity.properties.clientId
output jobIdentityPrincipalId string = jobIdentity.properties.principalId
output keyVaultName string = vault.name
output keyVaultUri string = vault.properties.vaultUri
