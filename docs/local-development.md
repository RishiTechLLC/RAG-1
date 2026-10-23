# Local development and debugging

All projects can be run and debugged on your machine at the same time. The only cloud dependencies you need while developing are **Azure OpenAI** and **Azure AI Search**, because neither has a local emulator. SQL Server and Blob Storage run in Docker.

## Prerequisites

| Tool | Version | Why |
|------|---------|-----|
| .NET SDK | 10.0.100+ | all back-end projects |
| Docker Desktop / Podman | recent | SQL Server + Azurite containers |
| Node.js | 24 LTS (or 22.22.3+) | Angular 22 |
| Azure CLI | recent | `az login` for Entra ID auth to Azure OpenAI / AI Search |
| IDE | Visual Studio 2026, VS Code + C# Dev Kit, or Rider | debugging |

You need an Azure OpenAI resource with a chat deployment (default name `gpt-5-mini`) and an embedding deployment (default `text-embedding-3-small`), plus an Azure AI Search service (the Free tier works for experiments; semantic ranking needs Basic). Running `infra.yml` once creates both. For local access you can either:

* **Use your identity (recommended).** Run `az login`, then grant yourself *Cognitive Services OpenAI User* on the OpenAI resource and *Search Index Data Contributor* + *Search Service Contributor* on the Search service. Setting `AZURE_DEVELOPER_PRINCIPAL_ID` (your user object id) before running the infra deployment grants these roles automatically.
* **Use keys.** Put the API keys in user secrets. This only works if the resources allow key auth; the Bicep template disables it for Azure OpenAI.

## Option A — Aspire (one F5 for everything)

```bash
cd src/Aspire/RagPlatform.AppHost
dotnet user-secrets set "Azure:OpenAI:Endpoint" "https://<aoai>.openai.azure.com/"
dotnet user-secrets set "Azure:Search:Endpoint" "https://<search>.search.windows.net"
# optional overrides
dotnet user-secrets set "Azure:OpenAI:ChatDeployment" "gpt-5-mini"
dotnet user-secrets set "Azure:OpenAI:EmbeddingDeployment" "text-embedding-3-small"
dotnet user-secrets set "Azure:OpenAI:ApiKey" "<key>"      # only if not using az login
dotnet user-secrets set "Azure:Search:ApiKey" "<key>"      # only if not using az login

cd ../../Frontend/chat-ui && npm install && cd -   # first time only

dotnet run --project src/Aspire/RagPlatform.AppHost
```

* **Visual Studio:** set `RagPlatform.AppHost` as the startup project and press F5. The debugger attaches to all four services.
* **VS Code:** pick the **"Aspire: whole platform"** launch configuration (needs C# Dev Kit + the Aspire extension).
* **Rider:** run the `RagPlatform.AppHost` configuration (Aspire plugin).

What you get:

| Resource | URL |
|----------|-----|
| Aspire dashboard (logs, traces, metrics) | http://localhost:15180 (login link printed in the console) |
| Angular chat UI | http://localhost:4200 |
| Rag.Api | http://localhost:5101/openapi/v1.json |
| Students.Api | http://localhost:5201/openapi/v1.json |
| Mcp.Server | http://localhost:5301/mcp |
| Agents.Api | http://localhost:5401/openapi/v1.json |

The SQL Server and Azurite containers are **persistent** (they survive restarts). On first start `Students.Api` creates and seeds the database, and `Rag.Api` uploads `samples/documents` into the Azurite `documents` container.

**The vector index loads itself.** In Development, `Rag.Api` checks the search index on startup. If the index is empty, it ingests the documents in the background with the `Markdown` strategy. Look for `Auto-ingestion done` in the `rag-api` logs, or check `GET http://localhost:5101/api/stats`. It skips this when chunks already exist, so restarts don't re-ingest. Settings: `Rag:AutoIngest:Enabled` and `Rag:AutoIngest:Strategy` in `Rag.Api/appsettings.Development.json`.

To re-ingest after changing documents, or to add another strategy, call ingestion yourself. Use the `Rag.Api.http` file, or:

```bash
curl -X POST http://localhost:5101/api/ingest -H "content-type: application/json" -d '{"strategy":"Semantic"}'
```

Then open http://localhost:4200 and ask *"What is the late submission policy?"* (knowledge agent) or *"Show the grades for student S2026005"* (MCP → SQL).

### Debugging tips

* Put breakpoints in `Agents.Api/Agents/AgentCatalog.cs` (agent wiring), `Tools/TracingFunction` (every tool and agent call goes through it), `Mcp.Server/Tools/*.cs` and `Students.Api/Endpoints`. They are all hit in a single request.
* In the dashboard, **Traces** shows one distributed trace per chat turn: the orchestrator's LLM calls, the sub-agent invocation, the MCP HTTP call and the SQL query. In Development, `Agents.Api` also records prompts and completions on the spans (`EnableSensitiveData`).
* Debug the Angular app with the **"Angular (Chrome)"** launch config, or with the browser dev tools (source maps are enabled in dev builds).

## Option B — without Aspire

1. Start the containers: `docker compose -f docker-compose.infra.yml up -d`.
2. Put the Azure settings in user secrets for `Rag.Api`, `Rag.Ingestion` and `Agents.Api`:
   ```bash
   dotnet user-secrets --project src/Rag/Rag.Api set "AzureOpenAI:Endpoint" "https://<aoai>.openai.azure.com/"
   dotnet user-secrets --project src/Rag/Rag.Api set "Search:Endpoint" "https://<search>.search.windows.net"
   # repeat for src/Rag/Rag.Ingestion, and AzureOpenAI:Endpoint for src/Agents/Agents.Api
   ```
3. Start the services. In VS Code use the **"All services (no Aspire)"** compound launch. From a terminal, run one per window:
   ```bash
   dotnet run --project src/Students/Students.Api     # :5201
   dotnet run --project src/Mcp/Mcp.Server            # :5301
   dotnet run --project src/Rag/Rag.Api               # :5101
   dotnet run --project src/Agents/Agents.Api         # :5401
   cd src/Frontend/chat-ui && npm start               # :4200
   ```

The default URLs in each `appsettings.json` match these ports, so no extra wiring is needed.

## Running ingestion with a chosen strategy

```bash
dotnet run --project src/Rag/Rag.Ingestion                                  # interactive menu
dotnet run --project src/Rag/Rag.Ingestion -- --strategy Semantic
dotnet run --project src/Rag/Rag.Ingestion -- --strategy Recursive --chunk-size 800 --overlap 100 --prefix policies/
dotnet run --project src/Rag/Rag.Ingestion -- --upload samples/documents --strategy Markdown --recreate-index
```

The VS Code launch config **"Rag.Ingestion (choose strategy)"** asks for the strategy in a drop-down and runs it under the debugger. In Visual Studio, pick one of the launch profiles in `Rag.Ingestion/Properties/launchSettings.json`.

Compare strategies by ingesting the same documents more than once and searching with a filter:

```bash
curl -X POST localhost:5101/api/search -H "content-type: application/json" \
  -d '{"query":"refund after week 3","strategy":"Semantic","top":3}'
curl localhost:5101/api/stats      # chunk counts per strategy
```

## Testing the MCP server on its own

```bash
npx @modelcontextprotocol/inspector
# Transport: Streamable HTTP, URL: http://localhost:5301/mcp
```

To use it from VS Code / GitHub Copilot, add this to `.vscode/mcp.json`:

```json
{ "servers": { "campus-data": { "type": "http", "url": "http://localhost:5301/mcp" } } }
```

## Tests

```bash
dotnet test --solution RagPlatform.slnx              # chunking strategy unit tests
cd src/Frontend/chat-ui && npm run test:ci           # Angular (Vitest)
```

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `401/403` from Azure OpenAI or Search | Run `az login` and check the RBAC roles above (role assignments can take ~5 minutes to apply), or configure keys. |
| `Search:Endpoint is not configured` | Set the AppHost user secrets (Option A) or the project's user secrets (Option B). |
| Knowledge agent says it *couldn't find* something that is in the documents | Run ingestion first (`POST /api/ingest`) and check `GET /api/stats` shows chunks. By default the agent searches chunks from every strategy; if you set `Downstream:KnowledgeBaseStrategy` on Agents.Api, documents must have been ingested with that strategy. |
| Agents panel says *MCP server unreachable* | Start `Mcp.Server` first. The orchestrator retries the MCP connection every 30 s. |
| SQL login failures with Option B | The compose file and `Students.Api/appsettings.Development.json` share the password `Your_strong_password123`. Change both together. |
| Angular `/api` calls fail | Make sure `Agents.Api` runs on :5401, or set `AGENTS_API_URL` before `npm start`. |
| Port already in use | Stop the other run, or change `applicationUrl` in the project's `Properties/launchSettings.json`. |
