using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MongoDB.Driver;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Stores follow-up records separately from action items and queries reminder history.</summary>
public sealed class MongoFollowUpRepository(MongoContext context) : IFollowUpRepository
{
    /// <summary>Records one follow-up.</summary>
    public Task AddAsync(FollowUp followUp, CancellationToken cancellationToken = default) =>
        context.FollowUps.InsertOneAsync(followUp, cancellationToken: cancellationToken);

    /// <summary>Records multiple follow-ups.</summary>
    public Task AddManyAsync(IReadOnlyCollection<FollowUp> followUps, CancellationToken cancellationToken = default) =>
        followUps.Count == 0
            ? Task.CompletedTask
            : context.FollowUps.InsertManyAsync(followUps, cancellationToken: cancellationToken);

    /// <summary>Checks whether an item already has a follow-up of this channel on a UTC date.</summary>
    public Task<bool> ExistsOnDateAsync(
        Guid actionItemId,
        string channel,
        DateTime utcDate,
        CancellationToken cancellationToken = default)
    {
        var start = DateTime.SpecifyKind(utcDate.Date, DateTimeKind.Utc);
        var end = start.AddDays(1);
        var filter = Builders<FollowUp>.Filter.Eq(followUp => followUp.ActionItemId, actionItemId)
            & Builders<FollowUp>.Filter.Eq(followUp => followUp.Channel, channel)
            & Builders<FollowUp>.Filter.Gte(followUp => followUp.SentAt, start)
            & Builders<FollowUp>.Filter.Lt(followUp => followUp.SentAt, end);
        return context.FollowUps.Find(filter).AnyAsync(cancellationToken);
    }

    /// <summary>Gets all follow-ups for an action item in chronological order.</summary>
    public async Task<IReadOnlyList<FollowUp>> GetByActionItemAsync(
        Guid actionItemId,
        CancellationToken cancellationToken = default) =>
        await context.FollowUps.Find(followUp => followUp.ActionItemId == actionItemId)
            .SortBy(followUp => followUp.SentAt).ToListAsync(cancellationToken);

    /// <summary>Gets every follow-up for operational verification.</summary>
    public async Task<IReadOnlyList<FollowUp>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await context.FollowUps.Find(Builders<FollowUp>.Filter.Empty).ToListAsync(cancellationToken);
}
