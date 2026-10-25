# Deploying to Azure with GitHub Actions

## What gets created (`infra/main.bicep`)

| Resource | Purpose |
|----------|---------|
| User-assigned managed identity | One identity for all four apps (RBAC, no secrets) |
| Storage account + `documents` container | RAG source documents (shared-key access disabled) |
| Azure AI Search (Basic, semantic ranker free plan) | Vector + keyword index `rag-chunks` |
| Azure OpenAI + 2 deployments | `gpt-5-mini` (chat) and `text-embedding-3-small` (embeddings), local auth disabled |
| **On-prem SQL (default, `AZURE_SQL_MODE=OnPrem`):** Azure Relay hybrid connection + Key Vault | The Students API reaches your SQL Server through an App Service Hybrid Connection. The connection string is stored in Key Vault and read with the managed identity |
| *or* Azure SQL server + `students` database (`AZURE_SQL_MODE=Azure`) | Entra-only auth; the managed identity is the Entra admin |
| App Service plan (Linux) + 4 web apps | `rag-api`, `students-api`, `mcp-server`, `agents-api` (.NET 10), tagged `service=<name>` |
| Static Web App (Free) | Angular chat UI |
| Log Analytics + Application Insights | OpenTelemetry traces/logs/metrics from every service |

Model names and versions, capacities and SKUs are parameters in `main.bicep`. If `gpt-5-mini` is not available in your region, set `AZURE_OPENAI_LOCATION` to a region that has it, or change `chatModelName` / `chatModelVersion`.

## One-time setup

### 1. Create an Entra app registration for GitHub (OIDC)

```bash
SUB=$(az account show --query id -o tsv)
RG=rg-ragdemo
REPO=<github-owner>/<repo>

az group create -n $RG -l eastus2

APP_ID=$(az ad app create --display-name "github-$RG" --query appId -o tsv)
SP_ID=$(az ad sp create --id $APP_ID --query id -o tsv)

# Contributor to create resources + RBAC Administrator so Bicep can assign roles to the managed identity
az role assignment create --assignee-object-id $SP_ID --assignee-principal-type ServicePrincipal \
  --role Contributor --scope /subscriptions/$SUB/resourceGroups/$RG
az role assignment create --assignee-object-id $SP_ID --assignee-principal-type ServicePrincipal \
  --role "Role Based Access Control Administrator" --scope /subscriptions/$SUB/resourceGroups/$RG

# Trust GitHub's OIDC tokens for the "production" environment used by the deploy jobs
az ad app federated-credential create --id $APP_ID --parameters "{
  \"name\": \"github-production\",
  \"issuer\": \"https://token.actions.githubusercontent.com\",
  \"subject\": \"repo:$REPO:environment:production\",
  \"audiences\": [\"api://AzureADTokenExchange\"]
}"

echo "AZURE_CLIENT_ID=$APP_ID  AZURE_TENANT_ID=$(az account show --query tenantId -o tsv)  AZURE_SUBSCRIPTION_ID=$SUB  SP_OBJECT_ID=$SP_ID"
```

### 2. Configure the GitHub repository

**Settings → Environments → New environment `production`**. Add required reviewers here if you want deployments to need approval.

**Settings → Secrets and variables → Actions**

| Type | Name | Value |
|------|------|-------|
| Secret | `AZURE_CLIENT_ID` | app registration client id |
| Secret | `AZURE_TENANT_ID` | tenant id |
| Secret | `AZURE_SUBSCRIPTION_ID` | subscription id |
| Variable | `AZURE_RESOURCE_GROUP` | `rg-ragdemo` |
| Variable | `AZURE_ENV_NAME` | short name, e.g. `ragdemo` (3–16 chars) |
| Variable | `AZURE_LOCATION` | e.g. `eastus2` |
| Variable | `AZURE_OPENAI_LOCATION` | optional; region for Azure OpenAI |
| Variable | `AZURE_DEPLOYER_PRINCIPAL_ID` | `SP_OBJECT_ID` from step 1. Lets the **ingestion** job read Blob, write the index and call embeddings |
| Variable | `AZURE_DEVELOPER_PRINCIPAL_ID` | optional; your user object id (`az ad signed-in-user show --query id -o tsv`) for local debugging against Azure |
| Variable | `AZURE_SQL_MODE` | `OnPrem` (default) or `Azure` |
| Variable | `AZURE_ONPREM_SQL_HOST` | OnPrem: SQL Server host name as your network resolves it, e.g. `SQLSRV01` or `sqlsrv01.corp.local` |
| Variable | `AZURE_ONPREM_SQL_PORT` | OnPrem: TCP port, default `1433` |
| Secret | `ONPREM_SQL_CONNECTION_STRING` | OnPrem: e.g. `Server=tcp:SQLSRV01,1433;Database=students;User Id=app_students;Password=...;TrustServerCertificate=True;Connection Timeout=60` (see below) |

### 3. First deployment (in this order)

1. **Actions → Infrastructure → Run workflow.** This creates all resources and prints the outputs in the run summary.
2. **OnPrem SQL only:** install the Hybrid Connection Manager and connect it (next section).
3. **Actions → Students API + MCP server → Run workflow.** The Students API creates the tables and seeds sample data on first start, retrying until SQL Server is reachable.
4. **Actions → RAG (ingestion + API) → Run workflow** with `run_ingestion = true`, `upload_samples = true` and a **strategy** from the drop-down.
5. **Actions → Agents API → Run workflow.**
6. **Actions → Frontend (Angular) → Run workflow.** It writes the Agents API URL into `config.json` and uploads the app to Static Web Apps.

After that, pushes to `main` redeploy only the projects whose paths changed. `ci.yml` builds and tests every pull request.

## On-prem SQL Server (Hybrid Connection)

```
Students.Api (App Service) ──► Azure Relay "onprem-sql" ◄── Hybrid Connection Manager (your network) ──► SQLSRV01:1433
                                 (outbound HTTPS 443 only, no inbound firewall ports, no VPN)
```

**Prepare SQL Server**

1. Use a **fixed TCP port**. For a named instance, set a static port in SQL Server Configuration Manager → TCP/IP → IPAll; dynamic ports don't work with Hybrid Connections. Enable TCP/IP.
2. Create a **SQL login** for the app. Windows authentication isn't possible from App Service:
   ```sql
   CREATE LOGIN app_students WITH PASSWORD = '<strong password>';
   -- first run creates the database/tables: either grant dbcreator once ...
   ALTER SERVER ROLE dbcreator ADD MEMBER app_students;
   -- ... or create the database yourself and make the login its owner/user:
   -- CREATE DATABASE students; USE students; CREATE USER app_students FOR LOGIN app_students; ALTER ROLE db_owner ADD MEMBER app_students;
   ```
   After the first run you can reduce it to `db_datareader` + `db_datawriter`, and set the App Service setting `Database__InitializeOnStartup=false`.
3. Set the GitHub variables and secret from the table above. The `Server=` in the connection string must use **exactly** the same host and port as `AZURE_ONPREM_SQL_HOST` / `AZURE_ONPREM_SQL_PORT`. App Service only tunnels that host:port.

**Connect the Hybrid Connection Manager (once, after the Infrastructure workflow)**

1. On a Windows machine in your network that can reach the SQL Server (it can be the SQL Server itself), download and install the **Hybrid Connection Manager**. In the Azure portal: App Service `…-students-api` → **Networking → Hybrid connections** → *Download connection manager*.
2. Open *Hybrid Connection Manager UI* → **Add a new Hybrid Connection** → sign in → select the subscription → pick **`relay-<prefix>/onprem-sql`** → Save.
3. It should show **Connected**. In the portal, the hybrid connection on the Students API shows *Status: Connected*.
4. The machine only needs **outbound TCP 443** to `*.servicebus.windows.net`. No inbound rules are needed.

Check it works: `https://<prefix>-students-api.azurewebsites.net/health` returns `Healthy`, and the log stream shows `Database ready`. Until the Hybrid Connection Manager is connected, the API keeps running and retries every 30 seconds. `/health` reports `Unhealthy` meanwhile.

For high availability, install the Hybrid Connection Manager on a second machine with the same connection. Each connection supports multiple listeners.

**Notes**
* Requires an App Service plan of Basic tier or higher (the template uses B1). Azure Relay Hybrid Connections are billed per listener (roughly US$10/month).
* Changing the password: update the `ONPREM_SQL_CONNECTION_STRING` secret and re-run the Infrastructure workflow. Key Vault gets the new value; restart the Students API so it reads the new value.
* Key Vault uses soft delete. If you delete and recreate the resource group with the same `AZURE_ENV_NAME`, purge the old vault first (`az keyvault purge -n kv…`).
* For a site-to-site VPN / ExpressRoute instead, add VNet integration to the Students API and use `AZURE_SQL_MODE=OnPrem` with the SQL Server's private IP or DNS name. In that case the Hybrid Connection isn't needed and can be removed.

## How the pipelines work

* **OIDC login.** `azure/login@v2` exchanges the GitHub token for an Entra token. No client secret is stored.
* **Finding the target app.** The reusable workflow `_deploy-webapp.yml` looks up the App Service by its `service` tag, so the pipelines don't hard-code generated names. It then runs `dotnet publish`, deploys with `azure/webapps-deploy@v3`, and smoke-tests `/alive`.
* **Ingestion in CI.** The `ingest` job in `rag.yml` runs `Rag.Ingestion` on the runner as the GitHub service principal (`DefaultAzureCredential` picks up the Azure CLI login). Your workflow-dispatch inputs (`strategy`, `chunk_size`, `overlap`, `upload_samples`, `recreate_index`) become CLI arguments.
* **Front-end configuration.** One Angular build works in any environment because the API URL is written to `public/config.json` during deployment.

## Production hardening checklist

* Lock down `Students.Api` and `Mcp.Server`: App Service access restrictions or VNet integration with private endpoints so only `Mcp.Server` and `Agents.Api` can reach them.
* Replace the MCP shared key with **App Service Authentication (Entra ID)** and have `Agents.Api` call it with its managed identity.
* Add authentication to the chat UI and `Agents.Api` (Static Web Apps auth or MSAL Angular + JWT bearer).
* SQL permissions: give the app login/identity only `db_datareader` / `db_datawriter` after the first run, and move schema changes to EF Core migrations (`Database__InitializeOnStartup=false`). For Azure SQL mode, make an Entra **group** the SQL admin.
* Move session storage from `IMemoryCache` to Redis or Cosmos DB (serialize with `AIAgent.SerializeSessionAsync`) if you scale `Agents.Api` past one instance.
* Use private endpoints for Storage, AI Search, Azure OpenAI and SQL, and disable public network access.
