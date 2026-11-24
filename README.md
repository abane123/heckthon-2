# Meeting-to-Action Pipeline

A runnable demo skeleton for extracting meeting action items, reviewing them, creating task-provider tickets, and simulating reminders and escalations. Backend projects target .NET 10 because the machine used to scaffold this workspace did not have .NET 8 installed and the SDK installation was declined.

## Prerequisites

- .NET 10 SDK
- Node.js 20.19+ or 22.12+
- npm

## MongoDB options

The API uses `mongodb://localhost:27017` and the `meeting_actions_dev` database in Development. A local MongoDB installation works directly; Docker is optional.

To use the optional MongoDB 7 Compose service from the repository root:

```powershell
docker compose up -d
```

The optional service keeps its data in a named volume. To also start Mongo Express on `http://localhost:8081`, run:

```powershell
docker compose --profile tools up -d
```

## Run the backend

From the repository root:

```powershell
dotnet restore MeetingActions.sln
dotnet build MeetingActions.sln
dotnet run --project src\MeetingActions.Api
```

The API listens on the URLs shown by `dotnet run`; Swagger is available at `/swagger` in Development. Local MongoDB settings are in `src/MeetingActions.Api/appsettings.Development.json`.

To connect to MongoDB Atlas or Azure Cosmos DB for MongoDB, override the connection string and database name with ASP.NET Core user-secrets:

```powershell
dotnet user-secrets init --project src/MeetingActions.Api
dotnet user-secrets set "MongoDB:ConnectionString" "<your-mongodb-connection-string>" --project src/MeetingActions.Api
dotnet user-secrets set "MongoDB:DatabaseName" "<your-database-name>" --project src/MeetingActions.Api
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://your-resource.example" --project src\MeetingActions.Api
dotnet user-secrets set "AzureOpenAI:ApiKey" "<your-key>" --project src\MeetingActions.Api
dotnet user-secrets set "AzureDevOps:PersonalAccessToken" "<your-token>" --project src\MeetingActions.Api
dotnet user-secrets set "Teams:WebhookUrl" "https://your-webhook.example" --project src\MeetingActions.Api
```

No external provider is activated by default: extraction uses the local stub and Azure DevOps returns demo ticket IDs. Change `TaskProvider:Type` to `MockJira` to use the in-memory Jira-style provider.

The base `appsettings.json` selects the built-in Stub transcript extractor.
`appsettings.Development.json` selects Ollama at `http://localhost:11434` with
the `llama3.1:8b` model. Start Ollama and install the model with
`ollama pull llama3.1:8b`, or override `Llm:Provider` to `Stub`. Ollama settings
and validation are documented in [docs/CONFIGURATION.md](docs/CONFIGURATION.md).
`GET /api/llm/info` reports the selected provider and any required setup action;
Ollama availability is reported separately in `GET /api/health`.
Set `Llm:FallbackToStub` to `true` to explicitly allow the API to use the built-in
extractor when the selected Ollama service is unavailable.

If MongoDB is unreachable, the API still starts, logs the database initialization failure, and reports a degraded state from `/api/health`. Database-dependent endpoints return a 503 ProblemDetails response. Verify that your local MongoDB service is running and listening on port 27017; if you chose Compose, check `docker compose ps` and `docker compose logs mongo`.

### Troubleshooting Ollama

- Run `ollama list` to check installed models, and open `http://localhost:11434` to confirm the local Ollama service is reachable.
- The first extraction after a model load can be slow, especially on CPU; allow time for the model to load. Startup seeding uses the deterministic Stub extractor and never waits for Ollama.
- To use the built-in engine instead, set `$env:Llm__Provider = "Stub"` before starting the API.

## Run the UI

In another terminal:

```powershell
cd ui
npm install
npm run dev
```

The Vite development server is normally at `http://localhost:5173`. Set `VITE_API_URL` in `ui\.env.local` if the backend is not at `http://localhost:5000`.

## API endpoints

| Method | Endpoint | Purpose |
| --- | --- | --- |
| POST | `/api/meetings` | Create a meeting and extract draft actions |
| GET | `/api/meetings` | List meetings |
| GET | `/api/meetings/{id}` | Get a meeting and its actions |
| GET | `/api/meetings/{id}/items` | List meeting actions |
| POST | `/api/meetings/{id}/create-tickets` | Create tickets for approved actions |
| GET | `/api/meetings/{id}/next-agenda` | Get open actions from earlier same-title meetings |
| PUT | `/api/items/{id}` | Edit a draft action |
| POST | `/api/items/{id}/approve` | Approve a draft action |
| POST | `/api/items/{id}/reject` | Reject a draft action |
| POST | `/api/items/{id}/reply` | Apply a Done, PushToFriday, Blocked, or Reassign reply |
| GET | `/api/items/{id}/audit` | Get the action audit trail |
| GET | `/api/items` | List all tracker actions |
| GET | `/api/dashboard` | Get summary metrics and open counts per owner |
| POST | `/api/dev/simulate-days?days=1` | Advance virtual time and run follow-ups |
| GET | `/api/sample-transcript` | Load the bundled sample transcript |

## Demo flow

1. Open **New Meeting**, load the sample transcript, and submit it.
2. Review draft items, fill missing owners or dates, and approve selected actions.
3. Use **Create tickets** to create demo tickets.
4. Open **Dashboard**, select **Simulate tomorrow**, then inspect reminders, due-date checks, and follow-ups.
5. Create another meeting with the same title and use its `/api/meetings/{id}/next-agenda` endpoint to see earlier open actions.

## Tests

```powershell
dotnet test MeetingActions.sln
Set-Location ui
npm test
```

MongoDB integration tests use `MONGO_TEST_CONNECTION` when it is set. To run them against a local MongoDB server:

```powershell
$env:MONGO_TEST_CONNECTION = "mongodb://localhost:27017"
dotnet test MeetingActions.sln
Remove-Item Env:\MONGO_TEST_CONNECTION
```

The fixture creates a unique `meeting_actions_test_<guid>` database for the test run and drops it during cleanup. The configured connection string must not select a database outside the `meeting_actions_test_` prefix. If the variable is unset, tests use Testcontainers when Docker is available; otherwise the MongoDB integration tests are skipped with a clear message.

## Transcript evaluation

The synthetic evaluation corpus contains eight English, Hinglish, Devanagari,
and mixed-language transcripts with expected owner,
due-date phrase, and source-quote labels. Run it with the no-setup Stub provider:

```powershell
dotnet run --project samples/eval/MeetingActions.Eval.csproj -- Stub --runs 3
```

When Ollama is running and the configured model is installed, run:

```powershell
dotnet run --project samples/eval/MeetingActions.Eval.csproj -- Ollama --model llama3.1:8b --runs 2
```

Use `--model <name>` to override the configured model and `--runs <n>` to
average metrics over repeated runs. The report includes precision, recall,
owner and due-date-phrase accuracy, extracted/expected item counts by language,
dropped hallucinations, and average/minimum/maximum analysis times. Undefined
percentages are shown as `N/A`. Reports are written to
`samples/eval/results-<provider>-<model>.md` (filename-invalid characters such
as `:` are replaced with `-`). See [samples/eval/README.md](samples/eval/README.md)
for the ground-truth label policy. Ollama settings can be overridden with the
usual `Ollama__BaseUrl`, `Ollama__Model`, and related environment variables.
The live Ollama unit test is skipped unless `OLLAMA_LIVE_TEST=1` is set.

## Integration stubs

- `AzureOpenAiExtractor` is a placeholder and is not selected by default; implement its request/response mapping before enabling it.
- `AzureDevOpsProvider` currently logs and returns fake ticket IDs; replace its marked TODO sites with Azure DevOps REST calls.
- `TeamsWebhookNotifier` logs the intended notification; it does not contact Teams.
