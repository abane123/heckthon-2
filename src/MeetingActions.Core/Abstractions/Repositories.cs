using MeetingActions.Core.Domain;

namespace MeetingActions.Core.Abstractions;

/// <summary>Reads and writes meetings and their associated action items.</summary>
public interface IMeetingRepository
{
    /// <summary>Gets all meetings with their action items.</summary>
    Task<IReadOnlyList<Meeting>> GetMeetingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets one meeting with its action items.</summary>
    Task<Meeting?> GetMeetingAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Persists a meeting and its draft items.</summary>
    Task AddMeetingAsync(Meeting meeting, CancellationToken cancellationToken = default);

    /// <summary>Persists meeting metadata changes.</summary>
    Task UpdateMeetingAsync(Meeting meeting, CancellationToken cancellationToken = default);

    /// <summary>Gets open items from earlier meetings sharing this meeting's title.</summary>
    Task<IReadOnlyList<ActionItem>> GetNextAgendaItemsAsync(Guid meetingId, CancellationToken cancellationToken = default);
}

/// <summary>Queries and atomically updates action items.</summary>
public interface IActionItemRepository
{
    /// <summary>Gets one action item by its identifier.</summary>
    Task<ActionItem?> GetItemAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Gets action items for a meeting.</summary>
    Task<IReadOnlyList<ActionItem>> GetItemsAsync(Guid meetingId, CancellationToken cancellationToken = default);

    /// <summary>Gets every action item.</summary>
    Task<IReadOnlyList<ActionItem>> GetAllItemsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets items in the requested workflow status.</summary>
    Task<IReadOnlyList<ActionItem>> GetByStatusAsync(ActionStatus status, CancellationToken cancellationToken = default);

    /// <summary>Gets items due on or before a date in the requested statuses.</summary>
    Task<IReadOnlyList<ActionItem>> GetDueOnOrBeforeAsync(
        DateTime date,
        IReadOnlyCollection<ActionStatus> statuses,
        CancellationToken cancellationToken = default);

    /// <summary>Gets items assigned to a user.</summary>
    Task<IReadOnlyList<ActionItem>> GetByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>Gets active workflow items used by dashboard owner counts.</summary>
    Task<IReadOnlyList<ActionItem>> GetOpenForDashboardAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts action items independently of meeting document storage.</summary>
    Task AddManyAsync(IReadOnlyCollection<ActionItem> items, CancellationToken cancellationToken = default);

    /// <summary>Updates an item only when its version still matches the caller's snapshot.</summary>
    Task<bool> TryUpdateAsync(ActionItem item, int expectedVersion, CancellationToken cancellationToken = default);
}

/// <summary>Queries and persists users used to resolve action owners.</summary>
public interface IUserRepository
{
    /// <summary>Gets all users.</summary>
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds one user by case-insensitive display name.</summary>
    Task<User?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Inserts users for the demo environment.</summary>
    Task AddManyAsync(IReadOnlyCollection<User> users, CancellationToken cancellationToken = default);
}

/// <summary>Records and queries owner replies and follow-up notifications.</summary>
public interface IFollowUpRepository
{
    /// <summary>Records one follow-up.</summary>
    Task AddAsync(FollowUp followUp, CancellationToken cancellationToken = default);

    /// <summary>Records multiple follow-ups.</summary>
    Task AddManyAsync(IReadOnlyCollection<FollowUp> followUps, CancellationToken cancellationToken = default);

    /// <summary>Checks whether an item already has a follow-up of this channel on a UTC date.</summary>
    Task<bool> ExistsOnDateAsync(
        Guid actionItemId,
        string channel,
        DateTime utcDate,
        CancellationToken cancellationToken = default);

    /// <summary>Gets all follow-ups for an action item in chronological order.</summary>
    Task<IReadOnlyList<FollowUp>> GetByActionItemAsync(Guid actionItemId, CancellationToken cancellationToken = default);

    /// <summary>Gets every follow-up for operational verification.</summary>
    Task<IReadOnlyList<FollowUp>> GetAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Records and queries the immutable audit trail for action items.</summary>
public interface IAuditEventRepository
{
    /// <summary>Records one audit event.</summary>
    Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>Records multiple audit events.</summary>
    Task AddManyAsync(IReadOnlyCollection<AuditEvent> auditEvents, CancellationToken cancellationToken = default);

    /// <summary>Gets an action item's audit events in chronological order.</summary>
    Task<IReadOnlyList<AuditEvent>> GetByActionItemAsync(Guid actionItemId, CancellationToken cancellationToken = default);
}

/// <summary>Seeds and resets the complete demo data set without exposing database technology.</summary>
public interface IDemoDataStore
{
    /// <summary>Returns whether the store has no existing demo users or meetings.</summary>
    Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default);

    /// <summary>Seeds the initial users, meeting, and extracted items when the store is empty.</summary>
    Task SeedAsync(IReadOnlyCollection<User> users, Meeting meeting, CancellationToken cancellationToken = default);

    /// <summary>Clears persisted demo data and replaces it with the supplied scenario.</summary>
    Task ResetAsync(IReadOnlyCollection<User> users, Meeting meeting, CancellationToken cancellationToken = default);
}

/// <summary>Checks whether the configured persistence provider is reachable.</summary>
public interface IPersistenceHealthCheck
{
    /// <summary>Returns whether the persistence provider can answer a health probe.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
