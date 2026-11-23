using MeetingActions.Api.Contracts;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MeetingActions.Api.Controllers;

/// <summary>Edits, reviews, and tracks individual action items.</summary>
[ApiController]
[Route("api/items")]
public sealed class ItemsController(
    IMeetingRepository repository,
    MeetingActionsDbContext dbContext,
    ActionItemStateMachine stateMachine,
    IClock clock,
    ITaskProvider taskProvider) : ControllerBase
{
    /// <summary>Edits a draft's title, owner, and due date.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ActionItem>> Update(Guid id, UpdateActionItemRequest request, CancellationToken cancellationToken)
    {
        var item = await repository.GetItemAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        if (item.Status != ActionStatus.Draft)
        {
            return Conflict("Only draft items can be edited.");
        }

        item.Title = request.Title.Trim();
        item.OwnerName = request.OwnerName?.Trim();
        item.OwnerId = item.OwnerName is null ? null : await dbContext.Users
            .Where(user => user.Name == item.OwnerName).Select(user => (Guid?)user.Id).FirstOrDefaultAsync(cancellationToken);
        item.DueDate = request.DueDate;
        item.NeedsInput.Remove("Owner");
        item.NeedsInput.Remove("Due date");
        if (string.IsNullOrWhiteSpace(item.OwnerName))
        {
            item.NeedsInput.Add("Owner");
        }

        if (item.DueDate is null)
        {
            item.NeedsInput.Add("Due date");
        }

        AddAudit(item, "Draft edited", "Organizer");
        await repository.SaveChangesAsync(cancellationToken);
        return Ok(item);
    }

    /// <summary>Approves a draft action item through the workflow state machine.</summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<ActionItem>> Approve(Guid id, CancellationToken cancellationToken)
    {
        var item = await repository.GetItemAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            stateMachine.Transition(item, ActionStatus.Approved);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }

        AddAudit(item, "Approved", "Organizer");
        await repository.SaveChangesAsync(cancellationToken);
        return Ok(item);
    }

    /// <summary>Returns an approved item to draft review through the workflow state machine.</summary>
    [HttpPost("{id:guid}/unapprove")]
    public async Task<ActionResult<ActionItem>> Unapprove(Guid id, CancellationToken cancellationToken)
    {
        var item = await repository.GetItemAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            stateMachine.Transition(item, ActionStatus.Draft);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }

        AddAudit(item, "Approval undone", "Organizer");
        await repository.SaveChangesAsync(cancellationToken);
        return Ok(item);
    }

    /// <summary>Rejects a draft action item.</summary>
    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<ActionItem>> Reject(Guid id, CancellationToken cancellationToken)
    {
        var item = await repository.GetItemAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            stateMachine.Transition(item, ActionStatus.Rejected);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }

        AddAudit(item, "Rejected", "Organizer");
        await repository.SaveChangesAsync(cancellationToken);
        return Ok(item);
    }

    /// <summary>Restores a rejected action item to draft review.</summary>
    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<ActionItem>> Restore(Guid id, CancellationToken cancellationToken)
    {
        var item = await repository.GetItemAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            stateMachine.Transition(item, ActionStatus.Draft);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }

        AddAudit(item, "Reopened for review", "Organizer");
        await repository.SaveChangesAsync(cancellationToken);
        return Ok(item);
    }

    /// <summary>Records a quick reply and applies its supported action to the item.</summary>
    [HttpPost("{id:guid}/reply")]
    public async Task<ActionResult<ActionItem>> Reply(Guid id, ActionReplyRequest request, CancellationToken cancellationToken)
    {
        var item = await repository.GetItemAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        switch (request.Action)
        {
            case "Done":
                stateMachine.Transition(item, ActionStatus.Done);
                break;
            case "PushToFriday":
                var daysUntilFriday = ((int)DayOfWeek.Friday - (int)clock.UtcNow.DayOfWeek + 7) % 7;
                item.DueDate = clock.UtcNow.Date.AddDays(daysUntilFriday == 0 ? 7 : daysUntilFriday);
                if (item.ExternalTicketId is not null)
                {
                    await taskProvider.UpdateDueDateAsync(item.ExternalTicketId, item.DueDate, cancellationToken);
                }
                break;
            case "Blocked":
                item.NeedsInput.Add("Owner response: blocked");
                break;
            case "Reassign":
                if (string.IsNullOrWhiteSpace(request.ReassignTo))
                {
                    return BadRequest("ReassignTo is required for a Reassign reply.");
                }

                item.OwnerName = request.ReassignTo.Trim();
                item.OwnerId = await dbContext.Users.Where(user => user.Name == item.OwnerName)
                    .Select(user => (Guid?)user.Id).FirstOrDefaultAsync(cancellationToken);
                break;
            default:
                return BadRequest("Action must be Done, PushToFriday, Blocked, or Reassign.");
        }

        AddAudit(item, $"Owner reply: {request.Action}", item.OwnerName ?? "Owner");
        dbContext.FollowUps.Add(new FollowUp
        {
            ActionItemId = item.Id,
            SentAt = clock.UtcNow,
            Channel = "quick-reply",
            Response = request.Action
        });
        await repository.SaveChangesAsync(cancellationToken);
        return Ok(item);
    }

    /// <summary>Lists every action item for the tracker.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ActionItem>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await repository.GetAllItemsAsync(cancellationToken));

    /// <summary>Gets the audit trail for one action item.</summary>
    [HttpGet("{id:guid}/audit")]
    public async Task<ActionResult<IReadOnlyList<AuditEvent>>> GetAudit(Guid id, CancellationToken cancellationToken)
    {
        if (await repository.GetItemAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        return Ok(await repository.GetAuditEventsAsync(id, cancellationToken));
    }

    private void AddAudit(ActionItem item, string eventType, string actor) =>
        dbContext.AuditEvents.Add(new AuditEvent
        {
            ActionItemId = item.Id,
            EventType = eventType,
            Actor = actor,
            CreatedAt = clock.UtcNow
        });
}
