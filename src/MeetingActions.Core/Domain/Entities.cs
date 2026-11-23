using System.Text.Json.Serialization;

namespace MeetingActions.Core.Domain;

/// <summary>Represents a recorded meeting and its transcript.</summary>
public sealed class Meeting
{
    /// <summary>Gets or sets the meeting identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets the meeting title.</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>Gets or sets the date of the meeting.</summary>
    public DateTime MeetingDate { get; set; }
    /// <summary>Gets or sets the source transcript.</summary>
    public string Transcript { get; set; } = string.Empty;
    /// <summary>Gets or sets the attendees' display names.</summary>
    public List<string> Attendees { get; set; } = [];
    /// <summary>Gets or sets the meeting action items.</summary>
    public List<ActionItem> Items { get; set; } = [];
}

/// <summary>Represents an extracted action requiring review or follow-up.</summary>
public sealed class ActionItem
{
    /// <summary>Gets or sets the action identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets the owning meeting identifier.</summary>
    public Guid MeetingId { get; set; }
    /// <summary>Gets or sets the navigation to the owning meeting.</summary>
    [JsonIgnore]
    public Meeting? Meeting { get; set; }
    /// <summary>Gets or sets the action title.</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>Gets or sets the owner's user identifier.</summary>
    public Guid? OwnerId { get; set; }
    /// <summary>Gets or sets the owner's display name.</summary>
    public string? OwnerName { get; set; }
    /// <summary>Gets or sets the due date when known.</summary>
    public DateTime? DueDate { get; set; }
    /// <summary>Gets or sets the due date wording from the transcript.</summary>
    public string DueDateText { get; set; } = string.Empty;
    /// <summary>Gets or sets the current workflow status.</summary>
    public ActionStatus Status { get; set; } = ActionStatus.Draft;
    /// <summary>Gets or sets extraction confidence from zero to one.</summary>
    public double Confidence { get; set; }
    /// <summary>Gets or sets the supporting transcript quote.</summary>
    public string SourceQuote { get; set; } = string.Empty;
    /// <summary>Gets or sets the transcript timestamp when available.</summary>
    public string Timestamp { get; set; } = string.Empty;
    /// <summary>Gets or sets information required from the organizer.</summary>
    public List<string> NeedsInput { get; set; } = [];
    /// <summary>Gets or sets the external ticket identifier.</summary>
    public string? ExternalTicketId { get; set; }
    /// <summary>Gets or sets the number of deadline slips.</summary>
    public int SlipCount { get; set; }
}

/// <summary>Represents a person who can own action items.</summary>
public sealed class User
{
    /// <summary>Gets or sets the user identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets the user's name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the user's email address.</summary>
    public string Email { get; set; } = string.Empty;
    /// <summary>Gets or sets the task provider's user identifier.</summary>
    public string? ExternalUserId { get; set; }
}

/// <summary>Represents a notification sent for an action item.</summary>
public sealed class FollowUp
{
    /// <summary>Gets or sets the follow-up identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets the related action identifier.</summary>
    public Guid ActionItemId { get; set; }
    /// <summary>Gets or sets when the follow-up was sent.</summary>
    public DateTime SentAt { get; set; }
    /// <summary>Gets or sets the delivery channel.</summary>
    public string Channel { get; set; } = string.Empty;
    /// <summary>Gets or sets the recipient's response, if any.</summary>
    public string? Response { get; set; }
}

/// <summary>Represents an auditable action-item event.</summary>
public sealed class AuditEvent
{
    /// <summary>Gets or sets the audit event identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Gets or sets the related action identifier.</summary>
    public Guid ActionItemId { get; set; }
    /// <summary>Gets or sets the event type.</summary>
    public string EventType { get; set; } = string.Empty;
    /// <summary>Gets or sets the actor responsible for the event.</summary>
    public string Actor { get; set; } = string.Empty;
    /// <summary>Gets or sets when the event occurred.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>Describes the action-item workflow lifecycle.</summary>
public enum ActionStatus
{
    /// <summary>Waiting for organizer review.</summary>
    Draft,
    /// <summary>Approved by the organizer.</summary>
    Approved,
    /// <summary>A task-provider ticket has been created.</summary>
    Created,
    /// <summary>Work is underway.</summary>
    InProgress,
    /// <summary>The due date passed without completion.</summary>
    Overdue,
    /// <summary>The work has been escalated.</summary>
    Escalated,
    /// <summary>The work is complete.</summary>
    Done,
    /// <summary>The organizer rejected the item.</summary>
    Rejected
}
