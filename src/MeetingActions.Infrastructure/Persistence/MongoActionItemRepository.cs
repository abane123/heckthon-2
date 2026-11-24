using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MongoDB.Driver;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Queries action items and performs version-checked atomic document updates.</summary>
public sealed class MongoActionItemRepository(MongoContext context) : IActionItemRepository
{
    private static readonly ActionStatus[] DashboardStatuses =
    [
        ActionStatus.Approved,
        ActionStatus.Created,
        ActionStatus.InProgress,
        ActionStatus.Overdue,
        ActionStatus.Escalated
    ];

    /// <summary>Gets one action item by its identifier.</summary>
    public async Task<ActionItem?> GetItemAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.ActionItems.Find(item => item.Id == id).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Gets action items for a meeting.</summary>
    public async Task<IReadOnlyList<ActionItem>> GetItemsAsync(Guid meetingId, CancellationToken cancellationToken = default) =>
        await context.ActionItems.Find(item => item.MeetingId == meetingId).ToListAsync(cancellationToken);

    /// <summary>Gets every action item.</summary>
    public async Task<IReadOnlyList<ActionItem>> GetAllItemsAsync(CancellationToken cancellationToken = default) =>
        await context.ActionItems.Find(Builders<ActionItem>.Filter.Empty).ToListAsync(cancellationToken);

    /// <summary>Gets items in the requested workflow status.</summary>
    public async Task<IReadOnlyList<ActionItem>> GetByStatusAsync(
        ActionStatus status,
        CancellationToken cancellationToken = default) =>
        await context.ActionItems.Find(item => item.Status == status).ToListAsync(cancellationToken);

    /// <summary>Gets items due on or before a date in the requested statuses.</summary>
    public async Task<IReadOnlyList<ActionItem>> GetDueOnOrBeforeAsync(
        DateTime date,
        IReadOnlyCollection<ActionStatus> statuses,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<ActionItem>.Filter.Lte(item => item.DueDate, date)
            & Builders<ActionItem>.Filter.In(item => item.Status, statuses);
        return await context.ActionItems.Find(filter).ToListAsync(cancellationToken);
    }

    /// <summary>Gets items assigned to a user.</summary>
    public async Task<IReadOnlyList<ActionItem>> GetByOwnerIdAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default) =>
        await context.ActionItems.Find(item => item.OwnerId == ownerId).ToListAsync(cancellationToken);

    /// <summary>Gets active workflow items used by dashboard owner counts.</summary>
    public async Task<IReadOnlyList<ActionItem>> GetOpenForDashboardAsync(CancellationToken cancellationToken = default) =>
        await context.ActionItems.Find(Builders<ActionItem>.Filter.In(item => item.Status, DashboardStatuses))
            .ToListAsync(cancellationToken);

    /// <summary>Inserts action items independently of meeting document storage.</summary>
    public Task AddManyAsync(IReadOnlyCollection<ActionItem> items, CancellationToken cancellationToken = default) =>
        items.Count == 0
            ? Task.CompletedTask
            : context.ActionItems.InsertManyAsync(items, cancellationToken: cancellationToken);

    /// <summary>Updates an item only when its version still matches the caller's snapshot.</summary>
    public async Task<bool> TryUpdateAsync(
        ActionItem item,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<ActionItem>.Filter.Eq(current => current.Id, item.Id)
            & Builders<ActionItem>.Filter.Eq(current => current.Version, expectedVersion);
        var update = Builders<ActionItem>.Update
            .Set(current => current.Title, item.Title)
            .Set(current => current.OwnerId, item.OwnerId)
            .Set(current => current.OwnerName, item.OwnerName)
            .Set(current => current.SuggestedOwnerName, item.SuggestedOwnerName)
            .Set(current => current.DueDate, item.DueDate)
            .Set(current => current.DueDateText, item.DueDateText)
            .Set(current => current.Status, item.Status)
            .Set(current => current.Confidence, item.Confidence)
            .Set(current => current.SourceQuote, item.SourceQuote)
            .Set(current => current.Timestamp, item.Timestamp)
            .Set(current => current.NeedsInput, item.NeedsInput)
            .Set(current => current.ExternalTicketId, item.ExternalTicketId)
            .Set(current => current.SlipCount, item.SlipCount)
            .Set(current => current.Version, expectedVersion + 1);
        var result = await context.ActionItems.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
        if (result.MatchedCount == 0)
        {
            return false;
        }

        item.Version = expectedVersion + 1;
        return true;
    }
}
