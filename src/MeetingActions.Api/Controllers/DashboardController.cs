using MeetingActions.Api.Contracts;
using MeetingActions.Core.Domain;
using MeetingActions.Core.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingActions.Api.Controllers;

/// <summary>Provides aggregate action-item metrics for the dashboard.</summary>
[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(IActionItemRepository repository) : ControllerBase
{
    /// <summary>Calculates on-time, slip, and open-owner metrics.</summary>
    [HttpGet]
    public async Task<ActionResult<DashboardResponse>> Get(CancellationToken cancellationToken)
    {
        var allItems = await repository.GetAllItemsAsync(cancellationToken);
        var items = await repository.GetOpenForDashboardAsync(cancellationToken);
        var completed = allItems.Where(item => item.Status == ActionStatus.Done).ToArray();
        var dueItems = allItems.Where(item => item.DueDate is not null).ToArray();
        double? onTime = completed.Length == 0
            ? null
            : completed.Count(item => item.SlipCount == 0) * 100d / completed.Length;
        double? slipRate = dueItems.Length == 0
            ? null
            : dueItems.Count(item => item.SlipCount > 0) * 100d / dueItems.Length;
        var openItems = items.GroupBy(item => item.OwnerName ?? "Unassigned")
            .ToDictionary(group => group.Key, group => group.Count());
        return Ok(new DashboardResponse(onTime, slipRate, openItems));
    }
}
