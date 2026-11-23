using MeetingActions.Core.Abstractions;
using MeetingActions.Infrastructure.Llm;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;

namespace MeetingActions.Api.Controllers;

/// <summary>Reports API health, version, and current UTC time.</summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController(
    IClock clock,
    IPersistenceHealthCheck persistenceHealth,
    ILlmHealth llmHealth,
    IOptions<LlmOptions> llmOptions) : ControllerBase
{
    /// <summary>Returns persistence health and a separately degraded LLM provider status.</summary>
    [HttpGet]
    public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
    {
        var available = await persistenceHealth.IsAvailableAsync(cancellationToken);
        var provider = llmOptions.Value.Provider;
        var modelHealth = await llmHealth.CheckAsync(cancellationToken);
        var modelStatus = modelHealth.Reachable && modelHealth.ModelInstalled ? "Healthy" : "Degraded";
        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
        return available
            ? Ok(new
            {
                Status = "Healthy",
                Version = version,
                UtcTime = clock.UtcNow,
                Llm = new { Provider = provider, Status = modelStatus, Model = modelHealth.ModelName }
            })
            : Ok(new
            {
                Status = "Degraded",
                Version = version,
                UtcTime = clock.UtcNow,
                Message = "The persistence service is unavailable.",
                Llm = new { Provider = provider, Status = modelStatus, Model = modelHealth.ModelName }
            });
    }
}
