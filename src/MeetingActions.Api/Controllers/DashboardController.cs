using MeetingActions.Api.Contracts;
using MeetingActions.Core.Domain;
using MeetingActions.Core.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingActions.Api.Controllers;

/// <summary>Provides aggregate action-item metrics for the dashboard.</summary>
[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(IMeetingRepository repository) : ControllerBase
{
    /// <summary>Calculates on-time, slip, and open-owner metrics.</summary>
    [HttpGet]
    public async Task<ActionResult<DashboardResponse>> Get(CancellationToken cancellationToken)
    {
        var items = await repository.GetAllItemsAsync(cancellationToken);
        var completed = items.Where(item => item.Status == ActionStatus.Done).ToArray();
        var onTime = completed.Length == 0 ? 0 : completed.Count(item => item.SlipCount == 0) * 100d / completed.Length;
        var slipRate = items.Count == 0 ? 0 : items.Count(item => item.SlipCount > 0) * 100d / items.Count;
        var openItems = items.Where(item => item.Status is not (ActionStatus.Done or ActionStatus.Rejected))
            .GroupBy(item => item.OwnerName ?? "Unassigned")
            .ToDictionary(group => group.Key, group => group.Count());
        return Ok(new DashboardResponse(onTime, slipRate, openItems));
    }
}
