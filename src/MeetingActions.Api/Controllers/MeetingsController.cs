using MeetingActions.Api.Contracts;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MeetingActions.Api.Controllers;

/// <summary>Manages meetings, extraction, agendas, and ticket creation.</summary>
[ApiController]
[Route("api/meetings")]
public sealed class MeetingsController(
    IMeetingRepository repository,
    MeetingActionsDbContext dbContext,
    ILlmExtractor extractor,
    ITaskProvider taskProvider,
    ActionItemStateMachine stateMachine,
    IClock clock,
    ILogger<MeetingsController> logger) : ControllerBase
{
    /// <summary>Creates a meeting and persists supported extracted action candidates.</summary>
    [HttpPost]
    public async Task<ActionResult<Meeting>> Create([FromBody] CreateMeetingRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Transcript))
        {
            return BadRequest("Title and transcript are required.");
        }

        var meeting = new Meeting
        {
            Title = request.Title.Trim(),
            MeetingDate = request.Date,
            Transcript = request.Transcript,
            Attendees = request.Attendees.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).ToList()
        };
        var users = await dbContext.Users.ToListAsync(cancellationToken);
        meeting.Items = (await extractor.ExtractAsync(meeting.Transcript, meeting.MeetingDate, meeting.Attendees, cancellationToken))
            .Select(item =>
            {
                item.MeetingId = meeting.Id;
                item.OwnerId = users.FirstOrDefault(user =>
                    string.Equals(user.Name, item.OwnerName, StringComparison.OrdinalIgnoreCase))?.Id;
                return item;
            }).ToList();
        await repository.AddMeetingAsync(meeting, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = meeting.Id }, meeting);
    }

    /// <summary>Lists meetings with their draft and approved actions.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Meeting>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await repository.GetMeetingsAsync(cancellationToken));

    /// <summary>Gets one meeting with all of its action items.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Meeting>> Get(Guid id, CancellationToken cancellationToken)
    {
        var meeting = await repository.GetMeetingAsync(id, cancellationToken);
        return meeting is null ? NotFound() : Ok(meeting);
    }

    /// <summary>Lists the action items belonging to one meeting.</summary>
    [HttpGet("{id:guid}/items")]
    public async Task<ActionResult<IReadOnlyList<ActionItem>>> GetItems(Guid id, CancellationToken cancellationToken)
    {
        if (await repository.GetMeetingAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        return Ok(await repository.GetItemsAsync(id, cancellationToken));
    }

    /// <summary>Creates external tickets for all approved items in a meeting.</summary>
    [HttpPost("{id:guid}/create-tickets")]
    public async Task<ActionResult<TicketCreationResponse>> CreateTickets(Guid id, CancellationToken cancellationToken)
    {
        var meeting = await repository.GetMeetingAsync(id, cancellationToken);
        if (meeting is null)
        {
            return NotFound();
        }

        var createdItems = new List<ActionItem>();
        var failures = new List<TicketCreationFailure>();
        foreach (var item in meeting.Items.Where(item => item.Status == ActionStatus.Approved))
        {
            try
            {
                var ticketId = await taskProvider.CreateTaskAsync(item, cancellationToken);
                if (string.IsNullOrWhiteSpace(ticketId))
                {
                    throw new InvalidOperationException("The task provider returned an empty ticket ID.");
                }

                item.ExternalTicketId = ticketId;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Ticket creation failed for action item {ActionItemId}", item.Id);
                failures.Add(new TicketCreationFailure(
                    item.Id,
                    item.Title,
                    "The ticket provider could not create this item. You can retry it."));
                continue;
            }

            stateMachine.Transition(item, ActionStatus.Created);
            dbContext.AuditEvents.Add(new AuditEvent
            {
                ActionItemId = item.Id,
                EventType = "Ticket created",
                Actor = "Organizer",
                CreatedAt = clock.UtcNow
            });
            createdItems.Add(item);
        }

        await repository.SaveChangesAsync(cancellationToken);
        return Ok(new TicketCreationResponse(createdItems, failures));
    }

    /// <summary>Returns open actions from earlier meetings with the same title.</summary>
    [HttpGet("{id:guid}/next-agenda")]
    public async Task<ActionResult<IReadOnlyList<ActionItem>>> GetNextAgenda(Guid id, CancellationToken cancellationToken)
    {
        var meeting = await repository.GetMeetingAsync(id, cancellationToken);
        if (meeting is null)
        {
            return NotFound();
        }

        var previousMeetings = await dbContext.Meetings
            .Where(other => other.Id != id && other.Title == meeting.Title && other.MeetingDate < meeting.MeetingDate)
            .SelectMany(other => other.Items)
            .Where(item => item.Status != ActionStatus.Done && item.Status != ActionStatus.Rejected)
            .ToListAsync(cancellationToken);
        return Ok(previousMeetings);
    }
}
