using System.Text.Json;
using MeetingActions.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace MeetingActions.Infrastructure.Llm;

/// <summary>Checks Ollama reachability and whether the configured model is installed.</summary>
public sealed class OllamaLlmHealth(
    IHttpClientFactory httpClientFactory,
    IOptions<OllamaOptions> options) : ILlmHealth
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Queries Ollama's model tags endpoint and returns a degraded result on availability failures.</summary>
    public async Task<LlmHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        // Health must not inherit the much longer extraction timeout or hold up API diagnostics.
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var client = httpClientFactory.CreateClient(OllamaOptions.HttpClientName);
            using var response = await client.GetAsync("api/tags", linkedToken.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new LlmHealthResult(
                    false,
                    false,
                    settings.Model,
                    $"Ollama returned HTTP {(int)response.StatusCode} from /api/tags.");
            }

            await using var content = await response.Content.ReadAsStreamAsync(linkedToken.Token);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: linkedToken.Token);
            if (!document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array)
            {
                return new LlmHealthResult(false, false, settings.Model, "Ollama returned an invalid model list.");
            }

            var installed = models.EnumerateArray().Any(model =>
                ReadModelName(model).Equals(settings.Model, StringComparison.OrdinalIgnoreCase));
            return new LlmHealthResult(
                true,
                installed,
                settings.Model,
                installed ? null : $"Model not installed: run 'ollama pull {settings.Model}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new LlmHealthResult(
                false,
                false,
                settings.Model,
                $"Ollama health check timed out after {ProbeTimeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException)
        {
            return new LlmHealthResult(
                false,
                false,
                settings.Model,
                $"Cannot reach Ollama at {settings.BaseUrl}. Is it running?");
        }
        catch (JsonException)
        {
            return new LlmHealthResult(false, false, settings.Model, "Ollama returned invalid model metadata.");
        }
    }

    /// <summary>Reads either Ollama's name or model field from a model-tag entry.</summary>
    private static string ReadModelName(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString() ?? string.Empty;
        }

        return model.TryGetProperty("model", out var modelName) && modelName.ValueKind == JsonValueKind.String
            ? modelName.GetString() ?? string.Empty
            : string.Empty;
    }
}
