using './main.bicep'

// Values are read from environment variables so the same file works locally and in GitHub Actions.
param environmentName = readEnvironmentVariable('AZURE_ENV_NAME', 'ragdemo')
param location = readEnvironmentVariable('AZURE_LOCATION', 'eastus2')
param openAiLocation = readEnvironmentVariable('AZURE_OPENAI_LOCATION', 'eastus2')
param deployerPrincipalId = readEnvironmentVariable('AZURE_DEPLOYER_PRINCIPAL_ID', '')
param developerPrincipalId = readEnvironmentVariable('AZURE_DEVELOPER_PRINCIPAL_ID', '')

// Student database: 'OnPrem' (your SQL Server via Hybrid Connection) or 'Azure' (new Azure SQL database)
param sqlMode = readEnvironmentVariable('AZURE_SQL_MODE', 'OnPrem')
param onPremSqlHost = readEnvironmentVariable('AZURE_ONPREM_SQL_HOST', '')
param onPremSqlPort = int(readEnvironmentVariable('AZURE_ONPREM_SQL_PORT', '1433'))
param onPremSqlConnectionString = readEnvironmentVariable('ONPREM_SQL_CONNECTION_STRING', '')
