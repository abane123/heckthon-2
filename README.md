# Meeting-to-Action Pipeline

A runnable demo skeleton for extracting meeting action items, reviewing them, creating task-provider tickets, and simulating reminders and escalations. Backend projects target .NET 10 because the machine used to scaffold this workspace did not have .NET 8 installed and the SDK installation was declined.

## Prerequisites

- .NET 10 SDK
- Node.js 20.19+ or 22.12+
- npm

## Run the backend

From the repository root:

```powershell
dotnet restore MeetingActions.sln
dotnet build MeetingActions.sln
dotnet run --project src\MeetingActions.Api
```

The API listens on the URLs shown by `dotnet run`; Swagger is available at `/swagger` in Development. SQLite uses `src/MeetingActions.Api/appsettings.Development.json` for its local connection string and creates its schema from the included initial migration.

Configuration values can be overridden with ASP.NET Core user-secrets:

```powershell
dotnet user-secrets init --project src/MeetingActions.Api
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://your-resource.example" --project src\MeetingActions.Api
dotnet user-secrets set "AzureOpenAI:ApiKey" "<your-key>" --project src\MeetingActions.Api
dotnet user-secrets set "AzureDevOps:PersonalAccessToken" "<your-token>" --project src\MeetingActions.Api
dotnet user-secrets set "Teams:WebhookUrl" "https://your-webhook.example" --project src\MeetingActions.Api
```

No external provider is activated by default: extraction uses the local stub and Azure DevOps returns demo ticket IDs. Change `TaskProvider:Type` to `MockJira` to use the in-memory Jira-style provider.

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

## Integration stubs

- `AzureOpenAiExtractor` is a placeholder and is not selected by default; implement its request/response mapping before enabling it.
- `AzureDevOpsProvider` currently logs and returns fake ticket IDs; replace its marked TODO sites with Azure DevOps REST calls.
- `TeamsWebhookNotifier` logs the intended notification; it does not contact Teams.
