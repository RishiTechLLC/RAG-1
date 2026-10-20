# Deploying to Azure with GitHub Actions

## What gets created (`infra/main.bicep`)

| Resource | Purpose |
|----------|---------|
| User-assigned managed identity | One identity for all four apps (RBAC, no secrets) |
| Storage account + `documents` container | RAG source documents (shared-key access disabled) |
| Azure AI Search (Basic, semantic ranker free plan) | Vector + keyword index `rag-chunks` |
| Azure OpenAI + 2 deployments | `gpt-5-mini` (chat) and `text-embedding-3-small` (embeddings), local auth disabled |
| Azure SQL server + `students` database | Entra-only auth; the managed identity is the Entra admin |
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

### 3. First deployment (in this order)

1. **Actions → Infrastructure → Run workflow.** This creates all resources and prints the outputs in the run summary.
2. **Actions → Students API + MCP server → Run workflow.** The Students API creates and seeds the SQL schema on first start.
3. **Actions → RAG (ingestion + API) → Run workflow** with `run_ingestion = true`, `upload_samples = true` and a **strategy** from the drop-down.
4. **Actions → Agents API → Run workflow.**
5. **Actions → Frontend (Angular) → Run workflow.** It writes the Agents API URL into `config.json` and uploads the app to Static Web Apps.

After that, pushes to `main` redeploy only the projects whose paths changed. `ci.yml` builds and tests every pull request.

## How the pipelines work

* **OIDC login.** `azure/login@v2` exchanges the GitHub token for an Entra token. No client secret is stored.
* **Finding the target app.** The reusable workflow `_deploy-webapp.yml` looks up the App Service by its `service` tag, so the pipelines don't hard-code generated names. It then runs `dotnet publish`, deploys with `azure/webapps-deploy@v3`, and smoke-tests `/alive`.
* **Ingestion in CI.** The `ingest` job in `rag.yml` runs `Rag.Ingestion` on the runner as the GitHub service principal (`DefaultAzureCredential` picks up the Azure CLI login). Your workflow-dispatch inputs (`strategy`, `chunk_size`, `overlap`, `upload_samples`, `recreate_index`) become CLI arguments.
* **Front-end configuration.** One Angular build works in any environment because the API URL is written to `public/config.json` during deployment.

## Production hardening checklist

* Lock down `Students.Api` and `Mcp.Server`: App Service access restrictions or VNet integration with private endpoints so only `Mcp.Server` and `Agents.Api` can reach them.
* Replace the MCP shared key with **App Service Authentication (Entra ID)** and have `Agents.Api` call it with its managed identity.
* Add authentication to the chat UI and `Agents.Api` (Static Web Apps auth or MSAL Angular + JWT bearer).
* Make an Entra **group** the SQL admin and give the app identity a contained user with only `db_datareader` / `db_datawriter`. Move from `EnsureCreated` to EF Core migrations.
* Move session storage from `IMemoryCache` to Redis or Cosmos DB (serialize with `AIAgent.SerializeSessionAsync`) if you scale `Agents.Api` past one instance.
* Use private endpoints for Storage, AI Search, Azure OpenAI and SQL, and disable public network access.
