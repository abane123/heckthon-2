namespace MeetingActions.Api.Contracts;

/// <summary>Fields used to create a meeting and extract draft actions.</summary>
public sealed record CreateMeetingRequest(string Title, DateTime Date, IReadOnlyList<string> Attendees, string Transcript);

/// <summary>Names an attendee to add to an existing meeting.</summary>
public sealed record AddMeetingAttendeeRequest(string Name);

/// <summary>Fields an organizer can edit on a draft action.</summary>
public sealed record UpdateActionItemRequest(string Title, string? OwnerName, DateTime? DueDate);

/// <summary>Represents a quick reply submitted by an action owner.</summary>
public sealed record ActionReplyRequest(string Action, string? ReassignTo = null);

/// <summary>Summarizes action progress metrics for the dashboard, with null values when a rate has no denominator.</summary>
public sealed record DashboardResponse(double? OnTimeRate, double? SlipRate, IReadOnlyDictionary<string, int> OpenItemsPerOwner);

/// <summary>Reports successfully created tickets and any item-specific failures.</summary>
public sealed record TicketCreationResponse(
    IReadOnlyList<MeetingActions.Core.Domain.ActionItem> CreatedItems,
    IReadOnlyList<TicketCreationFailure> Failures);

/// <summary>Describes a ticket-creation failure without exposing provider internals.</summary>
public sealed record TicketCreationFailure(Guid ActionItemId, string Title, string Detail);

/// <summary>Reports the selected LLM provider, model availability, and any setup action required.</summary>
public sealed record LlmInfoResponse(
    string Provider,
    string Model,
    string Status,
    string Message,
    bool FallbackToStub,
    int MaxTranscriptCharsPerChunk);
