using MeetingActions.Core.Domain;

namespace MeetingActions.Core.Abstractions;

/// <summary>Creates and queries work items in an external task system.</summary>
public interface ITaskProvider
{
    /// <summary>Creates a ticket for an approved action item.</summary>
    Task<string> CreateTaskAsync(ActionItem item, CancellationToken cancellationToken = default);
    /// <summary>Finds tickets that may duplicate the supplied action.</summary>
    Task<IReadOnlyList<string>> FindSimilarAsync(ActionItem item, CancellationToken cancellationToken = default);
    /// <summary>Gets the current external ticket status.</summary>
    Task<string?> GetStatusAsync(string ticketId, CancellationToken cancellationToken = default);
    /// <summary>Updates the due date of an external ticket.</summary>
    Task UpdateDueDateAsync(string ticketId, DateTime? dueDate, CancellationToken cancellationToken = default);
}

/// <summary>Extracts candidate action items from meeting transcripts.</summary>
public interface ILlmExtractor
{
    /// <summary>Extracts action candidates with their source evidence.</summary>
    Task<IReadOnlyList<ActionItem>> ExtractAsync(string transcript, DateTime meetingDate, IReadOnlyList<string> attendees, CancellationToken cancellationToken = default);
}

/// <summary>Sends notifications and optional quick-reply choices.</summary>
public interface INotifier
{
    /// <summary>Sends a message to the named recipient.</summary>
    Task SendAsync(string recipient, string message, IReadOnlyList<string>? quickReplyOptions = null, CancellationToken cancellationToken = default);
}

/// <summary>Provides the current time to application services.</summary>
public interface IClock
{
    /// <summary>Gets the current UTC time.</summary>
    DateTime UtcNow { get; }
}

/// <summary>Provides a controllable clock for deterministic demos and tests.</summary>
public sealed class VirtualClock(DateTime? initialTime = null) : IClock
{
    /// <summary>Gets the current virtual UTC time.</summary>
    public DateTime UtcNow { get; private set; } = DateTime.SpecifyKind(initialTime ?? DateTime.UtcNow, DateTimeKind.Utc);

    /// <summary>Moves virtual time forward by the specified number of days.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The requested number of days is negative.</exception>
    public void AdvanceDays(int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        UtcNow = UtcNow.AddDays(days);
    }
}

/// <summary>Provides real UTC time.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Gets the current UTC time.</summary>
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Reads meeting and action data for API operations.</summary>
public interface IMeetingRepository
{
    /// <summary>Gets meetings with their action items.</summary>
    Task<IReadOnlyList<Meeting>> GetMeetingsAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets a meeting with its action items by identifier.</summary>
    Task<Meeting?> GetMeetingAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Gets action items for a meeting.</summary>
    Task<IReadOnlyList<ActionItem>> GetItemsAsync(Guid meetingId, CancellationToken cancellationToken = default);
    /// <summary>Gets an action item by identifier.</summary>
    Task<ActionItem?> GetItemAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Gets all action items.</summary>
    Task<IReadOnlyList<ActionItem>> GetAllItemsAsync(CancellationToken cancellationToken = default);
    /// <summary>Persists the supplied meeting graph.</summary>
    Task AddMeetingAsync(Meeting meeting, CancellationToken cancellationToken = default);
    /// <summary>Persists pending changes.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets audit events for an action item.</summary>
    Task<IReadOnlyList<AuditEvent>> GetAuditEventsAsync(Guid actionItemId, CancellationToken cancellationToken = default);
}
