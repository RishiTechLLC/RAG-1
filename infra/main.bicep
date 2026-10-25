// =====================================================================================
// RAG + MCP + multi-agent platform - all Azure resources for one environment.
//
//   Storage (documents) ─► Rag.Api ─► Azure AI Search (vectors) ◄─ Agents.Api ─► Static Web App (Angular)
//                                         Azure OpenAI ◄──────────────┤
//   Azure SQL ◄─ Students.Api ◄─ Mcp.Server ◄─────────────────────────┘
//
// Every app runs on Linux App Service with one user-assigned managed identity, so there
// are no keys or passwords in app settings (except the optional MCP shared secret).
// Deploy: az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam
// =====================================================================================

targetScope = 'resourceGroup'

@description('Short environment name used in resource names, e.g. "ragdemo-dev".')
@minLength(3)
@maxLength(16)
param environmentName string

@description('Region for all resources except the Static Web App.')
param location string = resourceGroup().location

@description('Static Web Apps is only offered in a few regions.')
@allowed(['westus2', 'centralus', 'eastus2', 'westeurope', 'eastasia'])
param staticWebAppLocation string = 'eastus2'

@description('Region for Azure OpenAI (model availability differs per region).')
param openAiLocation string = location

@description('Chat model used by the agents and the RAG answer endpoint.')
param chatModelName string = 'gpt-5-mini'
param chatModelVersion string = '2025-08-07'
@description('Thousands of tokens per minute for the chat deployment.')
param chatModelCapacity int = 50

@description('Embedding model used for chunk vectors.')
param embeddingModelName string = 'text-embedding-3-small'
param embeddingModelVersion string = '1'
param embeddingDimensions int = 1536
param embeddingModelCapacity int = 120

@description('Azure AI Search tier. "basic" supports semantic ranking (free plan); "free" does not support managed identity RBAC well.')
@allowed(['basic', 'standard', 'standard2'])
param searchSku string = 'basic'

@description('App Service plan SKU for the four APIs.')
param appServiceSku string = 'B1'

@description('Object id of the GitHub Actions service principal (OIDC). Grants it data access so the ingestion workflow can run. Leave empty to skip.')
param deployerPrincipalId string = ''

@description('Object id of a developer/user to grant the same data-plane roles (for local debugging against Azure). Leave empty to skip.')
param developerPrincipalId string = ''

@description('Where the student database lives: "OnPrem" = your own SQL Server reached through an App Service Hybrid Connection; "Azure" = a new Azure SQL database.')
@allowed(['OnPrem', 'Azure'])
param sqlMode string = 'OnPrem'

@description('OnPrem: host name of the SQL Server exactly as the on-prem network resolves it (e.g. "SQLSRV01" or "sqlsrv01.corp.local"). Must match the Server= value in the connection string.')
param onPremSqlHost string = ''

@description('OnPrem: TCP port of the SQL Server. Hybrid Connections need a fixed port (named instances must be pinned to one).')
param onPremSqlPort int = 1433

@description('OnPrem: SQL Server connection string using a SQL login (Windows auth is not possible from App Service), e.g. "Server=tcp:SQLSRV01,1433;Database=students;User Id=app_students;Password=...;TrustServerCertificate=True". Stored in Key Vault.')
@secure()
param onPremSqlConnectionString string = ''

@description('Shared secret between Agents.Api and the MCP server. Defaults to a new random value on every deployment.')
@secure()
param mcpApiKey string = newGuid()

var suffix = substring(uniqueString(subscription().id, resourceGroup().id, environmentName), 0, 6)
var prefix = toLower('${environmentName}-${suffix}')
var tags = { environment: environmentName, workload: 'rag-multi-agent' }
var useOnPremSql = sqlMode == 'OnPrem'


// ---------------------------------------------------------------------------------------
// Monitoring
// ---------------------------------------------------------------------------------------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${prefix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${prefix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------------------------------------------------------------------------------------
// Identity shared by all apps
// ---------------------------------------------------------------------------------------
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${prefix}'
  location: location
  tags: tags
}

// ---------------------------------------------------------------------------------------
// Storage account with the "documents" container (RAG source documents)
// ---------------------------------------------------------------------------------------
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  #disable-next-line BCP334 // prefix is always >= 10 characters
  name: take('st${replace(prefix, '-', '')}', 24)
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // Entra ID only
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource documentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'documents'
}

// ---------------------------------------------------------------------------------------
// Azure AI Search (vector store)
// ---------------------------------------------------------------------------------------
resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: 'srch-${prefix}'
  location: location
  tags: tags
  sku: { name: searchSku }
  properties: {
    replicaCount: 1
    partitionCount: 1
    semanticSearch: 'free'
    // Keys stay available for quick experiments, RBAC is what the apps use.
    authOptions: {
      aadOrApiKey: { aadAuthFailureMode: 'http401WithBearerChallenge' }
    }
  }
}

// ---------------------------------------------------------------------------------------
// Azure OpenAI
// ---------------------------------------------------------------------------------------
resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: 'oai-${prefix}'
  location: openAiLocation
  tags: tags
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: 'oai-${prefix}'
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true // Entra ID only
  }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: chatModelName
  sku: { name: 'GlobalStandard', capacity: chatModelCapacity }
  properties: {
    model: { format: 'OpenAI', name: chatModelName, version: chatModelVersion }
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: embeddingModelName
  sku: { name: 'GlobalStandard', capacity: embeddingModelCapacity }
  properties: {
    model: { format: 'OpenAI', name: embeddingModelName, version: embeddingModelVersion }
  }
  dependsOn: [chatDeployment] // deployments on one account must be created sequentially
}

// ---------------------------------------------------------------------------------------
// Option 1 (sqlMode = 'OnPrem'): your own SQL Server through an App Service Hybrid Connection.
//   Azure Relay hybrid connection <- Hybrid Connection Manager (installed on-prem) -> SQLHOST:PORT
//   The connection string (SQL login) lives in Key Vault; the app reads it with its managed identity.
// ---------------------------------------------------------------------------------------
resource relay 'Microsoft.Relay/namespaces@2021-11-01' = if (useOnPremSql) {
  name: 'relay-${prefix}'
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' }
}

resource sqlHybridConnection 'Microsoft.Relay/namespaces/hybridConnections@2021-11-01' = if (useOnPremSql) {
  parent: relay
  name: 'onprem-sql'
  properties: {
    requiresClientAuthorization: true
    // App Service and the Hybrid Connection Manager read the target endpoint from this metadata.
    userMetadata: '[{"key":"endpoint","value":"${onPremSqlHost}:${onPremSqlPort}"}]'
  }
}

resource sqlHybridSendRule 'Microsoft.Relay/namespaces/hybridConnections/authorizationRules@2021-11-01' = if (useOnPremSql) {
  parent: sqlHybridConnection
  name: 'defaultSender'
  properties: { rights: ['Send'] }
}

resource sqlHybridListenRule 'Microsoft.Relay/namespaces/hybridConnections/authorizationRules@2021-11-01' = if (useOnPremSql) {
  parent: sqlHybridConnection
  name: 'defaultListener'
  properties: { rights: ['Listen'] }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = if (useOnPremSql) {
  name: take('kv-${replace(prefix, '-', '')}', 24)
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource studentsDbSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (useOnPremSql) {
  parent: keyVault
  name: 'StudentsDb'
  properties: { value: onPremSqlConnectionString }
}

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (useOnPremSql) {
  scope: keyVault
  name: guid(keyVault.id, identity.id, '4633458b-17de-408a-b874-0445c86b69e6')
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User
  }
}

// ---------------------------------------------------------------------------------------
// Option 2 (sqlMode = 'Azure'): Azure SQL (student information) - Entra-only authentication
// The managed identity is the Entra admin so Students.Api can create the schema on first run.
// For production, make a group the admin and grant the app a contained user with
// db_datareader/db_datawriter instead.
// ---------------------------------------------------------------------------------------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = if (!useOnPremSql) {
  name: 'sql-${prefix}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: identity.name
      sid: identity.properties.principalId
      tenantId: subscription().tenantId
      principalType: 'Application'
    }
  }
}

resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01' = if (!useOnPremSql) {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = if (!useOnPremSql) {
  parent: sqlServer
  name: 'students'
  location: location
  tags: tags
  sku: { name: 'Basic', tier: 'Basic' }
}

// ---------------------------------------------------------------------------------------
// Role assignments (apps' managed identity + optional deployer / developer)
// ---------------------------------------------------------------------------------------
module appDataAccess 'modules/data-access.bicep' = {
  name: 'data-access-apps'
  params: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    storageAccountName: storage.name
    searchServiceName: search.name
    openAiAccountName: openAi.name
  }
}

module deployerDataAccess 'modules/data-access.bicep' = if (!empty(deployerPrincipalId)) {
  name: 'data-access-deployer'
  params: {
    principalId: deployerPrincipalId
    principalType: 'ServicePrincipal'
    storageAccountName: storage.name
    searchServiceName: search.name
    openAiAccountName: openAi.name
  }
}

module developerDataAccess 'modules/data-access.bicep' = if (!empty(developerPrincipalId)) {
  name: 'data-access-developer'
  params: {
    principalId: developerPrincipalId
    principalType: 'User'
    storageAccountName: storage.name
    searchServiceName: search.name
    openAiAccountName: openAi.name
  }
}

// ---------------------------------------------------------------------------------------
// Front end: Static Web App (Angular)
// ---------------------------------------------------------------------------------------
resource staticWebApp 'Microsoft.Web/staticSites@2024-04-01' = {
  name: 'swa-${prefix}'
  location: staticWebAppLocation
  tags: union(tags, { service: 'chat-ui' })
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

// ---------------------------------------------------------------------------------------
// App Service plan + the four APIs
// ---------------------------------------------------------------------------------------
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-${prefix}'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: appServiceSku }
  properties: { reserved: true }
}

var commonSettings = [
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
]

var openAiSettings = [
  { name: 'AzureOpenAI__Endpoint', value: openAi.properties.endpoint }
  { name: 'AzureOpenAI__ChatDeployment', value: chatDeployment.name }
  { name: 'AzureOpenAI__EmbeddingDeployment', value: embeddingDeployment.name }
  { name: 'AzureOpenAI__EmbeddingDimensions', value: string(embeddingDimensions) }
]

var storageSettings = [
  { name: 'Storage__AccountUri', value: storage.properties.primaryEndpoints.blob }
  { name: 'Storage__ContainerName', value: documentsContainer.name }
]

// Per-app settings (in addition to commonSettings). Downstream URLs use the deterministic app names.
var serviceSettings = {
  'rag-api': concat(openAiSettings, storageSettings, [
    { name: 'Search__Endpoint', value: 'https://${search.name}.search.windows.net' }
    { name: 'Search__IndexName', value: 'rag-chunks' }
  ])
  'students-api': [
    {
      name: 'ConnectionStrings__StudentsDb'
      value: useOnPremSql
        ? '@Microsoft.KeyVault(SecretUri=${studentsDbSecret!.properties.secretUri})'
        : 'Server=tcp:${sqlServer!.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase!.name};Authentication=Active Directory Managed Identity;User Id=${identity.properties.clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;'
    }
    { name: 'Database__InitializeOnStartup', value: 'true' }
  ]
  'mcp-server': concat(storageSettings, [
    { name: 'StudentsApi__Url', value: 'https://${prefix}-students-api.azurewebsites.net' }
    { name: 'Mcp__ApiKey', value: mcpApiKey }
  ])
  'agents-api': concat(openAiSettings, [
    { name: 'Downstream__RagApiUrl', value: 'https://${prefix}-rag-api.azurewebsites.net' }
    { name: 'Downstream__McpServerUrl', value: 'https://${prefix}-mcp-server.azurewebsites.net' }
    { name: 'Downstream__McpApiKey', value: mcpApiKey }
    { name: 'Cors__AllowedOrigins__0', value: 'https://${staticWebApp.properties.defaultHostname}' }
  ])
}

var serviceKeys = ['rag-api', 'students-api', 'mcp-server', 'agents-api']

resource apps 'Microsoft.Web/sites@2024-04-01' = [for key in serviceKeys: {
  name: '${prefix}-${key}'
  location: location
  tags: union(tags, { service: key })
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identity.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
      appSettings: concat(commonSettings, serviceSettings[key])
    }
  }
  dependsOn: [appDataAccess, sqlAllowAzure, keyVaultSecretsUser]
}]

// Connects the Students API to the hybrid connection (OnPrem mode).
resource studentsApiHybridConnection 'Microsoft.Web/sites/hybridConnectionNamespaces/relays@2024-04-01' = if (useOnPremSql) {
  name: '${apps[1].name}/${relay.name}/${sqlHybridConnection.name}'
  properties: {
    serviceBusNamespace: relay.name
    serviceBusSuffix: '.servicebus.windows.net'
    relayName: sqlHybridConnection.name
    relayArmUri: sqlHybridConnection.id
    hostname: onPremSqlHost
    port: onPremSqlPort
    sendKeyName: sqlHybridSendRule.name
    sendKeyValue: sqlHybridSendRule!.listKeys().primaryKey
  }
}

// ---------------------------------------------------------------------------------------
// Outputs (used by the GitHub workflows and printed after deployment)
// ---------------------------------------------------------------------------------------
output resourcePrefix string = prefix
output ragApiName string = apps[0].name
output studentsApiName string = apps[1].name
output mcpServerName string = apps[2].name
output agentsApiName string = apps[3].name
output agentsApiUrl string = 'https://${apps[3].properties.defaultHostName}'
output mcpServerUrl string = 'https://${apps[2].properties.defaultHostName}/mcp'
output staticWebAppName string = staticWebApp.name
output staticWebAppUrl string = 'https://${staticWebApp.properties.defaultHostname}'
output storageAccountName string = storage.name
output storageBlobEndpoint string = storage.properties.primaryEndpoints.blob
output searchEndpoint string = 'https://${search.name}.search.windows.net'
output openAiEndpoint string = openAi.properties.endpoint
output chatDeploymentName string = chatDeployment.name
output embeddingDeploymentName string = embeddingDeployment.name
output sqlMode string = sqlMode
output sqlServerName string = useOnPremSql ? '${onPremSqlHost}:${onPremSqlPort} (on-prem)' : sqlServer!.name
output hybridConnectionName string = useOnPremSql ? '${relay.name}/${sqlHybridConnection.name}' : ''
output managedIdentityClientId string = identity.properties.clientId
