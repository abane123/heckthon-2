using MeetingActions.Api.Contracts;
using MeetingActions.Core.Abstractions;
using MeetingActions.Infrastructure.Llm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MeetingActions.Api.Controllers;

/// <summary>Exposes the configured transcript-analysis provider and model status.</summary>
[ApiController]
[Route("api/llm")]
public sealed class LlmController(
    ILlmHealth llmHealth,
    IOptions<LlmOptions> llmOptions,
    IOptions<OllamaOptions> ollamaOptions) : ControllerBase
{
    /// <summary>Returns active-provider information and actionable model setup guidance.</summary>
    [HttpGet("info")]
    [ProducesResponseType<LlmInfoResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<LlmInfoResponse>> GetInfo(CancellationToken cancellationToken)
    {
        var settings = llmOptions.Value;
        var health = await llmHealth.CheckAsync(cancellationToken);
        var status = health.Reachable && health.ModelInstalled ? "Healthy" : "Degraded";
        var message = health.Message ?? (settings.Provider.Equals("Stub", StringComparison.OrdinalIgnoreCase)
            ? "Using the built-in rules engine; no external model is required."
            : "Ollama is ready.");
        return Ok(new LlmInfoResponse(
            settings.Provider,
            health.ModelName,
            status,
            message,
            settings.FallbackToStub,
            ollamaOptions.Value.MaxTranscriptCharsPerChunk));
    }
}
