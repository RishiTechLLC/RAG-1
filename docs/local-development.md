# Local development and debugging

All projects can be run and debugged on your machine at the same time. **No Docker is used.** The apps run as normal processes against services you already have:

* **SQL Server:** your own server (on-prem, SQL Express or LocalDB)
* **Azure Storage account:** holds the documents to ingest
* **Azure OpenAI** and **Azure AI Search:** no local emulators exist, so these are always in Azure

## Prerequisites

| Tool | Version | Why |
|------|---------|-----|
| .NET SDK | 10.0.100+ | all back-end projects |
| SQL Server | 2016+ (any edition) | student database |
| Node.js | 24 LTS (or 22.22.3+) | Angular 22 |
| Azure CLI | recent | `az login` for Entra ID auth to Azure OpenAI / AI Search |
| IDE | Visual Studio 2026, VS Code + C# Dev Kit, or Rider | debugging |

You need an Azure OpenAI resource with a chat deployment (default name `gpt-5-mini`) and an embedding deployment (default `text-embedding-3-small`), an Azure AI Search service with the semantic ranker enabled, and an Azure Storage account. For local access you can either:

* **Use your identity (recommended).** Run `az login`, then grant yourself *Cognitive Services OpenAI User* on the OpenAI resource, *Search Index Data Contributor* + *Search Service Contributor* on the Search service, and *Storage Blob Data Contributor* on the storage account. Setting `AZURE_DEVELOPER_PRINCIPAL_ID` (your user object id) before running the infra deployment grants these roles automatically.
* **Use keys.** Put the API keys in user secrets. This only works if the resources allow key auth; the Bicep template disables it for Azure OpenAI.

## Option A — Aspire (one F5 for everything)

```bash
cd src/Aspire/RagPlatform.AppHost
# your SQL Server (Windows auth shown; SQL auth: User Id=...;Password=...)
dotnet user-secrets set "ConnectionStrings:StudentsDb" "Server=MYSERVER;Database=students;Trusted_Connection=True;TrustServerCertificate=True"
# your Azure Storage account (Entra ID; or set Azure:Storage:ConnectionString instead)
dotnet user-secrets set "Azure:Storage:AccountUri" "https://<account>.blob.core.windows.net/"
dotnet user-secrets set "Azure:OpenAI:Endpoint" "https://<aoai>.openai.azure.com/"
dotnet user-secrets set "Azure:Search:Endpoint" "https://<search>.search.windows.net"
# optional overrides
dotnet user-secrets set "Azure:OpenAI:ChatDeployment" "gpt-5-mini"
dotnet user-secrets set "Azure:OpenAI:EmbeddingDeployment" "text-embedding-3-small"
dotnet user-secrets set "Azure:OpenAI:ApiKey" "<key>"      # only if not using az login
dotnet user-secrets set "Azure:Search:ApiKey" "<key>"      # only if not using az login
dotnet user-secrets set "Azure:Storage:ContainerName" "documents"   # default

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

If `ConnectionStrings:StudentsDb` or the storage account is missing, the AppHost stops at startup with the exact `dotnet user-secrets` command to run.

On first start, `Students.Api` creates and seeds the database (see the SQL section below). `Rag.Api` uploads any **missing** files from `samples/documents` into your storage container; existing blobs are never overwritten. To leave the container untouched, set `Azure:Storage:UploadSampleDocuments` to `false`.

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

## SQL Server connection

The AppHost passes `ConnectionStrings:StudentsDb` to the Students API:

```bash
cd src/Aspire/RagPlatform.AppHost
# Windows authentication (your Windows login)
dotnet user-secrets set "ConnectionStrings:StudentsDb" "Server=MYSERVER;Database=students;Trusted_Connection=True;TrustServerCertificate=True"
# or SQL authentication
dotnet user-secrets set "ConnectionStrings:StudentsDb" "Server=MYSERVER,1433;Database=students;User Id=myuser;Password=MyPassword;TrustServerCertificate=True"
```

Without Aspire, set the same key on the Students API itself: `dotnet user-secrets --project src/Students/Students.Api set "ConnectionStrings:StudentsDb" "..."`.

* `Server=` examples: `MYSERVER`, `MYSERVER\SQLEXPRESS` (named instance), `192.168.1.20,1433`, `(localdb)\MSSQLLocalDB`.
* `TrustServerCertificate=True` is needed when the server uses a self-signed certificate (most on-prem installs). SqlClient encrypts connections by default.
* On startup, the API **creates the database if it doesn't exist** (the login needs `CREATE DATABASE` permission, or create an empty database first). It **creates the `Students`, `Courses` and `Enrollments` tables only if they're missing** and **inserts sample data only into an empty `Students` table**. An existing database that already holds other tables is fine; nothing else is touched.
* Using your own data: set `Database:SeedSampleData=false`. To manage the schema yourself, also set `Database:InitializeOnStartup=false` and create the tables from the DDL in the PR description / `dotnet ef dbcontext script`.
* Minimum permissions after the first run: `db_datareader` + `db_datawriter` on the database.

## Option B — without Aspire

1. Set the connection strings in each project's user secrets:
   ```bash
   dotnet user-secrets --project src/Students/Students.Api set "ConnectionStrings:StudentsDb" "Server=MYSERVER;Database=students;Trusted_Connection=True;TrustServerCertificate=True"
   # Storage:AccountUri for Rag.Api, Rag.Ingestion and Mcp.Server
   dotnet user-secrets --project src/Rag/Rag.Api set "Storage:AccountUri" "https://<account>.blob.core.windows.net/"
   ```
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

## Automatic ingestion when you upload a file (Rag.Functions)

`src/Rag/Rag.Functions` is an Azure Functions app with a **blob trigger**. Every file you upload, or overwrite, in the `documents` container is downloaded, chunked, embedded and saved to Azure AI Search automatically. Re-uploading a file replaces its old chunks. Supported types are PDF, DOCX, MD, TXT, HTML, CSV and JSON; other files are skipped.

It starts **with the Aspire AppHost** as the `rag-functions` resource. Rag.Api keeps serving search for the agents.

**1. One-time setup**

* Install **Azure Functions Core Tools v4**. Visual Studio installs them with the *Azure development* workload. Otherwise run `winget install Microsoft.Azure.FunctionsCoreTools`. Without it, `rag-functions` fails with *"Unable to launch the Azure Functions Core Tools"*, and everything else still runs.
* The AppHost passes the Functions app the same settings as the other services: `Azure:Storage:*`, `Azure:Search:*` and `Azure:OpenAI:*` from its user secrets. The committed `src/Rag/Rag.Functions/local.settings.json` contains only `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`, which the Functions runtime needs to start. **Don't add secrets to it**, because it's tracked by Git.
  * With `Azure:Storage:AccountUri` (Entra ID), your signed-in account (Visual Studio / `az login`) also needs **Storage Blob Data Owner**, **Storage Queue Data Contributor** and **Storage Table Data Contributor** on the storage account. The Functions runtime keeps its leases, blob receipts and retry queue there. Wait about 5 minutes after assigning them.
  * With `Azure:Storage:ConnectionString`, no extra roles are needed.
* Optional AppHost settings: `RagFunctions:Strategy` (default `Markdown`) and `RagFunctions:Enabled` (`false` skips it, for example to save memory).

**2. Run it**

Press F5 on the AppHost. The dashboard shows `rag-functions`, and its console log lists `IngestUploadedDocument: blobTrigger`.

To run it on its own instead (without the AppHost), set the values from `src/Rag/Rag.Functions/local.settings.sample.json` as environment variables in your terminal, then run `cd src/Rag/Rag.Functions` and `func start`. Don't put them in the committed `local.settings.json`.

**3. Upload and test**

* Upload a file into the `documents` container: Azure portal → storage account → *Storage browser*, or Azure Storage Explorer, or:
  ```bash
  az storage blob upload --account-name <account> -c documents -f mydoc.pdf -n policies/mydoc.pdf --auth-mode login
  ```
* Within about a minute the `rag-functions` console log in the dashboard shows `Ingesting policies/mydoc.pdf …` and `Indexed policies/mydoc.pdf with Markdown: N chunks`. Ask about it in the chat UI.
* **Per-file strategy:** give the blob a metadata entry `strategy` = `Semantic` (or any other strategy) to override `Ingestion__Strategy` for that file. In Storage Explorer: right-click → Properties → Metadata. With the CLI: `--metadata strategy=Semantic`.

**Notes**

* On first start, the trigger also processes files already in the container that it hasn't seen before. That's expected.
* The blob trigger polls, so pickup can take up to about a minute (longer for containers with many thousands of files).
* Deleting a blob does **not** remove its chunks from the index. Delete them with a re-ingest using `recreateIndex`, or remove them in the Azure portal's Search explorer.
* Failures (for example a wrong Search endpoint) are retried 5 times. After that, the message goes to the `webjobs-blobtrigger-poison` queue in the storage account. Fix the setting and re-upload the file.

## Semantic cache (Redis)

Agents.Api can answer repeated questions straight from a cache. It embeds each new question, looks up the most similar earlier question in Redis, and returns that answer when the cosine similarity is at least `SemanticCache:SimilarityThreshold` (default **0.92**). A hit costs one embedding call instead of several LLM calls, and the chat trace shows a `semantic_cache` step.

**Run Redis.** It needs vector search, so use **Redis Stack** or **Redis 8**. The plain `redis:7` image doesn't have it.
```bash
docker run -d --name redis -p 6379:6379 redis/redis-stack-server:latest   # or: redis:8
cd src/Aspire/RagPlatform.AppHost
dotnet user-secrets set "ConnectionStrings:redis" "localhost:6379"
```
Without `ConnectionStrings:redis`, the cache is simply off. If Redis goes down while the app runs, chat keeps working without the cache and a warning is logged.

**What gets cached** (safety rules):
* Only the **first question of a conversation**. Follow-ups such as "what about week 5?" depend on earlier turns. After a cache hit, the next question still gets the cached exchange as context.
* Only answers **grounded in the document knowledge base**: the search returned passages and every step succeeded.
* **Never** answers that used MCP data (student records, grades, rosters, calendar, weather). That data is live and personal, and must not be served to other users.
* Answers that found nothing are **not** cached, so "I couldn't find it" doesn't outlive a later ingestion.

**Settings** (`SemanticCache` section of Agents.Api): `Enabled` (default `true`), `SimilarityThreshold` (default `0.92`; lower it to match more loosely), `TimeToLive` (default `1.00:00:00`, which is 24 h), `KeyPrefix` (`semcache`). Embeddings use `AzureOpenAI:EmbeddingDeployment`.

**Manage it:**
```bash
curl localhost:5401/api/cache/stats        # entries, hits, misses, threshold, TTL, connected
curl -X DELETE localhost:5401/api/cache    # flush, e.g. after re-ingesting changed documents
```

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
| `Cannot open database "students" requested by the login` (own SQL Server) | The login can't create databases. Create an empty `students` database, or grant `dbcreator`, then restart. |
| `A connection was successfully established... certificate chain was issued by an authority that is not trusted` | Add `TrustServerCertificate=True` to the connection string. |
| AppHost stops with *'ConnectionStrings:StudentsDb' is not configured* or *'Azure:Storage:AccountUri' is not configured* | Run the `dotnet user-secrets set` command printed in the message, in `src/Aspire/RagPlatform.AppHost`. |
| `403 AuthorizationPermissionMismatch` from Blob Storage | Grant your account *Storage Blob Data Contributor* on the storage account (Owner/Contributor is not enough for data access), wait ~5 minutes. Or use `Azure:Storage:ConnectionString` with an account key. |
| `KeyBasedAuthenticationNotPermitted` | The storage account disallows key access. Use `Azure:Storage:AccountUri` (Entra ID) instead of a connection string. |
| Angular `/api` calls fail | Make sure `Agents.Api` runs on :5401, or set `AGENTS_API_URL` before `npm start`. |
| Services show *Failed to start* and their log says *Timeout … waiting for the IDE to start a run session* | Visual Studio didn't launch the services in time. Make sure it isn't paused at a breakpoint or exception, or restart it. The AppHost allows 300 s (`DCP_IDE_REQUEST_TIMEOUT_SECONDS` in its `launchSettings.json`). Alternatively run `dotnet run --project src/Aspire/RagPlatform.AppHost` from a terminal and use Debug → Attach to Process. |
| Functions console: *Worker runtime cannot be 'None'* / *Can't determine project language from files* | `src/Rag/Rag.Functions/local.settings.json` is missing. Pull the latest `main` (it's committed), or create it with `{"IsEncrypted": false, "Values": {"FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated"}}`. |
| `rag-functions` fails with *Unable to launch the Azure Functions Core Tools* | Install Core Tools v4 (see the Rag.Functions section), or set `RagFunctions:Enabled=false` in the AppHost. |
| `rag-functions` log shows `403` / *AuthorizationPermissionMismatch* | Your account needs Storage Blob Data Owner, Queue Data Contributor and Table Data Contributor on the storage account. Wait about 5 minutes after assigning them. |
| Agents.Api log: *Semantic cache lookup failed* … *unknown command 'FT.INFO'* | Your Redis lacks vector search. Use `redis/redis-stack-server` or `redis:8`. |
| Cache never hits | Check `GET /api/cache/stats` (`connected`, `entries`). Only opening questions with document answers are cached; reworded questions need similarity ≥ `SemanticCache:SimilarityThreshold`. |
| Port already in use | Stop the other run, or change `applicationUrl` in the project's `Properties/launchSettings.json`. |
