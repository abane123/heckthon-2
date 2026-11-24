using MeetingActions.Api.Contracts;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using Microsoft.AspNetCore.Mvc;

namespace MeetingActions.Api.Controllers;

/// <summary>Edits, reviews, and tracks individual action items.</summary>
[ApiController]
[Route("api/items")]
public sealed class ItemsController(
    IActionItemRepository actionItemRepository,
    IUserRepository userRepository,
    IFollowUpRepository followUpRepository,
    IAuditEventRepository auditEventRepository,
    ActionItemStateMachine stateMachine,
    IClock clock,
    ITaskProvider taskProvider) : ControllerBase
{
    /// <summary>Edits a draft's title, owner, and due date.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ActionItem>> Update(Guid id, UpdateActionItemRequest request, CancellationToken cancellationToken)
    {
        var item = await actionItemRepository.GetItemAsync(id, cancellationToken);
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
        item.OwnerId = item.OwnerName is null
            ? null
            : (await userRepository.GetByNameAsync(item.OwnerName, cancellationToken))?.Id;
        item.SuggestedOwnerName = null;
        item.DueDate = request.DueDate;
        item.NeedsInput.RemoveAll(value =>
            value.Equals("owner", StringComparison.OrdinalIgnoreCase)
            || value.Equals("due date", StringComparison.OrdinalIgnoreCase)
            || value.Equals("speaker unknown", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(item.OwnerName))
        {
            item.NeedsInput.Add("Owner");
        }

        if (item.DueDate is null)
        {
            item.NeedsInput.Add("Due date");
        }

        var expectedVersion = item.Version;
        if (!await actionItemRepository.TryUpdateAsync(item, expectedVersion, cancellationToken))
        {
            return ConcurrencyConflict();
        }

        await AddAuditAsync(item, "Draft edited", "Organizer", cancellationToken);
        return Ok(item);
    }

    /// <summary>Approves a draft action item through the workflow state machine.</summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<ActionItem>> Approve(Guid id, CancellationToken cancellationToken)
    {
        var item = await actionItemRepository.GetItemAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        if (item.Status == ActionStatus.Draft
            && (string.IsNullOrWhiteSpace(item.OwnerName) || item.DueDate is null))
        {
            return MissingRequiredFieldsProblem(item);
        }

        try
        {
            var expectedVersion = item.Version;
            stateMachine.Transition(item, ActionStatus.Approved);
            if (!await actionItemRepository.TryUpdateAsync(item, expectedVersion, cancellationToken))
            {
                return ConcurrencyConflict();
            }
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }

        await AddAuditAsync(item, "Approved", "Organizer", cancellationToken);
        return Ok(item);
    }

    /// <summary>Returns an approved item to draft review through the workflow state machine.</summary>
    [HttpPost("{id:guid}/unapprove")]
    public async Task<ActionResult<ActionItem>> Unapprove(Guid id, CancellationToken cancellationToken)
    {
        var item = await actionItemRepository.GetItemAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            var expectedVersion = item.Version;
            stateMachine.Transition(item, ActionStatus.Draft);
            if (!await actionItemRepository.TryUpdateAsync(item, expectedVersion, cancellationToken))
            {
                return ConcurrencyConflict();
            }
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }

        await AddAuditAsync(item, "Approval undone", "Organizer", cancellationToken);
        return Ok(item);
    }

    /// <summary>Rejects a draft action item.</summary>
    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<ActionItem>> Reject(Guid id, CancellationToken cancellationToken)
    {
        var item = await actionItemRepository.GetItemAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            var expectedVersion = item.Version;
            stateMachine.Transition(item, ActionStatus.Rejected);
            if (!await actionItemRepository.TryUpdateAsync(item, expectedVersion, cancellationToken))
            {
                return ConcurrencyConflict();
            }
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }

        await AddAuditAsync(item, "Rejected", "Organizer", cancellationToken);
        return Ok(item);
    }

    /// <summary>Restores a rejected action item to draft review.</summary>
    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<ActionItem>> Restore(Guid id, CancellationToken cancellationToken)
    {
        var item = await actionItemRepository.GetItemAsync(id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        try
        {
            var expectedVersion = item.Version;
            stateMachine.Transition(item, ActionStatus.Draft);
            if (!await actionItemRepository.TryUpdateAsync(item, expectedVersion, cancellationToken))
            {
                return ConcurrencyConflict();
            }
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }

        await AddAuditAsync(item, "Reopened for review", "Organizer", cancellationToken);
        return Ok(item);
    }

    /// <summary>Records a quick reply and applies its supported action to the item.</summary>
    [HttpPost("{id:guid}/reply")]
    public async Task<ActionResult<ActionItem>> Reply(Guid id, ActionReplyRequest request, CancellationToken cancellationToken)
    {
        var item = await actionItemRepository.GetItemAsync(id, cancellationToken);
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
                item.OwnerId = (await userRepository.GetByNameAsync(item.OwnerName, cancellationToken))?.Id;
                break;
            default:
                return BadRequest("Action must be Done, PushToFriday, Blocked, or Reassign.");
        }

        var expectedVersion = item.Version;
        if (!await actionItemRepository.TryUpdateAsync(item, expectedVersion, cancellationToken))
        {
            return ConcurrencyConflict();
        }

        await AddAuditAsync(item, $"Owner reply: {request.Action}", item.OwnerName ?? "Owner", cancellationToken);
        await followUpRepository.AddAsync(new FollowUp
        {
            ActionItemId = item.Id,
            SentAt = clock.UtcNow,
            Channel = "quick-reply",
            Response = request.Action
        }, cancellationToken);
        return Ok(item);
    }

    /// <summary>Lists every action item for the tracker.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ActionItem>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await actionItemRepository.GetAllItemsAsync(cancellationToken));

    /// <summary>Gets the audit trail for one action item.</summary>
    [HttpGet("{id:guid}/audit")]
    public async Task<ActionResult<IReadOnlyList<AuditEvent>>> GetAudit(Guid id, CancellationToken cancellationToken)
    {
        if (await actionItemRepository.GetItemAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        return Ok(await auditEventRepository.GetByActionItemAsync(id, cancellationToken));
    }

    /// <summary>Records an audit event after its corresponding item update succeeds.</summary>
    private Task AddAuditAsync(
        ActionItem item,
        string eventType,
        string actor,
        CancellationToken cancellationToken) =>
        auditEventRepository.AddAsync(new AuditEvent
        {
            ActionItemId = item.Id,
            EventType = eventType,
            Actor = actor,
            CreatedAt = clock.UtcNow
        }, cancellationToken);

    /// <summary>Builds a traceable conflict response when a versioned action update loses a race.</summary>
    private ConflictObjectResult ConcurrencyConflict()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "The action item was changed by another request.",
            Detail = "Reload the action item and retry the operation.",
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return Conflict(problem);
    }

    /// <summary>Builds a traceable validation response for approval prerequisites that are missing.</summary>
    private BadRequestObjectResult MissingRequiredFieldsProblem(ActionItem item)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(item.OwnerName))
        {
            errors["ownerName"] = ["Missing owner"];
        }

        if (item.DueDate is null)
        {
            errors["dueDate"] = ["Missing due date"];
        }

        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "The action item is missing required fields.",
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return BadRequest(problem);
    }
}
