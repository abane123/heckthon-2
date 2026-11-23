using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace MeetingActions.Api.Controllers;

/// <summary>Exposes development-only helpers for deterministic demos.</summary>
[ApiController]
[Route("api/dev")]
public sealed class DevController(IClock clock, FollowUpService followUpService) : ControllerBase
{
    /// <summary>Returns the current virtual clock date for the application shell.</summary>
    [HttpGet("clock")]
    public ActionResult<object> GetClock() => Ok(new { CurrentDate = clock.UtcNow });

    /// <summary>Advances virtual time and returns the number of reminders sent in the follow-up pass.</summary>
    [HttpPost("simulate-days")]
    public async Task<IActionResult> SimulateDays([FromQuery] int days = 1, CancellationToken cancellationToken = default)
    {
        if (days < 1 || days > 365)
        {
            return BadRequest("Days must be between 1 and 365.");
        }

        if (clock is not VirtualClock virtualClock)
        {
            return Conflict("The virtual clock is not enabled.");
        }

        virtualClock.AdvanceDays(days);
        var remindersSent = await followUpService.RunAsync(cancellationToken);
        return Ok(new { virtualClock.UtcNow, AdvancedDays = days, RemindersSent = remindersSent });
    }
}
