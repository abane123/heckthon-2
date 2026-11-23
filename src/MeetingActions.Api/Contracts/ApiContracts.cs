namespace MeetingActions.Api.Contracts;

/// <summary>Fields used to create a meeting and extract draft actions.</summary>
public sealed record CreateMeetingRequest(string Title, DateTime Date, IReadOnlyList<string> Attendees, string Transcript);

/// <summary>Fields an organizer can edit on a draft action.</summary>
public sealed record UpdateActionItemRequest(string Title, string? OwnerName, DateTime? DueDate);

/// <summary>Represents a quick reply submitted by an action owner.</summary>
public sealed record ActionReplyRequest(string Action, string? ReassignTo = null);

/// <summary>Summarizes action progress metrics for the dashboard.</summary>
public sealed record DashboardResponse(double OnTimeRate, double SlipRate, IReadOnlyDictionary<string, int> OpenItemsPerOwner);

/// <summary>Reports successfully created tickets and any item-specific failures.</summary>
public sealed record TicketCreationResponse(
    IReadOnlyList<MeetingActions.Core.Domain.ActionItem> CreatedItems,
    IReadOnlyList<TicketCreationFailure> Failures);

/// <summary>Describes a ticket-creation failure without exposing provider internals.</summary>
public sealed record TicketCreationFailure(Guid ActionItemId, string Title, string Detail);
