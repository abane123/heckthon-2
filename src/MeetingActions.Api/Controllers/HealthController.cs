using MeetingActions.Core.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingActions.Api.Controllers;

/// <summary>Reports API health, version, and current UTC time.</summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController(IClock clock) : ControllerBase
{
    /// <summary>Returns the health status and runtime metadata.</summary>
    [HttpGet]
    public ActionResult<object> Get() => Ok(new
    {
        Status = "Healthy",
        Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
        UtcTime = clock.UtcNow
    });
}
