# Configuration

The API reads settings from `appsettings.json`, environment-specific settings,
environment variables, and ASP.NET Core user-secrets. Do not store credentials
or private transcripts in the repository.

## Transcript analysis

`Llm:Provider` selects the extractor:

| Setting | Default | Description |
| --- | --- | --- |
| `Llm:Provider` | `Stub` | `Stub` uses built-in deterministic rules; `Ollama` uses a local Ollama server. |
| `Llm:FallbackToStub` | `false` (`true` in Development) | When enabled, extraction can use Stub if Ollama is unavailable. |
| `Ollama:BaseUrl` | `http://localhost:11434` | Absolute HTTP or HTTPS address of the Ollama server. |
| `Ollama:Model` | `llama3.1:8b` | Model tag used for transcript analysis. |
| `Ollama:NumCtx` | `16384` | Explicit context window sent with model requests. |
| `Ollama:Temperature` | `0` | Model sampling temperature from 0 through 2. |
| `Ollama:RequestTimeoutSeconds` | `180` | Positive request timeout in seconds. |
| `Ollama:MaxTranscriptCharsPerChunk` | `6000` | Maximum transcript characters targeted per request chunk. |
| `Ollama:KeepAlive` | `10m` | Ollama model keep-alive duration. |

`appsettings.json` keeps `Llm:Provider` set to `Stub`, so the API works without
Ollama. Development configuration selects Ollama. Override it with
`Llm:Provider=Stub` when a local model is not available, or set
`Llm:FallbackToStub=true` to explicitly allow fallback.

Ollama chunks are also bounded by an estimated context-token budget: Latin
chunks use about four characters per token, Devanagari chunks about 1.5, and
1,500 tokens are reserved for output. A single speaker line that exceeds the
budget is fragmented with a warning that does not include transcript text.

For example, configure Ollama through environment variables in PowerShell:

```powershell
$env:Llm__Provider = "Ollama"
$env:Ollama__BaseUrl = "http://localhost:11434"
$env:Ollama__Model = "llama3.1:8b"
```

Or use user-secrets for per-developer settings:

```powershell
dotnet user-secrets set "Llm:Provider" "Ollama" --project src\MeetingActions.Api
dotnet user-secrets set "Ollama:BaseUrl" "http://localhost:11434" --project src\MeetingActions.Api
dotnet user-secrets set "Ollama:Model" "llama3.1:8b" --project src\MeetingActions.Api
```

The API reports provider and model availability from `GET /api/llm/info`; the
`llm` member of `GET /api/health` can be `Degraded` without changing the
overall API health when Ollama is unavailable.
