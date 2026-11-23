using MeetingActions.Api.Contracts;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace MeetingActions.Api.Controllers;

/// <summary>Manages meetings, extraction, agendas, and ticket creation.</summary>
[ApiController]
[Route("api/meetings")]
public sealed class MeetingsController(
    IMeetingRepository repository,
    IActionItemRepository actionItemRepository,
    IUserRepository userRepository,
    IAuditEventRepository auditEventRepository,
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
        var users = await userRepository.GetAllAsync(cancellationToken);
        var analysis = await extractor.ExtractAsync(
            meeting.Transcript, meeting.MeetingDate, meeting.Attendees, cancellationToken, meeting.Title);
        meeting.Items = analysis.Items
            .Select(item =>
            {
                item.MeetingId = meeting.Id;
                item.OwnerId = users.FirstOrDefault(user =>
                    string.Equals(user.Name, item.OwnerName, StringComparison.OrdinalIgnoreCase))?.Id;
                return item;
            }).ToList();
        SetAnalysisMetadata(meeting, analysis);
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

    /// <summary>Adds an attendee and resolves matching owners on this meeting's draft items.</summary>
    [HttpPost("{id:guid}/attendees")]
    public async Task<ActionResult<Meeting>> AddAttendee(
        Guid id,
        [FromBody] AddMeetingAttendeeRequest request,
        CancellationToken cancellationToken)
    {
        var meeting = await repository.GetMeetingAsync(id, cancellationToken);
        if (meeting is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            var problem = new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(request.Name)] = ["An attendee name is required."]
            })
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred.",
                Instance = HttpContext.Request.Path
            };
            problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
            return BadRequest(problem);
        }

        var attendeeName = request.Name.Trim();
        if (!meeting.Attendees.Contains(attendeeName, StringComparer.OrdinalIgnoreCase))
        {
            meeting.Attendees.Add(attendeeName);
        }

        var analysis = await extractor.ExtractAsync(
            meeting.Transcript, meeting.MeetingDate, meeting.Attendees, cancellationToken, meeting.Title);
        meeting.Decisions = analysis.Decisions.ToList();
        meeting.Analysis = ToMeetingAnalysisMetadata(analysis);
        var users = await userRepository.GetAllAsync(cancellationToken);
        foreach (var item in meeting.Items.Where(item =>
                     item.Status == ActionStatus.Draft
                     && item.OwnerId is null
                     && string.IsNullOrWhiteSpace(item.OwnerName)))
        {
            var candidate = analysis.Items.FirstOrDefault(extracted =>
                extracted.Timestamp == item.Timestamp
                && string.Equals(extracted.SourceQuote, item.SourceQuote, StringComparison.Ordinal));
            if (candidate is null)
            {
                continue;
            }

            item.OwnerName = candidate.OwnerName;
            item.OwnerId = users.FirstOrDefault(user =>
                string.Equals(user.Name, candidate.OwnerName, StringComparison.OrdinalIgnoreCase))?.Id;
            item.SuggestedOwnerName = candidate.SuggestedOwnerName;
            item.Confidence = candidate.Confidence;
            item.NeedsInput.RemoveAll(value =>
                value.Equals("owner", StringComparison.OrdinalIgnoreCase)
                || value.Equals("speaker unknown", StringComparison.OrdinalIgnoreCase));
            foreach (var missingField in candidate.NeedsInput.Where(value =>
                         value.Equals("owner", StringComparison.OrdinalIgnoreCase)
                         || value.Equals("speaker unknown", StringComparison.OrdinalIgnoreCase)))
            {
                if (!item.NeedsInput.Contains(missingField, StringComparer.OrdinalIgnoreCase))
                {
                    item.NeedsInput.Add(missingField);
                }
            }

            var expectedVersion = item.Version;
            if (!await actionItemRepository.TryUpdateAsync(item, expectedVersion, cancellationToken))
            {
                return ConcurrencyConflict();
            }
        }

        await repository.UpdateMeetingAsync(meeting, cancellationToken);
        return Ok(meeting);
    }

    /// <summary>Lists the action items belonging to one meeting.</summary>
    [HttpGet("{id:guid}/items")]
    public async Task<ActionResult<IReadOnlyList<ActionItem>>> GetItems(Guid id, CancellationToken cancellationToken)
    {
        if (await repository.GetMeetingAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        return Ok(await actionItemRepository.GetItemsAsync(id, cancellationToken));
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
            if (string.IsNullOrWhiteSpace(item.OwnerName))
            {
                failures.Add(new TicketCreationFailure(item.Id, item.Title, "Missing owner"));
                continue;
            }

            if (item.DueDate is null)
            {
                failures.Add(new TicketCreationFailure(item.Id, item.Title, "Missing due date"));
                continue;
            }

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

            var expectedVersion = item.Version;
            stateMachine.Transition(item, ActionStatus.Created);
            if (!await actionItemRepository.TryUpdateAsync(item, expectedVersion, cancellationToken))
            {
                failures.Add(new TicketCreationFailure(
                    item.Id,
                    item.Title,
                    "The action item changed while its ticket was being created. You can retry it."));
                continue;
            }

            await auditEventRepository.AddAsync(new AuditEvent
            {
                ActionItemId = item.Id,
                EventType = "Ticket created",
                Actor = "Organizer",
                CreatedAt = clock.UtcNow
            }, cancellationToken);
            createdItems.Add(item);
        }

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

        return Ok(await repository.GetNextAgendaItemsAsync(id, cancellationToken));
    }

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

    /// <summary>Copies completed analysis metadata to the API meeting entity.</summary>
    private static void SetAnalysisMetadata(Meeting meeting, AnalysisResult analysis)
    {
        meeting.Decisions = analysis.Decisions.ToList();
        meeting.Analysis = ToMeetingAnalysisMetadata(analysis);
    }

    /// <summary>Maps the internal extraction result to its backwards-compatible meeting response metadata.</summary>
    private static MeetingAnalysisMetadata ToMeetingAnalysisMetadata(AnalysisResult analysis) =>
        new(
            analysis.Provider,
            analysis.Model,
            analysis.ElapsedMilliseconds,
            analysis.DroppedCount,
            analysis.ChunkCount,
            analysis.Warnings);
}
